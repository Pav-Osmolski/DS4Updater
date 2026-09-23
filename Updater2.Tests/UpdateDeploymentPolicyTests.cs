using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests;

[TestClass]
public sealed class UpdateDeploymentPolicyTests
{
    private string root;

    [TestInitialize]
    public void CreateFolder()
    {
        root = Path.Combine(Path.GetTempPath(), "DS4Updater-deployment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void RemoveFixture()
    {
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
            Path.GetFileName(root).StartsWith("DS4Updater-deployment-", StringComparison.Ordinal))
            Directory.Delete(root, true);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RegisteredInstallIgnoresLeftoverPortableMarkerForLegacyAppHandoff(bool damaged)
    {
        string marker = Path.Combine(root, PortableUpdateProcessGuard.MarkerFileName);
        if (damaged) Directory.CreateDirectory(marker);
        else File.WriteAllText(marker, PortableUpdateProcessGuard.MarkerText);
        string[] args = { "-autolaunch", "-user", "--releaseTag", "VIIPERRC4.6.5", "--launchExe", "DS4Windows.exe" };
        Assert.AreEqual(UpdateDeployment.Managed, UpdateDeploymentPolicy.Resolve(args,
            Path.Combine(root, "DS4Updater.exe"), () => new[] { root + "\\" }));
    }

    [TestMethod]
    public void ExplicitPortableRequestNeverGainsManagedFallback()
    {
        foreach (string arg in new[] { "--portable-safe-v1", "--portable-worker", "--portable-typo" })
            Assert.AreEqual(UpdateDeployment.Portable, UpdateDeploymentPolicy.Resolve(new[] { arg },
                Path.Combine(root, "DS4Updater.exe"), () => new[] { root }));
    }

    [TestMethod]
    public void StagedManagedHandoffCannotEnterLegacyCopyLifecycle()
    {
        string[] args = { "--managed-safe-v1", "--targetDirectory", root,
            "--releaseTag", "VIIPERRC4.6.5", "--launchExe", "DS4Windows.exe" };
        string updater = Path.Combine(root, "cache", "DS4Updater.exe");
        Assert.AreEqual(UpdateDeployment.Managed, UpdateDeploymentPolicy.Resolve(args, updater, () => new[] { root }));
        Assert.AreEqual(UpdateDeployment.Managed, UpdateDeploymentPolicy.Resolve(new[] { "--managed-typo" }, updater));
        Assert.AreEqual(UpdateDeployment.Portable, UpdateDeploymentPolicy.Resolve(
            args.Concat(new[] { "--portable-typo" }).ToArray(), updater));
    }

    [TestMethod]
    public void OwnedWorkerCannotEnterManagedOrLegacyLifetime()
    {
        string worker = Path.Combine(root, "Updates", "portable-worker-" + Guid.NewGuid().ToString("N"), "DS4Updater.exe");
        Assert.AreEqual(UpdateDeployment.Portable, UpdateDeploymentPolicy.Resolve(Array.Empty<string>(), worker,
            () => new[] { Path.GetDirectoryName(worker) }));
    }

    [TestMethod]
    public void SeparatePortableCopyAndSamePrefixDirectoryRemainPortable()
    {
        File.WriteAllText(Path.Combine(root, PortableUpdateProcessGuard.MarkerFileName), "invalid still requires safe parser");
        foreach (string managed in new[] { root + "-installed", Path.Combine(root, "installed") })
            Assert.AreEqual(UpdateDeployment.Portable, UpdateDeploymentPolicy.Resolve(new[] { "-autolaunch" },
                Path.Combine(root, "DS4Updater.exe"), () => new[] { managed }));
    }

    [TestMethod]
    public void UnregisteredUnmarkedLegacyLayoutIsNotRetargeted()
    {
        Assert.AreEqual(UpdateDeployment.Legacy, UpdateDeploymentPolicy.Resolve(Array.Empty<string>(),
            Path.Combine(root, "DS4Updater.exe"), () => Array.Empty<string>()));
    }

    [TestMethod]
    public void BrokenRegistrationCannotAuthorizeAnyUpdate()
    {
        foreach (string registered in new[] { "relative\\folder", @"\\server\share", Path.GetPathRoot(root) })
            Assert.ThrowsException<InvalidDataException>(() => UpdateDeploymentPolicy.Resolve(Array.Empty<string>(),
                Path.Combine(root, "DS4Updater.exe"), () => new[] { registered }));
        Assert.ThrowsException<UnauthorizedAccessException>(() => UpdateDeploymentPolicy.Resolve(Array.Empty<string>(),
            Path.Combine(root, "DS4Updater.exe"), () => throw new UnauthorizedAccessException()));
    }
}
