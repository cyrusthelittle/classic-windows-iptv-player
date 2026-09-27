using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ClassicWindowsIptvPlayer.Core;

// Source-generated (de)serialization for the channel cache avoids reflection-based
// metadata resolution, which is measurably slower once a playlist reaches the tens
// or hundreds of thousands of channels that large IPTV providers hand out.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<Channel>))]
public sealed partial class ChannelJsonContext : JsonSerializerContext
{
}

public sealed class AccountSettings
{
    public string ServerUrl { get; set; } = string.Empty;
    public string M3uUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string EpgUrl { get; set; } = string.Empty;
    public string PreferredPlayerPath { get; set; } = string.Empty;

    public AccountSettings Clone() => new()
    {
        ServerUrl = ServerUrl,
        M3uUrl = M3uUrl,
        Username = Username,
        Password = Password,
        EpgUrl = EpgUrl,
        PreferredPlayerPath = PreferredPlayerPath
    };
}

public sealed class SavedAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public AccountSettings Settings { get; set; } = new();
    public DateTime? LastPlaylistUpdatedUtc { get; set; }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
            if (!string.IsNullOrWhiteSpace(Settings.Username)) return Settings.Username.Trim();
            if (!string.IsNullOrWhiteSpace(Settings.ServerUrl)) return Settings.ServerUrl.Trim();
            return "New account";
        }
    }
}

public enum MediaKind
{
    Live = 0,
    Movie = 1,
    Series = 2
}

