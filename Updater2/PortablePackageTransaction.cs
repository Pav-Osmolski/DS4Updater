using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DS4Updater;

// A stopped, explicitly selected portable installation only. Process ownership,
// registered-install detection and HTTPS release selection belong to the caller.
// Backups make ordinary I/O failures recoverable; this is not power-loss atomic.
internal sealed class PortablePackageTransaction : IDisposable
{
    internal const string ManifestName = ".ds4windows-managed-files.txt";
    internal const string MarkerName = "DS4Windows.portable";
    internal const string MarkerText = "DS4Windows portable package v1";
    private const string TransactionPrefix = "portable-update-";
    private const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumFileBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 10000;
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<string, string> payloadHashes = new(Paths);
    private readonly HashSet<string> previousOwnership;
    private readonly string previousManifestHash;
    private readonly bool splitPackage;
    private readonly FileStream transactionLock;
    private readonly List<string> createdDirectories = new();
    private string state = "prepared";
    private bool disposed, applyAttempted;

    internal string TargetRoot { get; }
    internal string TransactionRoot { get; }
    internal string StagedRoot { get; }
    internal bool RecoveryRequired { get; private set; }
    // Deterministic fault injection only; production never assigns this callback.
    internal Action<string, bool> BeforeMutationForTesting { get; set; }
    internal Action AfterLegacyAdoptionSnapshotForTesting { get; set; }

    private PortablePackageTransaction(string target, string transaction, FileStream owner,
        HashSet<string> previous, string previousHash, bool splitPackage)
    {
        TargetRoot = target;
        TransactionRoot = transaction;
        StagedRoot = Path.Combine(transaction, "stage");
        transactionLock = owner;
        previousOwnership = previous;
        previousManifestHash = previousHash;
        this.splitPackage = splitPackage;
    }

