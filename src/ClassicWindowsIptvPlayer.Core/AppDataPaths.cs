using System.Security.Cryptography;
using System.Text;

namespace ClassicWindowsIptvPlayer.Core;

/// <summary>One pinned root per process, resolved before logging or settings are initialized.</summary>
public static class AppDataPaths
{
    public const string OverrideVariable = "CYRUS_IPTV_DATA_ROOT";
    public const string PortableMarker = "portable-mode.txt";
    private static readonly Lazy<string> PinnedRoot = new(() => Initialize(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable(OverrideVariable)));
    public static string Root => PinnedRoot.Value;
    public static string Cache => Path.Combine(Root, "cache");
    public static string Logs => Path.Combine(AppContext.BaseDirectory, "logs");
    public static string Updates => Path.Combine(Root, "updates");
    public static string? MigrationNotice => File.Exists(Path.Combine(Root, "data-migration.txt"))
        ? "Saved data was copied to the Windows user data folder. Original files and a recovery snapshot were retained. Recording files remain in their original folder." : null;

    // Explicit arguments allow fixtures to exercise migration without normal Windows-user data.
    public static string Initialize(string executableDirectory, string localAppData, string? explicitRoot = null)
    {
        var legacy = Path.GetFullPath(executableDirectory);
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            if (!Path.IsPathFullyQualified(explicitRoot)) throw new InvalidDataException("The data-root override must be an absolute path.");
            var requested = Path.GetFullPath(explicitRoot);
            Directory.CreateDirectory(requested);
            return requested; // Explicit roots do not import install data.
        }
        if (File.Exists(Path.Combine(legacy, PortableMarker))) return legacy;
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new InvalidDataException("The Windows user data folder is unavailable. Set an absolute data-root override or enable portable mode.");
        var root = Path.Combine(Path.GetFullPath(localAppData), "ClassicWindowsIptvPlayer");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        using var gate = new Mutex(false, "Local\\CyrusIptvData-" + identity);
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Another app is preparing saved data. Try again after it finishes.");
        try
        {
            if (File.Exists(Path.Combine(root, "accounts.json")) || File.Exists(Path.Combine(root, "accounts.json.bak"))) return root;
            // Never merge packaged accounts into an existing root, including one with damaged/missing settings.
            if (Directory.Exists(root) && !ContainsOnlyDiagnostics(root) && !File.Exists(Path.Combine(root, "first-run.pending")))
                throw new MissingAccountSettingsException(root, legacy, "The saved data folder contains files but has no account settings. Restore its settings backup, or choose Start fresh.");
            var candidates = Directory.EnumerateFiles(legacy, "accounts.json*").ToArray();
            var hasState = candidates.Any(p => Path.GetFileName(p) is "accounts.json" or "accounts.json.bak");
            var hasCache = Directory.Exists(Path.Combine(legacy, "cache")) && Directory.EnumerateFileSystemEntries(Path.Combine(legacy, "cache")).Any();
            if (!hasState && (candidates.Length > 0 || hasCache))
                throw new MissingAccountSettingsException(root, legacy, "Legacy saved data has no account settings. Restore accounts.json or its backup, or choose Start fresh.");
            if (!hasState) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "first-run.pending"), "format=1"); return root; }
            var stage = root + ".migration-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stage);
            try
            {
                var sourceFiles = CollectLegacyFiles(legacy).ToArray();
                var sourceRevisions = sourceFiles.ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
                foreach (var path in candidates) CopyFile(path, Path.Combine(stage, Path.GetFileName(path)));
                foreach (var name in new[] { "cache", "logs" })
                    if (Directory.Exists(Path.Combine(legacy, name))) CopyTree(Path.Combine(legacy, name), Path.Combine(stage, name));
                // Snapshot before Load: that method repairs backups, upgrades schemas and finalizes interrupted captures.
                var recovery = Path.Combine(stage, "pre-migration");
                Directory.CreateDirectory(recovery);
                foreach (var path in Directory.GetFiles(stage)) CopyFile(path, Path.Combine(recovery, Path.GetFileName(path)));
                foreach (var name in new[] { "cache", "logs" })
                    if (Directory.Exists(Path.Combine(stage, name))) CopyTree(Path.Combine(stage, name), Path.Combine(recovery, name));
                ConfigStore.ProtectRecoveryCopies(recovery);
                var store = new ConfigStore(stage);
                store.NormalizeMigrationRecordingPaths(legacy);
                var state = store.Load();
                store.ValidateMigrationCaches();
                var queue = Path.Combine(stage, "cache", "feedback-outbox.dat");
                if (File.Exists(queue))
                    FeedbackOutbox.ValidateMigrationStorage(queue);
                state.RecordingFolder = string.IsNullOrWhiteSpace(state.RecordingFolder)
                    ? Path.Combine(legacy, "Recordings") : Path.GetFullPath(state.RecordingFolder, legacy);
                store.Save(state);
                _ = store.Load();
                // Legacy caches and retained backups may still be plaintext after validation.
                // Protect every staged copy before exposing the committed user-data root.
                ConfigStore.ProtectRecoveryCopies(stage);
                var marker = Path.Combine(stage, "logs", "active-session.txt");
                if (File.Exists(marker))
                {
                    try
                    {
                        var oldSession = Path.GetFullPath(File.ReadAllText(marker).Trim());
                        var logs = Path.Combine(legacy, "logs") + Path.DirectorySeparatorChar;
                        if (!oldSession.StartsWith(logs, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                        File.WriteAllText(marker, Path.Combine(root, "logs", Path.GetRelativePath(Path.Combine(legacy, "logs"), oldSession)));
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or NotSupportedException)
                    { File.Delete(marker); } // Snapshot retains damaged marker; unrelated crash metadata cannot block accounts.
                }
                File.WriteAllText(Path.Combine(stage, "data-migration.txt"), "format=1\nsource=" + legacy + "\nrecordings-not-moved=true\n");
                if (!sourceFiles.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(CollectLegacyFiles(legacy)) || sourceRevisions.Any(pair => !File.Exists(pair.Key) || new FileInfo(pair.Key).Length != pair.Value.Item1 || File.GetLastWriteTimeUtc(pair.Key) != pair.Value.Item2))
                    throw new IOException("Legacy data changed during migration. Close the previous app and retry.");
                if (Directory.Exists(root))
                {
                    if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
                    else Directory.Move(root, root + ".first-run-" + Guid.NewGuid().ToString("N")); // Retain unsaved first-run diagnostics.
                }
                Directory.Move(stage, root); // All-or-nothing directory commit on this volume.
                return root;
            }
            catch (Exception ex)
            {
                // Stage and originals intentionally survive for diagnosis/retry; never fall back to empty accounts.
                throw new InvalidDataException("Saved-data migration failed. Original data and the staged recovery copy were retained. Restore a known good backup or retry; no accounts were replaced.", ex);
            }
        }
        finally { gate.ReleaseMutex(); }
    }

    private static bool ContainsOnlyDiagnostics(string root) => Directory.EnumerateFileSystemEntries(root).All(path =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
        (Directory.Exists(path) ? Path.GetFileName(path).Equals("logs", StringComparison.OrdinalIgnoreCase)
            : Path.GetFileName(path) is "startup-crash.log" or "runtime-crash.log"));

    private static IEnumerable<string> CollectLegacyFiles(string root)
    {
        foreach (var file in Directory.GetFiles(root, "accounts.json*")) yield return file;
        foreach (var name in new[] { "cache", "logs" })
            if (Directory.Exists(Path.Combine(root, name)))
                foreach (var file in CollectTreeFiles(Path.Combine(root, name))) yield return file;
    }
    private static IEnumerable<string> CollectTreeFiles(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked saved-data folders cannot be migrated automatically.");
        foreach (var file in Directory.GetFiles(root))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked saved-data files cannot be migrated automatically.");
            yield return file;
        }
        foreach (var folder in Directory.GetDirectories(root))
            foreach (var file in CollectTreeFiles(folder)) yield return file;
    }

    private static void CopyFile(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked saved-data files cannot be migrated automatically.");
        File.Copy(source, destination, overwrite: false);
        File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
    }
    private static void CopyTree(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked saved-data folders cannot be migrated automatically.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var folder in Directory.GetDirectories(source)) CopyTree(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }
}
