using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests;

[TestClass]
public sealed class ReleasedPortablePackageTests
{
    [TestMethod]
    [TestCategory("ReleasedPackage")]
    public void ActualForkRc467ArchivePreservesProfilesCustomNamesAndLangLayout()
    {
        string archive = Environment.GetEnvironmentVariable("DS4UPDATER_RC467_PACKAGE");
        if (string.IsNullOrWhiteSpace(archive))
            Assert.Inconclusive("Opt in with DS4UPDATER_RC467_PACKAGE pointing to this fork's RC4.6.7 portable ZIP.");
        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.IsNotNull(zip.GetEntry("DS4Windows/Lang/de/DS4Windows.resources.dll"));
            Assert.IsNull(zip.GetEntry("DS4Windows/de/DS4Windows.resources.dll"));
        }
        VerifyCustomTransactions(archive,
            "F37E860A84F3A08D7FDAD6FD2D1C4D694F0344AEDF8634FDEDF6C350B54A3087",
            "VIIPERRC4.6.7", "5.0.12.0", null);
    }

    [TestMethod]
    [TestCategory("ReleasedPackage")]
    public void ActualRc461ArchiveKeepsCustomApphostsAcrossUpdatesAndNameChanges()
    {
        string archive = Environment.GetEnvironmentVariable("DS4UPDATER_RC461_PACKAGE");
        if (string.IsNullOrWhiteSpace(archive))
            Assert.Inconclusive("Opt in with DS4UPDATER_RC461_PACKAGE pointing to the unchanged RC4.6.1 portable ZIP.");
        const string digest = "0B5B05E491AA01F6EAC58A33B1742FEA62481F7ABDBBC5EBAAE64BE7E7604541";
        // Low-level transaction regression only. The production coordinator
        // rejects custom-only layout for this older, incompatible app version.
        VerifyCustomTransactions(archive, digest, "VIIPERRC4.6.1", "5.0.7.0", 552);
    }

    [TestMethod]
    [TestCategory("ReleasedPackage")]
    public void ActualRc462ArchiveKeepsCustomApphostsAcrossUpdatesAndNameChanges()
    {
        string archive = Environment.GetEnvironmentVariable("DS4UPDATER_RC462_PACKAGE");
        string digest = Environment.GetEnvironmentVariable("DS4UPDATER_RC462_SHA256");
        if (string.IsNullOrWhiteSpace(archive) && string.IsNullOrWhiteSpace(digest))
            Assert.Inconclusive("Opt in with DS4UPDATER_RC462_PACKAGE and the independently verified release SHA-256 in DS4UPDATER_RC462_SHA256.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(archive));
        Assert.IsTrue(digest != null && digest.Length == 64 && digest.All(Uri.IsHexDigit),
            "The release-candidate fixture requires an explicit independently verified SHA-256.");
        VerifyCustomTransactions(archive, digest.ToUpperInvariant(), "VIIPERRC4.6.2", "5.0.8.0", null);
    }

    private static void VerifyCustomTransactions(string archive, string digest, string tag, string version, int? expectedCount)
    {
        using (var input = File.OpenRead(archive))
            Assert.AreEqual(digest, Convert.ToHexString(SHA256.HashData(input)));

        string root = Path.Combine(Path.GetTempPath(), "ds4w-released-custom-tests-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "app");
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(target, PortablePackageTransaction.MarkerName), PortablePackageTransaction.MarkerText);
            File.WriteAllText(Path.Combine(target, "DS4Windows.exe"), "old default fixture, never executed");
            File.WriteAllText(Path.Combine(target, "DS4Windows.release"), "VIIPERRC4.6");
            File.WriteAllText(Path.Combine(target, PortablePackageTransaction.ManifestName),
                "DS4Windows.portable\nDS4Windows.exe\nDS4Windows.release\n");
            var userFiles = new Dictionary<string, string>
            {
                ["Profiles/Default.xml"] = "<Profile><Name>Keep my controller mapping</Name></Profile>",
                ["Actions.xml"] = "<Actions />", ["LinkedProfiles.xml"] = "<Links />",
                ["portable-data/VIIPER/viiper.key.txt"] = "non-secret synthetic fixture",
                ["Plugins/custom.dll"] = "user-owned fixture", ["Logs/session.txt"] = "keep fixture log"
            };
            foreach (var file in userFiles)
            {
                string path = Path.Combine(target, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, file.Value);
            }
            using var zip = ZipFile.OpenRead(archive);
            var expectedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries.Where(item => !item.FullName.EndsWith('/')))
            {
                string relative = entry.FullName["DS4Windows/".Length..];
                if (relative == PortablePackageTransaction.ManifestName) continue;
                using var expected = entry.Open();
                expectedHashes.Add(relative, Convert.ToHexString(SHA256.HashData(expected)));
            }
            if (expectedCount.HasValue) Assert.AreEqual(expectedCount.Value, expectedHashes.Count);
            Assert.IsTrue(expectedHashes.Count >= 500, "The real fixture must retain the complete portable payload.");
            string previous = null;
            foreach (string name in new[] { "Game.Pad", "Controller Companion" })
            {
                string setting = name + "\r\n";
                File.WriteAllText(Path.Combine(target, "custom_exe_name.txt"), setting);
                using (var transaction = PortablePackageTransaction.Prepare(target, archive, digest, tag))
                {
                    transaction.Apply(name);
                    Assert.IsFalse(transaction.RecoveryRequired);
                }
                foreach (var file in expectedHashes)
                {
                    string relative = file.Key == "DS4Windows.exe" ? name + ".exe" : file.Key;
                    using var actual = File.OpenRead(Path.Combine(target, relative));
                    Assert.AreEqual(file.Value, Convert.ToHexString(SHA256.HashData(actual)), relative);
                }
                foreach (string suffix in new[] { ".runtimeconfig.json", ".deps.json" })
                {
                    using var actual = File.OpenRead(Path.Combine(target, name + suffix));
                    Assert.AreEqual(expectedHashes["DS4Windows" + suffix], Convert.ToHexString(SHA256.HashData(actual)));
                }
                string[] expectedOwnership = expectedHashes.Keys.Where(path => path != "DS4Windows.exe")
                    .Concat(new[] { name + ".exe", name + ".runtimeconfig.json", name + ".deps.json" }).ToArray();
                CollectionAssert.AreEquivalent(expectedOwnership,
                    File.ReadAllLines(Path.Combine(target, PortablePackageTransaction.ManifestName)));
                using var operations = new PortableUpdateOperations(root, null);
                Assert.AreEqual(new PortableInstalledIdentity(version, tag, tag),
                    operations.ReadIdentity(target, name + ".exe"));
                Assert.IsFalse(File.Exists(Path.Combine(target, "DS4Windows.exe")));
                if (previous != null)
                    foreach (string suffix in new[] { ".exe", ".runtimeconfig.json", ".deps.json" })
                        Assert.IsFalse(File.Exists(Path.Combine(target, previous + suffix)));
                Assert.AreEqual(setting, File.ReadAllText(Path.Combine(target, "custom_exe_name.txt")));
                foreach (var file in userFiles) Assert.AreEqual(file.Value, File.ReadAllText(Path.Combine(target, file.Key)), file.Key);
                Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(target, "Updates"), "portable-update-*").Length);
                previous = name;
            }
        }
        finally
        {
            string verified = Path.GetFullPath(root);
            if (Path.GetDirectoryName(verified) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(verified).StartsWith("ds4w-released-custom-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unsafe released-package fixture cleanup.");
            if (Directory.Exists(verified)) Directory.Delete(verified, true);
        }
    }

    [TestMethod]
    [TestCategory("ReleasedPackage")]
    public void ActualRc451ArchiveStagesAndAppliesWithoutTouchingUserData()
    {
        string archive = Environment.GetEnvironmentVariable("DS4UPDATER_RC451_PACKAGE");
        if (string.IsNullOrWhiteSpace(archive))
            Assert.Inconclusive("Opt in with DS4UPDATER_RC451_PACKAGE pointing to the unchanged RC4.5.1 portable ZIP.");
        const string digest = "985B7DA39AB682FAE9ED27EC4B1622F58C240911454CFFC29CD661EC13BBA109";
        using (var input = File.OpenRead(archive))
            Assert.AreEqual(digest, Convert.ToHexString(SHA256.HashData(input)));

        string root = Path.Combine(Path.GetTempPath(), "ds4w-released-package-tests-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "app");
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(target, PortablePackageTransaction.MarkerName), PortablePackageTransaction.MarkerText);
            File.WriteAllText(Path.Combine(target, "DS4Windows.release"), "VIIPERRC4.5");
            File.WriteAllText(Path.Combine(target, "old-only.dll"), "old owned fixture");
            File.WriteAllText(Path.Combine(target, PortablePackageTransaction.ManifestName),
                "DS4Windows.portable\nDS4Windows.release\nold-only.dll\n");
            var userFiles = new Dictionary<string, string>
            {
                ["Profiles/Default.xml"] = "<Profile><Name>Keep my profile</Name></Profile>",
                ["Actions.xml"] = "<Actions />", ["LinkedProfiles.xml"] = "<Links />",
                ["portable-data/Nintendo/OriginalJoyConPairs.json"] = "[]",
                ["portable-data/VIIPER/viiper.key.txt"] = "non-secret synthetic fixture",
                ["Plugins/custom.dll"] = "user-owned fixture", ["Logs/session.txt"] = "keep fixture log"
            };
            foreach (var file in userFiles)
            {
                string path = Path.Combine(target, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, file.Value);
            }
            using (var transaction = PortablePackageTransaction.Prepare(target, archive, digest, "VIIPERRC4.5.1"))
            {
                Assert.AreEqual("VIIPERRC4.5", File.ReadAllText(Path.Combine(target, "DS4Windows.release")));
                Assert.IsFalse(File.Exists(Path.Combine(target, "DS4Windows.exe")));
                Assert.AreEqual("5.0.5.1", FileVersionInfo.GetVersionInfo(
                    "\\\\?\\" + Path.Combine(transaction.StagedRoot, "DS4Windows.exe")).FileVersion);
                transaction.Apply();
                Assert.IsFalse(transaction.RecoveryRequired);
            }
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries.Where(item => !item.FullName.EndsWith('/')))
            {
                string relative = entry.FullName["DS4Windows/".Length..];
                if (relative == PortablePackageTransaction.ManifestName) continue;
                using var expected = entry.Open();
                using var actual = File.OpenRead(Path.Combine(target, relative));
                Assert.AreEqual(Convert.ToHexString(SHA256.HashData(expected)),
                    Convert.ToHexString(SHA256.HashData(actual)), relative);
            }
            Assert.IsFalse(File.Exists(Path.Combine(target, "old-only.dll")));
            foreach (var file in userFiles) Assert.AreEqual(file.Value, File.ReadAllText(Path.Combine(target, file.Key)), file.Key);
            Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(target, "Updates"), "portable-update-*").Length);
        }
        finally
        {
            string verified = Path.GetFullPath(root);
            if (Path.GetDirectoryName(verified) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(verified).StartsWith("ds4w-released-package-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unsafe released-package fixture cleanup.");
            if (Directory.Exists(verified)) Directory.Delete(verified, true);
        }
    }
}