    internal static PortablePackageTransaction Prepare(string targetRoot, string archivePath,
        string expectedSha256, string expectedTag)
    {
        string target = ValidateTargetRoot(targetRoot);
        ValidateTag(expectedTag);
        string expectedHash = NormalizeHash(expectedSha256);
        string manifestPath = Child(target, ManifestName);
        ValidateNoReparse(manifestPath);
        var oldManifest = ReadBoundedTextSnapshot(manifestPath, 1024 * 1024);
        HashSet<string> previous = ParseManifest(oldManifest.Text);
        string oldHash = oldManifest.Sha256;
        string archive = Path.GetFullPath(archivePath);
        ValidateNoReparse(archive);
        using var archiveStream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (archiveStream.Length <= 0 || archiveStream.Length > MaximumArchiveBytes ||
            !Paths.Equals(HashStream(archiveStream), expectedHash))
            throw new InvalidDataException("The portable archive SHA-256 does not match the selected release.");
        archiveStream.Position = 0;
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        Dictionary<string, ZipArchiveEntry> files = InspectArchive(zip);
        if (!files.TryGetValue(ManifestName, out ZipArchiveEntry manifest))
            throw new InvalidDataException("The package ownership manifest is missing.");
        HashSet<string> incoming = ParseManifest(ReadEntryText(manifest, 1024 * 1024));
        if (files.Count != incoming.Count + 1 || !incoming.All(files.ContainsKey))
            throw new InvalidDataException("The package manifest and archive payload do not match exactly.");
        foreach (string path in incoming)
            if (!string.Equals(files[path].FullName, "DS4Windows/" + path, StringComparison.Ordinal))
                throw new InvalidDataException("Manifest path casing does not match its archive entry.");
        foreach (string required in new[] { "DS4Windows.exe", "DS4Windows.release",
                     MarkerName, "viiper.exe", "viiper.exe.sha256" })
            if (!incoming.Contains(required)) throw new InvalidDataException("Required package file is missing: " + required);
        bool splitPackage = PortableApplicationFiles.UsesSplitPackage(incoming);
        if (ReadEntryText(files[MarkerName], 128).TrimEnd('\r', '\n') != MarkerText ||
            ReadEntryText(files["DS4Windows.release"], 256).TrimEnd('\r', '\n') != expectedTag)
            throw new InvalidDataException("The portable marker or release identity is incorrect.");
        string[] brokers = incoming.Where(p => p.StartsWith("extras/VIIPER-", StringComparison.Ordinal) &&
            p.EndsWith("-x64.exe", StringComparison.Ordinal)).ToArray();
        if (brokers.Length != 1 || !incoming.Contains(brokers[0] + ".sha256"))
            throw new InvalidDataException("Exactly one pinned extras VIIPER executable is required.");

        string updates = Child(target, "Updates");
        ValidateNoReparse(updates);
        Directory.CreateDirectory(updates);
        string lockPath = Path.Combine(updates, ".portable-update.lock");
        ValidateNoReparse(lockPath);
        if (File.Exists(lockPath) && new FileInfo(lockPath).Length != 0)
            throw new IOException("An unrecognized portable-update lock file already exists.");
        FileStream owner = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        PortablePackageTransaction plan = null;
        try
        {
            if (Directory.EnumerateFileSystemEntries(updates, TransactionPrefix + "*").Any())
                throw new IOException("An earlier portable update requires inspection. Preserve its recovery folder in Updates.");
            string transaction = Path.Combine(updates, TransactionPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(transaction);
            plan = new PortablePackageTransaction(target, transaction, owner, previous, oldHash, splitPackage);
            Directory.CreateDirectory(plan.StagedRoot);
            plan.WriteJournal("staging", Array.Empty<Change>());
            foreach (var entry in files)
            {
                string staged = Child(plan.StagedRoot, entry.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(staged));
                using Stream source = entry.Value.Open();
                using var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                plan.payloadHashes.Add(entry.Key, CopyBounded(source, destination, entry.Value.Length));
                destination.Flush(true);
            }
            foreach (string relative in files.Keys)
                if (!Paths.Equals(plan.payloadHashes[relative], HashFile(Child(plan.StagedRoot, relative))))
                    throw new IOException("Staged payload copy failed verification: " + relative);
            string brokerHash = plan.payloadHashes["viiper.exe"];
            if (!Paths.Equals(brokerHash, ParseChecksumSidecar(ReadBoundedText(Child(plan.StagedRoot, "viiper.exe.sha256"), 256), "viiper.exe")) ||
                !Paths.Equals(brokerHash, plan.payloadHashes[brokers[0]]) ||
                !Paths.Equals(brokerHash, ParseChecksumSidecar(ReadBoundedText(Child(plan.StagedRoot, brokers[0] + ".sha256"), 256), Path.GetFileName(brokers[0]))))
                throw new InvalidDataException("Portable and extras VIIPER payloads do not match both recorded SHA-256 pins.");
            plan.WriteJournal("prepared", Array.Empty<Change>());
            return plan;
        }
        catch
        {
            if (plan != null) plan.Dispose();
            else owner.Dispose();
            throw;
        }
    }

    internal void Apply(string customExeBaseName = null, string installedExe = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (applyAttempted) throw new InvalidOperationException("This portable transaction has already been attempted.");
        applyAttempted = true;
        ValidateTargetRoot(TargetRoot);
        if (!Paths.Equals(previousManifestHash, HashFile(Child(TargetRoot, ManifestName))))
            throw new IOException("The installed ownership manifest changed after preparation.");
        foreach (var payload in payloadHashes)
            if (!Paths.Equals(payload.Value, HashFile(Child(StagedRoot, payload.Key))))
                throw new InvalidDataException("A staged payload changed after verification: " + payload.Key);
        using FileStream configuration = LockCustomConfiguration(customExeBaseName);
        var replacements = payloadHashes.Keys.Where(p => !Paths.Equals(p, ManifestName))
            .ToDictionary(p => p, p => Child(StagedRoot, p), Paths);
        HashSet<string> aliases = AddCustomAlias(customExeBaseName, replacements);
        Dictionary<string, string> legacySnapshots = SnapshotLegacyAliasAdoption(aliases, customExeBaseName, installedExe);
        AfterLegacyAdoptionSnapshotForTesting?.Invoke();
        // The installed ownership reflects the selected executable name. The
        // verified downloaded payload and its manifest stay untouched.
        string installedManifest = Path.Combine(TransactionRoot, "installed-manifest.txt");
        WriteTextNew(installedManifest, string.Join("\n", replacements.Keys.OrderBy(p => p, StringComparer.Ordinal)) + "\n");
        replacements.Add(ManifestName, installedManifest);
        var changes = replacements.Select(p => new Change(p.Key, p.Value, HashFile(p.Value))).ToList();
        foreach (string stale in previousOwnership)
            if (!replacements.ContainsKey(stale)) changes.Add(new Change(stale, null, null));
        if (aliases.Count != 0 && !previousOwnership.Contains("DS4Windows.exe"))
            // Also guard an absent default apphost. An unowned copy must fail
            // preflight, and a newly appearing copy must never be deleted.
            changes.Add(new Change("DS4Windows.exe", null, null));
        // The manifest is the final ownership publication, never the first mutation.
        changes = changes.OrderBy(c => Paths.Equals(c.Relative, ManifestName) ? 1 : 0)
            .ThenBy(c => c.Relative, StringComparer.Ordinal).ToList();
        var locks = new Dictionary<string, FileStream>(Paths);
        var touched = new List<Change>();
        try
        {
            foreach (Change change in changes)
            {
                string destination = Child(TargetRoot, change.Relative);
                ValidateNoReparse(destination);
                if (Directory.Exists(destination)) throw new IOException("A package file conflicts with a directory: " + change.Relative);
                change.Existed = File.Exists(destination);
                if (!change.Existed && legacySnapshots.ContainsKey(change.Relative))
                    throw new IOException("The verified legacy application disappeared during preflight: " + change.Relative);
                if (change.Existed && !Paths.Equals(change.Relative, ManifestName) &&
                    !previousOwnership.Contains(change.Relative) &&
                    !(aliases.Contains(change.Relative) && legacySnapshots.ContainsKey(change.Relative)))
                    throw new IOException("Refusing to overwrite an unowned file: " + change.Relative);
                if (!change.Existed) continue;
                // All target locks are obtained before any live file changes.
                // An open app/broker/updater therefore rejects the whole apply.
                var pin = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                locks.Add(change.Relative, pin);
                change.OriginalHash = HashStream(pin);
                if (legacySnapshots.TryGetValue(change.Relative, out string legacyHash) &&
                    !Paths.Equals(change.OriginalHash, legacyHash))
                    throw new IOException("The verified legacy application changed during preflight: " + change.Relative);
                change.Attributes = File.GetAttributes(destination);
                change.LastWriteUtc = File.GetLastWriteTimeUtc(destination);
                if (Paths.Equals(change.Relative, ManifestName) && !Paths.Equals(change.OriginalHash, previousManifestHash))
                    throw new IOException("The installed ownership manifest changed during preflight.");
            }
            string backupRoot = Path.Combine(TransactionRoot, "backup");
            foreach (Change change in changes.Where(c => c.Existed))
            {
                string backup = Child(backupRoot, change.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                FileStream pin = locks[change.Relative];
                pin.Position = 0;
                using (var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    pin.CopyTo(output);
                    output.Flush(true);
                }
                if (!Paths.Equals(change.OriginalHash, HashFile(backup)))
                    throw new IOException("The recovery backup failed verification: " + change.Relative);
            }
            WriteJournal("applying", changes);
            foreach (Change change in changes)
            {
                if (locks.Remove(change.Relative, out FileStream pin)) pin.Dispose();
                BeforeMutationForTesting?.Invoke(change.Relative, false);
                RequireConfigurationUnchanged(configuration);
                RequireUnchangedDestination(change, applied: false);
                if (change.Source == null)
                {
                    if (change.Existed) File.Delete(Child(TargetRoot, change.Relative));
                }
                else ReplaceFromVerifiedCopy(change.Source, change.Relative, change.NewHash, change.Existed);
                // A failed copy/move has not changed the live destination. In
                // particular, rollback must not delete an unowned file that
                // appeared after preflight and made a no-overwrite move fail.
                touched.Add(change);
            }
            foreach (Change change in changes)
            {
                string destination = Child(TargetRoot, change.Relative);
                if (change.Source == null ? File.Exists(destination) || Directory.Exists(destination) : !Paths.Equals(change.NewHash, HashFile(destination)))
                    throw new IOException("Final portable payload verification failed: " + change.Relative);
            }
            RequireConfigurationUnchanged(configuration);
            WriteJournal("committed", changes);
        }
        catch (Exception failure)
        {
            foreach (FileStream pin in locks.Values) pin.Dispose();
            locks.Clear();
            if (touched.Count == 0)
            {
                if (state == "applying")
                {
                    try { WriteJournal("rolled-back", changes); }
                    catch { RecoveryRequired = true; }
                }
                throw;
            }
            var rollbackErrors = new List<Exception>();
            foreach (Change change in touched.AsEnumerable().Reverse())
            {
                try
                {
                    BeforeMutationForTesting?.Invoke(change.Relative, true);
                    string destination = Child(TargetRoot, change.Relative);
                    // Recovery owns only the bytes this transaction actually
                    // installed. Preserve a concurrent user's edit and retain
                    // the verified backup instead of overwriting that edit.
                    RequireUnchangedDestination(change, applied: true);
                    if (change.Existed)
                    {
                        ReplaceFromVerifiedCopy(Child(Path.Combine(TransactionRoot, "backup"), change.Relative),
                            change.Relative, change.OriginalHash, File.Exists(destination));
                        File.SetLastWriteTimeUtc(destination, change.LastWriteUtc);
                        File.SetAttributes(destination, change.Attributes);
                    }
                    else if (File.Exists(destination)) File.Delete(destination);
                }
                catch (Exception rollback) { rollbackErrors.Add(rollback); }
            }
            RecoveryRequired = rollbackErrors.Count != 0;
            try { WriteJournal(RecoveryRequired ? "recovery-required" : "rolled-back", changes); }
            catch (Exception journalFailure) { RecoveryRequired = true; rollbackErrors.Add(journalFailure); }
            if (RecoveryRequired)
                throw new IOException("Update failed and recovery is incomplete. Do not launch DS4Windows. Preserve " + TransactionRoot,
                    new AggregateException(new[] { failure }.Concat(rollbackErrors)));
            throw new IOException("Update failed; the previous portable files were restored.", failure);
        }
        finally { foreach (FileStream pin in locks.Values) pin.Dispose(); }
    }

    private void RequireUnchangedDestination(Change change, bool applied)
    {
        string destination = Child(TargetRoot, change.Relative);
        ValidateNoReparse(destination);
        bool shouldExist = applied ? change.Source != null : change.Existed;
        string expectedHash = applied ? change.NewHash : change.OriginalHash;
        if (Directory.Exists(destination) || File.Exists(destination) != shouldExist ||
            (shouldExist && !Paths.Equals(expectedHash, HashFile(destination))))
            throw new IOException("A portable destination changed during the update; its current contents were preserved: " + change.Relative);
    }

    private Dictionary<string, string> SnapshotLegacyAliasAdoption(HashSet<string> aliases, string name, string installedExe)
    {
        var snapshots = new Dictionary<string, string>(Paths);
        string[] unowned = aliases.Where(path => !previousOwnership.Contains(path) &&
            File.Exists(Child(TargetRoot, path))).ToArray();
        if (unowned.Length == 0 || !Paths.Equals(installedExe, name + ".exe")) return snapshots;

        // Only the exact initiating apphost bound by the worker/process guard
        // can establish legacy ownership. A newly selected name is never an
        // invitation to adopt an unrelated file, even if its metadata matches.
        // Legacy runtime/deps copies must also match their owned counterparts.
        var pins = new Dictionary<string, FileStream>(Paths);
        try
        {
            string assembly = PortableApplicationFiles.GetInstalledAssemblyRelativePath(TargetRoot);
            string[] evidence = unowned.Append(installedExe).Append(assembly)
                .Concat(unowned.Where(path => !Paths.Equals(path, installedExe))
                    .Select(path => "DS4Windows" + path.Substring(name.Length)))
                .Distinct(Paths).ToArray();
            foreach (string relative in evidence)
            {
                if (!aliases.Contains(relative) && !previousOwnership.Contains(relative))
                    throw new IOException("Refusing legacy adoption without an owned application dependency: " + relative);
                string path = Child(TargetRoot, relative);
                ValidateNoReparse(path);
                var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                pins.Add(relative, pin);
                snapshots.Add(relative, HashStream(pin));
            }
            // Read the same PE identity used by the coordinator while read
            // handles deny writes/replacement. Exclusive preflight locks later
            // recheck every evidence hash before any installed bytes change.
            PortableUpdateOperations.ReadInstalledIdentity(TargetRoot, installedExe);
            foreach (string relative in unowned.Where(path => !Paths.Equals(path, installedExe)))
                if (!Paths.Equals(snapshots[relative], snapshots["DS4Windows" + relative.Substring(name.Length)]))
                    throw new IOException("Refusing to overwrite an unowned legacy sidecar: " + relative);
            return snapshots;
        }
        finally { foreach (FileStream pin in pins.Values) pin.Dispose(); }
    }

    private HashSet<string> AddCustomAlias(string name, Dictionary<string, string> replacements)
    {
        var aliases = new HashSet<string>(Paths);
        if (name == null) return aliases;
        ValidateRelative(name);
        if (name.Contains('/') || name.Length > 100 ||
            Paths.Equals(name, "viiper") || Paths.Equals(name, "DS4Updater"))
            throw new InvalidDataException("The custom executable name is unsafe.");
        try { PortableUpdateProcessGuard.ValidateCustomExeName(name + ".exe"); }
        catch (ArgumentException error) { throw new InvalidDataException("The custom executable name is unsafe.", error); }
        if (Paths.Equals(name, "DS4Windows")) return aliases;
        string[] suffixes = splitPackage ? new[] { ".exe" } : new[] { ".exe", ".runtimeconfig.json", ".deps.json" };
        foreach (string suffix in suffixes)
        {
            string relative = name + suffix;
            ValidateRelative(relative);
            if (replacements.ContainsKey(relative)) throw new InvalidDataException("The custom alias conflicts with a packaged file.");
            replacements.Add(relative, Child(StagedRoot, "DS4Windows" + suffix));
            aliases.Add(relative);
        }
        // A split apphost resolves both sidecars through its embedded Runtime
        // assembly path regardless of the name of the executable.
        replacements.Remove("DS4Windows.exe");
        return aliases;
    }

    private FileStream LockCustomConfiguration(string requestedName)
    {
        string path = Child(TargetRoot, "custom_exe_name.txt");
        ValidateNoReparse(path);
        FileStream pin;
        try { pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) when (requestedName == null) { return null; }
        try
        {
            if (pin.Length > 512) throw new InvalidDataException("Portable metadata is oversized.");
            using var buffer = new MemoryStream();
            CopyBounded(pin, buffer, pin.Length);
            string configured = StrictUtf8.GetString(buffer.ToArray()).TrimStart('\uFEFF').Trim();
            string expected = requestedName ?? "";
            if (Paths.Equals(configured, "DS4Windows")) configured = "";
            if (Paths.Equals(expected, "DS4Windows")) expected = "";
            if (!string.Equals(configured, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The custom executable name does not match the existing configuration.");
            // This read handle denies writes and deletion until apply and any
            // rollback finish, preserving the exact setting we validated.
            return pin;
        }
        catch { pin.Dispose(); throw; }
    }

    private void RequireConfigurationUnchanged(FileStream configuration)
    {
        if (configuration != null) return; // Its open handle prevents changes.
        string path = Child(TargetRoot, "custom_exe_name.txt");
        ValidateNoReparse(path);
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("The custom executable setting changed during the update. Retry the update.");
    }

    private void ReplaceFromVerifiedCopy(string source, string relative, string expectedHash, bool overwrite)
    {
        ValidateNoReparse(source);
        string destination = Child(TargetRoot, relative);
        EnsureTargetDirectories(Path.GetDirectoryName(destination));
        ValidateNoReparse(destination);
        string temporary = Path.Combine(Path.GetDirectoryName(destination), ".ds4w-new-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (!Paths.Equals(expectedHash, HashFile(temporary))) throw new IOException("Copy verification failed: " + relative);
            ValidateNoReparse(destination);
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void EnsureTargetDirectories(string directory)
    {
        ValidateNoReparse(directory);
        if (Directory.Exists(directory)) return;
        if (!AtOrBelow(directory, TargetRoot)) throw new IOException("Target directory escaped the portable root.");
        EnsureTargetDirectories(Path.GetDirectoryName(directory));
        Directory.CreateDirectory(directory);
        createdDirectories.Add(directory);
    }

    private void WriteJournal(string nextState, IEnumerable<Change> changes)
    {
        ValidateNoReparse(TransactionRoot);
        string journal = Path.Combine(TransactionRoot, "transaction.json");
        string text = JsonSerializer.Serialize(new { format = 1, targetRoot = TargetRoot, state = nextState,
            changes = changes.Select(c => new { path = c.Relative, existed = c.Existed,
                previousSha256 = c.OriginalHash, packageSha256 = c.NewHash }) });
        using var output = new FileStream(journal, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] bytes = StrictUtf8.GetBytes(text);
        output.Write(bytes);
        output.Flush(true);
        state = nextState;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!RecoveryRequired && state != "applying")
            {
                foreach (string directory in createdDirectories.AsEnumerable().Reverse())
                {
                    ValidateNoReparse(directory);
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                }
                DeleteOwnedTree(TransactionRoot);
            }
        }
        finally { transactionLock.Dispose(); }
    }

    private void DeleteOwnedTree(string directory)
    {
        if (!AtOrBelow(directory, TransactionRoot) || !Path.GetFileName(TransactionRoot).StartsWith(TransactionPrefix, StringComparison.Ordinal))
            throw new IOException("Invalid transaction cleanup target.");
        ValidateNoReparse(directory);
        if (!Directory.Exists(directory)) return;
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            ValidateNoReparse(entry);
            if (Directory.Exists(entry)) DeleteOwnedTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory);
    }

    internal static string ValidateTargetRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("A portable target must be an explicit local absolute directory.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (Paths.Equals(root, Path.GetPathRoot(root))) throw new InvalidDataException("A drive root is not a portable installation.");
        if (new DriveInfo(Path.GetPathRoot(root)).DriveType != DriveType.Fixed)
            throw new InvalidDataException("The portable update target must be on a local fixed disk.");
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
        {
            string protectedRoot = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(protectedRoot) && AtOrBelow(root, protectedRoot))
                throw new InvalidDataException("The portable updater cannot modify system or managed-install storage.");
        }
        foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            if (Paths.Equals(root, Environment.GetFolderPath(folder)))
                throw new InvalidDataException("A user-data root is not a portable installation.");
        string userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string broad in new[] { Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                     Path.GetDirectoryName(userRoot), Path.Combine(userRoot, "Downloads") })
            if (!string.IsNullOrEmpty(broad) && Paths.Equals(root, broad))
                throw new InvalidDataException("A broad user-data directory is not a portable installation.");
        ValidateNoReparse(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        if (ReadBoundedText(Child(root, MarkerName), 128).TrimEnd('\r', '\n') != MarkerText)
            throw new InvalidDataException("The target does not have a valid portable package marker.");
        return root;
    }

    private static Dictionary<string, ZipArchiveEntry> InspectArchive(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumEntries) throw new InvalidDataException("Invalid archive entry count.");
        var files = new Dictionary<string, ZipArchiveEntry>(Paths);
        var entries = new HashSet<string>(Paths);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            int type = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                (type != 0 && type != 0x8000 && type != 0x4000))
                throw new InvalidDataException("Archive links and special files are not supported.");
            if (!name.StartsWith("DS4Windows/", StringComparison.Ordinal) || name.Contains('\\'))
                throw new InvalidDataException("Every archive entry must be under the exact DS4Windows/ root.");
            bool directory = name.EndsWith("/", StringComparison.Ordinal);
            string relative = name[11..].TrimEnd('/');
            if (relative.Length == 0)
            {
                if (!directory || !entries.Add("/")) throw new InvalidDataException("Duplicate archive root.");
                continue;
            }
            ValidateRelative(relative, allowManifest: true);
            if (!entries.Add(relative)) throw new InvalidDataException("Duplicate or case-colliding archive entry: " + relative);
            if (directory)
            {
                if (entry.Length != 0) throw new InvalidDataException("Directory entries cannot contain payloads.");
                continue;
            }
            if (entry.Length < 0 || entry.Length > MaximumFileBytes ||
                (entry.Length > 10 * 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 1000))
                throw new InvalidDataException("Archive entry exceeds decompression limits.");
            total = checked(total + entry.Length);
            if (total > MaximumArchiveBytes) throw new InvalidDataException("Archive expands beyond the package limit.");
            files.Add(relative, entry);
        }
        ValidateComponentCasing(entries.Where(p => p != "/"));
        foreach (string relative in entries.Where(p => p != "/"))
            for (int slash = relative.IndexOf('/'); slash >= 0; slash = relative.IndexOf('/', slash + 1))
                if (files.ContainsKey(relative[..slash])) throw new InvalidDataException("Archive file/directory collision.");
        return files;
    }

    private static HashSet<string> ParseManifest(string text)
    {
        var paths = new HashSet<string>(Paths);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is string line)
        {
            ValidateRelative(line);
            if (!paths.Add(line) || paths.Count > MaximumEntries) throw new InvalidDataException("Duplicate or oversized ownership manifest.");
        }
        if (paths.Count == 0) throw new InvalidDataException("The ownership manifest is empty.");
        ValidateComponentCasing(paths);
        foreach (string relative in paths)
            for (int slash = relative.IndexOf('/'); slash >= 0; slash = relative.IndexOf('/', slash + 1))
                if (paths.Contains(relative[..slash])) throw new InvalidDataException("Manifest file/directory collision.");
        return paths;
    }

    private static void ValidateComponentCasing(IEnumerable<string> paths)
    {
        var spellings = new Dictionary<string, string>(Paths);
        foreach (string path in paths)
        {
            int end = 0;
            while (end < path.Length)
            {
                int separator = path.IndexOf('/', end);
                string prefix = separator < 0 ? path : path[..separator];
                if (spellings.TryGetValue(prefix, out string prior) && !string.Equals(prior, prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("Package path components have conflicting casing: " + prefix);
                spellings[prefix] = prefix;
                if (separator < 0) break;
                end = separator + 1;
            }
        }
    }

    private static void ValidateRelative(string relative, bool allowManifest = false)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 512 || relative.Contains('\\') ||
            Path.IsPathRooted(relative) || (!allowManifest && Paths.Equals(relative, ManifestName)))
            throw new InvalidDataException("Invalid package-relative path: " + relative);
        string[] components = relative.Split('/');
        foreach (string part in components)
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part != part.Trim() || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Any(char.IsControl) ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                ((stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 stem.Length == 4 && stem[3] >= '1' && stem[3] <= '9'))
                throw new InvalidDataException("Unsafe Windows package path: " + relative);
        }
        string first = components[0];
        if (new[] { "Profiles", "Logs", "Plugins", "portable-data", "Keys", "Updates", "Update Files" }.Contains(first, Paths) ||
            new[] { "DS4Windows.xml", "Profiles.xml", "Actions.xml", "AutoProfiles.xml", "Auto Profiles.xml",
                "LinkedProfiles.xml", "Linked Profiles.xml", "ControllerConfigs.xml", "settings.xml", "custom_exe_name.txt", "viiper.key.txt", "viiper.json" }.Contains(relative, Paths) ||
            new[] { ".key", ".pem", ".pfx", ".p12", ".log" }.Contains(Path.GetExtension(relative), Paths))
            throw new InvalidDataException("Package ownership cannot include user data: " + relative);
    }

    private static void ValidateTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 128 || tag != tag.Trim() || tag.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new InvalidDataException("The selected release tag is invalid.");
    }

    private static string NormalizeHash(string text)
    {
        string digest = text?.Trim();
        if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true) digest = digest[7..];
        if (digest?.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid SHA-256 digest.");
        return digest.ToUpperInvariant();
    }

    internal static string ParseChecksumSidecar(string text, string expectedFileName)
    {
        string line = text?.Trim();
        // Published packages use sha256sum's binary-file form. Keep accepting
        // historical bare digests, but never discard an arbitrary filename or
        // additional line when interpreting a named checksum record.
        if (line != null && line.Length > 66 && line[64] == ' ' && (line[65] == '*' || line[65] == ' '))
        {
            if (!string.Equals(line[66..], expectedFileName, StringComparison.Ordinal))
                throw new InvalidDataException("The checksum record names a different package file.");
            return NormalizeHash(line[..64]);
        }
        return NormalizeHash(line);
    }

    private static string ReadEntryText(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length > maximum) throw new InvalidDataException("Package metadata is oversized.");
        using Stream source = entry.Open();
        using var buffer = new MemoryStream();
        CopyBounded(source, buffer, entry.Length);
        return StrictUtf8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }

    private static string ReadBoundedText(string path, int maximum)
        => ReadBoundedTextSnapshot(path, maximum).Text;

    internal static (string Text, string Sha256) ReadBoundedTextSnapshot(string path, int maximum)
    {
        ValidateNoReparse(path);
        // Parse and fingerprint the exact same immutable read snapshot. Two
        // separate opens could otherwise bind one manifest's ownership list to
        // another manifest's hash when updates or local edits overlap.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > maximum) throw new InvalidDataException("Portable metadata is oversized.");
        using var buffer = new MemoryStream();
        CopyBounded(source, buffer, source.Length);
        byte[] bytes = buffer.ToArray();
        return (StrictUtf8.GetString(bytes).TrimStart('\uFEFF'), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string CopyBounded(Stream source, Stream destination, long expectedLength)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            copied = checked(copied + read);
            if (copied > expectedLength || copied > MaximumFileBytes) throw new InvalidDataException("Expanded file exceeds its declared length.");
            hash.AppendData(buffer, 0, read);
            destination.Write(buffer, 0, read);
        }
        if (copied != expectedLength) throw new InvalidDataException("The package entry is truncated.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void WriteTextNew(string path, string text)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(StrictUtf8.GetBytes(text));
        file.Flush(true);
    }

    private static string HashFile(string path)
    {
        ValidateNoReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HashStream(stream);
    }
    private static string HashStream(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    private static string Child(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (Paths.Equals(path, root) || !AtOrBelow(path, root)) throw new InvalidDataException("Path escaped its approved root.");
        return path;
    }
    private static bool AtOrBelow(string path, string root) => Paths.Equals(path, root) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateNoReparse(string path)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Portable update paths cannot traverse filesystem links: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private sealed class Change
    {
        internal readonly string Relative, Source, NewHash;
        internal bool Existed;
        internal string OriginalHash;
        internal FileAttributes Attributes;
        internal DateTime LastWriteUtc;
        internal Change(string relative, string source, string hash) { Relative = relative; Source = source; NewHash = hash; }
    }
}
