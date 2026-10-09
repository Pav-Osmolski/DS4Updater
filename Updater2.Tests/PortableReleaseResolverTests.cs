using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DS4Updater.Dtos;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Updater.Tests;

[TestClass]
public sealed class PortableReleaseResolverTests
{
    private const string FutureTag = "VIIPERRC4.6";
    private const string BinaryVersion = "9.2.3.4";
    private const string ZipName = "DS4Windows_VIIPER_x64.zip";
    private const string ReceiptName = "RELEASE-BUILD.json";
    private const long ReleaseId = 12345;
    private static readonly string ZipHash = new('a', 64);

    [DataTestMethod]
    [DataRow("VIIPERRC4.5.3", "5.0.5.3")]
    [DataRow("VIIPERRC4.5.4", "8.2.7.19")]
    [DataRow("VIIPERRC4.6", "9.2.3.4")]
    [DataRow("VIIPERRC99.7.2", "65535.65535.65535.65535")]
    public void FutureReleaseUsesVerifiedReceiptVersionWithoutTagFormula(string tag, string version)
    {
        Fixture fixture = Create(tag, version);
        PortableReleaseIdentity identity = Resolve(fixture);
        Assert.AreEqual(tag, identity.Tag);
        Assert.AreEqual(new Version(version), identity.FileVersion);
        Assert.IsTrue(identity.FromBuildReceipt);
        Assert.AreSame(fixture.Release.assets[0], identity.Asset);
        Assert.IsTrue(identity.VerifyInstalled(new(version, tag, tag)));
    }

    [DataTestMethod]
    [DataRow("v9.2.3.4", "9.2.3.4", "9.2.3.4")]
    [DataRow("v9.2.3.4-rc1", "9.2.3.4", "9.2.3.4")]
    [DataRow("v9.2.3", "9.2.3", "9.2.3.0")]
    public void NumericWorkflowReceiptUsesItsExactUnprefixedPackageIdentity(
        string tag, string receiptVersion, string peVersion)
    {
        PortableReleaseIdentity identity = Resolve(Create(tag, receiptVersion));
        string packageTag = tag[1..];
        Assert.AreEqual(tag, identity.Tag);
        Assert.AreEqual(packageTag, identity.PackageTag);
        Assert.AreEqual(new Version(peVersion), identity.FileVersion);
        Assert.IsTrue(identity.FromBuildReceipt);
        Assert.IsTrue(identity.VerifyInstalled(new(peVersion, packageTag, packageTag)));
        Assert.IsTrue(identity.VerifyInstalled(new(peVersion, tag, packageTag)));
        Assert.IsFalse(identity.VerifyInstalled(new(peVersion, packageTag, tag)),
            "The numeric workflow's package marker must match exactly, not the API tag.");
        Assert.IsFalse(identity.VerifyInstalled(new(peVersion, packageTag, "9.2.3.4-rc2")));
        Assert.IsFalse(identity.VerifyInstalled(new(peVersion, "9.2.3.4-rc2", packageTag)));
        Assert.IsFalse(identity.VerifyInstalled(new("9.2.3.5", packageTag, packageTag)));

        PortableReleaseIdentity named = Resolve(Create());
        Assert.AreEqual(FutureTag, named.PackageTag);
        Assert.IsFalse(named.VerifyInstalled(new(BinaryVersion, FutureTag[1..], FutureTag)));
        Assert.ThrowsException<InvalidDataException>(() => Resolve(Create(version: "9.2.3")),
            "Numeric workflow normalization must not weaken named-release PE metadata.");
    }

    [DataTestMethod]
    [DataRow(ZipName)]
    [DataRow("DS4Windows_VIIPERRC4.6_x64.zip")]
    [DataRow("DS4Windows_9.2.3.4_x64.zip")]
    public void FutureReceiptSelectsOneExactlyBoundPortableName(string name)
    {
        Fixture fixture = Create(zipName: name);
        Assert.AreEqual(name, Resolve(fixture).Asset.name);
    }

