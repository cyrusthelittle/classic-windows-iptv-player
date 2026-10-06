namespace ClassicWindowsIptvPlayer.Core;

/// <summary>Only missing settings permit an explicitly requested fresh start.</summary>
public sealed class MissingAccountSettingsException : IOException
{
    public string DataRoot { get; }
    private readonly string _recordingFolder;
    internal MissingAccountSettingsException(string dataRoot, string legacyRoot, string message) : base(message)
    {
        DataRoot = dataRoot;
        _recordingFolder = Path.Combine(legacyRoot, "Recordings");
    }

    /// <summary>Call only after the user explicitly chooses Start fresh. Existing data is retained.</summary>
    public void StartFresh()
    {
        AuthenticatedUpdate.RejectLinks(DataRoot);
        using var lease = AppDataLease.Acquire(DataRoot);
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(DataRoot.ToUpperInvariant())));
        using var gate = new Mutex(false, "Local\\CyrusIptvData-" + identity);
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Another app is preparing saved data. Try again after it finishes.");
        string? stage = null;
        try
        {
            var accounts = Path.Combine(DataRoot, "accounts");
            Directory.CreateDirectory(accounts);
            var primary = Path.Combine(accounts, "accounts.json");
            if (File.Exists(primary) || File.Exists(primary + ".bak"))
                throw new IOException("Account settings were restored. Reopen the app to use them; no files were replaced.");
            Directory.CreateDirectory(DataRoot);
            stage = Path.Combine(DataRoot, "temp", ".fresh-settings-" + Guid.NewGuid().ToString("N"));
            var store = new ConfigStore(stage);
            store.Save(new AppState { RecordingFolder = _recordingFolder });
            _ = store.Load();
            if (File.Exists(primary) || File.Exists(primary + ".bak"))
                throw new IOException("Account settings were restored. Reopen the app to use them; no files were replaced.");
            File.Move(store.StatePath, primary, overwrite: false);
        }
        finally
        {
            // This private stage contains only newly generated default settings.
            try
            {
                if (stage is not null && Directory.Exists(stage) &&
                    string.Equals(Path.GetDirectoryName(Path.GetFullPath(stage)), Path.Combine(Path.GetFullPath(DataRoot), "temp"), StringComparison.OrdinalIgnoreCase))
                {
                    AuthenticatedUpdate.RejectLinks(stage);
                    // The stage's sole directory is an empty cache created by ConfigStore.
                    var cache = Path.Combine(stage, "cache");
                    if (Directory.Exists(cache))
                    {
                        AuthenticatedUpdate.RejectLinks(cache);
                        Directory.Delete(cache, recursive: false);
                    }
                    foreach (var path in Directory.EnumerateFiles(stage))
                    {
                        AuthenticatedUpdate.RejectLinks(path);
                        File.Delete(path);
                    }
                    Directory.Delete(stage, recursive: false);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Retain only newly generated defaults if cleanup is blocked. */ }
            finally { gate.ReleaseMutex(); }
        }
    }
}
