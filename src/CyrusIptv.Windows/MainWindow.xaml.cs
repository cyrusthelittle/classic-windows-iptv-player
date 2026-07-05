using CyrusIptv.Core;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace CyrusIptv.Windows;

public partial class MainWindow : Window
{
    // Safety ceiling so a pathologically large playlist can't hang the UI while
    // sorting/rendering. Items view otherwise shows every matching channel.
    private const int MaxPlayableCacheItems = 50000;

    private readonly LoginResult _login;
    private readonly ConfigStore _store = new();
    private readonly PlaylistService _playlistService = new();
    private readonly StreamProbeService _streamProbeService = new();
    private readonly RemoteControlService _remoteControlService = new();
    private readonly StreamInfoTracker _streamInfoTracker = new();
    private AppState _state;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _controlsHideTimer;
    private readonly DispatcherTimer _volumeOsdTimer;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private MediaSearchIndex? _searchIndex;
    private List<Channel> _channels = [];
    private List<Channel> _filteredChannels = [];
    private List<Channel> _visibleChannels = [];
    private List<ChannelListEntry> _visibleEntries = [];
    private IReadOnlyList<PlaybackCandidate> _currentCandidates = [];
    private List<SubtitleOption> _subtitleOptions = [];
    private Channel? _currentChannel;
    private PauseResumeSnapshot? _pausedPlayback;
    private string? _activeFolder;
    private string? _activeLetter;
    private bool _isSeeking;
    private bool _channelsVisible = true;
    private bool _suppressBufferChange;
    private bool _isFullScreen;
    private bool _cursorHidden;
    private bool _channelsVisibleBeforeFullScreen = true;
    private bool _isSwitchingChannel;
    private bool _isPausedByUser;
    private bool _isShuttingDown;
    // XAML initializes the slider to 100 before the saved value is restored. Keep
    // its ValueChanged event from overwriting the persisted volume during startup.
    private bool _suppressVolumeChange = true;
    private bool _suppressChannelSelectionChange;
    private bool _isHandlingPlaybackError;
    private bool _isChangingAccount;
    private DateTime? _livePauseStartedUtc;
    private TimeSpan _liveBehind = TimeSpan.Zero;
    private long? _pendingResumeTimeMs;
    private string _pendingResumeChannelId = string.Empty;
    private int _pendingResumeSeekAttempts;
    private int _mediaKindMode;
    private int _viewMode = 2;
    // 0 = Folders (browse by playlist category), 1 = A-Z (browse by first letter), 2 = Items (flat, everything).
    private int _browseMode;
    private int _currentCandidateIndex = -1;
    private WindowState _windowStateBeforeFullScreen;
    private WindowStyle _windowStyleBeforeFullScreen;
    private ResizeMode _resizeModeBeforeFullScreen;
    private bool _topmostBeforeFullScreen;
    private double _leftBeforeFullScreen;
    private double _topBeforeFullScreen;
    private double _widthBeforeFullScreen;
    private double _heightBeforeFullScreen;
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _sourceProbeCts;

    public MainWindow(LoginResult login)
    {
        _login = login;
        _state = _store.Load();
        AppLogger.Info("MainWindow constructing. accountId=" + login.AccountId + "; updatePlaylist=" + login.UpdatePlaylist);
        _state.SelectedAccountId = login.AccountId;
        var selectedAccount = _state.EnsureSelectedAccount();
        selectedAccount.Settings = login.Account.Clone();
        _state.Account = selectedAccount.Settings.Clone();
        _store.Save(_state);

        InitializeComponent();
        DarkModeMenuItem.IsChecked = _state.DarkMode;
        ApplyDefaultStartupFilters();
        InitializeButtonIcons();
        UpdateSearchScopeButtons();
        UpdateMediaKindButtons();
        UpdateViewModeButtons();

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilters(); };

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _positionTimer.Tick += (_, _) =>
        {
            UpdatePlaybackPosition();
            UpdateLiveDelayUi();
            UpdateStreamInfo();
        };

        _controlsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _controlsHideTimer.Tick += (_, _) => HideControlsIfFullScreen();

        _volumeOsdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        _volumeOsdTimer.Tick += (_, _) =>
        {
            _volumeOsdTimer.Stop();
            VolumeOsdPopup.IsOpen = false;
        };

