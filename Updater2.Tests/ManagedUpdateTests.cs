using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DS4Updater.Dtos;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests;

[TestClass]
public sealed class ManagedUpdateTests
{
    private const string Tag = "VIIPERRC4.6.5";
    private const string Root = @"C:\Program Files\DS4Windows";
    private const string Version = "5.0.11.0";
    private const string Installer = "DS4Windows_5.0.11.0_Setup_x64.exe";
    private const string Updater = Root + @"\DS4Updater.exe";

    [TestMethod]
    public void LegacyInstalledArgumentsAreCompatibilityHintsNotLaunchOrPathGrants()
    {
        var request = ManagedUpdateRequest.Parse(new[] { "-autolaunch", "-user", "--releaseTag", Tag,
            "--launchExe", "MyController.exe" }, Updater, _ => Root);
        Assert.AreEqual(new ManagedUpdateRequest(Root, Tag, "MyController.exe"), request);
        Assert.IsNull(ManagedUpdateRequest.Parse(Array.Empty<string>(), Updater, _ => Root).ReleaseTag);
    }

    [TestMethod]
    public void ExplicitManagedBootstrapRequiresExactRegisteredTarget()
    {
        string bootstrap = @"C:\Users\test\AppData\Local\DS4Windows\Updater\managed-123\DS4Updater.exe";
        string[] args = { "--managed-safe-v1", "--targetDirectory", Root, "--releaseTag", Tag };
        Assert.AreEqual(Root, ManagedUpdateRequest.Parse(args, bootstrap,
            _ => throw new AssertFailedException("A staged updater is not the installed image."), target => target).TargetDirectory);
        Assert.ThrowsException<InvalidDataException>(() => ManagedUpdateRequest.Parse(args, bootstrap, _ => null, _ => null));
        Assert.ThrowsException<InvalidDataException>(() => ManagedUpdateRequest.Parse(args, bootstrap, _ => null, _ => @"D:\other"));
        Assert.ThrowsException<ArgumentException>(() => ManagedUpdateRequest.Parse(args.Skip(1).ToArray(), bootstrap, _ => Root, _ => Root));
    }

    [DataTestMethod]
    [DataRow("--portable-safe-v1")]
    [DataRow("--portable-worker")]
    [DataRow("--originalExe")]
    [DataRow("/quiet")]
    [DataRow("--unknown")]
    public void ManagedParserRejectsOtherLifetimesAndUnknownFlags(string option) =>
        Assert.ThrowsException<ArgumentException>(() => ManagedUpdateRequest.Parse(new[] { option }, Updater, _ => Root));

    [TestMethod]
    public void ManagedParserRejectsDuplicatesMissingValuesAndUntrustedNames()
    {
        foreach (string[] args in new[]
        {
            new[] { "-autolaunch", "-autolaunch" }, new[] { "--releaseTag" },
            new[] { "--releaseTag", Tag, "--releaseTag", Tag }, new[] { "--releaseTag", "../evil" },
            new[] { "--launchExe", "..\\other.exe" }, new[] { "--launchExe", "" },
            new[] { "--managed-safe-v1", "--targetDirectory", @"\\server\share" },
            new[] { "--managed-safe-v1", "--targetDirectory", @"relative\path" },
        })
            Assert.ThrowsException<ArgumentException>(() => ManagedUpdateRequest.Parse(args, Updater, _ => Root, _ => Root));
        Assert.ThrowsException<InvalidDataException>(() => ManagedUpdateRequest.Parse(Array.Empty<string>(), Updater, _ => @"D:\other"));
    }

