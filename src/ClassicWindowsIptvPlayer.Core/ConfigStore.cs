using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassicWindowsIptvPlayer.Core;

public sealed record PortableImportPreview(int AccountCount, int MatchingAccountCount,
    int FavoriteCount, int FavoriteFolderCount, int ChannelRuleCount, int GroupRuleCount,
    IReadOnlyList<PortableSourcePreview> Sources);
public sealed record PortableSourcePreview(string AccountId, int FavoriteCount, int FavoriteFolderCount,
    int ChannelRuleCount, int GroupRuleCount);

public sealed class ConfigStore
{
    private const string ProtectedHeader = "CIPTV2\n";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClassicWindowsIptvPlayer local data v2");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _appFolder;
    private readonly string _statePath;
    private readonly string _channelCachePath;

    public ConfigStore(string? dataDirectory = null)
    {
        var root = dataDirectory ?? AppContext.BaseDirectory;
        _appFolder = Path.Combine(root, "cache");
        _statePath = Path.Combine(root, "accounts.json");
        _channelCachePath = Path.Combine(_appFolder, "channels.json.gz");
        Directory.CreateDirectory(_appFolder);
    }

    public string? RecoveryNotice { get; private set; }
    public string StatePath => _statePath;
    public string ChannelCachePath => _channelCachePath;

