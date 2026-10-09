using DS4Updater.Dtos;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace DS4Updater.Tests;

[TestClass]
public sealed class PortableReleasePolicyTests
{
    private const string Tag = "VIIPERRC4.5.1";
    private const string Name = "DS4Windows_VIIPER_x64.zip";
    private static readonly string Hash = new('a', 64);

    [DataTestMethod]
    [DataRow("VIIPERRC4", "5.0.0.0")]
    [DataRow("VIIPERRC4.0", "5.0.0.0")]
    [DataRow("VIIPERRC4.1", "5.0.1.0")]
    [DataRow("VIIPERRC4.2", "5.0.2.0")]
    [DataRow("VIIPERRC4.3", "5.0.3.0")]
    [DataRow("VIIPERRC4.4", "5.0.4.0")]
    [DataRow("VIIPERRC4.5", "5.0.5.0")]
    [DataRow("VIIPERRC4.5.1", "5.0.5.1")]
    [DataRow("VIIPERRC4.5.2", "5.0.5.2")]
    public void KnownRcTagIsVerifiedAgainstItsWindowsVersionNotItsOrdinal(string tag, string version)
    {
        Assert.IsTrue(ReleaseChannelPolicy.VerifyInstalledIdentity(tag, version, tag, tag));
        Assert.IsTrue(ReleaseChannelPolicy.VerifyInstalledIdentity(tag, version, version, tag));
        Assert.IsTrue(ReleaseChannelPolicy.VerifyInstalledIdentity(tag, version, version, null),
            "Historical release metadata may predate the release marker.");
        Assert.IsFalse(ReleaseChannelPolicy.VerifyInstalledIdentity(tag, "4.5.1.0", tag, tag));
    }

    [DataTestMethod]
    [DataRow("5.0.5.0", "VIIPERRC4.5.1", "VIIPERRC4.5.1")]
    [DataRow("5.0.5.1", "VIIPERRC4.5", "VIIPERRC4.5.1")]
    [DataRow("5.0.5.1", "5.0.5.0", "VIIPERRC4.5.1")]
    [DataRow("5.0.5.1", "VIIPERRC4.5.1", "VIIPERRC4.5")]
    [DataRow("5.0.5.1", "5.0.5.1-preview", "VIIPERRC4.5.1")]
    [DataRow("5.0.5.1", "5.0.5.1-unknown", "VIIPERRC4.5.1")]
    [DataRow("5.0.5.1", null, "VIIPERRC4.5.1")]
    [DataRow("unknown", "VIIPERRC4.5.1", "VIIPERRC4.5.1")]
    [DataRow(null, "VIIPERRC4.5.1", "VIIPERRC4.5.1")]
    [DataRow("5.0.5.1 garbage", "VIIPERRC4.5.1", "VIIPERRC4.5.1")]
    public void MarkerCannotHideWrongMissingOrConflictingPeIdentity(string file, string product, string marker) =>
        Assert.IsFalse(ReleaseChannelPolicy.VerifyInstalledIdentity(Tag, file, product, marker));

