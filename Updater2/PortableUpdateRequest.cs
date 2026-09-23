using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DS4Updater;

internal sealed record PortableUpdateRequest(string TargetDirectory, int ParentPid,
    long ParentStartUtcTicks, string ReleaseTag, string LaunchExe, bool IsWorker,
    string OriginalExe = null)
{
    internal const string PortableFlag = "--portable-safe-v1";
    internal string CustomExeBaseName => string.Equals(LaunchExe, "DS4Windows.exe", StringComparison.OrdinalIgnoreCase)
        ? null : Path.GetFileNameWithoutExtension(LaunchExe);
    internal string InstalledExe => OriginalExe ?? LaunchExe;
    internal static bool IsPortableInvocation(string[] args) => args.Any(a =>
        a.StartsWith("--portable-", StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldUsePortableLifetime(string[] args, string executablePath) =>
        UpdateDeploymentPolicy.Resolve(args, executablePath) == UpdateDeployment.Portable;

    internal static PortableUpdateRequest Parse(string[] args, string executablePath)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        bool portable = false, worker = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == PortableFlag) { if (portable) throw new ArgumentException("Duplicate portable flag."); portable = true; }
            else if (arg == "--portable-worker") { if (worker) throw new ArgumentException("Duplicate worker flag."); worker = true; }
            else if (new[] { "--parentPid", "--parentStartUtcTicks", "--releaseTag", "--launchExe", "--targetDirectory", "--originalExe" }.Contains(arg))
            {
                if (++i == args.Length || !options.TryAdd(arg, args[i])) throw new ArgumentException("Missing or duplicate portable option: " + arg);
            }
            else throw new ArgumentException("Unrecognized portable update option: " + arg);
        }
        if (!portable || !options.TryGetValue("--parentPid", out string parent) ||
            !int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
            !options.TryGetValue("--parentStartUtcTicks", out string started) ||
            !long.TryParse(started, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks ||
            !options.TryGetValue("--releaseTag", out string tag) || string.IsNullOrWhiteSpace(tag) || tag.Length > 128 ||
            tag != tag.Trim() || tag.Any(c => char.IsControl(c) || c is '/' or '\\') ||
            !options.TryGetValue("--launchExe", out string launch) || string.IsNullOrWhiteSpace(launch))
            throw new ArgumentException("The portable update request is incomplete or invalid.");
        PortableUpdateProcessGuard.ValidateCustomExeName(launch);
        options.TryGetValue("--originalExe", out string original);
        if (options.ContainsKey("--originalExe"))
        {
            if (!worker || string.IsNullOrWhiteSpace(original))
                throw new ArgumentException("Only a bound worker can carry the initiating executable name.");
            PortableUpdateProcessGuard.ValidateCustomExeName(original);
        }
        string executable = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetFileName(executable), "DS4Updater.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The portable updater executable has an unexpected name.");
        string target;
        if (worker)
        {
            if (!options.TryGetValue("--targetDirectory", out target) || !Path.IsPathFullyQualified(target) ||
                target.StartsWith("\\\\", StringComparison.Ordinal)) throw new ArgumentException("A worker requires its exact local target directory.");
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            string workerFolder = Path.GetDirectoryName(executable);
            string name = Path.GetFileName(workerFolder);
            if (!name.StartsWith("portable-worker-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(name[16..], "N", out _) ||
                !string.Equals(Path.GetDirectoryName(workerFolder), Path.Combine(target, "Updates"), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The updater worker is not inside its exact owned target folder.");
        }
        else
        {
            if (options.ContainsKey("--targetDirectory")) throw new ArgumentException("A launching updater cannot retarget another installation.");
            target = Path.GetDirectoryName(executable);
        }
        return new(target, pid, ticks, tag, launch, worker, original);
    }

    internal string[] WorkerArguments()
    {
        var arguments = new List<string> { PortableFlag, "--portable-worker", "--targetDirectory", TargetDirectory,
            "--parentPid", ParentPid.ToString(CultureInfo.InvariantCulture), "--parentStartUtcTicks", ParentStartUtcTicks.ToString(CultureInfo.InvariantCulture),
            "--releaseTag", ReleaseTag, "--launchExe", LaunchExe };
        if (OriginalExe != null) { arguments.Add("--originalExe"); arguments.Add(OriginalExe); }
        return arguments.ToArray();
    }
}

internal sealed record PortableWorkerRecord(int Format, PortableUpdateRequest Request,
    string WorkerSha256, int LauncherPid, long LauncherStartUtcTicks, string LauncherPath);

internal static class PortableWorkerSession
{
    internal static void Start(PortableUpdateRequest request)
    {
        if (request.IsWorker) throw new InvalidOperationException("A worker cannot launch another worker.");
        string target = PortableUpdateProcessGuard.ValidateTargetRoot(request.TargetDirectory);
        string executable = Environment.ProcessPath ?? throw new IOException("The updater process path is unavailable.");
        if (!string.Equals(Path.GetDirectoryName(executable), target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The launcher is not running from the selected portable root.");
        // The release publishes one self-contained single-file executable.
        // Copying only an apphost from a framework-dependent build is unsafe.
        if (!string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location))
            throw new IOException("Use the published self-contained portable updater, not a development apphost.");
        // The default apphost may still be running when the user has selected
        // a custom name. Bind the worker to that selected name now; never let
        // a later config change silently retarget the verified update/relaunch.
        request = ResolveLaunchConfiguration(request);
        string updates = Path.Combine(target, "Updates");
        PortablePackageTransaction.ValidateNoReparse(updates);
        Directory.CreateDirectory(updates);
        string folder = Path.Combine(updates, "portable-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string workerPath = Path.Combine(folder, "DS4Updater.exe");
        PortablePackageTransaction.ValidateNoReparse(executable);
        using (var source = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(workerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(output);
            output.Flush(true);
        }
        string workerHash = HashFile(workerPath);
        if (!string.Equals(workerHash, HashFile(executable), StringComparison.Ordinal)) throw new IOException("Worker copy verification failed.");
        using Process launcher = Process.GetCurrentProcess();
        var record = new PortableWorkerRecord(1, request with { IsWorker = true }, workerHash,
            launcher.Id, launcher.StartTime.ToUniversalTime().Ticks, executable);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record));
        using (var file = new FileStream(Path.Combine(folder, "request.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(true);
        }
        var start = new ProcessStartInfo(workerPath) { UseShellExecute = false, WorkingDirectory = folder };
        foreach (string argument in record.Request.WorkerArguments()) start.ArgumentList.Add(argument);
        using Process child = Process.Start(start) ?? throw new IOException("The isolated updater worker could not start.");
    }

    internal static PortableWorkerRecord ValidateWorker(PortableUpdateRequest request, string executablePath,
        Func<string, string> validateRoot = null)
    {
        if (!request.IsWorker) throw new ArgumentException("A worker request is required.");
        if (PortableUpdateRequest.Parse(request.WorkerArguments(), executablePath) != request)
            throw new InvalidDataException("The worker location does not match the requested target.");
        string target = (validateRoot ?? PortableUpdateProcessGuard.ValidateTargetRoot)(request.TargetDirectory);
        if (!string.Equals(target, request.TargetDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The worker target changed during validation.");
        PortablePackageTransaction.ValidateNoReparse(executablePath);
        string recordPath = Path.Combine(Path.GetDirectoryName(executablePath), "request.json");
        PortablePackageTransaction.ValidateNoReparse(recordPath);
        PortableWorkerRecord record = JsonSerializer.Deserialize<PortableWorkerRecord>(
            PortablePackageTransaction.ReadBoundedTextSnapshot(recordPath, 8192).Text);
        if (record == null || record.Format != 1 || record.Request != request || record.LauncherPid <= 0 ||
            record.LauncherStartUtcTicks <= 0 || record.LauncherStartUtcTicks > DateTime.MaxValue.Ticks ||
            !string.Equals(record.LauncherPath, Path.Combine(request.TargetDirectory, "DS4Updater.exe"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(record.WorkerSha256, HashFile(executablePath), StringComparison.Ordinal))
            throw new InvalidDataException("The worker does not match its original portable update request.");
        ValidateLaunchConfiguration(request, beforeUpdate: true);
        return record;
    }

    internal static void ValidateLaunchConfiguration(PortableUpdateRequest request, bool beforeUpdate = false)
    {
        PortableUpdateProcessGuard.ValidateCustomExeName(request.LaunchExe);
        PortableUpdateProcessGuard.ValidateCustomExeName(request.InstalledExe);
        string path = Path.Combine(request.TargetDirectory, beforeUpdate ? request.InstalledExe : request.LaunchExe);
        PortablePackageTransaction.ValidateNoReparse(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The selected DS4Windows executable is missing.", path);
        string configured = ReadConfiguredCustomName(request.TargetDirectory);
        if (!string.Equals(configured, request.CustomExeBaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The requested executable does not match the existing custom-name setting. Restart the update from your selected executable.");
    }

    internal static PortableUpdateRequest ResolveLaunchConfiguration(PortableUpdateRequest request)
    {
        if (request.IsWorker || request.OriginalExe != null) throw new InvalidOperationException("A bound request cannot change its selected executable.");
        PortableUpdateProcessGuard.ValidateCustomExeName(request.LaunchExe);
        string original = Path.Combine(request.TargetDirectory, request.LaunchExe);
        PortablePackageTransaction.ValidateNoReparse(original);
        if (!File.Exists(original)) throw new FileNotFoundException("The initiating DS4Windows executable is missing.", original);
        string configured = ReadConfiguredCustomName(request.TargetDirectory);
        string destination = configured == null ? "DS4Windows.exe" : configured + ".exe";
        if (!string.Equals(request.LaunchExe, destination, StringComparison.Ordinal))
            request = request with { OriginalExe = request.LaunchExe, LaunchExe = destination };
        // The destination may not exist when a user changed/cleared the name.
        // The old apphost remains the identity checked before installation.
        ValidateLaunchConfiguration(request, beforeUpdate: true);
        return request;
    }

    private static string ReadConfiguredCustomName(string targetDirectory)
    {
        string configuration = Path.Combine(targetDirectory, "custom_exe_name.txt");
        PortablePackageTransaction.ValidateNoReparse(configuration);
        string name;
        try { name = PortablePackageTransaction.ReadBoundedTextSnapshot(configuration, 512).Text.Trim(); }
        catch (FileNotFoundException) { return null; }
        if (string.IsNullOrEmpty(name) || string.Equals(name, "DS4Windows", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Length > 100) throw new InvalidDataException("The configured executable name is too long.");
        PortableUpdateProcessGuard.ValidateCustomExeName(name + ".exe");
        return name;
    }

    private static string HashFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