    public AppState Load()
    {
        RecoveryNotice = null;
        if (!File.Exists(_statePath) && !File.Exists(_statePath + ".bak"))
        {
            var empty = new AppState();
            empty.EnsureAccounts();
            return empty;
        }
        var bytes = ReadRecoverable(_statePath, ValidateState);
        var state = JsonSerializer.Deserialize<AppState>(Unprotect(bytes), JsonOptions)
            ?? throw new InvalidDataException("Settings file is empty.");
        state.CachedChannels = [];
        state.EnsureAccounts();
        if (state.SchemaVersion > AppState.CurrentSchemaVersion)
            throw new InvalidDataException("Settings were written by a newer app version.");
        if (!Encoding.UTF8.GetString(bytes).StartsWith(ProtectedHeader, StringComparison.Ordinal) ||
            state.SchemaVersion < AppState.CurrentSchemaVersion || state.LegacyFavoriteIds is not null ||
            state.LegacyFavoriteFolders is not null || state.LegacyRecent is not null)
        {
            var legacyChannels = File.Exists(_channelCachePath) ? ReadChannelCache(_channelCachePath) : new List<Channel>();
            if (!File.Exists(_statePath + ".migration.bak")) File.WriteAllBytes(_statePath + ".migration.bak", Protect(bytes));
            state.MigrateLegacyCollections(legacyChannels);
            if (legacyChannels.Count > 0 && !HasChannelCacheForAccount(state.SelectedAccountId))
                SaveChannelCache(state.SelectedAccountId, legacyChannels);
            UpgradeLegacyCaches();
            File.WriteAllBytes(_statePath + ".bak", Protect(bytes));
            AtomicWrite(_statePath, Protect(JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions)), replaceBackup: false);
            RecoveryNotice = "Settings were upgraded. Original settings and cache were kept as .migration.bak files. Favorites that could not be matched remain saved for a later provider refresh.";
        }
        return state;
    }

    public void Save(AppState state)
    {
        state.EnsureAccounts();
        state.SchemaVersion = AppState.CurrentSchemaVersion;
        AtomicWrite(_statePath, Protect(JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions)));
    }

    public bool HasChannelCache => File.Exists(_channelCachePath) || File.Exists(_channelCachePath + ".bak");
    public bool HasChannelCacheForAccount(string accountId)
    {
        var path = GetChannelCachePath(accountId);
        return File.Exists(path) || File.Exists(path + ".bak");
    }

    public List<Channel> LoadChannelCache() => ReadChannelCache(_channelCachePath);
    public List<Channel> LoadChannelCache(string accountId) => ReadChannelCache(GetChannelCachePath(accountId));

    public EpgGuideSnapshot? LoadGuideCache(string accountId)
    {
        var path = GetGuideCachePath(accountId);
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return null;
        var bytes = ReadRecoverable(path, ValidateGuideCache);
        using var input = new MemoryStream(Unprotect(bytes));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<EpgGuideSnapshot>(gzip, JsonOptions);
    }

    public void SaveGuideCache(string accountId, EpgGuideSnapshot snapshot)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            JsonSerializer.Serialize(gzip, snapshot, JsonOptions);
        AtomicWrite(GetGuideCachePath(accountId), Protect(output.ToArray()));
    }

    private static bool ValidateGuideCache(byte[] bytes)
    {
        using var input = new MemoryStream(Unprotect(bytes));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var snapshot = JsonSerializer.Deserialize<EpgGuideSnapshot>(gzip, JsonOptions);
        return snapshot?.Programmes is not null && snapshot.Aliases is not null && snapshot.Channels is not null;
    }

    private List<Channel> ReadChannelCache(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return [];
        var bytes = ReadRecoverable(path, ValidateCache);
        using var input = new MemoryStream(Unprotect(bytes));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(gzip, ChannelJsonContext.Default.ListChannel) ?? [];
    }

    public void SaveChannelCache(IReadOnlyList<Channel> channels) => WriteChannelCache(_channelCachePath, channels);
    public void SaveChannelCache(string accountId, IReadOnlyList<Channel> channels) => WriteChannelCache(GetChannelCachePath(accountId), channels);

    private static void WriteChannelCache(string path, IReadOnlyList<Channel> channels, bool replaceBackup = true)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            JsonSerializer.Serialize(gzip, channels as List<Channel> ?? [.. channels], ChannelJsonContext.Default.ListChannel);
        AtomicWrite(path, Protect(output.ToArray()), replaceBackup);
    }

    private void UpgradeLegacyCaches()
    {
        foreach (var path in Directory.EnumerateFiles(_appFolder, "channels*.json.gz"))
        {
            var original = File.ReadAllBytes(path);
            if (Encoding.UTF8.GetString(original).StartsWith(ProtectedHeader, StringComparison.Ordinal)) continue;
            var channels = ReadChannelCache(path);
            if (!File.Exists(path + ".migration.bak")) File.WriteAllBytes(path + ".migration.bak", Protect(original));
            File.WriteAllBytes(path + ".bak", Protect(original));
            WriteChannelCache(path, channels, replaceBackup: false);
        }
    }

    // Explicit, secret-free organization export. Routine Save never writes plaintext.
    public void ExportPortable(string path, AppState state)
    {
        var export = new
        {
            Format = "cyrus-portable-organization-v1",
            Accounts = state.Accounts.Select(a => new { a.Id }).ToArray(),
            Libraries = state.AccountLibraries.Select(pair => new
            {
                AccountId = pair.Key,
                pair.Value.FavoriteIds,
                pair.Value.FavoriteFolders,
                pair.Value.ChannelOrganization,
                pair.Value.GroupOrganization
            }).ToArray()
        };
        AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions), replaceBackup: false);
    }

    public PortableImportPreview PreviewPortable(string path, AppState state)
    {
        var import = ReadPortable(path);
        var known = state.Accounts.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = import.Libraries.Where(l => known.Contains(l.AccountId)).ToArray();
        return new PortableImportPreview(import.Libraries.Count, matched.Length,
            matched.Sum(l => l.FavoriteIds.Count), matched.Sum(l => l.FavoriteFolders.Count),
            matched.Sum(l => l.ChannelOrganization.Count), matched.Sum(l => l.GroupOrganization.Count),
            import.Libraries.Select(l => new PortableSourcePreview(l.AccountId, l.FavoriteIds.Count,
                l.FavoriteFolders.Count, l.ChannelOrganization.Count, l.GroupOrganization.Count)).ToArray());
    }

    public void ImportPortable(string path, AppState state)
    {
        var import = ReadPortable(path);
        var known = state.Accounts.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in import.Libraries.Where(l => known.Contains(l.AccountId)))
        {
            if (!state.AccountLibraries.TryGetValue(item.AccountId, out var library))
                state.AccountLibraries[item.AccountId] = library = new AccountLibraryState();
            library.FavoriteIds = item.FavoriteIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            library.FavoriteFolders = item.FavoriteFolders;
            library.ChannelOrganization = item.ChannelOrganization;
            library.GroupOrganization = item.GroupOrganization;
            // Recent playback and viewing progress are intentionally left intact.
        }
        Save(state);
    }

    public void ImportPortableIntoAccount(string path, AppState state, string sourceAccountId, string destinationAccountId)
    {
        if (!state.Accounts.Any(a => string.Equals(a.Id, destinationAccountId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Destination account does not exist.");
        var source = ReadPortable(path).Libraries.SingleOrDefault(l => string.Equals(l.AccountId, sourceAccountId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Source account was not found in the export.");
        if (!state.AccountLibraries.TryGetValue(destinationAccountId, out var library))
            state.AccountLibraries[destinationAccountId] = library = new AccountLibraryState();
        library.FavoriteIds = source.FavoriteIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        library.FavoriteFolders = source.FavoriteFolders;
        library.ChannelOrganization = source.ChannelOrganization;
        library.GroupOrganization = source.GroupOrganization;
        Save(state);
    }

    private static PortableOrganization ReadPortable(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Organization file is too large.");
        var import = JsonSerializer.Deserialize<PortableOrganization>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Organization file is empty.");
        if (import.Format != "cyrus-portable-organization-v1" || import.Libraries is null ||
            import.Libraries.Any(l => string.IsNullOrWhiteSpace(l.AccountId) || l.FavoriteIds is null ||
                l.FavoriteFolders is null || l.ChannelOrganization is null || l.GroupOrganization is null) ||
            import.Libraries.Select(l => l.AccountId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != import.Libraries.Count)
            throw new InvalidDataException("Unsupported or invalid organization file.");
        return import;
    }

    public void CreateBackup(string path)
    {
        // The encrypted local files remain encrypted in this archive. Windows user
        // protection means restoration requires the same Windows user profile.
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        if (File.Exists(_statePath)) archive.CreateEntryFromFile(_statePath, "accounts.json");
        foreach (var source in Directory.EnumerateFiles(_appFolder, "*.json.gz"))
            archive.CreateEntryFromFile(source, "cache/" + Path.GetFileName(source));
    }

    public void RestoreBackup(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(e => e.Name.Length > 0).ToArray();
        if (entries.Length == 0 || entries.Length > 1000 || entries.All(e => e.FullName != "accounts.json"))
            throw new InvalidDataException("Backup is missing settings or has too many entries.");
        var staged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Length > 256 * 1024 * 1024 ||
                !(entry.FullName == "accounts.json" ||
                  (entry.FullName.StartsWith("cache/", StringComparison.Ordinal) &&
                   entry.FullName.EndsWith(".json.gz", StringComparison.Ordinal) &&
                   !entry.FullName[6..].Contains('/'))))
                throw new InvalidDataException("Backup contains an invalid entry.");
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            staged.Add(entry.FullName, memory.ToArray());
        }
        if (!ValidateState(staged["accounts.json"])) throw new InvalidDataException("Backup settings are invalid or belong to another Windows user.");
        foreach (var item in staged.Where(e => e.Key != "accounts.json"))
        {
            if (item.Key.StartsWith("cache/channels-", StringComparison.Ordinal) || item.Key == "cache/channels.json.gz")
            {
                if (!ValidateCache(item.Value)) throw new InvalidDataException("Backup channel cache is invalid.");
            }
            else if (item.Key.StartsWith("cache/guide-", StringComparison.Ordinal))
            {
                if (!ValidateGuideCache(item.Value)) throw new InvalidDataException("Backup guide cache is invalid.");
            }
            else throw new InvalidDataException("Backup contains an unknown cache file.");
        }
        // All entries are validated before any current data is replaced. AtomicWrite
        // retains each prior file as a .bak recovery copy.
        AtomicWrite(_statePath, staged["accounts.json"]);
        foreach (var item in staged.Where(e => e.Key != "accounts.json"))
            AtomicWrite(Path.Combine(_appFolder, Path.GetFileName(item.Key)), item.Value);
    }

    private sealed class PortableOrganization
    {
        public string Format { get; set; } = string.Empty;
        public List<PortableLibrary> Libraries { get; set; } = [];
    }

    private sealed class PortableLibrary
    {
        public string AccountId { get; set; } = string.Empty;
        public List<string> FavoriteIds { get; set; } = [];
        public List<FavoriteFolder> FavoriteFolders { get; set; } = [];
        public Dictionary<string, ChannelOrganization> ChannelOrganization { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, GroupOrganization> GroupOrganization { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static bool ValidateState(byte[] bytes)
    {
        var plaintext = Unprotect(bytes);
        using var document = JsonDocument.Parse(plaintext);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            (!document.RootElement.TryGetProperty("Accounts", out _) && !document.RootElement.TryGetProperty("Account", out _)))
            return false;
        var state = JsonSerializer.Deserialize<AppState>(plaintext, JsonOptions);
        return state?.Accounts is not null && state.Account is not null && state.AccountLibraries is not null &&
            state.Accounts.All(account => account is not null);
    }

    private static bool ValidateCache(byte[] bytes)
    {
        using var input = new MemoryStream(Unprotect(bytes));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(gzip, ChannelJsonContext.Default.ListChannel) is not null;
    }

    private byte[] ReadRecoverable(string path, Func<byte[], bool> validate)
    {
        try
        {
            var primary = File.ReadAllBytes(path);
            if (validate(primary)) return primary;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or InvalidDataException or UnauthorizedAccessException or FormatException) { }
        try
        {
            var backup = File.ReadAllBytes(path + ".bak");
            if (!validate(backup)) throw new InvalidDataException("Backup validation failed.");
            var temp = path + ".recovery.tmp";
            File.WriteAllBytes(temp, backup);
            File.Move(temp, path, true);
            RecoveryNotice = $"Recovered {Path.GetFileName(path)} from its last good backup. The damaged file was replaced.";
            return backup;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} and its backup could not be read. Existing files were left in place. Restore a known good copy or use a new data directory.", ex);
        }
    }

    private static void AtomicWrite(string path, byte[] bytes, bool replaceBackup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path) && replaceBackup) File.Replace(temp, path, path + ".bak", true);
        else if (File.Exists(path)) File.Move(temp, path, true);
        else File.Move(temp, path);
    }

    private static byte[] Protect(byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected local storage requires Windows.");
        var protectedBytes = WindowsDataProtection.Protect(bytes, Entropy);
        return Encoding.UTF8.GetBytes(ProtectedHeader + Convert.ToBase64String(protectedBytes));
    }

    private static byte[] Unprotect(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (!text.StartsWith(ProtectedHeader, StringComparison.Ordinal)) return bytes;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected local storage requires Windows.");
        return WindowsDataProtection.Unprotect(Convert.FromBase64String(text[ProtectedHeader.Length..]), Entropy);
    }

    public void ClearChannelCache() => DeleteCache(_channelCachePath);
    public void ClearChannelCache(string accountId) => DeleteCache(GetChannelCachePath(accountId));
    private static void DeleteCache(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
    }

    public string GetChannelCachePath(string accountId)
    {
        var safeId = string.IsNullOrWhiteSpace(accountId) ? "default" : accountId;
        foreach (var invalid in Path.GetInvalidFileNameChars()) safeId = safeId.Replace(invalid, '_');
        return Path.Combine(_appFolder, $"channels-{safeId}.json.gz");
    }

    public string GetGuideCachePath(string accountId) =>
        Path.Combine(_appFolder, "guide-" + SanitizeAccountId(accountId) + ".json.gz");

    private static string SanitizeAccountId(string accountId)
    {
        var safe = string.IsNullOrWhiteSpace(accountId) ? "default" : accountId;
        foreach (var invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '_');
        return safe;
    }
}
