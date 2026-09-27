using ClassicWindowsIptvPlayer.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace ClassicWindowsIptvPlayer.Windows;

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
    private AccountEditingSession _session;
    private bool _loadingAccount;
    public string? PlayingAccountId { get; init; }

    public LoginResult LoginResult { get; private set; } = new() { Account = new AccountSettings(), AccountId = string.Empty, UpdatePlaylist = true };

    public LoginWindow()
    {
        InitializeComponent();
        _state = _store.Load();
        _session = new AccountEditingSession(_state);
        AppLogger.Info("Login window opened. accounts=" + _state.Accounts.Count + "; selectedAccountId=" + _state.SelectedAccountId);
        RefreshAccountsList();
        SelectAccount(_session.SelectedId);
        if (_store.RecoveryNotice is { } notice)
            MessageBox.Show(this, notice, "Saved data recovery", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        CaptureFields();
        var requestedUpdate = UpdatePlaylistCheck.IsChecked == true;
        if (!CommitDrafts()) return;

        var selected = _session.Selected;
        var hasCache = _store.HasChannelCacheForAccount(selected.Id);
        var mustUpdate = selected.LastPlaylistUpdatedUtc is null || !hasCache;
        _state.SelectedAccountId = selected.Id;
        _state.Account = selected.Settings.Clone();
        _store.Save(_state);

        LoginResult = new LoginResult
        {
            Account = selected.Settings.Clone(),
            AccountId = selected.Id,
            UpdatePlaylist = mustUpdate || requestedUpdate
        };
        AppLogger.Info("Login continue. accountId=" + selected.Id + "; updatePlaylist=" + LoginResult.UpdatePlaylist + "; hasCache=" + hasCache);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("Login cancelled. Uncommitted account edits discarded.");
        DialogResult = false;
    }

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAccount || AccountsList.SelectedItem is not AccountListItem item) return;
        CaptureFields();
        _session.Select(item.Id);
        LoadSelectedAccountIntoFields();
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        CaptureFields();
        var account = _session.Add();
        RefreshAccountsList();
        SelectAccount(account.Id);
        StatusText.Text = "New account is a draft. Enter its details, then Save or Continue.";
    }

    private void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Accounts.Count <= 1)
        {
            StatusText.Text = "At least one account is required.";
            return;
        }
        var account = _session.Selected;
        if (string.Equals(account.Id, PlayingAccountId, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Open another account before removing the account currently playing.";
            return;
        }
        var answer = MessageBox.Show(this,
            $"Remove '{account.DisplayName}' and its saved playlist cache when you Save or Continue?",
            "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _session.RemoveSelected();
        RefreshAccountsList();
        SelectAccount(_session.SelectedId);
        StatusText.Text = "Removal is pending. Save or Continue to apply it; Cancel discards it.";
    }

    private void SaveAccount_Click(object sender, RoutedEventArgs e)
    {
        CaptureFields();
        if (!CommitDrafts()) return;
        StatusText.Text = "Accounts saved. Cancel closes this window without opening another account.";
    }

    private bool CommitDrafts()
    {
        var issue = _session.Validate();
        if (issue is not null)
        {
            SelectAccount(issue.AccountId);
            StatusText.Text = issue.Message + " No changes were saved.";
            FocusInvalidField(issue.Field);
            return false;
        }

        var selectedId = _session.SelectedId;
        var removedIds = _session.ApplyTo(_state);
        _store.Save(_state);
        foreach (var id in removedIds) _store.ClearChannelCache(id);
        _session = new AccountEditingSession(_state);
        _session.Select(selectedId);
        RefreshAccountsList();
        SelectAccount(selectedId);
        AppLogger.Info("Account drafts saved. accounts=" + _state.Accounts.Count + "; removed=" + removedIds.Count);
        return true;
    }

    private void CaptureFields()
    {
        if (_loadingAccount || AccountTypeBox.SelectedIndex < 0) return;
        var previous = _session.Selected.Settings;
        var useM3u = AccountTypeBox.SelectedIndex == 1;
        _session.EditSelected(AccountNameBox.Text, new AccountSettings
        {
            ServerUrl = useM3u ? string.Empty : ServerUrlBox.Text.Trim(),
            M3uUrl = useM3u ? M3uUrlBox.Text.Trim() : string.Empty,
            Username = useM3u ? string.Empty : UsernameBox.Text.Trim(),
            Password = useM3u ? string.Empty : PasswordBox.Password.Trim(),
            EpgUrl = EpgUrlBox.Text.Trim(),
            PreferredPlayerPath = previous.PreferredPlayerPath
        });
    }

    private void FocusInvalidField(string field)
    {
        switch (field)
        {
            case "AccountName": AccountNameBox.Focus(); break;
            case "ServerUrl": ServerUrlBox.Focus(); break;
            case "M3uUrl": M3uUrlBox.Focus(); break;
            case "Username": UsernameBox.Focus(); break;
            case "Password": PasswordBox.Focus(); break;
            case "EpgUrl": EpgUrlBox.Focus(); break;
        }
    }

    private void RefreshAccountsList()
    {
        _loadingAccount = true;
        try { AccountsList.ItemsSource = _session.Accounts.Select(AccountListItem.FromAccount).ToList(); }
        finally { _loadingAccount = false; }
    }

    private void SelectAccount(string accountId)
    {
        _loadingAccount = true;
        try
        {
            _session.Select(accountId);
            var items = AccountsList.ItemsSource as IReadOnlyList<AccountListItem>;
            AccountsList.SelectedItem = items?.FirstOrDefault(i => string.Equals(i.Id, accountId, StringComparison.OrdinalIgnoreCase));
            LoadSelectedAccountIntoFields();
        }
        finally { _loadingAccount = false; }
    }

    private void LoadSelectedAccountIntoFields()
    {
        var account = _session.Selected;
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
            ? "No saved playlist is available. Opening this account will update it."
            : $"Last playlist update: {account.LastPlaylistUpdatedUtc!.Value.ToLocalTime():g}. Save commits edits; Cancel discards pending edits.";
    }

    private void AccountTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAccountTypeFields();

    private void UpdateAccountTypeFields()
    {
        if (XtreamFieldsPanel is null || M3uFieldsPanel is null) return;
        var useM3u = AccountTypeBox.SelectedIndex == 1;
        XtreamFieldsPanel.Visibility = useM3u ? Visibility.Collapsed : Visibility.Visible;
        M3uFieldsPanel.Visibility = useM3u ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => AccountsList.Focus();

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
