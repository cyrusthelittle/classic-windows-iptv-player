using CyrusIptv.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CyrusIptv.Windows;

public sealed class LoginResult
{
    public required AccountSettings Account { get; init; }
    public required string AccountId { get; init; }
    public bool UpdatePlaylist { get; init; }
}

public partial class LoginWindow : Window
{
    private readonly ConfigStore _store = new();
    private readonly AppState _state;
    private bool _loadingAccount;

    public LoginResult LoginResult { get; private set; } = new() { Account = new AccountSettings(), AccountId = string.Empty, UpdatePlaylist = true };

    public LoginWindow()
    {
        InitializeComponent();
        _state = _store.Load();
        _state.EnsureAccounts();
        AppLogger.Info("Login window opened. accounts=" + _state.Accounts.Count + "; selectedAccountId=" + _state.SelectedAccountId);
        RefreshAccountsList();
        SelectAccount(_state.SelectedAccountId);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        var selected = SaveSelectedAccount();
        if (selected is null) return;

        if (string.IsNullOrWhiteSpace(selected.Settings.ServerUrl) &&
            string.IsNullOrWhiteSpace(selected.Settings.M3uUrl))
        {
            StatusText.Text = "Enter a server URL or an M3U playlist link.";
            return;
        }

        var hasCache = _store.HasChannelCacheForAccount(selected.Id);
        var mustUpdate = selected.LastPlaylistUpdatedUtc is null || !hasCache;
        _store.Save(_state);

        LoginResult = new LoginResult
        {
            Account = selected.Settings.Clone(),
            AccountId = selected.Id,
            UpdatePlaylist = mustUpdate || UpdatePlaylistCheck.IsChecked == true
        };
        AppLogger.Info("Login continue. accountId=" + selected.Id + "; updatePlaylist=" + LoginResult.UpdatePlaylist + "; hasCache=" + hasCache);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("Login cancelled.");
        DialogResult = false;
    }

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAccount) return;
        if (AccountsList.SelectedItem is not AccountListItem item) return;
        _state.SelectedAccountId = item.Id;
        _state.Account = _state.EnsureSelectedAccount().Settings.Clone();
        _store.Save(_state);
        LoadSelectedAccountIntoFields();
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var account = _state.AddAccount();
        _store.Save(_state);
        RefreshAccountsList();
        SelectAccount(account.Id);
        AppLogger.Info("Account added. accountId=" + account.Id);
        StatusText.Text = "New account added. Enter its details, then save or continue.";
    }

    private void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountListItem item) return;
        if (_state.Accounts.Count <= 1)
        {
            StatusText.Text = "At least one account is required.";
            return;
        }

        _state.RemoveAccount(item.Id);
        _store.ClearChannelCache(item.Id);
        _store.Save(_state);
        AppLogger.Info("Account removed. accountId=" + item.Id);
        RefreshAccountsList();
        SelectAccount(_state.SelectedAccountId);
        StatusText.Text = "Account removed.";
    }

    private void SaveAccount_Click(object sender, RoutedEventArgs e)
    {
        var account = SaveSelectedAccount();
        if (account is null) return;
        RefreshAccountsList();
        SelectAccount(account.Id);
        AppLogger.Info("Account saved. accountId=" + account.Id);
        StatusText.Text = "Account saved.";
    }

    private SavedAccount? SaveSelectedAccount()
    {
        if (_loadingAccount) return _state.EnsureSelectedAccount();
        var selected = _state.EnsureSelectedAccount();
        var useM3u = AccountTypeBox.SelectedIndex == 1;
        var settings = new AccountSettings
        {
            ServerUrl = useM3u ? string.Empty : ServerUrlBox.Text.Trim(),
            M3uUrl = useM3u ? M3uUrlBox.Text.Trim() : string.Empty,
            Username = useM3u ? string.Empty : UsernameBox.Text.Trim(),
            Password = useM3u ? string.Empty : PasswordBox.Password.Trim(),
            EpgUrl = EpgUrlBox.Text.Trim(),
            PreferredPlayerPath = selected.Settings.PreferredPlayerPath
        };

        selected = _state.UpsertSelectedAccount(AccountNameBox.Text, settings);
        _store.Save(_state);
        return selected;
    }

    private void RefreshAccountsList()
    {
        _loadingAccount = true;
        try
        {
            var items = _state.Accounts.Select(AccountListItem.FromAccount).ToList();
            AccountsList.ItemsSource = items;
        }
        finally
        {
            _loadingAccount = false;
        }
    }

    private void SelectAccount(string accountId)
    {
        _loadingAccount = true;
        try
        {
            var items = AccountsList.ItemsSource as IReadOnlyList<AccountListItem>;
            AccountsList.SelectedItem = items?.FirstOrDefault(i => string.Equals(i.Id, accountId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _loadingAccount = false;
        }

        LoadSelectedAccountIntoFields();
    }

    private void LoadSelectedAccountIntoFields()
    {
        var account = _state.EnsureSelectedAccount();
        AccountNameBox.Text = account.Name;
        ServerUrlBox.Text = account.Settings.ServerUrl;
        M3uUrlBox.Text = account.Settings.M3uUrl;
        UsernameBox.Text = account.Settings.Username;
        PasswordBox.Password = account.Settings.Password;
        EpgUrlBox.Text = account.Settings.EpgUrl;
        AccountTypeBox.SelectedIndex = string.IsNullOrWhiteSpace(account.Settings.M3uUrl) ? 0 : 1;
        UpdateAccountTypeFields();

        var hasCache = _store.HasChannelCacheForAccount(account.Id);
        var mustUpdate = account.LastPlaylistUpdatedUtc is null || !hasCache;
        UpdatePlaylistCheck.IsChecked = mustUpdate;
        UpdatePlaylistCheck.IsEnabled = !mustUpdate;
        StatusText.Text = mustUpdate
            ? "This account has no updated playlist yet. It will update before opening."
            : $"Last playlist update: {FormatUpdatedAt(account.LastPlaylistUpdatedUtc)}. You can uncheck update for a faster login.";
    }

    private static string FormatUpdatedAt(DateTime? updatedUtc)
    {
        if (updatedUtc is null) return "Never";
        return updatedUtc.Value.ToLocalTime().ToString("g");
    }

    private void AccountTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAccountTypeFields();
    }

    private void UpdateAccountTypeFields()
    {
        if (XtreamFieldsPanel is null || M3uFieldsPanel is null) return;
        var useM3u = AccountTypeBox.SelectedIndex == 1;
        XtreamFieldsPanel.Visibility = useM3u ? Visibility.Collapsed : Visibility.Visible;
        M3uFieldsPanel.Visibility = useM3u ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AccountsList.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        Continue_Click(this, new RoutedEventArgs());
    }

    private sealed record AccountListItem(string Id, string Text)
    {
        public static AccountListItem FromAccount(SavedAccount account)
        {
            var updated = account.LastPlaylistUpdatedUtc is null
                ? "Never updated"
                : "Updated " + account.LastPlaylistUpdatedUtc.Value.ToLocalTime().ToString("g");
            return new AccountListItem(account.Id, account.DisplayName + Environment.NewLine + updated);
        }

        public override string ToString() => Text;
    }
}
