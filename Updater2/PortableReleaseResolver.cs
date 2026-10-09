using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DS4Updater.Dtos;

namespace DS4Updater;

internal sealed record PortableReleaseIdentity(string Tag, Version FileVersion,
    GitHubReleaseAsset Asset, bool FromBuildReceipt)
{
    internal string PackageTag => FromBuildReceipt && Tag.StartsWith("v", StringComparison.Ordinal) &&
        ReleaseChannelPolicy.IsNumericReleaseTag(Tag) ? Tag[1..] : Tag;

    internal bool VerifyInstalled(PortableInstalledIdentity identity)
    {
        if (identity is null) return false;
        if (!FromBuildReceipt)
            return ReleaseChannelPolicy.VerifyInstalledIdentity(Tag, identity.FileVersion,
                identity.ProductVersion, identity.ReleaseTag, FileVersion);
        // release.yml strips only a leading lowercase v from numeric tags for
        // InformationalVersion. Named RC tags always remain exact; the release
        // package marker follows that same deterministic workflow value.
        return identity.ReleaseTag == PackageTag &&
            (identity.ProductVersion == Tag || identity.ProductVersion == PackageTag) &&
            Version.TryParse(identity.FileVersion, out Version actual) &&
            new Version(actual.Major, actual.Minor, Math.Max(0, actual.Build), Math.Max(0, actual.Revision)) == FileVersion;
    }

    internal bool IsNonDowngradingBinary(string installedVersion) =>
        Version.TryParse(installedVersion, out Version installed) && FileVersion >=
        new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build), Math.Max(0, installed.Revision));

    internal void VerifyArchive(string path)
    {
        PortablePackageTransaction.ValidateNoReparse(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != Asset.size || !ReleaseChannelPolicy.TryGetAssetSha256(Asset, out string digest) ||
            !Convert.ToHexString(SHA256.HashData(file)).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The package does not match its verified release digest and size.");
    }
}

// A GitHub-digest-verified build record is publisher metadata, not a signature.
// It binds a named release to its PE version without guessing from RC ordinals.
internal static class PortableReleaseResolver
{
    internal const string ReceiptName = "RELEASE-BUILD.json";
    internal const int MaximumReceiptBytes = 64 * 1024;
    internal const int MaximumReceiptDepth = 16;
    private const int MaximumReceiptAssets = 128;

