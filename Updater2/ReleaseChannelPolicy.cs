using DS4Updater.Dtos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DS4Updater
{
    internal static class ReleaseChannelPolicy
    {
        internal const string InstalledReleaseFileName = "DS4Windows.release";

        private static readonly Regex prereleaseNameRegex = new(
            @"(?i)(alpha|beta|preview|pre[- ]?release|prerelease|release candidate|viiperrc|(?:^|[^a-z])rc(?:\d|[^a-z]|$))",
            RegexOptions.Compiled);

        // Keep channel ordering aligned with DS4Windows' ReleaseChannelPolicy.
        // Named RC ordinals are not Windows file versions (RC4.5 = 5.0.5.0).
        private static readonly Regex viiperPrereleaseTagRegex = new(
            @"^VIIPER(?<phase>RC|Beta)(?<number>\d+(?:\.\d+){0,3})?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex numericReleaseTagRegex = new(
            @"^v?(?<number>\d+(?:\.\d+){1,3})(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex sha256DigestRegex = new(
            @"^sha256:(?<hash>[0-9a-f]{64})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool IsPrereleaseBuild(string versionText)
        {
            return !string.IsNullOrWhiteSpace(versionText) &&
                prereleaseNameRegex.IsMatch(versionText);
        }

        internal static bool IsPrereleaseInstall(
            string versionText,
            string installedReleaseTag)
        {
            return IsPrereleaseBuild(versionText) ||
                IsPrereleaseBuild(installedReleaseTag);
        }

        internal static bool IsPrerelease(GitHubRelease release)
        {
            return release is not null &&
                (release.prerelease || IsPrereleaseBuild(release.tag_name));
        }

        internal static DateTimeOffset GetReleaseDate(GitHubRelease release)
        {
            return release?.published_at ?? release?.created_at ?? DateTimeOffset.MinValue;
        }

        internal static GitHubRelease SelectPreferredRelease(
            IEnumerable<GitHubRelease> releases,
            bool currentBuildIsPrerelease)
        {
            GitHubRelease[] published = (releases ?? Array.Empty<GitHubRelease>())
                .Where(release => release is not null &&
                    !release.draft &&
                    !string.IsNullOrWhiteSpace(release.tag_name))
                .ToArray();

            GitHubRelease latestStable = SelectNewest(
                published.Where(release => !IsPrerelease(release)));
            if (!currentBuildIsPrerelease)
            {
                return latestStable;
            }

            GitHubRelease latestPrerelease = SelectNewest(
                published.Where(IsPrerelease));
            if (latestPrerelease is null)
            {
                return latestStable;
            }

            if (latestStable is not null &&
                GetReleaseDate(latestStable) > GetReleaseDate(latestPrerelease))
            {
                return latestStable;
            }

            return latestPrerelease;
        }

        internal static bool ShouldUpdate(
            GitHubRelease selectedRelease,
            string currentVersionText,
            bool currentBuildIsPrerelease,
            string installedReleaseTag)
        {
            if (selectedRelease is null || selectedRelease.draft ||
                string.IsNullOrWhiteSpace(selectedRelease.tag_name))
            {
                return false;
            }

            bool markerMatches = !string.IsNullOrWhiteSpace(installedReleaseTag) &&
                string.Equals(installedReleaseTag.Trim(), selectedRelease.tag_name,
                    StringComparison.OrdinalIgnoreCase);
            if (markerMatches && IsPrerelease(selectedRelease))
            {
                return false;
            }

            if (markerMatches &&
                TryParseReleaseVersion(currentVersionText, out Version markedCurrentVersion) &&
                TryParseReleaseVersion(selectedRelease.tag_name, out Version markedReleaseVersion) &&
                NormalizeVersion(markedCurrentVersion) >= NormalizeVersion(markedReleaseVersion))
            {
                return false;
            }

            if (currentBuildIsPrerelease)
            {
                if (IsPrerelease(selectedRelease))
                {
                    if (TryParseViiperPrereleaseTag(installedReleaseTag,
                            out int currentPhase, out Version currentOrdinal))
                    {
                        if (TryParseViiperPrereleaseTag(selectedRelease.tag_name,
                                out int candidatePhase, out Version candidateOrdinal))
                        {
                            return candidatePhase > currentPhase ||
                                candidatePhase == currentPhase && candidateOrdinal > currentOrdinal;
                        }

                        return TryParseReleaseVersion(currentVersionText, out Version currentBinary) &&
                            TryParseNumericReleaseTag(selectedRelease.tag_name, out Version candidateBinary) &&
                            candidateBinary > NormalizeVersion(currentBinary);
                    }

                    if (TryParseNumericReleaseTag(installedReleaseTag, out Version installedNumeric))
                    {
                        return TryParseNumericReleaseTag(selectedRelease.tag_name, out Version candidateNumeric) &&
                            TryParseReleaseVersion(currentVersionText, out Version runningNumeric) &&
                            candidateNumeric > installedNumeric &&
                            candidateNumeric > NormalizeVersion(runningNumeric);
                    }

                    // Legacy, unmarked prerelease installs get one opportunity
                    // to acquire the exact marker. Unknown marked channels do not.
                    return string.IsNullOrWhiteSpace(installedReleaseTag);
                }

                return TryParseReleaseVersion(currentVersionText, out Version prereleaseVersion) &&
                    TryParseNumericReleaseTag(selectedRelease.tag_name, out Version stableVersion) &&
                    NormalizeVersion(prereleaseVersion) <= stableVersion;
            }

            if (IsPrerelease(selectedRelease))
            {
                return false;
            }

            return TryParseReleaseVersion(currentVersionText, out Version currentVersion) &&
                TryParseNumericReleaseTag(selectedRelease.tag_name, out Version selectedVersion) &&
                NormalizeVersion(currentVersion) < selectedVersion;
        }

        internal static GitHubRelease SelectRequestedRelease(
            IEnumerable<GitHubRelease> releases, string requestedTag)
        {
            if (string.IsNullOrWhiteSpace(requestedTag)) return null;
            GitHubRelease[] matches = (releases ?? Array.Empty<GitHubRelease>())
                .Where(release => release is not null && !release.draft &&
                    string.Equals(release.tag_name, requestedTag.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            // An exact tag request is never a request for its numeric substring,
            // a newer tag, or a similarly named draft release.
            return matches.Length == 1 ? matches[0] : null;
        }

        internal static GitHubReleaseAsset SelectPortableAsset(GitHubRelease release, string architecture)
        {
            return TryGetExpectedFileVersion(release?.tag_name, out Version expectedVersion) ?
                SelectPortableAsset(release, architecture, expectedVersion) : null;
        }

        internal static GitHubReleaseAsset SelectPortableAsset(GitHubRelease release, string architecture,
            Version expectedVersion)
        {
            if (release is null || release.draft || expectedVersion is null ||
                (architecture != "x64" && architecture != "x86")) return null;

            string[] names =
            {
                $"DS4Windows_VIIPER_{architecture}.zip",
                $"DS4Windows_{NumericPrefixFreeTag(release.tag_name)}_{architecture}.zip",
                $"DS4Windows_{expectedVersion}_{architecture}.zip",
            };
            GitHubReleaseAsset[] candidates = (release.assets ?? Array.Empty<GitHubReleaseAsset>())
                .Where(asset => asset is not null && asset.size.GetValueOrDefault() > 0 &&
                    names.Contains(asset.name, StringComparer.OrdinalIgnoreCase) &&
                    HasExactReleaseAssetUrl(release.tag_name, asset) &&
                    TryGetAssetSha256(asset, out _))
                .Take(2).ToArray();
            // Ambiguous packages are not interchangeable; ask the publisher to
            // provide one authoritative package instead of silently choosing.
            return candidates.Length == 1 ? candidates[0] : null;
        }

        private static string NumericPrefixFreeTag(string tag) =>
            tag.Length > 1 && (tag[0] == 'v' || tag[0] == 'V') && char.IsDigit(tag[1]) ?
                tag.Substring(1) : tag;

        internal static bool TryGetAssetSha256(GitHubReleaseAsset asset, out string hash)
        {
            hash = null;
            Match match = sha256DigestRegex.Match(asset?.digest ?? "");
            if (!match.Success) return false;
            hash = match.Groups["hash"].Value.ToLowerInvariant();
            return true;
        }

        internal static bool HasExactReleaseAssetUrl(string tag, GitHubReleaseAsset asset)
        {
            return Uri.TryCreate(asset.browser_download_url, UriKind.Absolute, out Uri uri) &&
                uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
                string.Equals(uri.AbsolutePath,
                    "/Pav-Osmolski/DS4Windows/releases/download/" + Uri.EscapeDataString(tag) +
                    "/" + Uri.EscapeDataString(asset.name), StringComparison.Ordinal);
        }

        internal static bool VerifyInstalledIdentity(string tag, string fileVersion,
            string productVersion, string installedReleaseTag)
        {
            return TryGetExpectedFileVersion(tag, out Version expected) &&
                VerifyInstalledIdentity(tag, fileVersion, productVersion, installedReleaseTag, expected);
        }

        internal static bool VerifyInstalledIdentity(string tag, string fileVersion,
            string productVersion, string installedReleaseTag, Version expected)
        {
            if (expected is null || !Version.TryParse(fileVersion?.Trim(), out Version actual) ||
                NormalizeVersion(actual) != expected) return false;

            if (!string.IsNullOrWhiteSpace(installedReleaseTag) &&
                !string.Equals(installedReleaseTag.Trim(), tag.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            // Current packages use the exact tag as ProductVersion. Historical
            // releases used the numeric binary version. Neither a stale marker
            // nor an arbitrary prerelease name can substitute for the right PE.
            if (string.Equals(productVersion?.Trim(), tag.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            return TryParseNumericReleaseTag(productVersion, out Version product) && product == expected &&
                !productVersion.Contains('-');
        }

        internal static bool TryGetExpectedFileVersion(string tag, out Version version)
        {
            version = tag?.Trim().ToUpperInvariant() switch
            {
                "VIIPERRC4" or "VIIPERRC4.0" => new Version(5, 0, 0, 0),
                "VIIPERRC4.1" => new Version(5, 0, 1, 0),
                "VIIPERRC4.2" => new Version(5, 0, 2, 0),
                "VIIPERRC4.3" => new Version(5, 0, 3, 0),
                "VIIPERRC4.4" => new Version(5, 0, 4, 0),
                "VIIPERRC4.5" => new Version(5, 0, 5, 0),
                "VIIPERRC4.5.1" => new Version(5, 0, 5, 1),
                "VIIPERRC4.5.2" => new Version(5, 0, 5, 2),
                _ => null,
            };
            return version is not null || TryParseNumericReleaseTag(tag, out version);
        }

        internal static bool IsSupportedReleaseTag(string tag) =>
            !string.IsNullOrWhiteSpace(tag) && tag == tag.Trim() &&
            (TryParseNumericReleaseTag(tag, out _) || TryParseViiperPrereleaseTag(tag, out _, out _));

        internal static bool IsNumericReleaseTag(string tag) => TryParseNumericReleaseTag(tag, out _);

        private static Version NormalizeVersion(Version version) => new(
            version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

        private static bool TryParseNumericReleaseTag(string tag, out Version version)
        {
            version = null;
            Match match = numericReleaseTagRegex.Match(tag?.Trim() ?? "");
            if (!match.Success || !Version.TryParse(match.Groups["number"].Value, out Version parsed))
                return false;
            version = NormalizeVersion(parsed);
            return true;
        }

        private static bool TryParseViiperPrereleaseTag(string tag, out int phase, out Version ordinal)
        {
            phase = 0;
            ordinal = null;
            Match match = viiperPrereleaseTagRegex.Match(tag?.Trim() ?? "");
            if (!match.Success) return false;
            phase = string.Equals(match.Groups["phase"].Value, "RC", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            string number = match.Groups["number"].Value;
            if (number.Length == 0)
            {
                if (phase != 0) return false;
                number = "1";
            }
            if (!number.Contains('.')) number += ".0";
            if (!Version.TryParse(number, out Version parsed)) return false;
            ordinal = NormalizeVersion(parsed);
            return true;
        }

        internal static bool TryParseReleaseVersion(
            string versionText,
            out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(versionText))
            {
                return false;
            }

            Match match = Regex.Match(versionText, @"\d+(?:\.\d+){1,3}");
            return match.Success && Version.TryParse(match.Value, out version);
        }

        private static GitHubRelease SelectNewest(IEnumerable<GitHubRelease> releases)
        {
            return releases
                .OrderByDescending(GetReleaseDate)
                .ThenByDescending(release =>
                    TryParseReleaseVersion(release.tag_name, out Version version) ?
                        version : new Version(0, 0, 0))
                .FirstOrDefault();
        }
    }
}
