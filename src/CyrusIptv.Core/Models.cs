using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CyrusIptv.Core;

public sealed class AccountSettings
{
    public string ServerUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string PreferredPlayerPath { get; set; } = string.Empty;

    public AccountSettings Clone() => new()
    {
        ServerUrl = ServerUrl,
        Username = Username,
        Password = Password,
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
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = "Uncategorized";
    public string Logo { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string RawInfo { get; set; } = string.Empty;
    public MediaKind MediaKind { get; set; } = MediaKind.Live;

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
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public MediaKind MediaKind { get; set; } = MediaKind.Live;
    public DateTime PlayedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AppState
{
    public AccountSettings Account { get; set; } = new();
    public List<SavedAccount> Accounts { get; set; } = [];
    public string SelectedAccountId { get; set; } = string.Empty;
    public List<string> FavoriteIds { get; set; } = [];
    public List<RecentItem> Recent { get; set; } = [];

    // Stored in a separate compressed cache file. Keeping this out of state.json
    // prevents huge RAM spikes when saving favorites/recent items.
    [JsonIgnore]
    public List<Channel> CachedChannels { get; set; } = [];

    public DateTime? CachedAtUtc { get; set; }

    // Playback buffer in milliseconds. Higher values reduce lag/stutter on weak or unstable networks,
    // but channel switching will take a little longer.
    public int PlaybackBufferMs { get; set; } = 1000;

    // Saved player audio state. 100 is normal volume; LibVLC supports values above 100.
    public int VolumeLevel { get; set; } = 100;
    public bool Muted { get; set; } = false;

    // Local phone/browser remote control. Disabled by default for privacy/security.
    public bool RemoteControlEnabled { get; set; } = false;
    public int RemoteControlPort { get; set; } = 53177;

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
            Accounts.Add(new SavedAccount { Name = "New account" });
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
               !string.IsNullOrWhiteSpace(Account.Username) ||
               !string.IsNullOrWhiteSpace(Account.Password);
    }

    private static string BuildFallbackName(AccountSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Username)) return settings.Username.Trim();
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
