using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DS4Updater.Dtos;
using Microsoft.Win32.SafeHandles;

namespace DS4Updater;

internal interface IManagedUpdateOperations
{
    string ValidateRoot(string target);
    PortableInstalledIdentity ReadIdentity(string root, string launchExe);
    Task<GitHubRelease> FetchReleaseAsync(string tag, bool prerelease, CancellationToken cancellation);
    Task<byte[]> DownloadReceiptAsync(GitHubReleaseAsset asset, CancellationToken cancellation);
    Task<string> DownloadInstallerAsync(GitHubReleaseAsset asset, CancellationToken cancellation);
    void LaunchVerifiedInstaller(string path, PortableReleaseIdentity release);
}

// Installed copies go through the existing installer and its rollback/process
// coordination. No ZIP extraction, portable-marker writes, or process killing.
internal static class ManagedUpdateCoordinator
{
    internal const long MaximumInstallerBytes = 1024L * 1024 * 1024;

    internal static GitHubReleaseAsset RequireInstaller(GitHubRelease release, string architecture, Version version)
    {
        if (release is null || release.draft || architecture != "x64" || version is null)
            throw new InvalidDataException("No supported installed update was selected.");
        string name = $"DS4Windows_{version}_Setup_x64.exe";
        GitHubReleaseAsset[] candidates = (release.assets ?? Array.Empty<GitHubReleaseAsset>())
            .Where(asset => asset is not null && string.Equals(asset.name, name, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (candidates.Length != 1 || candidates[0].name != name ||
            candidates[0].size.GetValueOrDefault() <= 0 || candidates[0].size > MaximumInstallerBytes ||
            !ReleaseChannelPolicy.HasExactReleaseAssetUrl(release.tag_name, candidates[0]) ||
            !ReleaseChannelPolicy.TryGetAssetSha256(candidates[0], out _))
            throw new InvalidDataException("The release does not contain one verified installer for this installation.");
        return candidates[0];
    }

    internal static async Task<PortableReleaseIdentity> ExecuteAsync(ManagedUpdateRequest request,
        IManagedUpdateOperations operations, IProgress<PortableUpdateProgress> progress, CancellationToken cancellation)
    {
        ValidateRoot(request, operations);
        cancellation.ThrowIfCancellationRequested();
        PortableInstalledIdentity installed = operations.ReadIdentity(request.TargetDirectory, request.LaunchExe);
        bool prerelease = ReleaseChannelPolicy.IsPrereleaseInstall(installed.ProductVersion, installed.ReleaseTag);
        progress?.Report(new("Checking for an update for this installation…"));
        GitHubRelease release = await operations.FetchReleaseAsync(request.ReleaseTag, prerelease, cancellation).ConfigureAwait(false);
        if (release is null)
            throw new InvalidDataException("A published release could not be found for this installation.");
        if (release.draft || (request.ReleaseTag != null && request.ReleaseTag != release.tag_name))
            throw new InvalidDataException("The release does not match the requested published update.");
        if (!ReleaseChannelPolicy.ShouldUpdate(release, installed.FileVersion, prerelease, installed.ReleaseTag))
        {
            if (request.ReleaseTag is null) return null;
            throw new InvalidDataException("The requested release is not a forward update for this installation.");
        }
        GitHubReleaseAsset receipt = PortableReleaseResolver.SelectBuildReceipt(release) ??
            throw new InvalidDataException("This installed update needs a verified release build record.");
        byte[] receiptBytes = await operations.DownloadReceiptAsync(receipt, cancellation).ConfigureAwait(false);
        PortableReleaseIdentity resolved = PortableReleaseResolver.ResolveInstaller(release, "x64", receiptBytes);
        if (!resolved.IsNonDowngradingBinary(installed.FileVersion))
            throw new InvalidDataException("The requested release would downgrade the installed Windows binaries.");
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new("Downloading and verifying DS4Windows Setup…"));
        string installer = await operations.DownloadInstallerAsync(resolved.Asset, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        ValidateRoot(request, operations);
        if (operations.ReadIdentity(request.TargetDirectory, request.LaunchExe) != installed)
            throw new IOException("The installation changed while the update was prepared. Please check for updates again.");
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new("Opening Setup. Follow its instructions to finish the update…", Applying: true));
        operations.LaunchVerifiedInstaller(installer, resolved);
        return resolved; // Setup opened; installation success is NOT asserted.
    }

    private static void ValidateRoot(ManagedUpdateRequest request, IManagedUpdateOperations operations)
    {
        if (!string.Equals(operations.ValidateRoot(request.TargetDirectory), request.TargetDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The registered installation location changed. Nothing was launched.");
    }
}

internal sealed class ManagedUpdateOperations : IManagedUpdateOperations, IDisposable
{
    private readonly string stage;
    private readonly PortableUpdateOperations transport;
    private readonly Func<string, string> validateRegisteredRoot;
    private readonly Action<ProcessStartInfo> startInstaller;
    private readonly Func<string, string> readFileVersion;
    private readonly List<SafeFileHandle> stagePins = new();
    private bool launched;
    private string downloadedInstaller;

    internal ManagedUpdateOperations(Func<string, string> validateRegisteredRoot = null,
        HttpMessageHandler handler = null, Action<ProcessStartInfo> startInstaller = null,
        Func<string, string> readFileVersion = null, string stagingParent = null)
    {
        this.validateRegisteredRoot = validateRegisteredRoot ?? UpdateDeploymentPolicy.ValidateManagedRoot;
        this.startInstaller = startInstaller ?? StartInstaller;
        this.readFileVersion = readFileVersion ?? (path => FileVersionInfo.GetVersionInfo(path).FileVersion);
        string parent = PortableUpdateProcessGuard.NormalizeLocalPath(stagingParent ?? Path.GetTempPath());
        PortablePackageTransaction.ValidateNoReparse(parent);
        stage = Path.Combine(parent, "DS4Windows-managed-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            using WindowsIdentity current = WindowsIdentity.GetCurrent();
            foreach (SecurityIdentifier sid in new[] { current.User,
                         new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.CreateDirectory(security, stage);
            // Hold ancestor names against replacement while the download and
            // elevated launch are pending. File validation alone cannot pin a
            // renamed parent directory across a UAC prompt.
            for (string directory = stage; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            {
                SafeFileHandle pin = CreateFileW(directory, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (pin.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    pin.Dispose();
                    throw new Win32Exception(error);
                }
                stagePins.Add(pin);
            }
            PortablePackageTransaction.ValidateNoReparse(stage);
        }
        catch
        {
            foreach (SafeFileHandle pin in stagePins) pin.Dispose();
            throw;
        }
        transport = new PortableUpdateOperations(stage, null, handler);
    }

    public string ValidateRoot(string target)
    {
        string registered = validateRegisteredRoot(target);
        // The shipped AIO currently targets this location, with no custom
        // destination option. A registry record elsewhere is not permission
        // to migrate the user's installation into Program Files.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string installerTarget = Path.GetFullPath(Path.Combine(programFiles, "DS4Windows"));
        if (!Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess ||
            !string.Equals(registered, installerTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The standard installer cannot update this registered location in place. Your installation has not been changed.");
        return registered;
    }

    public PortableInstalledIdentity ReadIdentity(string root, string launchExe) =>
        PortableUpdateOperations.ReadInstalledIdentity(root, launchExe);

    public Task<GitHubRelease> FetchReleaseAsync(string tag, bool prerelease, CancellationToken cancellation) =>
        tag is null ? transport.FetchPreferredReleaseAsync(prerelease, cancellation) : transport.FetchReleaseAsync(tag, cancellation);

    public Task<byte[]> DownloadReceiptAsync(GitHubReleaseAsset asset, CancellationToken cancellation) =>
        transport.DownloadBuildReceiptAsync(asset, cancellation);

    public async Task<string> DownloadInstallerAsync(GitHubReleaseAsset asset, CancellationToken cancellation)
    {
        if (asset is null || asset.size.GetValueOrDefault() <= 0 || asset.size > ManagedUpdateCoordinator.MaximumInstallerBytes ||
            string.IsNullOrEmpty(asset.name) || Path.GetFileName(asset.name) != asset.name ||
            asset.name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || asset.name.Any(char.IsControl))
            throw new InvalidDataException("The installer download metadata is invalid.");
        string temporary = await transport.DownloadAsync(asset, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        PortablePackageTransaction.ValidateNoReparse(stage);
        string destination = Path.Combine(stage, asset.name);
        PortablePackageTransaction.ValidateNoReparse(destination);
        File.Move(temporary, destination);
        downloadedInstaller = destination;
        return destination;
    }

    public void LaunchVerifiedInstaller(string path, PortableReleaseIdentity release)
    {
        string expected = Path.Combine(stage, release.Asset.name);
        if (!string.Equals(path, expected, StringComparison.Ordinal) || path != downloadedInstaller)
            throw new InvalidDataException("Setup is not the installer staged by this updater.");
        PortablePackageTransaction.ValidateNoReparse(path);
        // Deny writes and deletion from hash verification through ShellExecute
        // (including UAC). Do not execute a second, unverified file snapshot.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != release.Asset.size || !ReleaseChannelPolicy.TryGetAssetSha256(release.Asset, out string digest) ||
            !Convert.ToHexString(SHA256.HashData(file)).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer does not match its verified release digest and size.");
        file.Position = 0;
        ValidatePeImage(file);
        if (!Version.TryParse(readFileVersion(path), out Version version) || version != release.FileVersion)
            throw new InvalidDataException("The installer Windows version does not match the selected release.");
        var start = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = stage };
        start.ArgumentList.Add("/install");
        start.ArgumentList.Add("/norestart");
        startInstaller(start);
        launched = true;
    }

    internal static void ValidatePeImage(Stream input)
    {
        try
        {
            using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
            PEHeaders headers = pe.PEHeaders;
            if (headers.PEHeader is null ||
                (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0 ||
                (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0 ||
                (headers.CoffHeader.Machine is not Machine.I386 and not Machine.Amd64))
                throw new InvalidDataException("The downloaded setup is not a supported Windows executable.");
        }
        catch (BadImageFormatException error)
        {
            throw new InvalidDataException("The downloaded setup has an invalid Windows executable header.", error);
        }
    }

    private static void StartInstaller(ProcessStartInfo start)
    {
        using Process setup = Process.Start(start) ?? throw new IOException("Windows could not open DS4Windows Setup.");
    }

    public void Dispose()
    {
        transport.Dispose();
        // Keep a successfully launched installer available for Burn. Failed
        // attempts remove only this invocation's two known download names.
        if (launched)
        {
            foreach (SafeFileHandle pin in stagePins) pin.Dispose();
            return;
        }
        foreach (string path in new[] { Path.Combine(stage, "package.zip"), downloadedInstaller })
        {
            if (path is null) continue;
            try { PortablePackageTransaction.ValidateNoReparse(path); if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }
        foreach (SafeFileHandle pin in stagePins) pin.Dispose();
        try { PortablePackageTransaction.ValidateNoReparse(stage); Directory.Delete(stage, recursive: false); }
        catch (Exception) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share,
        IntPtr securityAttributes, uint creation, uint flags, IntPtr templateFile);
}
