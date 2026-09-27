namespace ClassicWindowsIptvPlayer.Core;

// An account manager session owns copies. Selection and field edits never mutate AppState.
public sealed class AccountEditingSession
{
    private readonly List<SavedAccount> _accounts;
    private readonly HashSet<string> _originalIds;

    public IReadOnlyList<SavedAccount> Accounts => _accounts;
    public string SelectedId { get; private set; }

    public AccountEditingSession(AppState state)
    {
        state.EnsureAccounts();
        _accounts = state.Accounts.Select(Clone).ToList();
        _originalIds = _accounts.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SelectedId = state.SelectedAccountId;
    }

    public SavedAccount Selected => _accounts.First(a => string.Equals(a.Id, SelectedId, StringComparison.OrdinalIgnoreCase));

    public void Select(string id)
    {
        if (_accounts.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))) SelectedId = id;
    }

    public SavedAccount Add()
    {
        var account = new SavedAccount { Name = "New account" };
        _accounts.Add(account);
        SelectedId = account.Id;
        return account;
    }

    public bool RemoveSelected()
    {
        if (_accounts.Count <= 1) return false;
        _accounts.RemoveAll(a => string.Equals(a.Id, SelectedId, StringComparison.OrdinalIgnoreCase));
        SelectedId = _accounts[0].Id;
        return true;
    }

    public void EditSelected(string name, AccountSettings settings)
    {
        var selected = Selected;
        selected.Name = name.Trim();
        selected.Settings = settings.Clone();
    }

    public AccountValidationIssue? Validate()
    {
        foreach (var account in _accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Name))
                return new(account.Id, "AccountName", "Enter an account name.");

            var settings = account.Settings;
            if (!string.IsNullOrWhiteSpace(settings.M3uUrl))
            {
                if (!IsHttpUrl(settings.M3uUrl))
                    return new(account.Id, "M3uUrl", "Enter a valid HTTP or HTTPS M3U playlist URL.");
            }
            else
            {
                if (!IsHttpUrl(settings.ServerUrl))
                    return new(account.Id, "ServerUrl", "Enter a valid HTTP or HTTPS server URL.");
                if (string.IsNullOrWhiteSpace(settings.Username))
                    return new(account.Id, "Username", "Enter a username.");
                if (string.IsNullOrWhiteSpace(settings.Password))
                    return new(account.Id, "Password", "Enter a password.");
            }

            if (!string.IsNullOrWhiteSpace(settings.EpgUrl) && !IsHttpUrl(settings.EpgUrl))
                return new(account.Id, "EpgUrl", "Enter a valid HTTP or HTTPS EPG URL, or leave it blank.");
        }
        return null;
    }

    public IReadOnlyList<string> ApplyTo(AppState state)
    {
        var issue = Validate();
        if (issue is not null) throw new InvalidOperationException(issue.Message);
        var removedIds = _originalIds.Except(_accounts.Select(a => a.Id), StringComparer.OrdinalIgnoreCase).ToArray();
        state.Accounts = _accounts.Select(Clone).ToList();
        // Saving account details does not select another account for the running player.
        if (!state.Accounts.Any(a => string.Equals(a.Id, state.SelectedAccountId, StringComparison.OrdinalIgnoreCase)))
            state.SelectedAccountId = SelectedId;
        state.Account = state.EnsureSelectedAccount().Settings.Clone();
        return removedIds;
    }

    private static bool IsHttpUrl(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("://", StringComparison.Ordinal)) return false;
            text = "http://" + text;
        }
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static SavedAccount Clone(SavedAccount source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Settings = source.Settings.Clone(),
        LastPlaylistUpdatedUtc = source.LastPlaylistUpdatedUtc
    };
}

public sealed record AccountValidationIssue(string AccountId, string Field, string Message);
