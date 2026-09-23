using System;
using System.Collections.Generic;
using System.IO;

namespace DS4Updater;

internal sealed record ManagedUpdateRequest(string TargetDirectory, string ReleaseTag, string LaunchExe)
{
    internal const string ManagedFlag = "--managed-safe-v1";

    internal static ManagedUpdateRequest Parse(string[] args, string executablePath,
        Func<string, string> findManagedRoot = null, Func<string, string> validateManagedRoot = null)
    {
        if (args is null) throw new ArgumentNullException(nameof(args));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string tag = null, launch = "DS4Windows.exe", target = null;
        bool explicitManaged = false;
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (option is null || !seen.Add(option))
                throw new ArgumentException("The installed update contains a missing or duplicate option.");
            switch (option)
            {
                // Accepted for older callers. Setup owns the install and final
                // launch; these hints never grant another path or privilege.
                case "-autolaunch":
                case "-user":
                    break;
                case ManagedFlag:
                    explicitManaged = true;
                    break;
                case "--targetDirectory":
                    if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || !Path.IsPathFullyQualified(args[i]) ||
                        args[i].StartsWith(@"\\", StringComparison.Ordinal))
                        throw new ArgumentException("The installed update target must be an exact local registered directory.");
                    target = PortableUpdateProcessGuard.NormalizeLocalPath(args[i]);
                    break;
                case "--releaseTag":
                    if (++i == args.Length || args[i]?.Length > 128 ||
                        !ReleaseChannelPolicy.IsSupportedReleaseTag(args[i]))
                        throw new ArgumentException("The selected release tag is invalid.");
                    tag = args[i];
                    break;
                case "--launchExe":
                    if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]))
                        throw new ArgumentException("The installed executable name is missing.");
                    launch = PortableUpdateProcessGuard.ValidateCustomExeName(args[i]);
                    break;
                default:
                    throw new ArgumentException("Unrecognized installed update option: " + option);
            }
        }
        string executable = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetFileName(executable), "DS4Updater.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The installed updater executable has an unexpected name.");
        string root;
        if (target != null)
        {
            if (!explicitManaged) throw new ArgumentException("Only an explicit installed update can specify its registered target.");
            root = (validateManagedRoot ?? UpdateDeploymentPolicy.ValidateManagedRoot)(target);
            if (!string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed update target is not its registered directory.");
        }
        else
        {
            root = (findManagedRoot ?? (path => UpdateDeploymentPolicy.FindManagedRoot(path)))(executable);
            if (string.IsNullOrEmpty(root) || !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                    Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The updater is not in its registered DS4Windows installation.");
        }
        return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), tag, launch);
    }
}