    internal static GitHubReleaseAsset SelectBuildReceipt(GitHubRelease release)
    {
        if (release is null || release.draft || !ReleaseChannelPolicy.IsSupportedReleaseTag(release.tag_name))
            throw new InvalidDataException("The release identity is not supported.");
        GitHubReleaseAsset[] receipts = (release.assets ?? Array.Empty<GitHubReleaseAsset>())
            .Where(asset => asset is not null && string.Equals(asset.name, ReceiptName, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (receipts.Length == 0) return null;
        if (receipts.Length != 1 || receipts[0].name != ReceiptName ||
            receipts[0].size.GetValueOrDefault() <= 0 || receipts[0].size > MaximumReceiptBytes ||
            !ReleaseChannelPolicy.HasExactReleaseAssetUrl(release.tag_name, receipts[0]) ||
            !ReleaseChannelPolicy.TryGetAssetSha256(receipts[0], out _))
            throw new InvalidDataException("The release build record metadata is missing, ambiguous or invalid.");
        return receipts[0];
    }

    internal static PortableReleaseIdentity Resolve(GitHubRelease release, string architecture, byte[] receiptBytes)
        => ResolveCore(release, architecture, receiptBytes, RequirePackage, requireReceipt: false);

    internal static PortableReleaseIdentity ResolveInstaller(GitHubRelease release, string architecture, byte[] receiptBytes)
        => ResolveCore(release, architecture, receiptBytes, ManagedUpdateCoordinator.RequireInstaller, requireReceipt: true);

    private static PortableReleaseIdentity ResolveCore(GitHubRelease release, string architecture, byte[] receiptBytes,
        Func<GitHubRelease, string, Version, GitHubReleaseAsset> selectAsset, bool requireReceipt)
    {
        GitHubReleaseAsset receipt = SelectBuildReceipt(release);
        if (receipt is null)
        {
            if (requireReceipt || receiptBytes is not null ||
                !ReleaseChannelPolicy.TryGetExpectedFileVersion(release.tag_name, out Version historicalVersion))
                throw new InvalidDataException("This named release needs a verified RELEASE-BUILD.json record.");
            GitHubReleaseAsset historicalAsset = RequirePackage(release, architecture, historicalVersion);
            return new(release.tag_name, historicalVersion, historicalAsset, false);
        }
        if (receiptBytes is null || receiptBytes.Length > MaximumReceiptBytes ||
            receiptBytes.LongLength != receipt.size ||
            !ReleaseChannelPolicy.TryGetAssetSha256(receipt, out string receiptHash) ||
            !Convert.ToHexString(SHA256.HashData(receiptBytes)).Equals(receiptHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The release build record does not match its GitHub digest and size.");

        try
        {
            ReadOnlyMemory<byte> json = receiptBytes;
            if (receiptBytes.Length >= 3 && receiptBytes[0] == 0xEF && receiptBytes[1] == 0xBB && receiptBytes[2] == 0xBF)
                json = json[3..];
            using JsonDocument document = JsonDocument.Parse(json,
                new JsonDocumentOptions { MaxDepth = MaximumReceiptDepth });
            JsonElement root = document.RootElement;
            RejectDuplicateProperties(root);
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out JsonElement schema) || !schema.TryGetInt32(out int schemaNumber) || schemaNumber != 1 ||
                RequiredString(root, "repository") != "Pav-Osmolski/DS4Windows" ||
                RequiredString(root, "tag") != release.tag_name ||
                release.id.GetValueOrDefault() <= 0 || ReadReleaseId(root) != release.id ||
                !TryReadPeVersion(RequiredString(root, "binaryVersion"), release.tag_name, out Version expected))
                throw new InvalidDataException("The release build record identity is inconsistent.");

            // Historical named mappings and numeric tags remain additional
            // constraints, never overridden by conflicting publisher metadata.
            if (ReleaseChannelPolicy.TryGetExpectedFileVersion(release.tag_name, out Version known) && known != expected)
                throw new InvalidDataException("The build record conflicts with the release's established Windows version.");
            GitHubReleaseAsset package = selectAsset(release, architecture, expected);
            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array ||
                assets.GetArrayLength() == 0 || assets.GetArrayLength() > MaximumReceiptAssets)
                throw new InvalidDataException("The release build record asset list is invalid.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string packageHash = null;
            foreach (JsonElement entry in assets.EnumerateArray())
            {
                string name = RequiredString(entry, "name");
                string hash = RequiredString(entry, "sha256");
                if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                    name.Any(char.IsControl) || !names.Add(name) || !Regex.IsMatch(hash, "\\A[0-9A-Fa-f]{64}\\z"))
                    throw new InvalidDataException("The release build record contains an invalid or duplicate asset.");
                if (name == package.name) packageHash = hash;
            }
            if (!ReleaseChannelPolicy.TryGetAssetSha256(package, out string expectedHash) ||
                !string.Equals(packageHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package GitHub digest does not match the release build record.");
            return new(release.tag_name, expected, package, true);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The release build record is malformed.", error);
        }
    }

    private static GitHubReleaseAsset RequirePackage(GitHubRelease release, string architecture, Version version)
    {
        GitHubReleaseAsset package = ReleaseChannelPolicy.SelectPortableAsset(release, architecture, version);
        if (package is null || package.size > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The release does not contain one verified portable package for this architecture.");
        return package;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The build record is missing a required text field: " + name);
        return value.GetString();
    }

    private static long ReadReleaseId(JsonElement root)
    {
        if (!root.TryGetProperty("releaseId", out JsonElement value)) return 0;
        string text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return Regex.IsMatch(text ?? "", "\\A[1-9][0-9]{0,18}\\z") &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : 0;
    }

    private static bool TryReadPeVersion(string text, string tag, out Version version)
    {
        version = null;
        bool numericTag = ReleaseChannelPolicy.IsNumericReleaseTag(tag);
        string pattern = numericTag ? "\\A[0-9]{1,5}(?:\\.[0-9]{1,5}){1,3}\\z" :
            "\\A[0-9]{1,5}(?:\\.[0-9]{1,5}){3}\\z";
        if (!Regex.IsMatch(text ?? "", pattern) || !Version.TryParse(text, out Version parsed) ||
            parsed.ToString() != text || parsed.Major > ushort.MaxValue || parsed.Minor > ushort.MaxValue ||
            parsed.Build > ushort.MaxValue || parsed.Revision > ushort.MaxValue) return false;
        version = new(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        // Numeric release.yml inputs can omit trailing zero components. Only
        // the numeric tag's already-established version can normalize them;
        // named records still require four explicit Windows components.
        return !numericTag || (ReleaseChannelPolicy.TryGetExpectedFileVersion(tag, out Version known) && known == version);
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The build record contains duplicate JSON fields.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
