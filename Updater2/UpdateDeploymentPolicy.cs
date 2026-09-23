using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace DS4Updater;

internal enum UpdateDeployment { Legacy, Portable, Managed }

// A portable ZIP used by older installed updaters can leave its marker in an
// MSI-owned folder. The machine registration, not that leftover marker, owns
// the installed deployment. Explicit portable requests still fail closed in
// the portable process guard; they never gain an installed-update fallback.
internal static class UpdateDeploymentPolicy
{
    internal static UpdateDeployment Resolve(string[] args, string executablePath,
        Func<IEnumerable<string>> managedRoots = null)
    {
        if (PortableUpdateRequest.IsPortableInvocation(args)) return UpdateDeployment.Portable;
        if (Array.Exists(args, arg => arg.StartsWith("--managed-", StringComparison.OrdinalIgnoreCase)))
            return UpdateDeployment.Managed;
        if (string.IsNullOrEmpty(executablePath)) return UpdateDeployment.Legacy;
        string folder = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        string name = Path.GetFileName(folder);
        if (name.StartsWith("portable-worker-", StringComparison.Ordinal) &&
            Guid.TryParseExact(name[16..], "N", out _)) return UpdateDeployment.Portable;
        if (FindManagedRoot(executablePath, managedRoots) != null) return UpdateDeployment.Managed;
        return HasPortableMarker(Path.Combine(folder, PortableUpdateProcessGuard.MarkerFileName))
            ? UpdateDeployment.Portable : UpdateDeployment.Legacy;
    }

    internal static string FindManagedRoot(string executablePath,
        Func<IEnumerable<string>> managedRoots = null)
    {
        string folder = PortableUpdateProcessGuard.NormalizeLocalPath(
            Path.GetDirectoryName(Path.GetFullPath(executablePath)));
        PortablePackageTransaction.ValidateNoReparse(folder);
        string resolved = Directory.Exists(folder)
            ? PortableUpdateProcessGuard.NormalizeLocalPath(PortableUpdateProcessHost.ResolveDirectory(folder)) : folder;
        string match = null;
        foreach (string value in (managedRoots ?? ReadManagedRoots)())
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            string registered = PortableUpdateProcessGuard.NormalizeLocalPath(value);
            if (registered == Path.GetPathRoot(registered))
                throw new InvalidDataException("The registered DS4Windows installation is not a directory.");
            PortablePackageTransaction.ValidateNoReparse(registered);
            if (Directory.Exists(registered))
                registered = PortableUpdateProcessGuard.NormalizeLocalPath(PortableUpdateProcessHost.ResolveDirectory(registered));
            if (string.Equals(resolved, registered, StringComparison.OrdinalIgnoreCase)) match = resolved;
        }
        return match;
    }

    internal static string ValidateManagedRoot(string directory)
    {
        string root = PortableUpdateProcessGuard.NormalizeLocalPath(directory);
        string registered = FindManagedRoot(Path.Combine(root, "DS4Updater.exe"));
        if (registered == null || !Directory.Exists(registered))
            throw new InvalidDataException("This folder is no longer the registered DS4Windows installation. Restart the update from DS4Windows.");
        return registered;
    }

    private static IEnumerable<string> ReadManagedRoots()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey key = machine.OpenSubKey(@"SOFTWARE\DS4Windows", writable: false);
            object value = key?.GetValue("InstallPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value != null && value is not string)
                throw new InvalidDataException("The registered DS4Windows installation path is not a string.");
            if (value is string path) yield return path;
        }
    }

    private static bool HasPortableMarker(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
