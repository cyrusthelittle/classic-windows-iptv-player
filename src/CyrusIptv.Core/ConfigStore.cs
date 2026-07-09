using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace CyrusIptv.Core;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _appFolder;
    private readonly string _statePath;
    private readonly string _channelCachePath;

    public ConfigStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _appFolder = Path.Combine(appData, "CyrusIptv");
        Directory.CreateDirectory(_appFolder);
        _statePath = Path.Combine(_appFolder, "state.json");
        _channelCachePath = Path.Combine(_appFolder, "channels.json.gz");
    }

    public AppState Load()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                var empty = new AppState();
                empty.EnsureAccounts();
                return empty;
            }

            var json = File.ReadAllText(_statePath);
            var state = JsonSerializer.Deserialize<AppState>(json, StateJsonOptions) ?? new AppState();
            state.CachedChannels = [];
            state.EnsureAccounts();
            return state;
        }
        catch
        {
            var state = new AppState();
            state.EnsureAccounts();
            return state;
        }
    }

    public void Save(AppState state)
    {
        state.EnsureAccounts();
        var tmpPath = _statePath + ".tmp";
        var json = JsonSerializer.Serialize(state, StateJsonOptions);
        File.WriteAllText(tmpPath, json);
        if (File.Exists(_statePath)) File.Delete(_statePath);
        File.Move(tmpPath, _statePath);
    }

    public bool HasChannelCache => File.Exists(_channelCachePath) && new FileInfo(_channelCachePath).Length > 0;
    public bool HasChannelCacheForAccount(string accountId)
    {
        var path = GetChannelCachePath(accountId);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    public List<Channel> LoadChannelCache()
    {
        try
        {
            if (!HasChannelCache) return [];
            using var file = File.OpenRead(_channelCachePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize(gzip, ChannelJsonContext.Default.ListChannel) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public List<Channel> LoadChannelCache(string accountId)
    {
        try
        {
            var path = GetChannelCachePath(accountId);
            if (!HasChannelCacheForAccount(accountId))
            {
                if (HasChannelCache) return LoadChannelCache();
                return [];
            }

            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize(gzip, ChannelJsonContext.Default.ListChannel) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveChannelCache(IReadOnlyList<Channel> channels)
    {
        Directory.CreateDirectory(_appFolder);
        var tmpPath = _channelCachePath + ".tmp";
        using (var file = File.Create(tmpPath))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(gzip, AsList(channels), ChannelJsonContext.Default.ListChannel);
        }

        if (File.Exists(_channelCachePath)) File.Delete(_channelCachePath);
        File.Move(tmpPath, _channelCachePath);
    }

    public void SaveChannelCache(string accountId, IReadOnlyList<Channel> channels)
    {
        Directory.CreateDirectory(_appFolder);
        var path = GetChannelCachePath(accountId);
        var tmpPath = path + ".tmp";
        using (var file = File.Create(tmpPath))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(gzip, AsList(channels), ChannelJsonContext.Default.ListChannel);
        }

        if (File.Exists(path)) File.Delete(path);
        File.Move(tmpPath, path);
    }

    private static List<Channel> AsList(IReadOnlyList<Channel> channels) => channels as List<Channel> ?? [.. channels];

    public void ClearChannelCache()
    {
        try
        {
            if (File.Exists(_channelCachePath)) File.Delete(_channelCachePath);
        }
        catch
        {
            // Ignore cache deletion issues.
        }
    }

    public void ClearChannelCache(string accountId)
    {
        try
        {
            var path = GetChannelCachePath(accountId);
            if (File.Exists(path)) File.Delete(path);

            // Also remove any leftover recent-channel cache file from older
            // versions that used to keep a separate fast-startup cache.
            var safeId = SanitizeFileName(string.IsNullOrWhiteSpace(accountId) ? "default" : accountId);
            var legacyRecentPath = Path.Combine(_appFolder, $"channels-{safeId}-recent.json.gz");
            if (File.Exists(legacyRecentPath)) File.Delete(legacyRecentPath);
        }
        catch
        {
            // Ignore cache deletion issues.
        }
    }

    public string GetChannelCachePath(string accountId)
    {
        var safeId = SanitizeFileName(string.IsNullOrWhiteSpace(accountId) ? "default" : accountId);
        return Path.Combine(_appFolder, $"channels-{safeId}.json.gz");
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }

    public string StatePath => _statePath;
    public string ChannelCachePath => _channelCachePath;
}