public sealed class Channel
{
    [JsonIgnore] public string? IdentityName { get; set; }
    [JsonIgnore] public string? IdentityGroup { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = "Uncategorized";
    public string Logo { get; set; } = string.Empty;
    public string EpgId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string RawInfo { get; set; } = string.Empty;
    public MediaKind MediaKind { get; set; } = MediaKind.Live;
    public string SeriesId { get; set; } = string.Empty;
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int? DurationMinutes { get; set; }
    public DateTimeOffset? AddedAt { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Group) ? Name : $"{Name}  •  {Group}";
}

public sealed class AccountProfile
{
    public string Username { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ExpiryDateText { get; set; } = string.Empty;
    public string CreatedAtText { get; set; } = string.Empty;
    public string IsTrial { get; set; } = string.Empty;
    public string ActiveConnections { get; set; } = string.Empty;
    public string MaxConnections { get; set; } = string.Empty;
    public string ServerTime { get; set; } = string.Empty;
    public string Timezone { get; set; } = string.Empty;
}

public sealed class RecentItem
{
    public string ChannelId { get; set; } = string.Empty;
    public string ItemKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public MediaKind MediaKind { get; set; } = MediaKind.Live;
    public DateTime PlayedAtUtc { get; set; } = DateTime.UtcNow;
    public string SeriesId { get; set; } = string.Empty;
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
}

public sealed class ViewingProgress
{
    public string ItemKey { get; set; } = string.Empty;
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public bool Watched { get; set; }
    public bool Dismissed { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class AccountLibraryState
{
    public List<string> FavoriteIds { get; set; } = [];
    public List<FavoriteFolder> FavoriteFolders { get; set; } = [];
    public List<RecentItem> Recent { get; set; } = [];
    public Dictionary<string, ViewingProgress> ViewingProgress { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> GuideMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int GuideOffsetMinutes { get; set; }
    public Dictionary<string, ChannelOrganization> ChannelOrganization { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GroupOrganization> GroupOrganization { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ChannelOrganization
{
    public string Name { get; set; } = string.Empty;
    public bool Hidden { get; set; }
    public int Order { get; set; }
}

public sealed class GroupOrganization
{
    public string Name { get; set; } = string.Empty;
    public bool Hidden { get; set; }
    public int Order { get; set; }
}

public sealed class FavoriteFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public List<string> ChannelIds { get; set; } = [];
}

public sealed class AppState
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    private const string Free1PlaylistUrl = "https://iptv-org.github.io/iptv/index.country.m3u";
    private const string Free2PlaylistUrl = "https://bestiptv.hacks.tools/api/download?type=all&slug=index";

    public AccountSettings Account { get; set; } = new();
    public List<SavedAccount> Accounts { get; set; } = [];
    public string SelectedAccountId { get; set; } = string.Empty;
    public Dictionary<string, AccountLibraryState> AccountLibraries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore]
    public List<string> FavoriteIds => SelectedLibrary.FavoriteIds;
    [JsonIgnore]
    public List<FavoriteFolder> FavoriteFolders => SelectedLibrary.FavoriteFolders;
    [JsonIgnore]
    public List<RecentItem> Recent => SelectedLibrary.Recent;

    // Read old global collections once; never write them into the new format.
    [JsonPropertyName("FavoriteIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyFavoriteIds { get; set; }
    [JsonPropertyName("FavoriteFolders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FavoriteFolder>? LegacyFavoriteFolders { get; set; }
    [JsonPropertyName("Recent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RecentItem>? LegacyRecent { get; set; }

    [JsonIgnore]
    public AccountLibraryState SelectedLibrary
    {
        get
        {
            var id = string.IsNullOrWhiteSpace(SelectedAccountId) ? "unassigned" : SelectedAccountId;
            AccountLibraries ??= new(StringComparer.OrdinalIgnoreCase);
            if (!AccountLibraries.TryGetValue(id, out var library))
                AccountLibraries[id] = library = new AccountLibraryState();
            return library;
        }
    }

    public bool MigrateLegacyCollections(IReadOnlyList<Channel> cachedChannels)
    {
        if (SchemaVersion > CurrentSchemaVersion)
            throw new InvalidOperationException("Settings were written by a newer app version.");
        if (SchemaVersion == CurrentSchemaVersion && LegacyFavoriteIds is null && LegacyFavoriteFolders is null && LegacyRecent is null)
            return false;
        EnsureAccounts();
        var library = SelectedLibrary;
        var oldToNew = cachedChannels.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => ItemIdentity.For(g.First()), StringComparer.OrdinalIgnoreCase);
        string Map(string id) => oldToNew.TryGetValue(id, out var key) ? key : id;
        foreach (var id in LegacyFavoriteIds ?? [])
            if (!library.FavoriteIds.Contains(Map(id), StringComparer.OrdinalIgnoreCase)) library.FavoriteIds.Add(Map(id));
        foreach (var folder in LegacyFavoriteFolders ?? [])
        {
            folder.ChannelIds = folder.ChannelIds.Select(Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            library.FavoriteFolders.Add(folder);
        }
        foreach (var item in LegacyRecent ?? [])
        {
            item.ItemKey = oldToNew.TryGetValue(item.ChannelId, out var key) ? key : ItemIdentity.For(item);
            library.Recent.Add(item);
        }
        LegacyFavoriteIds = null;
        LegacyFavoriteFolders = null;
        LegacyRecent = null;
        SchemaVersion = CurrentSchemaVersion;
        return true;
    }

    // Stored in a separate compressed cache file. Keeping this out of state.json
    // prevents huge RAM spikes when saving favorites/recent items.
    [JsonIgnore]
    public List<Channel> CachedChannels { get; set; } = [];

    public DateTime? CachedAtUtc { get; set; }

    // Playback buffer in milliseconds. Higher values reduce lag/stutter on weak or unstable networks,
    // but channel switching will take a little longer.
    public int PlaybackBufferMs { get; set; } = 1000;

    // How many times a stream open is attempted before giving up. Providers that
    // refuse the first request(s) after a channel change need a generous budget.
    public int ReconnectAttempts { get; set; } = 10;

    // Saved player audio state. 100 is normal volume; LibVLC supports values above 100.
    public int VolumeLevel { get; set; } = 100;
    public bool Muted { get; set; } = false;

    // Blank leaves LibVLC's stream default in place. Names/codes are matched only
    // when a decoded track advertises a recognizable language.
    public string PreferredAudioLanguage { get; set; } = string.Empty;
    public string PreferredSubtitleLanguage { get; set; } = string.Empty;

    // Shared transparency for the auto-hide controls panel and volume OSD.
    // Range: 0.0 (fully transparent) .. 1.0 (fully opaque)
    public double OsdOpacity { get; set; } = 0.7;

    // UI theme. Light stays the default so existing installs keep their look.
    public bool DarkMode { get; set; } = false;

    // EPG is opt-in because some providers expose very large or unreliable XMLTV feeds.
    public bool EpgEnabled { get; set; } = false;

    // Update checks are enabled by default. Users can turn them off from the
    // update prompt or Settings, while Help > Check for updates remains available.
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    // Tracks one-time additions to the built-in account list. This lets existing
    // installations receive new free accounts without restoring ones they remove.
    public int BuiltInAccountsVersion { get; set; }

    public SavedAccount EnsureSelectedAccount()
    {
        EnsureAccounts();
        var selected = Accounts.FirstOrDefault(a => string.Equals(a.Id, SelectedAccountId, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) return selected;

        selected = Accounts[0];
        SelectedAccountId = selected.Id;
        Account = selected.Settings.Clone();
        return selected;
    }

    public void EnsureAccounts()
    {
        foreach (var account in Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id)) account.Id = Guid.NewGuid().ToString("N");
            account.Settings ??= new AccountSettings();
        }

        if (Accounts.Count == 0 && HasLegacyAccount())
        {
            Accounts.Add(new SavedAccount
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(Account.Username) ? "Default account" : Account.Username.Trim(),
                Settings = Account.Clone(),
                LastPlaylistUpdatedUtc = CachedAtUtc
            });
        }

        if (Accounts.Count == 0)
        {
            Accounts.Add(new SavedAccount
            {
                Name = "Free Account 1",
                Settings = new AccountSettings
                {
                    M3uUrl = Free1PlaylistUrl
                }
            });
        }

        if (BuiltInAccountsVersion < 1)
        {
            if (!Accounts.Any(account => string.Equals(
                    account.Settings.M3uUrl,
                    Free2PlaylistUrl,
                    StringComparison.OrdinalIgnoreCase)))
            {
                Accounts.Add(new SavedAccount
                {
                    Name = "Free Account 2",
                    Settings = new AccountSettings { M3uUrl = Free2PlaylistUrl }
                });
            }

            BuiltInAccountsVersion = 1;
        }

        if (BuiltInAccountsVersion < 2)
        {
            foreach (var account in Accounts)
            {
                if (string.Equals(account.Settings.M3uUrl, Free1PlaylistUrl, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(account.Name, "Free account", StringComparison.OrdinalIgnoreCase))
                {
                    account.Name = "Free Account 1";
                }
                else if (string.Equals(account.Settings.M3uUrl, Free2PlaylistUrl, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(account.Name, "Free 2", StringComparison.OrdinalIgnoreCase))
                {
                    account.Name = "Free Account 2";
                }
            }

            BuiltInAccountsVersion = 2;
        }

        if (string.IsNullOrWhiteSpace(SelectedAccountId) ||
            !Accounts.Any(a => string.Equals(a.Id, SelectedAccountId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedAccountId = Accounts[0].Id;
        }

        var selected = Accounts.FirstOrDefault(a => string.Equals(a.Id, SelectedAccountId, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) Account = selected.Settings.Clone();
    }

    public SavedAccount UpsertSelectedAccount(string name, AccountSettings settings)
    {
        EnsureAccounts();
        var account = EnsureSelectedAccount();
        account.Name = string.IsNullOrWhiteSpace(name) ? BuildFallbackName(settings) : name.Trim();
        account.Settings = settings.Clone();
        SelectedAccountId = account.Id;
        Account = account.Settings.Clone();
        return account;
    }

    public SavedAccount AddAccount()
    {
        var account = new SavedAccount { Name = "New account" };
        Accounts.Add(account);
        SelectedAccountId = account.Id;
        Account = account.Settings.Clone();
        return account;
    }

    public void RemoveAccount(string accountId)
    {
        if (Accounts.Count <= 1) return;
        Accounts.RemoveAll(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (!Accounts.Any(a => string.Equals(a.Id, SelectedAccountId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedAccountId = Accounts[0].Id;
        }

        Account = EnsureSelectedAccount().Settings.Clone();
    }

    public void MarkSelectedPlaylistUpdated(DateTime updatedUtc)
    {
        var account = EnsureSelectedAccount();
        account.LastPlaylistUpdatedUtc = updatedUtc;
        CachedAtUtc = updatedUtc;
        Account = account.Settings.Clone();
    }

    private bool HasLegacyAccount()
    {
        return !string.IsNullOrWhiteSpace(Account.ServerUrl) ||
               !string.IsNullOrWhiteSpace(Account.M3uUrl) ||
               !string.IsNullOrWhiteSpace(Account.Username) ||
               !string.IsNullOrWhiteSpace(Account.Password);
    }

    private static string BuildFallbackName(AccountSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Username)) return settings.Username.Trim();
        if (!string.IsNullOrWhiteSpace(settings.M3uUrl)) return "M3U playlist";
        if (!string.IsNullOrWhiteSpace(settings.ServerUrl)) return settings.ServerUrl.Trim();
        return "New account";
    }
}

public enum ChannelViewMode
{
    All,
    Favorites,
    Recent
}

// A Series-kind Channel is either a placeholder standing in for the whole show
// (Url = "series:{seriesId}", not directly playable) or a real episode once
// PlaylistService.FetchSeriesEpisodesAsync has resolved it to a playable stream URL.
public static class SeriesPlaceholder
{
    private const string Scheme = "series:";

    public static string BuildUrl(string seriesId) => Scheme + seriesId;

    public static bool TryGetSeriesId(Channel channel, out string seriesId)
    {
        seriesId = string.Empty;
        if (channel.MediaKind != MediaKind.Series) return false;

        var url = channel.Url ?? string.Empty;
        if (!url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;

        seriesId = url[Scheme.Length..];
        return !string.IsNullOrWhiteSpace(seriesId);
    }
}