        Loaded += MainWindow_Loaded;
        Closing += (_, _) =>
        {
            SaveCurrentAudioState();
            CleanupPlayer();
        };
    }

    private void ApplyDefaultStartupFilters()
    {
        _browseMode = 0;
        _mediaKindMode = 1;
        _viewMode = 0;
        _activeFolder = null;
        _activeLetter = null;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AppLogger.Info("MainWindow loaded. Initializing player and playlist.");
            ShowAccountLoading("Starting the player...");

            // Loaded runs before WPF has necessarily painted the first frame.
            // Yield below render priority so the loading card is visible before
            // any native player initialization or playlist work starts.
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);

            await InitializePlayerAsync();
            InitializeBufferBox();
            InitializeVolumeControls();
            ApplyRemoteControlState(showStatus: false);
            await LoadChannelsAsync(_login.UpdatePlaylist);
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainWindow loaded failed.", ex);
            HideAccountLoading();
            StatusText.Text = "Startup error: " + ex.Message;
            MessageBox.Show(this, ex.ToString(), "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializePlayerAsync()
    {
        AppLogger.Info("Initializing LibVLC player. bufferMs=" + GetPlaybackBufferMs() + "; volume=" + _state.VolumeLevel + "; muted=" + _state.Muted);
        var buffer = GetPlaybackBufferMs();
        (_libVlc, _mediaPlayer) = await Task.Run(() =>
        {
            LibVLCSharp.Shared.Core.Initialize();
            var libVlc = new LibVLC(
                "--no-video-title-show",
                "--avcodec-hw=none",
                "--network-caching=" + buffer,
                "--live-caching=" + buffer,
                "--file-caching=" + buffer,
                "--http-reconnect");
            return (libVlc, new MediaPlayer(libVlc));
        });

        ApplySavedAudioState();
        _mediaPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            AppLogger.Info("MediaPlayer event: Playing. " + AppLogger.DescribeChannel(_currentChannel));
            PlayPauseButton.Content = IconFactory.Create(IconFactory.Pause);
            HideIdleBackground();
            RefreshSubtitleTracks();
            ShowControls();
            UpdateStreamInfo();
        }));
        _mediaPlayer.Paused += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            AppLogger.Info("MediaPlayer event: Paused. " + AppLogger.DescribeChannel(_currentChannel));
            PlayPauseButton.Content = IconFactory.Create(IconFactory.Play);
        }));
        _mediaPlayer.Stopped += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            AppLogger.Info("MediaPlayer event: Stopped. " + AppLogger.DescribeChannel(_currentChannel));
            PlayPauseButton.Content = IconFactory.Create(IconFactory.Play);
            // Stop also fires while switching channels; by the time this dispatched
            // action runs the next stream may already be playing.
            if (_mediaPlayer?.IsPlaying != true) ShowIdleBackground();
        }));
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            AppLogger.Warn("MediaPlayer event: EncounteredError. candidate=" + GetCurrentCandidateLogText() + "; " + AppLogger.DescribeChannel(_currentChannel));
            _ = HandlePlaybackErrorAsync();
        }));
        VideoView.MediaPlayer = _mediaPlayer;
        _positionTimer.Start();
        RefreshSubtitleTracks();
    }

    private void InitializeButtonIcons()
    {
        ToggleChannelsButton.Content = IconFactory.Create(IconFactory.Menu);
        PreviousButton.Content = IconFactory.Create(IconFactory.Previous);
        PlayPauseButton.Content = IconFactory.Create(IconFactory.Play);
        StopButton.Content = IconFactory.Create(IconFactory.Stop);
        NextButton.Content = IconFactory.Create(IconFactory.Next);
        MuteButton.Content = IconFactory.Create(_state.Muted ? IconFactory.Mute : IconFactory.Volume);
        FullScreenButton.Content = IconFactory.Create(IconFactory.FullScreen);
        RestartButton.Content = IconFactory.Labeled("Restart", IconFactory.Restart);
        CopyUrlButton.Content = IconFactory.Labeled("Copy URL", IconFactory.Copy);
        UpdateFavoriteButton();
    }

    private async Task LoadChannelsAsync(bool updatePlaylist)
    {
        var accountId = _state.SelectedAccountId;
        AppLogger.Info("LoadChannelsAsync begin. updatePlaylist=" + updatePlaylist + "; accountId=" + _state.SelectedAccountId);
        ShowAccountLoading(updatePlaylist
            ? "Connecting to your provider and updating the playlist..."
            : "Loading your saved playlist...");

        try
        {
            StatusText.Text = updatePlaylist ? "Updating playlist..." : "Loading cached playlist...";

            if (updatePlaylist)
            {
                var account = _state.Account.Clone();
                var channels = await Task.Run(() => _playlistService.LoadPlaylistAsync(account, CancellationToken.None));
                _channels = await Task.Run(() => channels.ToList());
                AppLogger.Info("Playlist downloaded. channels=" + _channels.Count);
                SetAccountLoadingMessage($"Saving {_channels.Count:N0} playlist items...");
                await Task.Run(() => _store.SaveChannelCache(accountId, _channels));
                _state.MarkSelectedPlaylistUpdated(DateTime.UtcNow);
                _store.Save(_state);
            }
            else
            {
                _channels = await Task.Run(() => _store.LoadChannelCache(accountId));
                AppLogger.Info("Loaded channel cache. channels=" + _channels.Count + "; accountId=" + accountId);
                if (_channels.Count == 0)
                {
                    AppLogger.Warn("Channel cache empty. Downloading playlist.");
                    SetAccountLoadingMessage("No saved playlist was found. Downloading it now...");
                    var account = _state.Account.Clone();
                    var channels = await Task.Run(() => _playlistService.LoadPlaylistAsync(account, CancellationToken.None));
                    _channels = await Task.Run(() => channels.ToList());
                    AppLogger.Info("Playlist downloaded after empty cache. channels=" + _channels.Count);
                    SetAccountLoadingMessage($"Saving {_channels.Count:N0} playlist items...");
                    await Task.Run(() => _store.SaveChannelCache(accountId, _channels));
                    _state.MarkSelectedPlaylistUpdated(DateTime.UtcNow);
                    _store.Save(_state);
                }
            }

            SetAccountLoadingMessage($"Preparing {_channels.Count:N0} playlist items...");
            _searchIndex = await Task.Run(() => new MediaSearchIndex(_channels));
            await ApplyFiltersAsync();
            StatusText.Text = $"Loaded {_channels.Count:N0} items";
            AppLogger.Info("LoadChannelsAsync complete. channels=" + _channels.Count);
        }
        finally
        {
            HideAccountLoading();
        }
    }

    private void ShowAccountLoading(string message)
    {
        AccountLoadingMessage.Text = message;
        AccountLoadingOverlay.Visibility = Visibility.Visible;
        VideoView.Visibility = Visibility.Collapsed;
        Cursor = System.Windows.Input.Cursors.Wait;
    }

    private void SetAccountLoadingMessage(string message)
    {
        AccountLoadingMessage.Text = message;
    }

    // A visible VideoView hosts a native LibVLC child window that paints over any
    // WPF content in the same area, so the idle background can only render
    // correctly while VideoView is collapsed. Swap the two together.
    private void ShowIdleBackground()
    {
        VideoView.Visibility = Visibility.Collapsed;
        IdleBackground.Visibility = Visibility.Visible;
    }

    private void HideIdleBackground()
    {
        IdleBackground.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Visible;
    }

    private void HideAccountLoading()
    {
        AccountLoadingOverlay.Visibility = Visibility.Collapsed;
        if (_mediaPlayer?.IsPlaying == true)
        {
            HideIdleBackground();
        }
        else
        {
            ShowIdleBackground();
        }
        Cursor = null;
    }

    private void ApplyFilters()
    {
        _ = ApplyFiltersAsync();
    }

    private async Task ApplyFiltersAsync()
    {
        MediaKind? kind = _mediaKindMode switch
        {
            1 => MediaKind.Live,
            2 => MediaKind.Movie,
            3 => MediaKind.Series,
            _ => null
        };

        var search = SearchBox.Text ?? string.Empty;
        var activeFolder = _activeFolder;
        var activeLetter = _activeLetter;
        var browseMode = _browseMode;
        var viewMode = _viewMode;
        var channels = _channels;
        var favorites = viewMode == 1 ? _state.FavoriteIds.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var recentUrls = viewMode == 2 ? _state.Recent.Take(20).Select(r => r.Url).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;

        try
        {
            var result = await Task.Run(
                () => BuildFilterResult(channels, search, kind, activeFolder, activeLetter, browseMode, favorites, recentUrls, cts.Token),
                cts.Token);

            if (!ReferenceEquals(_filterCts, cts) || cts.IsCancellationRequested) return;

            _filteredChannels = result.FilteredChannels;
            _visibleChannels = result.VisibleChannels;
            if (result.Folders is not null)
            {
                _visibleEntries = result.Folders
                    .Select(folder => ChannelListEntry.ForFolder(folder.Name, folder.Count))
                    .ToList();
            }
            else
            {
                _visibleEntries = _visibleChannels.Select(ChannelListEntry.ForChannel).ToList();
            }

            FolderBackButton.Visibility = result.ShowBackButton ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = result.CountText;
            ChannelList.ItemsSource = _visibleEntries;
            SelectPlayingChannelInVisibleList();
            if (!string.IsNullOrWhiteSpace(result.StatusText))
            {
                StatusText.Text = result.StatusText;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer search/filter run is already in progress.
        }
        finally
        {
            if (ReferenceEquals(_filterCts, cts))
            {
                _filterCts = null;
            }

            cts.Dispose();
        }
    }

    // A-Z, then 0-9, then "#" for anything else. Sort order for the letter folders below.
    private const string LetterBucketOrder = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#";

    private static FilterResult BuildFilterResult(
        IReadOnlyList<Channel> channels,
        string search,
        MediaKind? kind,
        string? activeFolder,
        string? activeLetter,
        int browseMode,
        HashSet<string>? favorites,
        HashSet<string>? recentUrls,
        CancellationToken cancellationToken)
    {
        var queryParts = SplitSearchQuery(search);
        var activeFolderName = NormalizeGroupName(activeFolder);

        // A folder drilled into from Folders mode stays in scope even after
        // switching to A-Z, so "A-Z" buckets only the channels inside that
        // folder instead of resetting to the whole library.
        bool InFolderScope(Channel channel) =>
            activeFolder is null || string.Equals(NormalizeGroupName(channel.Group), activeFolderName, StringComparison.OrdinalIgnoreCase);

        // Folders scope: browse by playlist category (the Group field).
        if (browseMode == 0 && activeFolder is null)
        {
            var folderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var channel in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ChannelMatchesFilter(channel, kind, queryParts, favorites, recentUrls)) continue;

                var folder = NormalizeGroupName(channel.Group);
                folderCounts.TryGetValue(folder, out var count);
                folderCounts[folder] = count + 1;
            }

            return new FilterResult
            {
                Folders = folderCounts
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new FolderResult(pair.Key, pair.Value))
                    .ToList(),
                CountText = $"{folderCounts.Count:N0} folders"
            };
        }

        // A-Z scope: browse via A-Z / 0-9 buckets by first letter, scoped to the
        // active folder (if any) so it never mixes in channels from outside it.
        if (browseMode == 1 && activeLetter is null)
        {
            var letterCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var channel in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!InFolderScope(channel)) continue;
                if (!ChannelMatchesFilter(channel, kind, queryParts, favorites, recentUrls)) continue;

                var letter = GetNameBucketKey(channel.Name);
                letterCounts.TryGetValue(letter, out var count);
                letterCounts[letter] = count + 1;
            }

            return new FilterResult
            {
                Folders = letterCounts
                    .OrderBy(pair => LetterBucketOrder.IndexOf(pair.Key, StringComparison.Ordinal))
                    .Select(pair => new FolderResult(pair.Key, pair.Value))
                    .ToList(),
                ShowBackButton = activeFolder is not null,
                CountText = $"{letterCounts.Count:N0} folders"
            };
        }

        // Items scope (or a drill-down into a category/letter) shows the complete
        // matching list -- no grouping, no truncation.
        var filtered = new List<Channel>(Math.Min(MaxPlayableCacheItems, channels.Count));
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!InFolderScope(channel)) continue;
            if (activeLetter is not null && !string.Equals(GetNameBucketKey(channel.Name), activeLetter, StringComparison.Ordinal)) continue;
            if (!ChannelMatchesFilter(channel, kind, queryParts, favorites, recentUrls)) continue;

            filtered.Add(channel);
            if (filtered.Count >= MaxPlayableCacheItems) break;
        }

        if (activeFolder is not null || activeLetter is not null)
        {
            filtered = filtered.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        return new FilterResult
        {
            FilteredChannels = filtered,
            VisibleChannels = filtered,
            ShowBackButton = activeFolder is not null || activeLetter is not null,
            CountText = $"{filtered.Count:N0} items"
        };
    }

    // Buckets a channel/movie under its first letter (A-Z), first digit (0-9),
    // or "#" for anything else (blank names, symbols, non-Latin titles, etc).
    private static string GetNameBucketKey(string? name)
    {
        var trimmed = (name ?? string.Empty).TrimStart();
        if (trimmed.Length == 0) return "#";

        var ch = char.ToUpperInvariant(trimmed[0]);
        if (ch is >= 'A' and <= 'Z') return ch.ToString();
        if (ch is >= '0' and <= '9') return ch.ToString();
        return "#";
    }

    private static bool ChannelMatchesFilter(Channel channel, MediaKind? kind, IReadOnlyList<string> queryParts, HashSet<string>? favorites, HashSet<string>? recentUrls)
    {
        if (kind is not null && channel.MediaKind != kind.Value) return false;
        if (favorites is not null && !favorites.Contains(channel.Id)) return false;
        if (recentUrls is not null && !recentUrls.Contains(channel.Url)) return false;
        return MatchesSearch(channel, queryParts);
    }

    private sealed class FilterResult
    {
        public List<FolderResult>? Folders { get; init; }
        public List<Channel> FilteredChannels { get; init; } = [];
        public List<Channel> VisibleChannels { get; init; } = [];
        public bool ShowBackButton { get; init; }
        public string CountText { get; init; } = "0";
        public string StatusText { get; init; } = string.Empty;
    }

    private sealed record FolderResult(string Name, int Count);

    private static string[] SplitSearchQuery(string search)
    {
        return (search ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool MatchesSearch(Channel channel, IReadOnlyList<string> queryParts)
    {
        if (queryParts.Count == 0) return true;
        foreach (var part in queryParts)
        {
            if (!ContainsIgnoreCase(channel.Name, part) &&
                !ContainsIgnoreCase(channel.Group, part) &&
                !ContainsIgnoreCase(channel.MediaKind.ToString(), part))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsIgnoreCase(string? value, string part)
    {
        return !string.IsNullOrEmpty(value) && value.Contains(part, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilters();

    private void AllMedia_Click(object sender, RoutedEventArgs e) => SetMediaKindMode(0);
    private void LiveTv_Click(object sender, RoutedEventArgs e) => SetMediaKindMode(1);
    private void Movies_Click(object sender, RoutedEventArgs e) => SetMediaKindMode(2);
    private void Series_Click(object sender, RoutedEventArgs e) => SetMediaKindMode(3);

    private void AllView_Click(object sender, RoutedEventArgs e) => SetViewMode(0);
    private void FavoritesView_Click(object sender, RoutedEventArgs e) => SetViewMode(1);
    private void RecentView_Click(object sender, RoutedEventArgs e) => SetViewMode(2);

    private void SearchFolders_Click(object sender, RoutedEventArgs e) => SetBrowseMode(0);
    private void SearchLetters_Click(object sender, RoutedEventArgs e) => SetBrowseMode(1);
    private void SearchItems_Click(object sender, RoutedEventArgs e) => SetBrowseMode(2);

    private void SetBrowseMode(int browseMode)
    {
        _browseMode = Math.Clamp(browseMode, 0, 2);
        // Keep whatever folder you've drilled into (e.g. via Folders mode) so
        // switching to A-Z buckets just that folder instead of everything.
        _activeLetter = null;
        UpdateSearchScopeButtons();
        ApplyFilters();
    }

    private void SetMediaKindMode(int mediaKindMode)
    {
        _mediaKindMode = Math.Clamp(mediaKindMode, 0, 3);
        UpdateMediaKindButtons();
        ApplyFilters();
    }

    private void SetViewMode(int viewMode)
    {
        _viewMode = Math.Clamp(viewMode, 0, 2);
        UpdateViewModeButtons();
        ApplyFilters();
    }

    private void UpdateSearchScopeButtons()
    {
        SetViewButtonState(SearchFoldersButton, _browseMode == 0);
        SetViewButtonState(SearchLettersButton, _browseMode == 1);
        SetViewButtonState(SearchItemsButton, _browseMode == 2);
    }

    private void UpdateMediaKindButtons()
    {
        SetViewButtonState(AllMediaButton, _mediaKindMode == 0);
        SetViewButtonState(LiveTvButton, _mediaKindMode == 1);
        SetViewButtonState(MoviesButton, _mediaKindMode == 2);
        SetViewButtonState(SeriesButton, _mediaKindMode == 3);
    }

    private void UpdateViewModeButtons()
    {
        SetViewButtonState(AllViewButton, _viewMode == 0);
        SetViewButtonState(FavoritesViewButton, _viewMode == 1);
        SetViewButtonState(RecentViewButton, _viewMode == 2);
    }

    private void SetViewButtonState(System.Windows.Controls.Button button, bool selected)
    {
        button.SetResourceReference(BackgroundProperty, selected ? "AccentBrush" : "ButtonBgBrush");
        if (selected)
        {
            button.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 165, 250));
        }
        else
        {
            button.SetResourceReference(BorderBrushProperty, "InputLightBorderBrush");
        }
        button.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
    }

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChannelSelectionChange) return;
        if (ChannelList.SelectedItem is ChannelListEntry { Channel: { } channel }) BuildSourceList(channel);
        UpdateFavoriteButton();
    }

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ActivateSelectedListEntry();
    }

    private void FolderBack_Click(object sender, RoutedEventArgs e)
    {
        // Pop one level at a time: out of a letter drill-down first (back to
        // that folder's letter buckets), then out of the folder itself.
        if (_activeLetter is not null)
        {
            _activeLetter = null;
        }
        else
        {
            _activeFolder = null;
        }
        ApplyFilters();
    }

    private void ActivateSelectedListEntry()
    {
        if (ChannelList.SelectedItem is not ChannelListEntry entry) return;
        if (entry.IsFolder)
        {
            EnterFolder(entry.FolderName);
            return;
        }

        if (entry.Channel is not null) PlayChannel(entry.Channel);
    }

    private void EnterFolder(string folderName)
    {
        if (_browseMode == 0)
        {
            _activeFolder = NormalizeGroupName(folderName);
        }
        else
        {
            _activeLetter = folderName;
        }
        ApplyFilters();
        if (_visibleEntries.Count > 0)
        {
            ChannelList.SelectedIndex = 0;
            ChannelList.ScrollIntoView(ChannelList.SelectedItem);
        }
    }

    private void SelectChannelInList(Channel channel)
    {
        if (_browseMode == 0)
        {
            _activeFolder = NormalizeGroupName(channel.Group);
        }
        else if (_browseMode == 1)
        {
            // Drop a stale folder scope if the channel isn't actually inside it,
            // otherwise the letter drill-down alone can't make it visible.
            if (_activeFolder is not null && !string.Equals(NormalizeGroupName(channel.Group), _activeFolder, StringComparison.OrdinalIgnoreCase))
            {
                _activeFolder = null;
            }
            _activeLetter = GetNameBucketKey(channel.Name);
        }
        ApplyFilters();

        // Select immediately when the channel is already in the current result.
        // ApplyFiltersAsync repeats this after replacing the ItemsSource.
        SelectPlayingChannelInVisibleList();
    }

    private void SelectPlayingChannelInVisibleList()
    {
        if (_currentChannel is null) return;

        var match = _visibleEntries.FirstOrDefault(e =>
            e.Channel is not null &&
            string.Equals(e.Channel.Id, _currentChannel.Id, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            _suppressChannelSelectionChange = true;
            try
            {
                ChannelList.SelectedItem = match;
                ChannelList.ScrollIntoView(match);
            }
            finally
            {
                _suppressChannelSelectionChange = false;
            }
        }
    }

    private static string NormalizeGroupName(string? group)
    {
        return string.IsNullOrWhiteSpace(group) ? "Uncategorized" : group.Trim();
    }

    private void BuildSourceList(Channel channel)
    {
        _currentCandidates = PlayerService.BuildPlaybackCandidates(channel, _state.Account);
        _currentCandidateIndex = _currentCandidates.Count == 0 ? -1 : 0;
        AppLogger.Info("Built source list. count=" + _currentCandidates.Count + "; " + AppLogger.DescribeChannel(channel));
    }

    private void PlayChannel(Channel channel, int? autoCandidateIndex = null)
    {
        PlayChannel(channel, autoCandidateIndex, resumeTimeMs: null, stopBeforePlay: true);
    }

    private void PlayChannel(Channel channel, int? autoCandidateIndex, long? resumeTimeMs)
    {
        PlayChannel(channel, autoCandidateIndex, resumeTimeMs, stopBeforePlay: true);
    }

    private void PlayChannel(Channel channel, int? autoCandidateIndex, long? resumeTimeMs, bool stopBeforePlay)
    {
        try
        {
            AppLogger.Info("PlayChannel begin. requestedIndex=" + (autoCandidateIndex?.ToString() ?? "auto") + "; resumeTimeMs=" + (resumeTimeMs?.ToString() ?? "none") + "; stopBeforePlay=" + stopBeforePlay + "; " + AppLogger.DescribeChannel(channel));
            if (resumeTimeMs is null) ClearPauseResumeState();
            if (_currentChannel is null || !string.Equals(_currentChannel.Id, channel.Id, StringComparison.OrdinalIgnoreCase))
            {
                ClearLiveDelay();
            }

            _currentChannel = channel;
            SelectChannelInList(channel);
            BuildSourceList(channel);
            UpdateFavoriteButton(channel);

            var candidate = GetCandidateForPlayback(autoCandidateIndex);
            if (candidate is null)
            {
                AppLogger.Warn("PlayChannel aborted: no playable candidate. " + AppLogger.DescribeChannel(channel));
                StatusText.Text = "No playable URL found.";
                return;
            }

            if (_libVlc is null || _mediaPlayer is null)
            {
                AppLogger.Warn("PlayChannel aborted: player not initialized. " + AppLogger.DescribeChannel(channel));
                StatusText.Text = "Player is not initialized yet.";
                return;
            }

            AppLogger.Info("Selected playback candidate. " + GetCandidateLogText(candidate, _currentCandidateIndex));
            var previousMedia = _currentMedia;
            var nextMedia = new Media(_libVlc, candidate.Url, FromType.FromLocation);
            var buffer = GetPlaybackBufferMs();
            nextMedia.AddOption(":network-caching=" + buffer);
            nextMedia.AddOption(":live-caching=" + buffer);
            nextMedia.AddOption(":file-caching=" + buffer);
            nextMedia.AddOption(":http-reconnect");

            _streamInfoTracker.ResetBandwidth();
            if (stopBeforePlay)
            {
                _isSwitchingChannel = true;
                try
                {
                    AppLogger.Info("Stopping current media before switch.");
                    _mediaPlayer.Stop();
                }
                catch (Exception ex)
                {
                    // Some providers leave the native player in a transient state while switching.
                    AppLogger.Warn("MediaPlayer.Stop failed while switching. " + ex.Message);
                }
                finally
                {
                    _isSwitchingChannel = false;
                }
            }
            else
            {
                AppLogger.Info("Skipping MediaPlayer.Stop before retry because playback is recovering from an error.");
            }

            _currentMedia = nextMedia;
            HideIdleBackground();
            var playStarted = _mediaPlayer.Play(_currentMedia);
            AppLogger.Info("MediaPlayer.Play called. started=" + playStarted + "; " + GetCurrentCandidateLogText());
            ApplySavedAudioState();
            if (resumeTimeMs is > 0)
            {
                QueueResumeSeek(channel.Id, resumeTimeMs.Value);
            }
            DisposeMediaLater(previousMedia);
            RefreshSubtitleTracks();
            NowPlayingText.Text = channel.Name;
            StatusText.Text = "Playing: " + channel.Name + " • " + candidate.Label;
            AddRecent(channel);
            ShowControls();
            UpdateStreamInfo();
            StartBackgroundSourceProbe(channel, _currentCandidates);
            AppLogger.Info("PlayChannel complete. " + AppLogger.DescribeChannel(channel));
        }
        catch (Exception ex)
        {
            AppLogger.Error("PlayChannel failed. " + AppLogger.DescribeChannel(channel), ex);
            StatusText.Text = "Play error: " + ex.Message;
        }
    }

    private void StartBackgroundSourceProbe(Channel channel, IReadOnlyList<PlaybackCandidate> candidates)
    {
        _sourceProbeCts?.Cancel();
        _sourceProbeCts?.Dispose();
        _sourceProbeCts = null;

        if (candidates.Count <= 1) return;

        AppLogger.Info("Starting background source probe. candidateCount=" + candidates.Count + "; " + AppLogger.DescribeChannel(channel));
        var cts = new CancellationTokenSource();
        _sourceProbeCts = cts;
        _ = ProbeSourcesInBackgroundAsync(channel, candidates.ToList(), cts);
    }

    private async Task ProbeSourcesInBackgroundAsync(Channel channel, IReadOnlyList<PlaybackCandidate> candidates, CancellationTokenSource cts)
    {
        try
        {
            StatusText.Text = "Testing sources in background...";
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(35));
            var results = await _streamProbeService.ProbeAsync(candidates, timeoutCts.Token);
            if (!ReferenceEquals(_sourceProbeCts, cts) || cts.IsCancellationRequested) return;
            if (_currentChannel is null || !string.Equals(_currentChannel.Id, channel.Id, StringComparison.OrdinalIgnoreCase)) return;

            var okCandidates = results
                .Where(r => r.LooksReachable)
                .Select(r => r.Candidate)
                .ToList();

            if (okCandidates.Count == 0)
            {
                StatusText.Text = "No tested source looked reachable.";
                return;
            }

            _currentCandidates = okCandidates;
            var playingUrl = GetCurrentPlaybackCandidate()?.Url;
            _currentCandidateIndex = !string.IsNullOrWhiteSpace(playingUrl)
                ? Math.Max(0, okCandidates.FindIndex(c => string.Equals(c.Url, playingUrl, StringComparison.OrdinalIgnoreCase)))
                : 0;
            StatusText.Text = $"Auto source check complete: {okCandidates.Count:N0} OK source(s).";
            AppLogger.Info("Background source probe complete. okCount=" + okCandidates.Count + "; " + AppLogger.DescribeChannel(channel));
            UpdateStreamInfo();
        }
        catch (OperationCanceledException)
        {
            // A newer channel selection is being tested.
            AppLogger.Info("Background source probe cancelled. " + AppLogger.DescribeChannel(channel));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Background source probe failed. " + AppLogger.DescribeChannel(channel), ex);
            if (ReferenceEquals(_sourceProbeCts, cts)) StatusText.Text = "Source test failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_sourceProbeCts, cts))
            {
                _sourceProbeCts = null;
            }

            cts.Dispose();
        }
    }

    private PlaybackCandidate? GetCandidateForPlayback(int? autoCandidateIndex = null)
    {
        if (_currentCandidates.Count == 0) return null;
        _currentCandidateIndex = Math.Max(0, Math.Min(autoCandidateIndex ?? 0, _currentCandidates.Count - 1));
        return _currentCandidates[_currentCandidateIndex];
    }

    private PlaybackCandidate? GetCurrentPlaybackCandidate()
    {
        if (_currentCandidates.Count == 0) return null;
        return _currentCandidateIndex >= 0 && _currentCandidateIndex < _currentCandidates.Count
            ? _currentCandidates[_currentCandidateIndex]
            : _currentCandidates[0];
    }

    private string GetCurrentCandidateLogText()
    {
        var candidate = GetCurrentPlaybackCandidate();
        return candidate is null ? "candidate=none" : GetCandidateLogText(candidate, _currentCandidateIndex);
    }

    private static string GetCandidateLogText(PlaybackCandidate candidate, int index)
    {
        return $"candidateIndex={index}; label={candidate.Label}; url={AppLogger.SanitizeUrl(candidate.Url)}";
    }

    private async Task HandlePlaybackErrorAsync()
    {
        if (_isPausedByUser) return;
        if (_isSwitchingChannel) return;
        if (_isHandlingPlaybackError)
        {
            AppLogger.Warn("Playback error ignored because another error recovery is already in progress. " + AppLogger.DescribeChannel(_currentChannel));
            return;
        }

        _isHandlingPlaybackError = true;
        try
        {
            await Task.Delay(350);
            if (_isShuttingDown || _isPausedByUser || _isSwitchingChannel) return;

            if (_currentChannel is not null && _currentCandidateIndex + 1 < _currentCandidates.Count)
            {
                var channel = _currentChannel;
                var nextIndex = _currentCandidateIndex + 1;
                AppLogger.Warn("Playback error. Trying next candidate. nextIndex=" + nextIndex + "; " + AppLogger.DescribeChannel(channel));
                StatusText.Text = $"Stream failed. Trying next format ({nextIndex + 1}/{_currentCandidates.Count})...";
                if (channel.MediaKind == MediaKind.Live) ClearLiveDelay();
                PlayChannel(channel, nextIndex, resumeTimeMs: null, stopBeforePlay: false);
                return;
            }

            AppLogger.Warn("Playback error on playlist URL. " + AppLogger.DescribeChannel(_currentChannel));
            StatusText.Text = "Playback error on playlist URL.";
            UpdateStreamInfo();
        }
        finally
        {
            _isHandlingPlaybackError = false;
        }
    }

    private void AddRecent(Channel channel)
    {
        _state.Recent.RemoveAll(r => string.Equals(r.Url, channel.Url, StringComparison.OrdinalIgnoreCase));
        _state.Recent.Insert(0, new RecentItem
        {
            ChannelId = channel.Id,
            Name = channel.Name,
            Group = channel.Group,
            Url = channel.Url,
            MediaKind = channel.MediaKind,
            PlayedAtUtc = DateTime.UtcNow
        });
        if (_state.Recent.Count > 20) _state.Recent.RemoveRange(20, _state.Recent.Count - 20);

        _store.Save(_state);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        if (_mediaPlayer is null) return;
        if (_mediaPlayer.IsPlaying) PauseCurrentPlayback();
        else if (_pausedPlayback is not null) ResumePausedPlayback();
        else if (_currentChannel is not null) PlayChannel(_currentChannel);
        else if (ChannelList.SelectedItem is ChannelListEntry { Channel: { } channel }) PlayChannel(channel);
        UpdateStreamInfo();
    }

    private void PauseCurrentPlayback()
    {
        if (_mediaPlayer is null) return;

        if (_currentChannel is not null)
        {
            var timeMs = Math.Max(0, _mediaPlayer.Time);
            _pausedPlayback = new PauseResumeSnapshot(
                _currentChannel,
                Math.Max(0, _currentCandidateIndex),
                timeMs);
        }

        _isPausedByUser = true;

        try
        {
            _mediaPlayer.Pause();
            StartLivePauseTracking();
        }
        catch
        {
            // Ignore pause errors while preserving the resume snapshot.
        }

        PlayPauseButton.Content = IconFactory.Create(IconFactory.Play);
        StatusText.Text = _pausedPlayback is null
            ? "Paused."
            : "Paused at " + FormatTime(_pausedPlayback.TimeMs) + ".";
        UpdateStreamInfo();
    }

    private void ResumePausedPlayback()
    {
        if (_mediaPlayer is null || _pausedPlayback is null) return;

        var snapshot = _pausedPlayback;
        _pausedPlayback = null;
        _isPausedByUser = false;

        try
        {
            _mediaPlayer.Play();
            FinishLivePauseTracking();
            PlayPauseButton.Content = IconFactory.Create(IconFactory.Pause);
            StatusText.Text = "Resuming: " + snapshot.Channel.Name;
        }
        catch
        {
            var canSeekBack = snapshot.Channel.MediaKind != MediaKind.Live && snapshot.TimeMs > 0;
            PlayChannel(snapshot.Channel, snapshot.CandidateIndex, canSeekBack ? snapshot.TimeMs : null);
            if (snapshot.Channel.MediaKind == MediaKind.Live) ClearLiveDelay();
            StatusText.Text = canSeekBack
                ? "Resuming: " + snapshot.Channel.Name
                : "Restarting live stream: " + snapshot.Channel.Name;
        }
    }

    private void StartLivePauseTracking()
    {
        if (_currentChannel?.MediaKind != MediaKind.Live) return;
        _livePauseStartedUtc = DateTime.UtcNow;
        UpdateLiveDelayUi();
    }

    private void FinishLivePauseTracking()
    {
        if (_livePauseStartedUtc is DateTime pausedAt)
        {
            _liveBehind += DateTime.UtcNow - pausedAt;
            _livePauseStartedUtc = null;
        }

        UpdateLiveDelayUi();
    }

    private void ClearLiveDelay()
    {
        _livePauseStartedUtc = null;
        _liveBehind = TimeSpan.Zero;
        UpdateLiveDelayUi();
    }

    private TimeSpan GetCurrentLiveBehind()
    {
        if (_currentChannel?.MediaKind != MediaKind.Live) return TimeSpan.Zero;
        var behind = _liveBehind;
        if (_livePauseStartedUtc is DateTime pausedAt)
        {
            behind += DateTime.UtcNow - pausedAt;
        }

        return behind < TimeSpan.Zero ? TimeSpan.Zero : behind;
    }

    private void UpdateLiveDelayUi()
    {
        var behind = GetCurrentLiveBehind();
        var show = behind >= TimeSpan.FromSeconds(1);
        var text = show ? "Behind live " + FormatLiveDelay(behind) : string.Empty;

        LiveDelayText.Text = text;
        LiveDelayText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GoLiveButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GoLiveMenuItem.IsEnabled = show;
        VideoGoLiveMenuItem.IsEnabled = show;
    }

    private static string FormatLiveDelay(TimeSpan delay)
    {
        if (delay.TotalHours >= 1) return delay.ToString(@"h\:mm\:ss");
        return delay.ToString(@"m\:ss");
    }

    private void ClearPauseResumeState()
    {
        _pausedPlayback = null;
        _isPausedByUser = false;
        _pendingResumeTimeMs = null;
        _pendingResumeChannelId = string.Empty;
        _pendingResumeSeekAttempts = 0;
    }

    private void QueueResumeSeek(string channelId, long timeMs)
    {
        _pendingResumeTimeMs = Math.Max(0, timeMs - 750);
        _pendingResumeChannelId = channelId;
        _pendingResumeSeekAttempts = 0;
        _ = TryApplyPendingResumeSeekAsync();
    }

    private async Task TryApplyPendingResumeSeekAsync()
    {
        while (_pendingResumeTimeMs is long targetMs && _pendingResumeSeekAttempts < 16)
        {
            await Task.Delay(_pendingResumeSeekAttempts == 0 ? 150 : 250);
            if (_pendingResumeTimeMs is null || _mediaPlayer is null || _currentChannel is null) return;
            if (!string.Equals(_currentChannel.Id, _pendingResumeChannelId, StringComparison.OrdinalIgnoreCase)) return;

            _pendingResumeSeekAttempts++;

            try
            {
                _mediaPlayer.Time = targetMs;
                _pendingResumeTimeMs = null;
                _pendingResumeChannelId = string.Empty;
                _pendingResumeSeekAttempts = 0;
                PlayPauseButton.Content = IconFactory.Create(IconFactory.Pause);
                StatusText.Text = "Resumed at " + FormatTime(targetMs) + ".";
                UpdatePlaybackPosition();
                UpdateStreamInfo();
                return;
            }
            catch
            {
                // The stream may not be seekable until metadata arrives. Try again briefly.
            }
        }

        _pendingResumeTimeMs = null;
        _pendingResumeChannelId = string.Empty;
        _pendingResumeSeekAttempts = 0;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        ClearPauseResumeState();
        ClearLiveDelay();
        try
        {
            _mediaPlayer?.Stop();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Stop failed: " + ex.Message;
        }

        UpdateStreamInfo();
    }

    private void DisposeMediaLater(Media? media)
    {
        if (media is null) return;
        _ = Task.Delay(2500).ContinueWith(_ =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!ReferenceEquals(media, _currentMedia)) media.Dispose();
                }
                catch
                {
                    // Ignore native cleanup races while switching streams.
                }
            }));
        }, TaskScheduler.Default);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => PlayRelative(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => PlayRelative(1);

    private void PlayRelative(int delta)
    {
        var playableChannels = _activeFolder is null && _activeLetter is null ? _filteredChannels : _visibleChannels;
        if (playableChannels.Count == 0) return;

        var index = _currentChannel is null
            ? -1
            : playableChannels.FindIndex(c => string.Equals(c.Id, _currentChannel.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0 && ChannelList.SelectedItem is ChannelListEntry { Channel: { } selected })
        {
            index = playableChannels.FindIndex(c => string.Equals(c.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        }
        if (index < 0) index = 0;

        index = Math.Clamp(index + delta, 0, playableChannels.Count - 1);
        PlayChannel(playableChannels[index]);
    }

    private void AddSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null)
        {
            StatusText.Text = "Player is not initialized yet.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select subtitle file",
            Filter = "Subtitle files (*.srt;*.sub;*.ass;*.ssa)|*.srt;*.sub;*.ass;*.ssa|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var subtitleUri = new Uri(dialog.FileName).AbsoluteUri;
            var added = _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, subtitleUri, true);
            RefreshSubtitleTracks();
            StatusText.Text = added ? "Subtitle added: " + System.IO.Path.GetFileName(dialog.FileName) : "Subtitle could not be added.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Add subtitle failed: " + ex.Message;
        }
    }

    private void RefreshSubtitleTracks()
    {
        if (SubtitleMenu is null || VideoSubtitleMenu is null) return;

        var options = new List<SubtitleOption>
        {
            new(-1, "Off")
        };

        if (_mediaPlayer is not null)
        {
            try
            {
                foreach (var track in _mediaPlayer.SpuDescription)
                {
                    if (track.Id < 0) continue;
                    var name = string.IsNullOrWhiteSpace(track.Name) ? "Subtitle " + track.Id : track.Name;
                    options.Add(new SubtitleOption(track.Id, name));
                }
            }
            catch
            {
                // Track list may be unavailable while the stream is still starting.
            }
        }

        _subtitleOptions = options;
        UpdateSubtitleMenus();
    }

    private void UpdateSubtitleMenus()
    {
        var activeId = _mediaPlayer?.Spu ?? -1;
        PopulateSubtitleMenu(SubtitleMenu, activeId);
        PopulateSubtitleMenu(VideoSubtitleMenu, activeId);
    }

    private void PopulateSubtitleMenu(WpfMenuItem menu, int activeId)
    {
        menu.Items.Clear();
        var playerReady = _mediaPlayer is not null;
        foreach (var option in _subtitleOptions)
        {
            menu.Items.Add(new WpfMenuItem
            {
                Header = option.Name,
                IsCheckable = true,
                IsChecked = option.Id == activeId,
                IsEnabled = playerReady,
                Tag = option
            });
        }

        foreach (var item in menu.Items.OfType<WpfMenuItem>())
        {
            item.Click += SubtitleMenuItem_Click;
        }

        menu.Items.Add(new Separator());
        var addItem = new WpfMenuItem
        {
            Header = "Add SRT...",
            IsEnabled = playerReady
        };
        addItem.Click += AddSubtitle_Click;
        menu.Items.Add(addItem);
    }

    private void SubtitleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfMenuItem { Tag: SubtitleOption option }) SelectSubtitle(option);
    }

    private void SelectSubtitle(SubtitleOption option)
    {
        if (_mediaPlayer is null) return;

        try
        {
            _mediaPlayer.SetSpu(option.Id);
            StatusText.Text = option.Id < 0 ? "Subtitles off." : "Subtitle selected: " + option.Name;
            UpdateSubtitleMenus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Subtitle selection failed: " + ex.Message;
        }
    }

    private void UpdatePlaybackPosition()
    {
        if (_mediaPlayer is null || _isSeeking) return;
        var length = _mediaPlayer.Length;
        var time = _mediaPlayer.Time;
        if (length > 0)
        {
            SeekSlider.IsEnabled = true;
            SeekSlider.Value = Math.Clamp((double)time / length * 1000.0, 0, 1000);
            TimeText.Text = FormatTime(time) + " / " + FormatTime(length);
        }
        else
        {
            SeekSlider.IsEnabled = false;
            SeekSlider.Value = 0;
            TimeText.Text = _mediaPlayer.IsPlaying ? "Live" : "00:00 / 00:00";
        }
    }

    private static string FormatTime(long ms)
    {
        if (ms <= 0) return "00:00";
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        if (_mediaPlayer is null || _mediaPlayer.Length <= 0) return;
        _mediaPlayer.Time = (long)(_mediaPlayer.Length * (SeekSlider.Value / 1000.0));
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isSeeking || _mediaPlayer is null || _mediaPlayer.Length <= 0) return;
        TimeText.Text = FormatTime((long)(_mediaPlayer.Length * (SeekSlider.Value / 1000.0))) + " / " + FormatTime(_mediaPlayer.Length);
    }

    private void InitializeVolumeControls()
    {
        _suppressVolumeChange = true;
        try
        {
            var volume = GetSavedVolumeLevel();
            VolumeSlider.Value = volume;
            ApplySavedAudioState();
            if (MuteButton is not null) MuteButton.Content = IconFactory.Create(_state.Muted ? IconFactory.Mute : IconFactory.Volume);
        }
        finally
        {
            _suppressVolumeChange = false;
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVolumeChange) return;
        SetVolume((int)e.NewValue);
    }

    private void SetVolume(int volume)
    {
        var safeVolume = Math.Max(0, Math.Min(150, volume));
        _state.VolumeLevel = safeVolume;
        if (_mediaPlayer is not null) _mediaPlayer.Volume = safeVolume;

        if (VolumeSlider is not null && Math.Abs(VolumeSlider.Value - safeVolume) > 0.5)
        {
            VolumeSlider.Value = safeVolume;
        }

        _store.Save(_state);
        if (IsLoaded)
        {
            ShowVolumeOsd();
            UpdateStreamInfo();
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => SetMute(!_state.Muted);

    private void SetMute(bool muted, bool save = true)
    {
        _state.Muted = muted;
        if (_mediaPlayer is not null) _mediaPlayer.Mute = muted;
        if (MuteButton is not null) MuteButton.Content = IconFactory.Create(muted ? IconFactory.Mute : IconFactory.Volume);
        if (save) _store.Save(_state);
        if (IsLoaded)
        {
            ShowVolumeOsd();
            UpdateStreamInfo();
        }
    }

    private int GetSavedVolumeLevel()
    {
        return Math.Max(0, Math.Min(150, _state.VolumeLevel));
    }

    private void ApplySavedAudioState()
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.Volume = GetSavedVolumeLevel();
        _mediaPlayer.Mute = _state.Muted;
    }

    private void SaveCurrentAudioState()
    {
        try
        {
            if (VolumeSlider is not null)
            {
                _state.VolumeLevel = Math.Clamp((int)Math.Round(VolumeSlider.Value), 0, 150);
            }

            _store.Save(_state);
            AppLogger.Info("Saved audio state on exit. volume=" + _state.VolumeLevel + "; muted=" + _state.Muted);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Could not save audio state on exit. " + ex.Message);
        }
    }

    private void ShowVolumeOsd()
    {
        VolumeOsdText.Text = _state.Muted ? "Muted" : $"Volume {_state.VolumeLevel}%";
        ApplyOsdOpacity();
        VolumeOsdPopup.IsOpen = true;
        _volumeOsdTimer.Stop();
        _volumeOsdTimer.Start();
    }

    private void ToggleChannels_Click(object sender, RoutedEventArgs e)
    {
        _channelsVisible = !_channelsVisible;
        SidebarColumn.Width = _channelsVisible ? new GridLength(360) : new GridLength(0);
        Sidebar.Visibility = _channelsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (_mediaPlayer is null) return;
        if (_isFullScreen) ExitFullScreen();
        else EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        _isFullScreen = true;
        _channelsVisibleBeforeFullScreen = _channelsVisible;
        _windowStateBeforeFullScreen = WindowState;
        _windowStyleBeforeFullScreen = WindowStyle;
        _resizeModeBeforeFullScreen = ResizeMode;
        _topmostBeforeFullScreen = Topmost;
        _leftBeforeFullScreen = Left;
        _topBeforeFullScreen = Top;
        _widthBeforeFullScreen = Width;
        _heightBeforeFullScreen = Height;

        AppMenu.Visibility = Visibility.Collapsed;
        MainStatusBar.Visibility = Visibility.Collapsed;
        Sidebar.Visibility = Visibility.Collapsed;
        SidebarSplitter.Visibility = Visibility.Collapsed;
        TopMenuRow.Height = new GridLength(0);
        StatusRow.Height = new GridLength(0);
        SidebarColumn.MinWidth = 0;
        SidebarColumn.Width = new GridLength(0);
        SplitterColumn.Width = new GridLength(0);
        ControlsRow.Height = new GridLength(0);

        AttachControlsToFullScreenPopup();

        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        CoverCurrentMonitor();

        ShowControls();
        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
        Activate();
    }

    private void ExitFullScreen()
    {
        _isFullScreen = false;
        SetCursorHidden(false);
        _controlsHideTimer.Stop();

        Topmost = _topmostBeforeFullScreen;
        WindowState = WindowState.Normal;
        WindowStyle = _windowStyleBeforeFullScreen;
        ResizeMode = _resizeModeBeforeFullScreen;
        Left = _leftBeforeFullScreen;
        Top = _topBeforeFullScreen;
        Width = _widthBeforeFullScreen;
        Height = _heightBeforeFullScreen;

        AppMenu.Visibility = Visibility.Visible;
        MainStatusBar.Visibility = Visibility.Visible;
        SidebarSplitter.Visibility = Visibility.Visible;
        TopMenuRow.Height = new GridLength(48);
        StatusRow.Height = new GridLength(28);
        SplitterColumn.Width = new GridLength(5);
        RestoreControlsToPlayerGrid();
        ControlsRow.Height = GridLength.Auto;
        SidebarColumn.MinWidth = 260;
        _channelsVisible = _channelsVisibleBeforeFullScreen;
        Sidebar.Visibility = _channelsVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = _channelsVisible ? new GridLength(360) : new GridLength(0);

        ControlsBar.Visibility = Visibility.Visible;
        WindowState = _windowStateBeforeFullScreen;
        Activate();
    }

    private void CoverCurrentMonitor()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var bounds = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new Point(bounds.Left, bounds.Top));
        var bottomRight = transform.Transform(new Point(bounds.Right, bounds.Bottom));

        Left = topLeft.X;
        Top = topLeft.Y;
        Width = bottomRight.X - topLeft.X;
        Height = bottomRight.Y - topLeft.Y;
    }

    private void VideoHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2) ToggleFullScreen();
        else ShowControls();
        e.Handled = true;
    }

    private void VideoHost_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowVideoContextMenu();
        e.Handled = true;
    }

    private void Window_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // MouseWheel routes via keyboard focus, not cursor position, so this has to be
        // handled at the window (tunneling) and hit-tested manually against VideoHost's
        // bounds to scope it to the playback area without blocking scrolling elsewhere
        // (e.g. the channel list).
        var pos = e.GetPosition(VideoHost);
        if (pos.X < 0 || pos.Y < 0 || pos.X > VideoHost.ActualWidth || pos.Y > VideoHost.ActualHeight) return;

        SetVolume(_state.VolumeLevel + (e.Delta > 0 ? 5 : -5));
        ShowControls();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) => ShowControls();
    private void ControlsBar_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => ShowControls();

    private void ShowControls()
    {
        SetCursorHidden(false);
        if (_isFullScreen)
        {
            AttachControlsToFullScreenPopup();
            RepositionFullScreenControlsPopup();
            FullScreenControlsPopup.IsOpen = true;
        }
        else
        {
            ControlsBar.Visibility = Visibility.Visible;
        }

        _controlsHideTimer.Stop();
        if (_isFullScreen) _controlsHideTimer.Start();
    }

    private void HideControlsIfFullScreen()
    {
        if (_isFullScreen)
        {
            FullScreenControlsPopup.IsOpen = false;
            // Hide the cursor together with the OSD. The video area is covered by
            // WPF overlay surfaces (including LibVLC's floating overlay window),
            // so the app-wide override is the only reliable way to reach them all.
            if (IsActive) SetCursorHidden(true);
        }
    }

    private void SetCursorHidden(bool hidden)
    {
        if (_cursorHidden == hidden) return;
        _cursorHidden = hidden;
        System.Windows.Input.Mouse.OverrideCursor = hidden ? System.Windows.Input.Cursors.None : null;
    }

    private void AttachControlsToFullScreenPopup()
    {
        if (ReferenceEquals(FullScreenControlsPopup.Child, ControlsBar)) return;

        if (PlayerGrid.Children.Contains(ControlsBar))
        {
            PlayerGrid.Children.Remove(ControlsBar);
        }

        ControlsBar.Visibility = Visibility.Visible;
        ControlsBar.Width = Math.Max(320, VideoHost.ActualWidth);
        ControlsBar.VerticalAlignment = VerticalAlignment.Bottom;
        ControlsBar.SetResourceReference(BackgroundProperty, "PanelBrush");
        ApplyOsdOpacity();
        ControlsBar.SetResourceReference(BorderBrushProperty, "StrokeBrush");
        ControlsBar.BorderThickness = new Thickness(1, 1, 1, 0);
        ControlsBar.Margin = new Thickness(0);
        FullScreenControlsPopup.Child = ControlsBar;
    }

    private void ToggleDarkMode_Click(object sender, RoutedEventArgs e)
    {
        _state.DarkMode = DarkModeMenuItem.IsChecked;
        ThemeManager.Apply(_state.DarkMode);
        _store.Save(_state);
        StatusText.Text = _state.DarkMode ? "Dark mode enabled." : "Light mode enabled.";
    }

    private void SetOsdOpacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem item && item.Tag is string s)
        {
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                SaveOsdOpacity(v);
            }
        }
    }

    private void SetCustomOsdOpacity_Click(object sender, RoutedEventArgs e)
    {
        var input = new System.Windows.Controls.TextBox
        {
            Text = Math.Round(Math.Clamp(_state.OsdOpacity, 0.0, 1.0) * 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Width = 90,
            Margin = new Thickness(0, 8, 0, 14),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        input.SelectAll();

        var okButton = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 80,
            IsCancel = true
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "Enter an OSD opacity from 0 to 100 percent:" });
        content.Children.Add(input);
        content.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "OSD opacity",
            Owner = this,
            Content = content,
            Width = 360,
            Height = 165,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        okButton.Click += (_, _) =>
        {
            if (!int.TryParse(input.Text, out var percent) || percent < 0 || percent > 100)
            {
                MessageBox.Show(dialog, "Enter a whole number from 0 to 100.", "Invalid opacity", MessageBoxButton.OK, MessageBoxImage.Warning);
                input.Focus();
                input.SelectAll();
                return;
            }

            dialog.DialogResult = true;
        };

        dialog.Loaded += (_, _) => input.Focus();
        if (dialog.ShowDialog() == true && int.TryParse(input.Text, out var selectedPercent))
        {
            SaveOsdOpacity(selectedPercent / 100.0);
        }
    }

    private void SaveOsdOpacity(double opacity)
    {
        _state.OsdOpacity = Math.Clamp(opacity, 0.0, 1.0);
        _store.Save(_state);
        ApplyOsdOpacity();
        StatusText.Text = $"Controls and volume OSD opacity set to {(int)Math.Round(_state.OsdOpacity * 100)}%.";
    }

    private void ApplyOsdOpacity()
    {
        var opacity = Math.Clamp(_state.OsdOpacity, 0.0, 1.0);
        if (VolumeOsdBorder is not null) VolumeOsdBorder.Opacity = opacity;
        if (ControlsBar is not null) ControlsBar.Opacity = _isFullScreen ? opacity : 1.0;
    }

    private void RestoreControlsToPlayerGrid()
    {
        FullScreenControlsPopup.IsOpen = false;
        if (ReferenceEquals(FullScreenControlsPopup.Child, ControlsBar))
        {
            FullScreenControlsPopup.Child = null;
        }

        if (!PlayerGrid.Children.Contains(ControlsBar))
        {
            PlayerGrid.Children.Add(ControlsBar);
        }

        Grid.SetRow(ControlsBar, 1);
        Grid.SetRowSpan(ControlsBar, 1);
        ControlsBar.Width = double.NaN;
        ControlsBar.VerticalAlignment = VerticalAlignment.Stretch;
        ControlsBar.SetResourceReference(BackgroundProperty, "PanelBrush");
        ControlsBar.SetResourceReference(BorderBrushProperty, "StrokeBrush");
        ControlsBar.BorderThickness = new Thickness(1, 1, 0, 0);
        ControlsBar.Margin = new Thickness(0);
        ControlsBar.Visibility = Visibility.Visible;
        ControlsBar.Opacity = 1.0;
    }

    private void RepositionFullScreenControlsPopup()
    {
        ControlsBar.Width = Math.Max(320, VideoHost.ActualWidth);
        ControlsBar.UpdateLayout();
        FullScreenControlsPopup.HorizontalOffset = 0;
        FullScreenControlsPopup.VerticalOffset = Math.Max(0, VideoHost.ActualHeight - ControlsBar.ActualHeight);
    }

    private async void UpdatePlaylist_Click(object sender, RoutedEventArgs e) => await LoadChannelsAsync(true);

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        _store.ClearChannelCache(_state.SelectedAccountId);
        var account = _state.EnsureSelectedAccount();
        account.LastPlaylistUpdatedUtc = null;
        _state.CachedAtUtc = null;
        _store.Save(_state);
        StatusText.Text = "Playlist cache cleared for this account.";
    }

    private async void AccountInfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Checking account...";
            var info = await _playlistService.FetchAccountInfoSummaryAsync(_state.Account, CancellationToken.None);
            MessageBox.Show(this, info, "Account information", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "Ready";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Account information error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ChangeAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_isChangingAccount) return;
        _isChangingAccount = true;

        SaveCurrentAudioState();
        _store.Save(_state);
        StopPlayback();
        var login = new LoginWindow { Owner = this };
        try
        {
            if (login.ShowDialog() != true) return;

            var result = login.LoginResult;
            AppLogger.Info("Changing account without restart. accountId=" + result.AccountId + "; updatePlaylist=" + result.UpdatePlaylist);

            ResetAccountView();

            // The account manager uses its own AppState instance and may have added
            // or removed accounts. Reload it so this window sees those changes.
            _state = _store.Load();
            _state.SelectedAccountId = result.AccountId;
            var selectedAccount = _state.EnsureSelectedAccount();
            selectedAccount.Settings = result.Account.Clone();
            _state.Account = selectedAccount.Settings.Clone();
            _store.Save(_state);

            InitializeBufferBox();
            InitializeVolumeControls();
            ApplyRemoteControlState(showStatus: false);
            await LoadChannelsAsync(result.UpdatePlaylist);
            AppLogger.Info("Account changed successfully. accountId=" + result.AccountId);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Account change failed.", ex);
            StatusText.Text = "Account change failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Account change error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isChangingAccount = false;
        }
    }

    private void ResetAccountView()
    {
        _filterCts?.Cancel();
        _sourceProbeCts?.Cancel();
        _sourceProbeCts?.Dispose();
        _sourceProbeCts = null;

        var previousMedia = _currentMedia;
        _currentMedia = null;
        previousMedia?.Dispose();
        _currentChannel = null;
        _currentCandidates = [];
        _currentCandidateIndex = -1;
        _searchIndex = null;
        _channels = [];
        _filteredChannels = [];
        _visibleChannels = [];
        _visibleEntries = [];
        _activeFolder = null;
        _activeLetter = null;

        SearchBox.Clear();
        ChannelList.ItemsSource = null;
        FolderBackButton.Visibility = Visibility.Collapsed;
        CountText.Text = "0 items";
        NowPlayingText.Text = "Select a channel to play";
        PlayPauseButton.Content = IconFactory.Create(IconFactory.Play);
        UpdateFavoriteButton();
        RefreshSubtitleTracks();
        UpdateStreamInfo();
        StatusText.Text = "Switching account...";
    }

    private void Buffer1_Click(object sender, RoutedEventArgs e) => SetBuffer(1000);
    private void Buffer3_Click(object sender, RoutedEventArgs e) => SetBuffer(3000);
    private void Buffer6_Click(object sender, RoutedEventArgs e) => SetBuffer(6000);
    private void Buffer10_Click(object sender, RoutedEventArgs e) => SetBuffer(10000);

    private void InitializeBufferBox()
    {
        _suppressBufferChange = true;
        try
        {
            var buffer = GetPlaybackBufferMs();
            BufferBox.SelectedIndex = buffer <= 1000 ? 0 : buffer <= 3000 ? 1 : buffer <= 6000 ? 2 : 3;
        }
        finally
        {
            _suppressBufferChange = false;
        }
    }

    private void BufferBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBufferChange) return;
        if (BufferBox.SelectedItem is ComboBoxItem item && item.Tag is string value && int.TryParse(value, out var ms))
        {
            SetBuffer(ms);
        }
    }

    private void SetBuffer(int ms)
    {
        _state.PlaybackBufferMs = Math.Max(500, Math.Min(30000, ms));
        _store.Save(_state);
        InitializeBufferBox();
        StatusText.Text = $"Buffer set to {_state.PlaybackBufferMs:N0} ms. Restart the stream to apply fully.";
        UpdateStreamInfo();
    }

    private int GetPlaybackBufferMs()
    {
        var value = _state.PlaybackBufferMs <= 0 ? 1000 : _state.PlaybackBufferMs;
        return Math.Max(500, Math.Min(30000, value));
    }

    private void RestartStream_Click(object sender, RoutedEventArgs e)
    {
        if (_currentChannel?.MediaKind == MediaKind.Live) ClearLiveDelay();
        if (_currentChannel is not null) PlayChannel(_currentChannel);
        else if (ChannelList.SelectedItem is ChannelListEntry { Channel: { } channel }) PlayChannel(channel);
    }

    private void GoLive_Click(object sender, RoutedEventArgs e)
    {
        if (_currentChannel?.MediaKind != MediaKind.Live)
        {
            StatusText.Text = "Go Live is only available for live streams.";
            return;
        }

        ClearPauseResumeState();
        ClearLiveDelay();
        PlayChannel(_currentChannel);
        StatusText.Text = "Back at live edge: " + _currentChannel.Name;
    }

    private void CopyStreamUrl_Click(object sender, RoutedEventArgs e)
    {
        var candidate = GetCurrentPlaybackCandidate();
        if (candidate is null)
        {
            StatusText.Text = "No stream URL to copy.";
            return;
        }
        Clipboard.SetText(candidate.Url);
        StatusText.Text = "Copied stream URL: " + candidate.Label;
    }

    private void ShowStreamInfo_Click(object sender, RoutedEventArgs e)
    {
        UpdateStreamInfo();
        var selectedSource = GetCurrentPlaybackCandidate();
        var message = StreamInfoText.Text;
        if (_currentChannel is not null)
        {
            message += "\n\nChannel: " + _currentChannel.Name;
            if (!string.IsNullOrWhiteSpace(_currentChannel.Group)) message += "\nGroup: " + _currentChannel.Group;
            message += "\nType: " + _currentChannel.MediaKind;
        }
        if (selectedSource is not null)
        {
            message += "\n\nCurrent source: " + selectedSource.Label + "\n" + selectedSource.Url;
        }
        MessageBox.Show(this, message, "Stream information", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is not ChannelListEntry { Channel: { } channel })
        {
            StatusText.Text = "Select a channel first.";
            return;
        }

        if (_state.FavoriteIds.Contains(channel.Id, StringComparer.OrdinalIgnoreCase))
        {
            _state.FavoriteIds.RemoveAll(id => string.Equals(id, channel.Id, StringComparison.OrdinalIgnoreCase));
            StatusText.Text = "Removed from favorites: " + channel.Name;
        }
        else
        {
            _state.FavoriteIds.Add(channel.Id);
            StatusText.Text = "Added to favorites: " + channel.Name;
        }
        _store.Save(_state);
        UpdateFavoriteButton(channel);
        if (_viewMode == 1) ApplyFilters();
    }

    private void UpdateFavoriteButton(Channel? channel = null)
    {
        channel ??= ChannelList.SelectedItem is ChannelListEntry { Channel: { } selected } ? selected : _currentChannel;
        var isFavorite = channel is not null && _state.FavoriteIds.Contains(channel.Id, StringComparer.OrdinalIgnoreCase);
        FavoriteButton.Content = IconFactory.Create(isFavorite ? IconFactory.Star : IconFactory.StarHollow);
        FavoriteButton.ToolTip = isFavorite ? "Remove from favorites" : "Add to favorites";
    }

    private void UpdateStreamInfo()
    {
        var source = GetCurrentPlaybackCandidate();
        var sourceLabel = source?.Label ?? "none";
        var channelName = _currentChannel?.Name ?? "none";
        var snapshot = _streamInfoTracker.Build(_mediaPlayer, _currentMedia, channelName, sourceLabel, "Auto", GetPlaybackBufferMs());
        var liveBehind = GetCurrentLiveBehind();
        StreamInfoText.Text = liveBehind >= TimeSpan.FromSeconds(1)
            ? snapshot.FullText + " | " + "Behind live: " + FormatLiveDelay(liveBehind)
            : snapshot.FullText;
    }

    private void ToggleRemoteControl_Click(object sender, RoutedEventArgs e)
    {
        _state.RemoteControlEnabled = !_state.RemoteControlEnabled;
        _store.Save(_state);
        ApplyRemoteControlState(showStatus: true);
    }

    private void ApplyRemoteControlState(bool showStatus)
    {
        try
        {
            if (_state.RemoteControlEnabled)
            {
                _remoteControlService.Start(_state.RemoteControlPort, HandleRemoteCommand);
                var urls = string.Join("  |  ", RemoteControlService.GetLocalUrls(_state.RemoteControlPort));
                if (showStatus) MessageBox.Show(this, "Remote control enabled:\n\n" + urls, "Remote Control", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "Remote control enabled: " + urls;
            }
            else
            {
                _remoteControlService.Stop();
                if (showStatus) StatusText.Text = "Remote control disabled.";
            }
        }
        catch (Exception ex)
        {
            _state.RemoteControlEnabled = false;
            _store.Save(_state);
            MessageBox.Show(this, "Remote control could not start.\n\n" + ex.Message + "\n\nTry another port or allow the app through Windows Firewall.", "Remote Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Remote control failed: " + ex.Message;
        }
    }

    private void HandleRemoteCommand(string command)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (command)
            {
                case "playpause": TogglePlayPause(); break;
                case "stop": StopPlayback(); break;
                case "previous": PlayRelative(-1); break;
                case "next": PlayRelative(1); break;
                case "fullscreen": ToggleFullScreen(); break;
                case "channels": ToggleChannels_Click(this, new RoutedEventArgs()); break;
                case "volume-up": SetVolume(_state.VolumeLevel + 5); break;
                case "volume-down": SetVolume(_state.VolumeLevel - 5); break;
                case "mute": SetMute(!_state.Muted); break;
                case "up": MoveSelection(-1); break;
                case "down": MoveSelection(1); break;
                case "select":
                    ActivateSelectedListEntry();
                    break;
                case "back":
                    if (_isFullScreen) ToggleFullScreen();
                    else if (!_channelsVisible) ToggleChannels_Click(this, new RoutedEventArgs());
                    else if (_activeFolder is not null || _activeLetter is not null) FolderBack_Click(this, new RoutedEventArgs());
                    break;
            }
        }));
    }

    private void MoveSelection(int delta)
    {
        if (_visibleEntries.Count == 0) return;
        var index = ChannelList.SelectedIndex;
        if (index < 0) index = 0;
        index = Math.Clamp(index + delta, 0, _visibleEntries.Count - 1);
        ChannelList.SelectedIndex = index;
        ChannelList.ScrollIntoView(ChannelList.SelectedItem);
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (IsTextInputFocused()) return;

        switch (e.Key)
        {
            case Key.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isFullScreen) ToggleFullScreen();
                e.Handled = true;
                break;
            case Key.Space:
                TogglePlayPause();
                e.Handled = true;
                break;
            case Key.Left:
                PlayRelative(-1);
                e.Handled = true;
                break;
            case Key.Right:
                PlayRelative(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.M:
                SetMute(!_state.Muted);
                e.Handled = true;
                break;
            case Key.S:
                StopPlayback();
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                SetVolume(_state.VolumeLevel + 5);
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                SetVolume(_state.VolumeLevel - 5);
                e.Handled = true;
                break;
            case Key.L when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                ToggleChannels_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private static bool IsTextInputFocused()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        while (focused is not null)
        {
            if (focused is System.Windows.Controls.TextBox) return true;
            focused = LogicalTreeHelper.GetParent(focused) ?? System.Windows.Media.VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private void ShowVideoContextMenu()
    {
        if (VideoHost.ContextMenu is null) return;
        ShowControls();
        VideoHost.ContextMenu.PlacementTarget = VideoHost;
        VideoHost.ContextMenu.IsOpen = true;
    }

    private sealed class ChannelListEntry
    {
        public string Name { get; private init; } = string.Empty;
        public string Group { get; private init; } = string.Empty;
        public Viewbox Icon { get; private init; } = IconFactory.Create(IconFactory.Tv, 18);
        public bool IsFolder { get; private init; }
        public string FolderName { get; private init; } = string.Empty;
        public Channel? Channel { get; private init; }

        public static ChannelListEntry ForFolder(string folderName, int itemCount)
        {
            return new ChannelListEntry
            {
                Name = folderName,
                Group = $"{itemCount:N0} channels - double-click to open",
                Icon = IconFactory.Create(IconFactory.Folder, 18),
                IsFolder = true,
                FolderName = folderName
            };
        }

        public static ChannelListEntry ForChannel(Channel channel)
        {
            return new ChannelListEntry
            {
                Name = channel.Name,
                Group = channel.Group,
                Icon = IconFactory.Create(channel.MediaKind == MediaKind.Live ? IconFactory.Tv : IconFactory.Cinema, 18),
                FolderName = NormalizeGroupName(channel.Group),
                Channel = channel
            };
        }
    }

    private sealed record SubtitleOption(int Id, string Name);

    private sealed record PauseResumeSnapshot(Channel Channel, int CandidateIndex, long TimeMs);

    private void CleanupPlayer()
    {
        try
        {
            PrepareWindowForShutdown();
            _sourceProbeCts?.Cancel();
            _sourceProbeCts?.Dispose();
            _sourceProbeCts = null;
            _positionTimer.Stop();
            _controlsHideTimer.Stop();
            _volumeOsdTimer.Stop();
            _remoteControlService.Dispose();
            _mediaPlayer?.Stop();
            _currentMedia?.Dispose();
            _mediaPlayer?.Dispose();
            _libVlc?.Dispose();
        }
        catch
        {
            // Ignore shutdown issues.
        }
    }

    private void PrepareWindowForShutdown()
    {
        _isShuttingDown = true;

        try
        {
            Mouse.Capture(null);
            Keyboard.ClearFocus();
            if (VideoHost.ContextMenu is not null) VideoHost.ContextMenu.IsOpen = false;
            FullScreenControlsPopup.IsOpen = false;
            VolumeOsdPopup.IsOpen = false;
            FullScreenControlsPopup.Child = null;
        }
        catch
        {
            // Best-effort UI cleanup before native player shutdown.
        }

        try
        {
            if (_isFullScreen)
            {
                if (!PlayerGrid.Children.Contains(ControlsBar))
                {
                    PlayerGrid.Children.Add(ControlsBar);
                }

                Grid.SetRow(ControlsBar, 1);
                Grid.SetRowSpan(ControlsBar, 1);
                ControlsBar.Width = double.NaN;
                ControlsBar.Visibility = Visibility.Visible;
                WindowStyle = _windowStyleBeforeFullScreen;
                ResizeMode = _resizeModeBeforeFullScreen;
                WindowState = WindowState.Normal;
            }

            Topmost = false;
        }
        catch
        {
            try { Topmost = false; } catch { }
        }
    }
}