    [TestMethod]
    public void Rc452RequiresItsOwnBinaryAndExactReleaseAsset()
    {
        const string hotfixTag = "VIIPERRC4.5.2";
        Assert.IsFalse(ReleaseChannelPolicy.VerifyInstalledIdentity(hotfixTag, "5.0.5.1", hotfixTag, hotfixTag));
        Assert.IsFalse(ReleaseChannelPolicy.VerifyInstalledIdentity(hotfixTag, "5.0.5.2", Tag, hotfixTag));
        Assert.IsFalse(ReleaseChannelPolicy.VerifyInstalledIdentity(hotfixTag, "5.0.5.2", hotfixTag, Tag));
        foreach (string name in new[] { Name, "DS4Windows_5.0.5.2_x64.zip", "DS4Windows_VIIPERRC4.5.2_x64.zip" })
        {
            var asset = Asset(name, hotfixTag);
            Assert.AreSame(asset, ReleaseChannelPolicy.SelectPortableAsset(Release(hotfixTag, new[] { asset }), "x64"));
        }
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(hotfixTag, new[] { Asset() }), "x64"));
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(hotfixTag,
            new[] { Asset("DS4Windows_5.0.5.1_x64.zip", hotfixTag) }), "x64"));
    }

    [DataTestMethod]
    [DataRow("VIIPERRC4.5.4")]
    [DataRow("VIIPERRC4.5.3.1")]
    [DataRow("VIIPERRC4.6")]
    [DataRow("VIIPERRC4.5.1-hotfix")]
    [DataRow("VIIPERBeta99")]
    [DataRow("v5.0.5.1.0")]
    [DataRow("prefix-5.0.5.1")]
    [DataRow("v5.0.5.1 garbage")]
    [DataRow(null)]
    public void UnknownIdentityIsRejectedInsteadOfGuessingWindowsVersion(string tag)
    {
        Assert.IsFalse(ReleaseChannelPolicy.TryGetExpectedFileVersion(tag, out _));
        Assert.IsFalse(ReleaseChannelPolicy.VerifyInstalledIdentity(tag, "5.0.5.1", tag, tag));
    }

    [TestMethod]
    public void NumericVersionsNormalizeMissingZeroComponentsButRetainExactReleaseMarker()
    {
        Assert.IsTrue(ReleaseChannelPolicy.VerifyInstalledIdentity("v5.0.5", "5.0.5.0", "5.0.5.0", "v5.0.5"));
        Assert.IsTrue(ReleaseChannelPolicy.VerifyInstalledIdentity("v5.0.6-rc1", "5.0.6.0", "v5.0.6-rc1", "v5.0.6-rc1"));
        Assert.IsFalse(ReleaseChannelPolicy.VerifyInstalledIdentity("v5.0.6-rc1", "5.0.6.0", "v5.0.6-rc2", "v5.0.6-rc1"));
        Assert.IsFalse(ReleaseChannelPolicy.VerifyInstalledIdentity("v5.0.5", "5.0.5.0", "5.0.5.0", "v5.0.5.0"));
    }

    [TestMethod]
    public void ExactRequestedTagNeverFallsBackToItsNumericVersionOrAnotherRelease()
    {
        var current = Release();
        var numeric = Release("v5.0.5.1");
        Assert.AreSame(current, ReleaseChannelPolicy.SelectRequestedRelease(new[] { numeric, current }, Tag));
        Assert.AreSame(current, ReleaseChannelPolicy.SelectRequestedRelease(new[] { current }, " viiperrc4.5.1 "));
        Assert.IsNull(ReleaseChannelPolicy.SelectRequestedRelease(new[] { current }, "5.0.5.1"));
        Assert.IsNull(ReleaseChannelPolicy.SelectRequestedRelease(new[] { current }, "VIIPERRC4.4"));
        Assert.IsNull(ReleaseChannelPolicy.SelectRequestedRelease(new[] { current }, null));
        Assert.IsNull(ReleaseChannelPolicy.SelectRequestedRelease(new[] { current with { draft = true } }, Tag));
        Assert.IsNull(ReleaseChannelPolicy.SelectRequestedRelease(new[] { current, current }, Tag));
    }

    [DataTestMethod]
    [DataRow("DS4Windows_VIIPER_x64.zip", "x64")]
    [DataRow("DS4Windows_5.0.5.1_x64.zip", "x64")]
    [DataRow("DS4Windows_VIIPERRC4.5.1_x64.zip", "x64")]
    [DataRow("DS4Windows_VIIPER_x86.zip", "x86")]
    public void VerifiedReleasePortableNamesAreAccepted(string name, string arch)
    {
        var asset = Asset(name);
        Assert.AreSame(asset, ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { asset }), arch));
        Assert.IsTrue(ReleaseChannelPolicy.TryGetAssetSha256(asset, out string expected));
        Assert.AreEqual(Hash, expected);
    }

    [TestMethod]
    public void StableNativeVersionZipIsAcceptedWithoutPermittingWrongArchitectureOrSourceArchive()
    {
        const string stableTag = "v4.0.2.3";
        var asset = Asset("DS4Windows_4.0.2.3_x64.zip", stableTag);
        Assert.AreSame(asset, ReleaseChannelPolicy.SelectPortableAsset(Release(stableTag, new[] { asset }), "x64"));
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { asset }), "x64"));
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { Asset() }), "x86"));
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { Asset() }), "arm64"));
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { Asset("DS4Windows-VIIPERRC4.5.1-SOURCE.zip") }), "x64"));
    }

    [DataTestMethod]
    [DataRow("http://github.com/Pav-Osmolski/DS4Windows/releases/download/VIIPERRC4.5.1/DS4Windows_VIIPER_x64.zip")]
    [DataRow("https://github.com.evil.example/Pav-Osmolski/DS4Windows/releases/download/VIIPERRC4.5.1/DS4Windows_VIIPER_x64.zip")]
    [DataRow("https://github.com/other/DS4Windows/releases/download/VIIPERRC4.5.1/DS4Windows_VIIPER_x64.zip")]
    [DataRow("https://github.com/hbashton/DS4Windows/releases/download/VIIPERRC4.5.1/DS4Windows_VIIPER_x64.zip")]
    [DataRow("https://github.com/Pav-Osmolski/DS4Windows/releases/download/VIIPERRC4.5/DS4Windows_VIIPER_x64.zip")]
    [DataRow("https://github.com/Pav-Osmolski/DS4Windows/releases/download/VIIPERRC4.5.1/DS4Windows_VIIPER_x86.zip")]
    [DataRow("https://github.com/Pav-Osmolski/DS4Windows/releases/download/VIIPERRC4.5.1/DS4Windows_VIIPER_x64.zip?other=1")]
    [DataRow("https://github.com/Pav-Osmolski/DS4Windows/releases/download/VIIPERRC4.5.1/DS4Windows_VIIPER_x64.zip#other")]
    [DataRow("https://user@github.com/Pav-Osmolski/DS4Windows/releases/download/VIIPERRC4.5.1/DS4Windows_VIIPER_x64.zip")]
    [DataRow("https://github.com:8443/Pav-Osmolski/DS4Windows/releases/download/VIIPERRC4.5.1/DS4Windows_VIIPER_x64.zip")]
    [DataRow("file:///C:/DS4Windows_VIIPER_x64.zip")]
    [DataRow("not a URI")]
    [DataRow(null)]
    public void PackageMustBelongToExactRepositoryReleaseAndNamedAsset(string url)
    {
        var asset = Asset() with { browser_download_url = url };
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { asset }), "x64"));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("sha256:1234")]
    [DataRow("sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [DataRow("sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void MissingOrUnsupportedExpectedDigestFailsBeforeDownload(string digest)
    {
        var asset = Asset() with { digest = digest };
        Assert.IsFalse(ReleaseChannelPolicy.TryGetAssetSha256(asset, out _));
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { asset }), "x64"));
    }

    [TestMethod]
    public void MissingSizeDraftAndAmbiguousAssetsFailClosed()
    {
        var asset = Asset();
        foreach (long? size in new long?[] { null, 0, -1 })
            Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { asset with { size = size } }), "x64"));
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { asset }) with { draft = true }, "x64"));
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { asset, asset }), "x64"));
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { asset, Asset("DS4Windows_5.0.5.1_x64.zip") }), "x64"));
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new GitHubReleaseAsset[] { null }), "x64"));
    }

    [TestMethod]
    public void AssetMetadataIsOptionalForLegacyCallersButRequiredForSafePortablePath()
    {
        var legacy = new GitHubReleaseAsset(Name, Asset().browser_download_url);
        Assert.IsNull(legacy.size);
        Assert.IsNull(legacy.digest);
        Assert.IsNull(ReleaseChannelPolicy.SelectPortableAsset(Release(assets: new[] { legacy }), "x64"));
        string json = JsonSerializer.Serialize(Asset());
        Assert.AreEqual(Asset(), JsonSerializer.Deserialize<GitHubReleaseAsset>(json));
    }

    private static GitHubRelease Release(string tag = Tag, GitHubReleaseAsset[] assets = null) =>
        new(tag, ReleaseChannelPolicy.IsPrereleaseBuild(tag), false,
            DateTimeOffset.Parse("2026-09-09T00:00:00Z"), null, assets ?? Array.Empty<GitHubReleaseAsset>());

    private static GitHubReleaseAsset Asset(string name = Name, string tag = Tag) =>
        new(name, $"https://github.com/Pav-Osmolski/DS4Windows/releases/download/{tag}/{name}", 12345, "sha256:" + Hash);
}