    [DataTestMethod]
    [DataRow("VIIPERRC4.5", "5.0.5.0")]
    [DataRow("VIIPERRC4.5.2", "5.0.5.2")]
    [DataRow("v8.1.2", "8.1.2.0")]
    public void AbsentReceiptKeepsHistoricalAndNumericLegacyCompatibility(string tag, string version)
    {
        GitHubRelease release = Release(tag, new[] { Asset(tag, ZipName, 100, ZipHash) });
        Assert.IsNull(PortableReleaseResolver.SelectBuildReceipt(release));
        PortableReleaseIdentity identity = PortableReleaseResolver.Resolve(release, "x64", null);
        Assert.IsFalse(identity.FromBuildReceipt);
        Assert.AreEqual(new Version(version), identity.FileVersion);
        Assert.IsTrue(identity.VerifyInstalled(new(version, version, tag)),
            "Legacy numeric ProductVersion remains valid only on the legacy path.");
    }

    [DataTestMethod]
    [DataRow("VIIPERRC4.5.3")]
    [DataRow("VIIPERRC4.5.4")]
    [DataRow("VIIPERRC4.6")]
    [DataRow("VIIPERRC99.7.2")]
    public void UnknownNamedReleaseWithoutReceiptFailsWithoutNewAllowlist(string tag)
    {
        GitHubRelease release = Release(tag, new[] { Asset(tag, ZipName, 100, ZipHash) });
        Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.Resolve(release, "x64", null));
    }

    [TestMethod]
    public void PresentReceiptNeverFallsBackToHistoricalMapping()
    {
        Fixture known = Create("VIIPERRC4.5.2", "5.0.5.2");
        Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.Resolve(known.Release, "x64", null));
        Assert.ThrowsException<InvalidDataException>(() => Resolve(known with { Bytes = new byte[] { 0 } }));
        Fixture invalid = Create("VIIPERRC4.5.2", "5.0.5.2", mutate: root => root["schema"] = 2);
        Assert.ThrowsException<InvalidDataException>(() => Resolve(invalid));
        Fixture conflicting = Create("VIIPERRC4.5.2", "9.2.3.4");
        Assert.ThrowsException<InvalidDataException>(() => Resolve(conflicting));
    }

    [TestMethod]
    public void ReceiptBytesWithoutTheirPublishedDigestCannotAuthorizeARelease()
    {
        Fixture known = Create("VIIPERRC4.5.2", "5.0.5.2");
        GitHubRelease absent = known.Release with { assets = new[] { known.Release.assets[0] } };
        Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.Resolve(absent, "x64", known.Bytes));
    }

    [TestMethod]
    public void ReceiptDigestAndExactLengthAreVerifiedBeforeSemanticTrust()
    {
        Fixture fixture = Create();
        byte[] modified = (byte[])fixture.Bytes.Clone();
        modified[^1] ^= 1;
        Assert.ThrowsException<InvalidDataException>(() => Resolve(fixture with { Bytes = modified }));
        Assert.ThrowsException<InvalidDataException>(() => Resolve(fixture with { Bytes = fixture.Bytes[..^1] }));
        Assert.ThrowsException<InvalidDataException>(() => Resolve(fixture with { Bytes = fixture.Bytes.Concat(new byte[] { 32 }).ToArray() }));
    }

    [TestMethod]
    public void Utf8BomIsAcceptedOnlyWhenIncludedInThePublishedDigest()
    {
        Fixture fixture = Create();
        byte[] withBom = new byte[] { 0xef, 0xbb, 0xbf }.Concat(fixture.Bytes).ToArray();
        Assert.IsTrue(Resolve(WithBytes(fixture, withBom)).FromBuildReceipt);
        Assert.ThrowsException<InvalidDataException>(() => Resolve(fixture with { Bytes = withBom }));
    }

    [TestMethod]
    public void ReceiptDerivedVersionStillEnforcesBinaryNonDowngrade()
    {
        PortableReleaseIdentity identity = Resolve(Create());
        Assert.IsTrue(identity.IsNonDowngradingBinary("9.2.3.3"));
        Assert.IsTrue(identity.IsNonDowngradingBinary(BinaryVersion));
        foreach (string newerOrInvalid in new[] { "9.2.3.5", "10.0.0.0", "invalid", null })
            Assert.IsFalse(identity.IsNonDowngradingBinary(newerOrInvalid));
        Assert.IsFalse(ReleaseChannelPolicy.ShouldUpdate(Create().Release, BinaryVersion, true, "VIIPERRC4.7"),
            "A valid receipt is not permission to weaken the independent RC ordinal ordering.");
    }

    [TestMethod]
    public void ReceiptMetadataRejectsUntrustedOriginMissingDigestAndAmbiguity()
    {
        Fixture fixture = Create();
        GitHubReleaseAsset receipt = fixture.Release.assets[1];
        foreach (GitHubReleaseAsset invalid in new[]
        {
            receipt with { digest = null }, receipt with { digest = "sha256:" + new string('z', 64) },
            receipt with { size = 0 }, receipt with { size = PortableReleaseResolver.MaximumReceiptBytes + 1 },
            receipt with { browser_download_url = "https://evil.example/" + ReceiptName },
            receipt with { browser_download_url = receipt.browser_download_url.Replace("https://", "http://") },
            receipt with { browser_download_url = receipt.browser_download_url + "?alter=1" },
            receipt with { browser_download_url = receipt.browser_download_url.Replace(FutureTag, "VIIPERRC4.5.2") },
        })
        {
            GitHubRelease release = fixture.Release with { assets = new[] { fixture.Release.assets[0], invalid } };
            Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.SelectBuildReceipt(release));
        }
        Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.SelectBuildReceipt(
            fixture.Release with { assets = fixture.Release.assets.Append(receipt).ToArray() }));
        Assert.ThrowsException<InvalidDataException>(() => Resolve(fixture with { Release = fixture.Release with { draft = true } }));
    }

    [DataTestMethod]
    [DataRow("schema", "1")]
    [DataRow("repository", "other/DS4Windows")]
    [DataRow("repository", "hbashton/DS4Windows")]
    [DataRow("repository", "Pav-Osmolski/ds4windows")]
    [DataRow("tag", "VIIPERRC4.5.2")]
    [DataRow("tag", "viiperrc4.6")]
    [DataRow("releaseId", "12346")]
    [DataRow("releaseId", "012345")]
    [DataRow("releaseId", "12345 ")]
    public void ReceiptMustDescribeTheExactSelectedRepositoryTagAndRelease(string field, string value)
    {
        Fixture fixture = Create(mutate: root => root[field] = value);
        Assert.ThrowsException<InvalidDataException>(() => Resolve(fixture));
    }

    [TestMethod]
    public void CanonicalReleaseIdSupportsWorkflowStringOrJsonIntegerButNotMissingApiIdentity()
    {
        Assert.IsTrue(Resolve(Create(mutate: root => root["releaseId"] = ReleaseId)).FromBuildReceipt);
        Fixture fixture = Create();
        foreach (long? invalid in new long?[] { null, 0, -1 })
            Assert.ThrowsException<InvalidDataException>(() => Resolve(fixture with
            { Release = fixture.Release with { id = invalid } }));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("9.2.3")]
    [DataRow("9.2.3.4.5")]
    [DataRow("09.2.3.4")]
    [DataRow("9.2.3.04")]
    [DataRow("9.2.3.-1")]
    [DataRow("9.2.3.65536")]
    [DataRow("65536.2.3.4")]
    [DataRow("9.2.3.4 ")]
    [DataRow(" 9.2.3.4")]
    [DataRow("9.2.3.4-preview")]
    [DataRow("9.2.3.4+build")]
    public void ReceiptPeVersionMustBeCanonicalAndRepresentableByWindows(string value)
    {
        Assert.ThrowsException<InvalidDataException>(() => Resolve(Create(version: value)));
    }

    [TestMethod]
    public void ZipHashMustAgreeBetweenReceiptAndPublishedAssetMetadata()
    {
        Fixture fixture = Create();
        GitHubReleaseAsset zip = fixture.Release.assets[0];
        Assert.ThrowsException<InvalidDataException>(() => Resolve(fixture with
        { Release = fixture.Release with { assets = new[] { zip with { digest = "sha256:" + new string('b', 64) }, fixture.Release.assets[1] } } }));
        Assert.ThrowsException<InvalidDataException>(() => Resolve(Create(mutate: root =>
            root["assets"] = Array.Empty<object>())));
        Assert.IsTrue(Resolve(Create(mutate: root => root["assets"] = new[]
        { Record(ZipName, ZipHash.ToUpperInvariant()) })).FromBuildReceipt);
    }

    [DataTestMethod]
    [DataRow("../outside.zip")]
    [DataRow("folder/file.zip")]
    [DataRow("folder\\file.zip")]
    [DataRow("file.zip:alternate")]
    public void ReceiptAssetNamesCannotContainPathsOrStreams(string name)
    {
        Fixture fixture = Create(mutate: root => root["assets"] = new[]
        { Record(ZipName, ZipHash), Record(name, ZipHash) });
        Assert.ThrowsException<InvalidDataException>(() => Resolve(fixture));
    }

    [TestMethod]
    public void DuplicateReceiptAssetsAndAmbiguousPortablePackagesAreRejected()
    {
        foreach (string duplicate in new[] { ZipName, ZipName.ToLowerInvariant() })
            Assert.ThrowsException<InvalidDataException>(() => Resolve(Create(mutate: root => root["assets"] = new[]
            { Record(ZipName, ZipHash), Record(duplicate, ZipHash) })));
        const string secondName = "DS4Windows_9.2.3.4_x64.zip";
        Fixture fixture = Create(mutate: root => root["assets"] = new[]
        { Record(ZipName, ZipHash), Record(secondName, ZipHash) });
        fixture = fixture with { Release = fixture.Release with
        { assets = fixture.Release.assets.Append(Asset(FutureTag, secondName, 100, ZipHash)).ToArray() } };
        Assert.ThrowsException<InvalidDataException>(() => Resolve(fixture));
        Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.Resolve(Create().Release, "arm64", Create().Bytes));
        Assert.ThrowsException<InvalidDataException>(() => PortableReleaseResolver.Resolve(Create().Release, "x86", Create().Bytes));
    }

    [TestMethod]
    public void HashValidJsonStillRejectsDuplicatePropertiesMalformedDocumentsAndResourceExcess()
    {
        Fixture valid = Create();
        string json = Encoding.UTF8.GetString(valid.Bytes);
        foreach (string invalid in new[]
        {
            "[]", json[..^1], json + "{}",
            json.Replace("\"schema\":1", "\"schema\":1,\"schema\":1"),
            json.Replace("\"name\":\"" + ZipName + "\"", "\"name\":\"" + ZipName + "\",\"name\":\"" + ZipName + "\""),
            json[..^1] + ",\"extra\":{\"duplicate\":1,\"duplicate\":2}}",
            json[..^1] + ",\"extra\":" + new string('[', 32) + "0" + new string(']', 32) + "}",
        }) Assert.ThrowsException<InvalidDataException>(() => Resolve(WithBytes(valid, Encoding.UTF8.GetBytes(invalid))));

        byte[] oversized = Encoding.UTF8.GetBytes(json[..^1] + ",\"padding\":\"" +
            new string('x', PortableReleaseResolver.MaximumReceiptBytes) + "\"}");
        Assert.ThrowsException<InvalidDataException>(() => Resolve(WithBytes(valid, oversized)));
        Assert.ThrowsException<InvalidDataException>(() => Resolve(Create(mutate: root => root["assets"] =
            new[] { Record(ZipName, ZipHash) }.Concat(Enumerable.Range(0, 128)
                .Select(index => Record("extra-" + index + ".txt", ZipHash))).ToArray())));
    }

    [TestMethod]
    public void ReceiptIdentityRequiresExactProductAndMarkerNotJustNumericPe()
    {
        PortableReleaseIdentity identity = Resolve(Create());
        foreach (PortableInstalledIdentity invalid in new[]
        {
            new PortableInstalledIdentity("9.2.3.3", FutureTag, FutureTag),
            new PortableInstalledIdentity("9.2.3.5", FutureTag, FutureTag),
            new PortableInstalledIdentity(BinaryVersion, BinaryVersion, FutureTag),
            new PortableInstalledIdentity(BinaryVersion, FutureTag, null),
            new PortableInstalledIdentity(BinaryVersion, "VIIPERRC4.5.2", FutureTag),
            new PortableInstalledIdentity(BinaryVersion, FutureTag, "VIIPERRC4.5.2"),
            new PortableInstalledIdentity(BinaryVersion, FutureTag.ToLowerInvariant(), FutureTag),
            new PortableInstalledIdentity(BinaryVersion, FutureTag, FutureTag.ToLowerInvariant()),
        }) Assert.IsFalse(identity.VerifyInstalled(invalid));
    }

    private static PortableReleaseIdentity Resolve(Fixture fixture) =>
        PortableReleaseResolver.Resolve(fixture.Release, "x64", fixture.Bytes);

    private static Fixture Create(string tag = FutureTag, string version = BinaryVersion,
        string zipName = ZipName, Action<Dictionary<string, object>> mutate = null)
    {
        // This is the current production RELEASE-BUILD schema, including its
        // workflow-produced string release/run IDs. All bytes remain synthetic.
        var root = new Dictionary<string, object>
        {
            ["schema"] = 1, ["repository"] = "Pav-Osmolski/DS4Windows", ["tag"] = tag,
            ["releaseId"] = ReleaseId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sourceCommit"] = new string('b', 40), ["runId"] = "67890", ["unsignedRc"] = true,
            ["binaryVersion"] = version, ["brokerSourceName"] = "VIIPER-0.1.3-rc4.5-SOURCE.zip",
            ["brokerCommit"] = new string('c', 40), ["assets"] = new[] { Record(zipName, ZipHash) },
        };
        mutate?.Invoke(root);
        return WithBytes(new(Release(tag, new[] { Asset(tag, zipName, 100, ZipHash) }), null),
            JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static Dictionary<string, object> Record(string name, string hash) =>
        new() { ["name"] = name, ["sha256"] = hash };

    private static Fixture WithBytes(Fixture fixture, byte[] bytes)
    {
        GitHubReleaseAsset receipt = Asset(fixture.Release.tag_name, ReceiptName, bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));
        return new(fixture.Release with { assets = fixture.Release.assets
            .Where(asset => asset.name != ReceiptName).Append(receipt).ToArray() }, bytes);
    }

    private static GitHubRelease Release(string tag, GitHubReleaseAsset[] assets) =>
        new(tag, tag.StartsWith("VIIPER", StringComparison.Ordinal), false,
            DateTimeOffset.Parse("2026-09-09T00:00:00Z"), DateTimeOffset.Parse("2026-09-08T00:00:00Z"), assets, id: ReleaseId);

    private static GitHubReleaseAsset Asset(string tag, string name, long size, string hash) =>
        new(name, "https://github.com/Pav-Osmolski/DS4Windows/releases/download/" +
            Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name), size, "sha256:" + hash);

    private sealed record Fixture(GitHubRelease Release, byte[] Bytes);
}