    [TestMethod]
    public void InstallerReceiptWorksWithoutPortableZipAndBindsExactVersionHashAndName()
    {
        Fixture fixture = MakeFixture();
        PortableReleaseIdentity identity = PortableReleaseResolver.ResolveInstaller(fixture.Release, "x64", fixture.Receipt);
        Assert.AreEqual(Installer, identity.Asset.name);
        Assert.AreEqual(new System.Version(Version), identity.FileVersion);
        Assert.IsTrue(identity.FromBuildReceipt);
        Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.Resolve(fixture.Release, "x64", fixture.Receipt),
            "An installer must not accidentally qualify as a portable package.");
    }

    [TestMethod]
    public void InstallerRejectsAbsentReceiptAmbiguousAssetWrongDigestAndWrongOrigin()
    {
        Fixture f = MakeFixture();
        GitHubReleaseAsset installer = f.Release.assets[0];
        foreach (GitHubRelease release in new[]
        {
            f.Release with { draft = true }, f.Release with { id = f.Release.id + 1 },
            f.Release with { assets = new[] { installer } },
            f.Release with { assets = f.Release.assets.Append(installer).ToArray() },
            f.Release with { assets = new[] { installer with { digest = "sha256:" + new string('f', 64) }, f.Release.assets[1] } },
            f.Release with { assets = new[] { installer with { name = Installer.ToLowerInvariant() }, f.Release.assets[1] } },
            f.Release with { assets = new[] { installer with { browser_download_url = "https://evil.example/" + Installer }, f.Release.assets[1] } },
            f.Release with { assets = new[] { installer with { size = ManagedUpdateCoordinator.MaximumInstallerBytes + 1 }, f.Release.assets[1] } },
        })
            Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.ResolveInstaller(release, "x64", f.Receipt));
        Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.ResolveInstaller(f.Release, "x86", f.Receipt));
        Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.ResolveInstaller(f.Release, "x64", f.Receipt[..^1]));
    }

    [TestMethod]
    public async Task ManagedUpdateLaunchesOnlyAfterRevalidatingRootAndInstalledIdentity()
    {
        var operations = new FakeOperations(MakeFixture());
        var request = new ManagedUpdateRequest(Root, Tag, "DS4Windows.exe");
        PortableReleaseIdentity result = await ManagedUpdateCoordinator.ExecuteAsync(request, operations, null, default);
        Assert.AreEqual(Tag, result.Tag);
        CollectionAssert.AreEqual(new[] { "root", "identity", "release", "receipt", "download", "root", "identity", "launch" }, operations.Calls);
    }

    [TestMethod]
    public async Task ManagedUpdateNeverLaunchesAfterTargetOrIdentityChanges()
    {
        foreach (bool targetChanged in new[] { false, true })
        {
            var operations = new FakeOperations(MakeFixture()) { ChangeRoot = targetChanged, ChangeIdentity = !targetChanged };
            await Assert.ThrowsExceptionAsync<IOException>(() => ManagedUpdateCoordinator.ExecuteAsync(
                new(Root, Tag, "DS4Windows.exe"), operations, null, default));
            Assert.IsFalse(operations.Calls.Contains("launch"));
        }
    }

    [TestMethod]
    public async Task ManagedUpdateRejectsTagAndBinaryDowngradesDraftsAndWrongChannel()
    {
        foreach (var installed in new[]
        {
            new PortableInstalledIdentity("5.0.10.0", "VIIPERRC4.6.6", "VIIPERRC4.6.6"),
            new PortableInstalledIdentity("5.0.12.0", "VIIPERRC4.6.4", "VIIPERRC4.6.4"),
            new PortableInstalledIdentity("4.0.0.0", "4.0.0.0", "v4.0.0.0"),
        })
        {
            var operations = new FakeOperations(MakeFixture()) { Installed = installed };
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => ManagedUpdateCoordinator.ExecuteAsync(
                new(Root, Tag, "DS4Windows.exe"), operations, null, default));
            Assert.IsFalse(operations.Calls.Contains("download"));
        }
        foreach (GitHubRelease release in new[] { MakeFixture().Release with { draft = true }, MakeFixture().Release with { tag_name = "VIIPERRC4.6.6" } })
        {
            var operations = new FakeOperations(MakeFixture() with { Release = release });
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => ManagedUpdateCoordinator.ExecuteAsync(
                new(Root, Tag, "DS4Windows.exe"), operations, null, default));
            Assert.IsFalse(operations.Calls.Contains("download"));
        }
    }

    [TestMethod]
    public async Task ManagedMissingTagUsesChannelSelectionWithoutReinstallingSameRelease()
    {
        var operations = new FakeOperations(MakeFixture()) { Installed = new(Version, Tag, Tag) };
        Assert.IsNull(await ManagedUpdateCoordinator.ExecuteAsync(new(Root, null, "DS4Windows.exe"), operations, null, default));
        Assert.IsFalse(operations.Calls.Contains("download"));
        Assert.IsNull(operations.RequestedTag);
        Assert.IsTrue(operations.Prerelease);
    }

    [TestMethod]
    public async Task CancellationAfterDownloadPreventsSetupLaunch()
    {
        using var cancellation = new CancellationTokenSource();
        var operations = new FakeOperations(MakeFixture()) { AfterDownload = cancellation.Cancel };
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => ManagedUpdateCoordinator.ExecuteAsync(
            new(Root, Tag, "DS4Windows.exe"), operations, null, cancellation.Token));
        Assert.IsFalse(operations.Calls.Contains("launch"));
    }

    [TestMethod]
    public async Task VerifiedInstallerIsPinnedAcrossFakeRunAsLaunchWithOnlySetupArguments()
    {
        Fixture fixture = MakeFixture();
        string parent = NewTemp();
        ProcessStartInfo launch = null;
        try
        {
            using var operations = new ManagedUpdateOperations(_ => Root,
                new StaticHandler(fixture.Image), start =>
                {
                    launch = start;
                    Assert.ThrowsException<IOException>(() => { using var mutation = new FileStream(start.FileName, FileMode.Open, FileAccess.Write); });
                    Assert.ThrowsException<IOException>(() => Directory.Move(start.WorkingDirectory, start.WorkingDirectory + "-renamed"));
                }, _ => Version, parent);
            PortableReleaseIdentity release = PortableReleaseResolver.ResolveInstaller(fixture.Release, "x64", fixture.Receipt);
            string path = await operations.DownloadInstallerAsync(release.Asset, default);
            operations.LaunchVerifiedInstaller(path, release);
            Assert.IsNotNull(launch);
            Assert.IsTrue(launch.UseShellExecute);
            Assert.AreEqual("runas", launch.Verb);
            Assert.AreEqual(Installer, Path.GetFileName(launch.FileName));
            Assert.AreEqual(Path.GetDirectoryName(path), launch.WorkingDirectory);
            CollectionAssert.AreEqual(new[] { "/install", "/norestart" }, launch.ArgumentList.ToArray());
        }
        finally { Directory.Delete(parent, true); }
    }

    [TestMethod]
    public async Task InstallerRejectsTamperWrongVersionAndMalformedPeBeforeFakeLaunch()
    {
        foreach (string failure in new[] { "tamper", "version", "header" })
        {
            Fixture fixture = MakeFixture(failure == "header" ? new byte[512] : null);
            string parent = NewTemp();
            bool started = false;
            try
            {
                using var operations = new ManagedUpdateOperations(_ => Root, new StaticHandler(fixture.Image),
                    _ => started = true, _ => failure == "version" ? "5.0.12.0" : Version, parent);
                PortableReleaseIdentity release = PortableReleaseResolver.ResolveInstaller(fixture.Release, "x64", fixture.Receipt);
                string path = await operations.DownloadInstallerAsync(release.Asset, default);
                if (failure == "tamper") File.AppendAllText(path, "changed");
                Assert.ThrowsException<InvalidDataException>(() => operations.LaunchVerifiedInstaller(path, release));
                Assert.IsFalse(started);
            }
            finally { Directory.Delete(parent, true); }
        }
    }

    [TestMethod]
    public void BasicImageValidationRejectsDllAndForeignArchitecture()
    {
        foreach (byte[] image in new[] { PeImage(characteristics: 0x2102), PeImage(machine: 0x1c4), new byte[32] })
            Assert.ThrowsException<InvalidDataException>(() => ManagedUpdateOperations.ValidatePeImage(new MemoryStream(image)));
    }

    private static string NewTemp() => Directory.CreateTempSubdirectory("DS4Updater-managed-test-").FullName;

    private sealed class StaticHandler(byte[] image) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
    }

    private sealed class FakeOperations(Fixture fixture) : IManagedUpdateOperations
    {
        internal List<string> Calls { get; } = new();
        internal PortableInstalledIdentity Installed { get; set; } = new("5.0.10.0", "VIIPERRC4.6.4", "VIIPERRC4.6.4");
        internal bool ChangeRoot, ChangeIdentity, Prerelease;
        internal string RequestedTag;
        internal Action AfterDownload;
        public string ValidateRoot(string target) { Calls.Add("root"); return ChangeRoot && Calls.Count > 1 ? @"D:\changed" : target; }
        public PortableInstalledIdentity ReadIdentity(string root, string launchExe)
        { Calls.Add("identity"); return ChangeIdentity && Calls.Count > 2 ? Installed with { FileVersion = "9.0.0.0" } : Installed; }
        public Task<GitHubRelease> FetchReleaseAsync(string tag, bool prerelease, CancellationToken cancellation)
        { Calls.Add("release"); RequestedTag = tag; Prerelease = prerelease; return Task.FromResult(fixture.Release); }
        public Task<byte[]> DownloadReceiptAsync(GitHubReleaseAsset asset, CancellationToken cancellation)
        { Calls.Add("receipt"); return Task.FromResult(fixture.Receipt); }
        public Task<string> DownloadInstallerAsync(GitHubReleaseAsset asset, CancellationToken cancellation)
        { Calls.Add("download"); AfterDownload?.Invoke(); return Task.FromResult(@"C:\stage\" + asset.name); }
        public void LaunchVerifiedInstaller(string path, PortableReleaseIdentity release) => Calls.Add("launch");
    }

    private sealed record Fixture(GitHubRelease Release, byte[] Receipt, byte[] Image);
    private static Fixture MakeFixture(byte[] image = null)
    {
        image ??= PeImage();
        string hash = Convert.ToHexString(SHA256.HashData(image));
        byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, repository = "Pav-Osmolski/DS4Windows", tag = Tag,
            releaseId = 100, binaryVersion = Version, assets = new[] { new { name = Installer, sha256 = hash } } });
        GitHubReleaseAsset Asset(string name, byte[] bytes) => new(name,
            $"https://github.com/Pav-Osmolski/DS4Windows/releases/download/{Tag}/{name}", bytes.Length,
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)));
        return new(new GitHubRelease(Tag, true, false, DateTimeOffset.UtcNow, null,
            new[] { Asset(Installer, image), Asset("RELEASE-BUILD.json", receipt) }, 100), receipt, image);
    }

    private static byte[] PeImage(ushort machine = 0x14c, ushort characteristics = 0x102)
    {
        byte[] bytes = new byte[1024];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), 0x80);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x94), 0xe0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), characteristics);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x98), 0x10b);
        return bytes;
    }
}
