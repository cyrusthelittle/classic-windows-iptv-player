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
    public static string Recordings => Path.Combine(AppContext.BaseDirectory, "Recordings");
    public static string Updates => Path.Combine(Root, "updates");
    public static string Temp => Path.Combine(Root, "temp");
    public static string Accounts => Path.Combine(Root, "accounts");
    public static string Playlists => Path.Combine(Root, "playlists");
    public static string? MigrationNotice => null;

    // Explicit arguments allow fixtures to exercise migration without normal Windows-user data.
    public static string Initialize(string executableDirectory, string localAppData, string? explicitRoot = null)
    {
        var executable = Path.GetFullPath(executableDirectory);
        if (explicitRoot is null && (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData)))
            throw new InvalidDataException("The Windows user data folder is unavailable. Set an absolute data-root override.");
        var selectedRoot = explicitRoot ?? Path.Combine(localAppData, "ClassicWindowsIptvPlayer");
        if (!Path.IsPathFullyQualified(selectedRoot)) throw new InvalidDataException("The data root must be absolute.");
        var root = Path.GetFullPath(selectedRoot);
        Directory.CreateDirectory(root);
        using var gate = new Mutex(false, "Local\\CyrusIptvData-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))));
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Another app is preparing portable data. Try again after it finishes.");
        try
        {
            var accounts = Path.Combine(root, "accounts");
            Directory.CreateDirectory(accounts);
            if (explicitRoot is null && !File.Exists(Path.Combine(accounts, "accounts.json")) && !File.Exists(Path.Combine(accounts, "accounts.json.bak")))
            {
                var executableAccounts = Path.Combine(executable, "accounts");
                var hasExecutableAccounts = File.Exists(Path.Combine(executable, "accounts.json")) || File.Exists(Path.Combine(executable, "accounts.json.bak")) ||
                    File.Exists(Path.Combine(executableAccounts, "accounts.json")) || File.Exists(Path.Combine(executableAccounts, "accounts.json.bak"));
                var hasFlatUserAccounts = File.Exists(Path.Combine(root, "accounts.json")) || File.Exists(Path.Combine(root, "accounts.json.bak"));
                var source = hasFlatUserAccounts ? root : hasExecutableAccounts ? executable : null;
                if (source is not null && Directory.Exists(source))
                {
                    using var sourceLease = AppDataLease.Acquire(source);
                    var stage = Path.Combine(root, "temp", "data-import-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stage);
                    var nestedAccounts = Path.Combine(source, "accounts");
                    var sourceAccounts = File.Exists(Path.Combine(nestedAccounts, "accounts.json")) ||
                        File.Exists(Path.Combine(nestedAccounts, "accounts.json.bak")) ? nestedAccounts : source;
                    Directory.CreateDirectory(Path.Combine(stage, "accounts"));
                    foreach (var file in Directory.GetFiles(sourceAccounts, "accounts.json*")) CopyFile(file, Path.Combine(stage, "accounts", Path.GetFileName(file)));
                    if (!source.Equals(root, StringComparison.OrdinalIgnoreCase))
                        foreach (var name in new[] { "cache", "playlists" })
                            if (Directory.Exists(Path.Combine(source, name))) CopyTree(Path.Combine(source, name), Path.Combine(stage, name));
                    var store = new ConfigStore(stage);
                    _ = store.Load();
                    store.ValidateMigrationCaches();
                    ConfigStore.ProtectRecoveryCopies(stage);
                    foreach (var name in new[] { "cache", "playlists" })
                        if (Directory.Exists(Path.Combine(stage, name))) CopyTree(Path.Combine(stage, name), Path.Combine(root, name));
                    // Publish the whole accounts directory only after all other files succeed.
                    var stagedAccounts = Path.Combine(stage, "accounts");
                    if (Directory.EnumerateFileSystemEntries(accounts).Any()) throw new IOException("Portable accounts folder contains incomplete settings. Recovery files were retained.");
                    Directory.Delete(accounts);
                    Directory.Move(stagedAccounts, accounts);
                    Directory.Delete(stage, true);
                }
            }

            foreach (var name in new[] { "cache", "updates", "playlists", "temp" }) Directory.CreateDirectory(Path.Combine(root, name));
            Directory.CreateDirectory(Logs);
            Directory.CreateDirectory(Recordings);
            return root;
        }
        finally { gate.ReleaseMutex(); }
    }

    private static bool ContainsOnlyDiagnostics(string root) => Directory.EnumerateFileSystemEntries(root).All(path =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
        (Directory.Exists(path) ? Path.GetFileName(path).Equals("logs", StringComparison.OrdinalIgnoreCase)
            : Path.GetFileName(path) is "startup-crash.log" or "runtime-crash.log"));

    private static void CopyFile(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked saved-data files cannot be migrated automatically.");
        if (File.Exists(destination))
        {
            using var original = File.OpenRead(source);
            using var existing = File.OpenRead(destination);
            if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(existing)))
                throw new IOException("Saved data conflicts with an existing portable file; both copies were retained.");
            return;
        }
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
