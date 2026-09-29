using ClassicWindowsIptvPlayer.Core;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Point = System.Windows.Point;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow : Window
{
    private readonly LoginResult _login;
    private readonly ConfigStore _store = new();
    private readonly PlaylistService _playlistService = new();
    private readonly EpgService _epgService = new();
    private readonly StreamProbeService _streamProbeService = new();
    private readonly RemoteControlService _remoteControlService = new();
    private readonly ConnectionBudget _recordingBudget = new();
    private RecordingIndex _recordingIndex = new();
    private AccountProfile? _recordingProfile;
    private RecordingService? _recordingService;
    private string? _activeRecordingId;
    private Task? _recordingStopTask;
    private Task? _recordingTransitionTask;
    private Action? _afterRecordingStops;
    private bool _stoppingForWindowClose;
    private bool _closeAfterRecordingStop;
    private string? _recordingFolder;
    private int _remoteGeneration;
    private readonly StreamInfoTracker _streamInfoTracker = new();
    private readonly GitHubUpdateService _updateService = new();
    private FeedbackOutbox _feedbackOutbox = null!;
    private readonly LibVLCSharp.WinForms.VideoView _videoView = new()
    {
        BackColor = System.Drawing.Color.Black,
        Dock = System.Windows.Forms.DockStyle.Fill,
        TabStop = false
    };
    private AppState _state;
    private List<Channel> _sourceChannels = [];

    private void RefreshOrganizedLibrary()
    {
        _channels = LibraryOrganization.Apply(_sourceChannels, _state.SelectedLibrary);
        _searchIndex = new MediaSearchIndex(_channels);
        ApplyFilters();
    }

    private void ExportOrganization_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export organization without credentials or stream URLs",
            Filter = "JSON files (*.json)|*.json",
            FileName = "cyrus-organization.json",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _store.ExportPortable(dialog.FileName, _state);
            MessageBox.Show(this, "Favorites, folders and organization rules were exported. Account credentials, provider URLs and playback URLs were excluded.",
                "Organization exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export failed: " + AppLogger.SanitizeException(ex), "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportOrganization_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Preview organization import", Filter = "JSON files (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var preview = _store.PreviewPortable(dialog.FileName, _state);
            if (preview.MatchingAccountCount == 0)
            {
                if (preview.Sources.Count == 0) throw new System.IO.InvalidDataException("The export has no libraries.");
                var selected = preview.Sources.Count == 1 ? 1 : 0;
                if (selected == 0)
                {
                    var choice = PromptForFolderName("Choose exported library", $"Library number (1–{preview.Sources.Count})", "1");
                    if (choice is null) return;
                    if (!int.TryParse(choice, out selected) || selected < 1 || selected > preview.Sources.Count)
                        throw new System.IO.InvalidDataException("Choose a valid exported library number.");
                }
                var source = preview.Sources[selected - 1];
                var destination = _state.EnsureSelectedAccount();
                var remapMessage = $"No account IDs match. Import exported library {selected} into '{destination.DisplayName}'?\n" +
                    $"Favorites: {source.FavoriteCount}; favorite folders: {source.FavoriteFolderCount}\n" +
                    $"Channel rules: {source.ChannelRuleCount}; group rules: {source.GroupRuleCount}\n\n" +
                    "This replaces that account's favorites and organization. Credentials and viewing progress stay as they are.";
                if (MessageBox.Show(this, remapMessage, "Preview organization import", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _store.ImportPortableIntoAccount(dialog.FileName, _state, source.AccountId, destination.Id);
                RefreshOrganizedLibrary();
                StatusText.Text = "Organization imported into current account.";
                return;
            }
            var message = $"Matching accounts: {preview.MatchingAccountCount} of {preview.AccountCount}\n" +
                $"Favorites: {preview.FavoriteCount}; favorite folders: {preview.FavoriteFolderCount}\n" +
                $"Channel rules: {preview.ChannelRuleCount}; group rules: {preview.GroupRuleCount}\n\n" +
                "Replace favorites and organization for matching accounts? Viewing progress and credentials stay as they are.";
            if (MessageBox.Show(this, message, "Preview organization import", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _store.ImportPortable(dialog.FileName, _state);
            RefreshOrganizedLibrary();
            StatusText.Text = "Organization imported.";
        }
        catch (Exception ex) { MessageBox.Show(this, AppLogger.SanitizeException(ex), "Import failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void BackupLocalData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Back up protected local data", Filter = "Cyrus backup (*.zip)|*.zip", FileName = "cyrus-local-backup.zip", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (System.IO.File.Exists(dialog.FileName))
            {
                MessageBox.Show(this, "Choose a new backup filename. Existing backups are never overwritten.", "Backup", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _store.Save(_state);
            _store.CreateBackup(dialog.FileName);
            MessageBox.Show(this, "Protected settings and saved libraries were backed up. This backup can be restored by the same Windows user profile.", "Backup complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, AppLogger.SanitizeException(ex), "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RestoreLocalData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Restore protected local data", Filter = "Cyrus backup (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "Restore settings and cached libraries from this backup? Current playback will stop. Existing local files retain recovery copies.",
                "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _store.RestoreBackup(dialog.FileName);
            StopPlayback();
            ResetAccountView();
            _state = _store.Load();
            _state.Account = _state.EnsureSelectedAccount().Settings.Clone();
            ApplyRemoteControlState(showStatus: false);
            await LoadChannelsAsync(false);
            StatusText.Text = "Local backup restored.";
        }
        catch (Exception ex) { MessageBox.Show(this, AppLogger.SanitizeException(ex), "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RestoreHiddenItems_Click(object sender, RoutedEventArgs e)
    {
        var library = _state.SelectedLibrary;
        var hiddenChannels = library.ChannelOrganization.Count(pair => pair.Value.Hidden);
        var hiddenGroups = library.GroupOrganization.Count(pair => pair.Value.Hidden);
        if (hiddenChannels + hiddenGroups == 0)
        {
            MessageBox.Show(this, "No hidden items or groups.", "Restore hidden items", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Show all {hiddenChannels} hidden items and {hiddenGroups} hidden groups?", "Restore hidden items",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var rule in library.ChannelOrganization.Values) rule.Hidden = false;
        foreach (var rule in library.GroupOrganization.Values) rule.Hidden = false;
        SaveOrganization();
    }
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _controlsHideTimer;
    private readonly DispatcherTimer _volumeOsdTimer;
    private readonly DispatcherTimer _loadingTimer;
    private int _loadingDotIndex;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private MediaSearchIndex? _searchIndex;
    private EpgGuide? _epgGuide;
    private DateTimeOffset? _epgFetchedAt;
    private DateTimeOffset? _epgLastAttempt;
    private bool _epgRefreshFailed;
    private EpgProgramme? _browseNow;
    private EpgProgramme? _browseNext;
    private static readonly TimeSpan GuideRefreshInterval = TimeSpan.FromHours(6);
    private List<Channel> _channels = [];
    private List<Channel> _filteredChannels = [];
    private List<Channel> _visibleChannels = [];
    private IReadOnlyList<ChannelListEntry> _visibleEntries = [];
    private readonly PlaybackBrowseState _playbackState = new();
    private List<SubtitleOption> _subtitleOptions = [];
    private Channel? _currentChannel => _playbackState.PlayingChannel;
    private PauseResumeSnapshot? _pausedPlayback;
    private string? _activeFolder;
    // null = favorite root, empty = Unfiled, otherwise a saved folder ID.
    private string? _activeFavoriteFolderId;
    private string? _activeLetter;
    // Set while browsing a series' episode list (drilled into via a series
    // placeholder). Takes over the channel list display; FolderBack_Click pops it
    // first, before folder/letter scope, so "back" always goes one level at a time.
    private string? _activeSeriesId;
    private string? _activeSeriesName;
    private List<Channel>? _activeSeriesEpisodes;
    private List<Channel>? _playingSeriesEpisodes;
    private int? _activeSeason;
    private double? _seriesReturnOffset;
    private VodSort _vodSort;
    private bool _isSeeking;
    private bool _isSeekingLiveTimeshift;
    private bool _updatingSeekSlider;
    private bool _channelsVisible = true;
    private double _savedSidebarWidth = 360;
    private bool _suppressBufferChange;
    private bool _isFullScreen;
    private bool _cursorHidden;
    private bool _hasPointerScreenPosition;
    private System.Drawing.Point _lastPointerScreenPosition;
    private DateTime _ignoreFullscreenPointerActivityUntilUtc = DateTime.MinValue;
    private bool _channelsVisibleBeforeFullScreen = true;
    private bool _isShuttingDown;
    // XAML initializes the slider to 100 before the saved value is restored. Keep
    // its ValueChanged event from overwriting the persisted volume during startup.
    private bool _suppressVolumeChange = true;
    private bool _suppressChannelSelectionChange;
    private bool _isChangingAccount;
    private bool _isCheckingForUpdates;
    private DateTime? _livePauseStartedUtc;
    private DateTime _lastEpgUiUpdateUtc = DateTime.MinValue;
    private TimeSpan _liveBehind = TimeSpan.Zero;
    private long? _pendingResumeTimeMs;
    private string _pendingResumeChannelId = string.Empty;
    private int _pendingResumeSeekAttempts;
    private int _mediaKindMode;
    private readonly LibraryNavigationHistory _libraryHistory = new();
    private LibraryPosition? _pendingLibraryRestore;
    private string _displayedCatalogScope = string.Empty;
    private int _viewMode = 2;
    // 0 = Folders (browse by playlist category), 1 = A-Z (browse by first letter), 2 = Items (flat, everything).
    private int _browseMode;
    private int _currentCandidateIndex => _playbackState.CandidateIndex;
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
    private CancellationTokenSource? _epgCts;
    private CancellationTokenSource? _libraryLoadCts;
    private CancellationTokenSource? _seriesLoadCts;
    private int _libraryLoadGeneration;
    private int _seriesLoadGeneration;
    private DateTime _lastProgressSaveUtc = DateTime.MinValue;
    private bool _skipNextProgressSave;
    private bool _suspendProgressSave;
    private TuneRequest? _offeredEndedRequest;
    // All stream startup, monitoring and recovery lives in the tuner; this
    // window only renders the states it reports and hosts the video surface.
    private ChannelTuner? _tuner;

    public MainWindow(LoginResult login)
    {
        _login = login;
        _state = _store.Load();
        _feedbackOutbox = CreateFeedbackOutbox(_state);
        AppLogger.Info("MainWindow constructing. accountId=" + login.AccountId + "; updatePlaylist=" + login.UpdatePlaylist);
        _state.SelectedAccountId = login.AccountId;
        var selectedAccount = _state.EnsureSelectedAccount();
        selectedAccount.Settings = login.Account.Clone();
        _state.Account = selectedAccount.Settings.Clone();
        _recordingIndex = _store.LoadRecordingIndex(login.AccountId) ?? new RecordingIndex();
        _store.Save(_state);

        InitializeComponent();
        ChannelList.ContextMenu = new System.Windows.Controls.ContextMenu();
        _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingDotIndex = (_loadingDotIndex + 1) % 4;
            UpdateLoadingDots();
        };
        InitializeVideoSurface();
        DarkModeMenuItem.IsChecked = _state.DarkMode;
        EpgEnabledMenuItem.IsChecked = _state.EpgEnabled;
        UpdateChecksMenuItem.IsChecked = _state.CheckForUpdatesOnStartup;
        UpdateEpgEnabledUi();
        ApplyDefaultStartupFilters();
        InitializeButtonIcons();
        UpdateSearchScopeButtons();
        UpdateMediaKindButtons();
        UpdateViewModeButtons();

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilters(); };

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _scheduledRecordingTimer.Tick += ScheduledRecordingTimer_Tick;
        _positionTimer.Tick += (_, _) =>
        {
            UpdatePlaybackPosition();
            UpdateLiveDelayUi();
            UpdateStreamInfo();
            if (_state.EpgEnabled && DateTime.UtcNow - _lastEpgUiUpdateUtc >= TimeSpan.FromSeconds(30))
            {
                _lastEpgUiUpdateUtc = DateTime.UtcNow;
                UpdateEpgDisplay();
                if (_epgCts is null && (!_epgLastAttempt.HasValue || DateTimeOffset.UtcNow - _epgLastAttempt.Value >= GuideRefreshInterval) &&
                    (!_epgFetchedAt.HasValue || DateTimeOffset.UtcNow - _epgFetchedAt.Value >= GuideRefreshInterval))
                    _ = RefreshEpgAsync(showStatus: false);
            }
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
        ContentRendered += MainWindow_ContentRendered;
        Activated += (_, _) =>
        {
            if (_isFullScreen && !_isShuttingDown) ShowControls();
        };
        Deactivated += (_, _) =>
        {
            if (_cursorHidden) SetCursorHidden(false);
        };
        Closing += async (_, e) =>
        {
            if (!_closeAfterRecordingStop && _recordingService?.ActiveCount > 0)
            {
                e.Cancel = true;
                if (_stoppingForWindowClose) return;
                _stoppingForWindowClose = true;
                await StopRecordingsSafelyAsync(RecordingStopReason.Shutdown);
                _stoppingForWindowClose = false;
                _closeAfterRecordingStop = true;
                Close();
                return;
            }
            _isShuttingDown = true;
            _scheduledRecordingTimer.Stop();
            _scheduledRecordings?.Dispose();
            _scheduledRecordings = null;
            _recordingService?.Dispose();
            _recordingService = null;
            _feedbackOutbox.Dispose();
            _multiViewWindow?.Close();
            _multiViewWindow = null;
            SaveRecordingIndex(_state.SelectedAccountId);
            SavePlaybackProgress(force: true);
            SaveCurrentAudioState();
            CleanupPlayer();
        };
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        if (_state.CheckForUpdatesOnStartup)
        {
            _ = CheckForUpdatesAsync(showUpToDateMessage: false);
        }
    }

    private void ApplyDefaultStartupFilters()
    {
        _browseMode = 0;
        _mediaKindMode = 1;
        _viewMode = 0;
        _activeFolder = null;
        _activeFavoriteFolderId = null;
        _activeLetter = null;
        _activeSeriesId = null;
        _activeSeriesName = null;
        _activeSeriesEpisodes = null;
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

            // LibVLC's native startup and reading the cached playlist off disk
            // are independent of each other, so run them side by side instead
            // of making one wait behind the other -- this is most of the win
            // on a cached-playlist launch, where the playlist load is fast but
            // used to sit behind the (slower) player init anyway.
            var playerTask = InitializePlayerAsync();
            var channelsTask = LoadChannelsAsync(_login.UpdatePlaylist, keepLoadingVisible: true);

            await Task.WhenAll(playerTask, channelsTask);
            InitializeBufferBox();
            InitializeVolumeControls();
            ApplyRemoteControlState(showStatus: false);
            HideAccountLoading();
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainWindow loaded failed.", ex);
            HideAccountLoading();
            StatusText.Text = "Startup error: " + AppLogger.SanitizeText(ex.Message);
            MessageBox.Show(this, AppLogger.SanitizeException(ex), "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializePlayerAsync()
    {
        AppLogger.Info("Initializing LibVLC player. bufferMs=" + GetPlaybackBufferMs() + "; volume=" + _state.VolumeLevel + "; muted=" + _state.Muted);
        var buffer = GetPlaybackBufferMs();
        _libVlc = await Task.Run(() =>
        {
            LibVLCSharp.Shared.Core.Initialize();
            return new LibVLC(
                "--no-video-title-show",
                "--embedded-video",
                "--avcodec-hw=none",
                "--network-caching=" + buffer,
                "--live-caching=" + buffer,
                "--file-caching=" + buffer,
                "--http-reconnect");
        });

        // Record native player errors (HTTP status, demux failures, ...) so failed
        // tune attempts in the app log show the server's actual refusal reason.
        _libVlc.Log += (_, e) =>
        {
            if (e.Level < LibVLCSharp.Shared.LogLevel.Error) return;
            AppLogger.Warn("libvlc " + (e.Module ?? "core") + ": " + e.Message);
        };

        _tuner = new ChannelTuner(_libVlc) { MaxAttempts = GetReconnectAttempts() };
        _tuner.PlayerAttached += OnTunerPlayerAttached;
        _tuner.ActiveMediaChanged += OnTunerActiveMediaChanged;
        _tuner.PlayerDetaching += OnTunerPlayerDetaching;
        _tuner.StateChanged += OnTunerStateChanged;
        _positionTimer.Start();
            InitializeRecordingService();
        RefreshSubtitleTracks();
        AppLogger.Info("LibVLC player initialized.");
    }

    private void InitializeRecordingService()
    {
        _scheduledRecordingTimer.Stop();
        _scheduledRecordings?.Dispose();
        _scheduledRecordings = null;
        _recordingService?.Dispose();
        SaveRecordingIndex(_state.SelectedAccountId);
        _recordingProfile = null;
        _recordingIndex = _store.LoadRecordingIndex(_state.SelectedAccountId) ?? new RecordingIndex();
        _recordingFolder = NormalizeRecordingFolder(_state.RecordingFolder);
        _recordingService = new RecordingService(_recordingBudget, _recordingIndex);
        _recordingService.StateChanged += snapshot => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isShuttingDown) return;
            if (snapshot.IsLive) _activeRecordingId = snapshot.RecordingId;
            else if (_activeRecordingId == snapshot.RecordingId) _activeRecordingId = null;
            RecordingStatusText.Text = $"{snapshot.StateText}: {snapshot.ChannelName}  {snapshot.Elapsed:mm\\:ss}  {snapshot.SizeText}";
            UpdateRecordingControls();
            RecordButton.IsEnabled = !snapshot.IsLive && _currentChannel?.MediaKind == MediaKind.Live && _tuner?.CanRecordActiveStream == true;
        }));
        _recordingService.CaptureFinished += outcome =>
        {
            var accountId = outcome.Entry?.AccountId ?? _state.SelectedAccountId;
            if (Dispatcher.CheckAccess())
                SaveRecordingIndex(accountId);
            else
                Dispatcher.Invoke(() => SaveRecordingIndex(accountId));

            Dispatcher.BeginInvoke(new Action(() =>
            {
            _activeRecordingId = null;
            var finalEntry = outcome.Entry is null ? null : _recordingIndex.Find(outcome.Entry.AccountId, outcome.Entry.Id);
            var hasUsableFile = outcome.HasPlayableFile || finalEntry is { ByteSize: > 0 };
            if (outcome.Succeeded || hasUsableFile)
            {
                var fileName = !string.IsNullOrWhiteSpace(outcome.FileName) ? outcome.FileName : finalEntry?.FileName ?? "recording";
                var size = outcome.ByteSize > 0 ? outcome.ByteSize : finalEntry?.ByteSize ?? 0;
                var stopped = outcome.Result == ClassicWindowsIptvPlayer.Core.RecordingOutcome.Stopped ||
                    finalEntry?.Outcome == ClassicWindowsIptvPlayer.Core.RecordingOutcome.Stopped;
                var message = $"{(stopped ? "Recording stopped" : "Recording finished")}: {fileName} ({RecordingPolicy.FormatBytes(size)})";
                RecordingStatusText.Text = message;
                StatusText.Text = message;
            }
            else
            {
                RecordingStatusText.Text = "Recording failed: " + AppLogger.SanitizeText(outcome.FailureReason);
                StatusText.Text = RecordingStatusText.Text;
            }
            StopRecordingButton.IsEnabled = false;
            UpdateRecordingControls();
            UpdateRecentRecordingButton();
            }));
        };
        UpdateRecordingControls();
        UpdateRecentRecordingButton();
        InitializeScheduledRecordingCoordinator();
    }

    private void SaveRecordingIndex(string accountId)
    {
        try { if (!string.IsNullOrWhiteSpace(accountId)) _store.SaveRecordingIndex(accountId, _recordingIndex); }
        catch (Exception ex) { AppLogger.Error("Recording index save failed.", ex); }
    }

    private void UpdateRecordingControls()
    {
        if (RecordButton is null) return;
        var hasActiveRecording = !string.IsNullOrEmpty(_activeRecordingId) &&
            _recordingService?.Find(_activeRecordingId)?.IsLive == true;
        var activeStreamAvailable = _tuner?.CanRecordActiveStream == true;
        RecordButton.IsEnabled = !_isShuttingDown && _recordingService is not null && _currentChannel?.MediaKind == MediaKind.Live &&
            activeStreamAvailable &&
            !hasActiveRecording;
        RecordButton.ToolTip = activeStreamAvailable
            ? "Record the playing stream. Playback briefly retunes to add or remove file output."
            : "Recording is available while a supported live stream is playing.";
        ToolTipService.SetShowOnDisabled(RecordButton, true);
        StopRecordingButton.Visibility = hasActiveRecording ? Visibility.Visible : Visibility.Collapsed;
        StopRecordingButton.IsEnabled = !_isShuttingDown && hasActiveRecording;
    }

    private void UpdateRecentRecordingButton()
    {
        var recent = _recordingIndex.Recent(_state.SelectedAccountId).FirstOrDefault(entry =>
            entry.Outcome != ClassicWindowsIptvPlayer.Core.RecordingOutcome.Recording && entry.ByteSize > 0);
        OpenRecordingButton.IsEnabled = recent is not null;
        var recentPath = recent is null ? null : !string.IsNullOrWhiteSpace(recent.FilePath)
            ? recent.FilePath
            : System.IO.Path.IsPathRooted(recent.FileName) ? recent.FileName
            : null;
        if (recentPath is not null && !System.IO.File.Exists(recentPath)) recentPath = null;
        OpenRecordingButton.Tag = recentPath;
        OpenRecordingButton.IsEnabled = recentPath is not null;
    }

    private static string? NormalizeRecordingFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try { return System.IO.Directory.Exists(folder) ? System.IO.Path.GetFullPath(folder) : null; }
        catch (Exception exception) when (exception is ArgumentException or System.IO.IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    private string GetRecordingFolder() => _recordingFolder ?? GetDefaultRecordingFolder();

    private static string GetDefaultRecordingFolder()
    {
        return System.IO.Path.Combine(AppContext.BaseDirectory, "Recordings");
    }

    private void ChooseRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where instant and scheduled recordings are saved by default.",
            ShowNewFolderButton = true,
            SelectedPath = System.IO.Directory.Exists(GetRecordingFolder()) ? GetRecordingFolder() : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        try
        {
            System.IO.Directory.CreateDirectory(dialog.SelectedPath);
            var destination = RecordingPolicy.ValidateDestination(dialog.SelectedPath);
            if (!destination.IsValid)
            {
                MessageBox.Show(this, destination.Message, "Recording folder unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _recordingFolder = destination.Folder;
            _state.RecordingFolder = destination.Folder;
            _store.Save(_state);
            StatusText.Text = "Default recording folder: " + destination.Folder;
        }
        catch (Exception exception)
        {
            AppLogger.Error("Could not save the default recording folder.", exception);
            MessageBox.Show(this, "The recording folder could not be saved. " + AppLogger.SanitizeText(exception.Message),
                "Recording folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingService is null || _currentChannel is null || _currentChannel.MediaKind != MediaKind.Live) return;
        if (_tuner?.CanRecordActiveStream != true)
        {
            const string explanation = "Recording is available while a supported live stream is playing.";
            RecordingStatusText.Text = explanation;
            MessageBox.Show(this, explanation, "Recording unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateRecordingControls();
            return;
        }
        var accountId = _state.SelectedAccountId;
        // The tuner owns the single playback input and briefly retunes it with
        // display + file output; never start the recorder's independent URL path.
        var profile = _recordingProfile ?? new AccountProfile();

        var folder = GetRecordingFolder();
        System.IO.Directory.CreateDirectory(folder);
        var container = _tuner.GetActiveRecordingContainer();
        var planTimeUtc = DateTimeOffset.UtcNow;
        var plan = RecordingPolicy.Evaluate(profile, folder, _currentChannel, TimeSpan.Zero,
            planTimeUtc, container, GetPlayingProgramme(), _recordingBudget,
            targets: new RecordingTargets([], path => System.IO.File.Exists(path)));
        if (!plan.IsAllowed || plan.Plan is null)
        {
            MessageBox.Show(this, plan.Message, "Cannot record", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var requestedStartUtc = DateTimeOffset.UtcNow;
        RecordButton.IsEnabled = false;
        RecordingStatusText.Text = "Starting: " + _currentChannel.Name;
        var tuner = _tuner;
        if (tuner is null)
        {
            UpdateRecordingControls();
            return;
        }
        var outcome = await _recordingService.StartRetunedAsync(new RecordingStartRequest
        {
            DestinationPath = plan.Plan.FilePath, AccountId = accountId,
            ChannelId = _currentChannel.Id, ChannelKey = ItemIdentity.For(_currentChannel), ChannelName = _currentChannel.Name,
            Profile = profile, Programme = GetPlayingProgramme(),
            Container = container, RequestedStartUtc = requestedStartUtc,
            BufferMs = GetPlaybackBufferMs()
        }, tuner.StartRecordingOutputAsync, tuner.StopRecordingOutputAsync, CancellationToken.None);
        if (!outcome.Accepted)
        {
            if (string.Equals(_activeRecordingId, outcome.RecordingId, StringComparison.Ordinal))
                _activeRecordingId = null;
            var finalEntry = outcome.Entry is null ? null : _recordingIndex.Find(outcome.Entry.AccountId, outcome.Entry.Id);
            if (outcome.HasPlayableFile || finalEntry is { ByteSize: > 0 })
            {
                var fileName = !string.IsNullOrWhiteSpace(outcome.FileName) ? outcome.FileName : finalEntry?.FileName ?? "recording";
                var size = outcome.ByteSize > 0 ? outcome.ByteSize : finalEntry?.ByteSize ?? 0;
                RecordingStatusText.Text = $"Recording saved: {fileName} ({RecordingPolicy.FormatBytes(size)})";
                StatusText.Text = RecordingStatusText.Text;
                UpdateRecentRecordingButton();
            }
            else
            {
                var reason = AppLogger.SanitizeText(outcome.FailureReason);
                var previousSuccess = _recordingIndex.Recent(accountId).FirstOrDefault(entry =>
                    entry.Outcome != ClassicWindowsIptvPlayer.Core.RecordingOutcome.Recording && entry.ByteSize > 0);
                var isDuplicateFailure = string.IsNullOrWhiteSpace(reason) && previousSuccess is not null &&
                    previousSuccess.RequestedStartUtc <= (outcome.Entry?.RequestedStartUtc ?? DateTimeOffset.MaxValue);
                if (!isDuplicateFailure)
                {
                    RecordingStatusText.Text = "Recording failed: " + (string.IsNullOrWhiteSpace(reason) ? "The capture did not produce a usable file." : reason);
                    StatusText.Text = RecordingStatusText.Text;
                }
            }
            UpdateRecordingControls();
            return;
        }
        SaveRecordingIndex(accountId);
        // A Stop during Starting can finish the capture before this awaited start
        // continuation resumes. Do not resurrect its ID or overwrite the final status.
        if (_recordingService.Find(outcome.RecordingId)?.IsLive != true)
        {
            UpdateRecordingControls();
            return;
        }
        _activeRecordingId = outcome.RecordingId;
        RecordingStatusText.Text = "Recording: " + _currentChannel.Name;
        UpdateRecordingControls();
    }

    private async void StopRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingService is null || string.IsNullOrEmpty(_activeRecordingId)) return;
        RecordingStatusText.Text = "Stopping recording…";
        StopRecordingButton.IsEnabled = false;
        await StopRecordingsSafelyAsync(RecordingStopReason.User);
        SaveRecordingIndex(_state.SelectedAccountId);
    }

    private void OpenRecentRecording_Click(object sender, RoutedEventArgs e)
    {
        var path = (sender as System.Windows.Controls.MenuItem)?.Tag as string ?? OpenRecordingButton.Tag as string;
        if (path is null || !System.IO.File.Exists(path))
        {
            MessageBox.Show(this, "The recent recording file could not be found.", "Open recording", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, AppLogger.SanitizeException(ex), "Open recording failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private EpgProgramme? GetPlayingProgramme()
    {
        if (_currentChannel is null || _currentChannel.MediaKind != MediaKind.Live) return null;
        _state.SelectedLibrary.GuideMappings.TryGetValue(ItemIdentity.For(_currentChannel), out var mappedId);
        return _epgGuide?.GetNowNext(_currentChannel, mappedId: mappedId,
            offsetMinutes: _state.SelectedLibrary.GuideOffsetMinutes).Now;
    }

    private void InitializeVideoSurface()
    {
        VideoViewHost.Child = _videoView;
        _videoView.MouseDown += (_, e) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
            if (e.Clicks >= 2) ToggleFullScreen();
            else ShowControls();
        }));
        _videoView.MouseUp += (_, e) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowVideoContextMenu();
        }));
        _videoView.MouseMove += (_, _) => Dispatcher.BeginInvoke(new Action(HandlePointerMovement));
        _videoView.MouseWheel += (_, e) => Dispatcher.BeginInvoke(new Action(() => ApplyVolumeWheel(e.Delta)));
        _videoView.GotFocus += (_, _) => Dispatcher.BeginInvoke(new Action(() => Focus()));
        _videoView.HandleCreated += (_, _) => AppLogger.Info("Video surface handle created. hwnd=0x" + _videoView.Handle.ToInt64().ToString("X"));
        _videoView.HandleDestroyed += (_, _) =>
        {
            if (_isShuttingDown || _mediaPlayer is null) return;
            AppLogger.Warn("Video surface handle was destroyed during playback; stopping to prevent a fallback output window.");
            try { _mediaPlayer.Stop(); }
            catch { }
            _tuner?.Stop();
        };
    }

    // Called by the tuner on its worker thread, synchronously before Play, so the
    // player must be bound to the video surface before this returns.
    private void OnTunerPlayerAttached(MediaPlayer player, Media media, TuneRequest request)
    {
        Dispatcher.Invoke(() =>
        {
            if (_isShuttingDown) return;
            // Under rapid zapping, attach callbacks can reach the UI thread out of
            // order; only the tuner's current player may own the video surface.
            if (!ReferenceEquals(player, _tuner?.CurrentPlayer)) return;
            _mediaPlayer = player;
            _currentMedia = media;
            _preferredAudioApplied = false;
            _preferredSubtitleApplied = false;

            // Materialize and assign the persistent child HWND before Play. If no
            // drawable is present, LibVLC falls back to a top-level window titled
            // "VLC Direct3D11 output" for streams such as Fokus TV.
            HideIdleBackground();
            VideoViewHost.UpdateLayout();
            _videoView.CreateControl();
            var videoHandle = _videoView.Handle;
            _videoView.MediaPlayer = player;
            if (videoHandle == IntPtr.Zero || player.Hwnd != videoHandle)
            {
                throw new InvalidOperationException("LibVLC did not accept the embedded video surface handle.");
            }
            if (!VerifyVideoSurfaceOwner(this))
                throw new InvalidOperationException("LibVLC video surface is not owned by the expected player window.");
            AppLogger.Info("Video surface attached. expectedHwnd=0x" + videoHandle.ToInt64().ToString("X") +
                "; playerHwnd=0x" + player.Hwnd.ToInt64().ToString("X") +
                "; handleCreated=" + _videoView.IsHandleCreated + "; channel=" + request.ChannelName);

            AttachUiEventHandlers(player);
            ApplySavedAudioState();
        });
    }

    // Called by the tuner before it tears a player down; every reference to it —
    // above all the video view binding — must be gone before this returns, because
    // detaching later would touch the disposed player's native handle and crash.
    private void OnTunerPlayerDetaching(MediaPlayer player)
    {
        if (_isShuttingDown) return;
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (_isShuttingDown) return;
                if (ReferenceEquals(_videoView.MediaPlayer, player)) _videoView.MediaPlayer = null;
                if (ReferenceEquals(_mediaPlayer, player))
                {
                    _mediaPlayer = null;
                    _currentMedia = null;
                    _preferredAudioApplied = false;
                    _preferredSubtitleApplied = false;
                }
            });
        }
        catch (Exception ex)
        {
            // Dispatcher may already be shutting down; the tuner proceeds either way.
            AppLogger.Warn("Player detach handler failed. " + AppLogger.SanitizeText(ex.Message));
        }
    }

    // A timeshift cursor replaces only the active Media input, not the player.
    // Update the UI's ownership reference synchronously before the tuner retires
    // the previous Media instance.
    private void OnTunerActiveMediaChanged(MediaPlayer player, Media media, TuneRequest request)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (_isShuttingDown || !ReferenceEquals(player, _mediaPlayer) ||
                    !ReferenceEquals(player, _tuner?.CurrentPlayer)) return;
                _currentMedia = media;
                _preferredAudioApplied = false;
                _preferredSubtitleApplied = false;
                ApplySavedAudioState();
                UpdateStreamInfo();
            });
        }
        catch (Exception exception)
        {
            AppLogger.Warn("Active media UI update failed. " + AppLogger.SanitizeText(exception.Message));
        }
    }

    // UI-only reactions to the active player. Recovery is the tuner's job; these
    // handlers ignore events from players the tuner has already replaced.
    private void AttachUiEventHandlers(MediaPlayer player)
    {
        player.Playing += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(player, _mediaPlayer)) return;
            PlayPauseButton.Content = IconFactory.Create(IconFactory.Pause);
            HideIdleBackground();
            RefreshSubtitleTracks();
            ShowControls();
            ApplyPreferredTracks(player);
            UpdateStreamInfo();
        }));
        player.Paused += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(player, _mediaPlayer)) return;
            PlayPauseButton.Content = IconFactory.Create(IconFactory.Play);
        }));
        player.Stopped += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(player, _mediaPlayer)) return;
            PlayPauseButton.Content = IconFactory.Create(IconFactory.Play);
            if (_mediaPlayer?.IsPlaying != true) ShowIdleBackground();
        }));
    }

    private void OnTunerStateChanged(TunerStateSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isShuttingDown) return;
            // A state for a channel the user has already zapped away from is stale.
            if (!_playbackState.Accepts(snapshot.Request)) return;
            RetryPlaybackButton.Visibility = snapshot.Status == TunerStatus.Failed ? Visibility.Visible : Visibility.Collapsed;

            switch (snapshot.Status)
            {
                case TunerStatus.Tuning:
                    StatusText.Text = snapshot.Attempt <= 1
                        ? "Opening: " + snapshot.Request?.ChannelName
                        : $"Reconnecting ({snapshot.Attempt}/{snapshot.MaxAttempts}): {snapshot.Request?.ChannelName}";
                    UpdateRecordingControls();
                    break;
                case TunerStatus.Playing:
                    StatusText.Text = AppLogger.SanitizeText("Playing: " + snapshot.Request?.ChannelName + " • " + snapshot.Request?.SourceLabel);
                    UpdateStreamInfo();
                    UpdateRecordingControls();
                    break;
                case TunerStatus.Ended:
                    StatusText.Text = "Finished: " + snapshot.Request?.ChannelName;
                    if (_currentChannel is { MediaKind: not MediaKind.Live } finished)
                    {
                        ViewingHistory.Finish(_state.SelectedLibrary, finished, DateTime.UtcNow);
                        if (!_suspendProgressSave) _store.Save(_state);
                        if (_viewMode is 2 or 3) ApplyFilters();
                        if (!ReferenceEquals(_offeredEndedRequest, snapshot.Request))
                        {
                            _offeredEndedRequest = snapshot.Request;
                            _ = OfferNextEpisodeAsync(finished);
                        }
                    }
                    break;
                case TunerStatus.Failed:
                    StatusText.Text = _catchupPlayback is { } archive
                        ? Catchup.FailureMessage(archive.Source, archive.Programme, DateTimeOffset.UtcNow)
                        : "Could not start the stream. " + AppLogger.SanitizeText(snapshot.Detail);
                    UpdateStreamInfo();
                    UpdateRecordingControls();
                    break;
            }
        }));
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
        RestartButton.Content = IconFactory.Labeled("Restart playing", IconFactory.Restart);
        CopyUrlButton.Content = IconFactory.Labeled("Copy playing URL", IconFactory.Copy);
        RestartButton.ToolTip = "Restart the playing channel";
        CopyUrlButton.ToolTip = "Copy the playing channel's URL";
        UpdateFavoriteButton();
    }

    private async Task LoadChannelsAsync(bool updatePlaylist, bool keepLoadingVisible = false)
    {
        _libraryLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _libraryLoadCts = cts;
        var generation = ++_libraryLoadGeneration;
        var accountId = _state.SelectedAccountId;
        AppLogger.Info("LoadChannelsAsync begin. updatePlaylist=" + updatePlaylist + "; accountId=" + _state.SelectedAccountId);
        ShowAccountLoading(updatePlaylist
            ? "Connecting to your provider and updating the playlist..."
            : "Loading your saved playlist...");

        try
        {
            StatusText.Text = updatePlaylist ? "Updating playlist..." : "Loading cached playlist...";
            var saved = await Task.Run(() => _store.LoadChannelCache(accountId), cts.Token);
            if (!IsCurrentLoad()) return;
            if (_store.RecoveryNotice is { } recoveryNotice)
                MessageBox.Show(this, recoveryNotice, "Saved library recovery", MessageBoxButton.OK, MessageBoxImage.Information);
            if (saved.Count > 0)
            {
                _sourceChannels = saved;
                _channels = await Task.Run(() => LibraryOrganization.Apply(saved, _state.SelectedLibrary), cts.Token);
                _searchIndex = await Task.Run(() => new MediaSearchIndex(_channels), cts.Token);
                if (!IsCurrentLoad()) return;
                await ApplyFiltersAsync();
                if (_state.EpgEnabled)
                {
                    try
                    {
                        var cachedGuide = await Task.Run(() => _store.LoadGuideCache(accountId), cts.Token);
                        if (!IsCurrentLoad()) return;
                        if (cachedGuide is not null)
                        {
                            _epgGuide = EpgGuide.FromSnapshot(cachedGuide);
                            _epgFetchedAt = cachedGuide.FetchedAt;
                            UpdateEpgDisplay();
                        }
                    }
                    catch (Exception ex) { AppLogger.Warn("Saved guide unavailable. " + AppLogger.SanitizeText(ex.Message)); }
                }
            }
            if (updatePlaylist || saved.Count == 0)
            {
                var account = _state.Account.Clone();
                var progress = new Progress<string>(stage => { if (IsCurrentLoad()) SetAccountLoadingMessage(stage); });
                var result = await _playlistService.LoadPlaylistResultAsync(account, cts.Token, progress);
                if (!IsCurrentLoad()) return;
                var missingSavedKind = saved.Select(item => item.MediaKind).Distinct()
                    .Any(kind => !result.Channels.Any(item => item.MediaKind == kind));
                if ((result.IsPartial || missingSavedKind) && saved.Count > 0)
                {
                    StatusText.Text = (missingSavedKind ? "Provider omitted a saved media category." : result.Message) +
                                      " Using saved library. Retry from Playlist menu.";
                    return;
                }
                SetAccountLoadingMessage($"Preparing {result.Channels.Count:N0} playlist items...");
                var replacement = result.Channels.ToList();
                var organized = await Task.Run(() => LibraryOrganization.Apply(replacement, _state.SelectedLibrary), cts.Token);
                var index = await Task.Run(() => new MediaSearchIndex(organized), cts.Token);
                if (!IsCurrentLoad()) return;
                if (!result.IsPartial)
                {
                    SetAccountLoadingMessage("Saving verified library...");
                    await Task.Run(() => _store.SaveChannelCache(accountId, replacement), cts.Token);
                    if (!IsCurrentLoad()) return;
                    _state.MarkSelectedPlaylistUpdated(DateTime.UtcNow);
                    _store.Save(_state);
                }
                _sourceChannels = replacement;
                _channels = organized;
                _searchIndex = index;
                await ApplyFiltersAsync();
                StatusText.Text = result.IsPartial ? result.Message + " Retry from Playlist menu." : $"Loaded {_channels.Count:N0} items";
            }
            else StatusText.Text = $"Loaded {_channels.Count:N0} saved items";
            AppLogger.Info("LoadChannelsAsync complete. channels=" + _channels.Count);
            if (_state.EpgEnabled)
            {
                try
                {
                    var snapshot = await Task.Run(() => _store.LoadGuideCache(accountId), cts.Token);
                    if (!IsCurrentLoad()) return;
                    if (snapshot is not null)
                    {
                        _epgGuide = EpgGuide.FromSnapshot(snapshot);
                        _epgFetchedAt = snapshot.FetchedAt;
                        UpdateEpgDisplay();
                    }
                }
                catch (Exception ex) { AppLogger.Warn("Saved guide unavailable. " + AppLogger.SanitizeText(ex.Message)); }
                if (!_epgFetchedAt.HasValue || DateTimeOffset.UtcNow - _epgFetchedAt.Value >= GuideRefreshInterval)
                    _ = RefreshEpgAsync(showStatus: false);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (generation == _libraryLoadGeneration) StatusText.Text = _channels.Count > 0 ? "Refresh canceled. Using saved library." : "Loading canceled. Retry or use a saved library.";
        }
        catch (Exception ex)
        {
            if (!IsCurrentLoad()) return;
            AppLogger.Error("Library load failed.", ex);
            StatusText.Text = _channels.Count > 0 ? "Provider failed. Using saved library. Retry from Playlist menu." : "Provider failed. Retry from Playlist menu.";
            SetAccountLoadingMessage("Provider unavailable: " + AppLogger.SanitizeText(ex.Message));
        }
        finally
        {
            if (generation == _libraryLoadGeneration)
            {
                _libraryLoadCts = null;
                if (!keepLoadingVisible) HideAccountLoading();
            }
            cts.Dispose();
        }
        bool IsCurrentLoad() => generation == _libraryLoadGeneration && accountId == _state.SelectedAccountId && !cts.IsCancellationRequested;
    }

    private void CancelLibraryLoad_Click(object sender, RoutedEventArgs e)
    {
        _libraryLoadCts?.Cancel();
        AccountLoadingMessage.Text = "Canceling provider request...";
        HideAccountLoading();
    }

    private async void RetryLibraryLoad_Click(object sender, RoutedEventArgs e) => await LoadChannelsAsync(true);

    private async void UseSavedLibrary_Click(object sender, RoutedEventArgs e)
    {
        _libraryLoadCts?.Cancel();
        HideAccountLoading();
        var accountId = _state.SelectedAccountId;
        try
        {
            var saved = await Task.Run(() => _store.LoadChannelCache(accountId));
            if (accountId != _state.SelectedAccountId) return;
            if (saved.Count == 0)
            {
                StatusText.Text = "No saved library is available. Retry the provider.";
                return;
            }
            var organized = await Task.Run(() => LibraryOrganization.Apply(saved, _state.SelectedLibrary));
            var index = await Task.Run(() => new MediaSearchIndex(organized));
            if (accountId != _state.SelectedAccountId) return;
            _sourceChannels = saved;
            _channels = organized;
            _searchIndex = index;
            await ApplyFiltersAsync();
            StatusText.Text = $"Using {_channels.Count:N0} saved items.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Saved library unavailable: " + AppLogger.SanitizeText(ex.Message);
        }
    }

    private void ShowAccountLoading(string message)
    {
        AccountLoadingMessage.Text = message;
        AccountLoadingOverlay.Visibility = Visibility.Visible;
        if (!_loadingTimer.IsEnabled)
        {
            _loadingDotIndex = 0;
            UpdateLoadingDots();
            _loadingTimer.Start();
        }
        VideoViewHost.Visibility = Visibility.Collapsed;
        Cursor = System.Windows.Input.Cursors.Wait;
    }

    private void UpdateLoadingDots()
    {
        LoadingDot1.Opacity = _loadingDotIndex == 0 ? 1 : 0.25;
        LoadingDot2.Opacity = _loadingDotIndex == 1 ? 1 : 0.25;
        LoadingDot3.Opacity = _loadingDotIndex == 2 ? 1 : 0.25;
        LoadingDot4.Opacity = _loadingDotIndex == 3 ? 1 : 0.25;
    }

    private void SetAccountLoadingMessage(string message)
    {
        AccountLoadingMessage.Text = message;
    }

    // A visible video host paints through a native child window over any
    // WPF content in the same area, so the idle background can only render
    // correctly while it is collapsed. Swap the two together.
    private void ShowIdleBackground()
    {
        VideoViewHost.Visibility = Visibility.Collapsed;
        IdleBackground.Visibility = Visibility.Visible;
    }

    private void HideIdleBackground()
    {
        IdleBackground.Visibility = Visibility.Collapsed;
        VideoViewHost.Visibility = Visibility.Visible;
    }

    private void HideAccountLoading()
    {
        _loadingTimer.Stop();
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
        var searchIndex = _searchIndex;
        var favorites = viewMode == 1 ? _state.FavoriteIds.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var favoriteFolders = viewMode == 1
            ? _state.FavoriteFolders.Select(folder => new FavoriteFolder
            {
                Id = folder.Id, Name = folder.Name, ChannelIds = [.. folder.ChannelIds]
            }).ToList()
            : null;
        var activeFavoriteFolderId = viewMode == 1 ? _activeFavoriteFolderId : null;
        var recentUrls = (HashSet<string>?)null;
        var library = _state.SelectedLibrary;
        var seriesEpisodes = _activeSeriesEpisodes;
        var activeSeason = _activeSeason;
        var vodSort = _vodSort;

        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;

        try
        {
            var result = await Task.Run(
                () => viewMode is 2 or 3
                    ? BuildHistoryFilterResult(library, channels, search, kind, viewMode == 3, cts.Token)
                    : seriesEpisodes is not null
                    ? BuildSeriesFilterResult(seriesEpisodes, search, activeSeason, cts.Token)
                    : BuildFilterResult(channels, searchIndex, search, kind, activeFolder, activeLetter, browseMode, favorites, recentUrls, favoriteFolders, activeFavoriteFolderId, vodSort, library.FavoriteIds, library.GroupOrganization, cts.Token),
                cts.Token);

            if (!ReferenceEquals(_filterCts, cts) || cts.IsCancellationRequested) return;

            _filteredChannels = result.FilteredChannels;
            _visibleChannels = result.VisibleChannels;
            if (result.Folders is not null)
            {
                _visibleEntries = result.Folders
                    .Select(folder => ChannelListEntry.ForFolder(folder.Name, folder.Count, folder.FavoriteFolderId))
                    .ToList();
            }
            else
            {
                var visibleChannels = _visibleChannels;
                _visibleEntries = new LazyList<ChannelListEntry>(visibleChannels.Count,
                    index => ChannelListEntry.ForChannel(visibleChannels[index],
                        library.ViewingProgress is not null && library.ViewingProgress.TryGetValue(ItemIdentity.For(visibleChannels[index]), out var progress)
                            ? progress : null));
            }

            FolderBackButton.Visibility = (result.ShowBackButton || seriesEpisodes is not null) ? Visibility.Visible : Visibility.Collapsed;
            NewFavoriteFolderButton.Visibility = viewMode == 1 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = result.CountText;
            UpdateBreadcrumb();
            UpdateFilterScope();
            VodSortPicker.Visibility = _mediaKindMode is 2 or 3 && seriesEpisodes is null ? Visibility.Visible : Visibility.Collapsed;
            VodDetailsButton.Visibility = _mediaKindMode is 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
            var selectedBeforeRefresh = _playbackState.SelectedChannel;
            var scope = $"{_state.SelectedAccountId}|{_mediaKindMode}|{_viewMode}|{_browseMode}|{_activeFolder}|{_activeFavoriteFolderId}|{_activeLetter}|{_activeSeriesId}|{_activeSeason}|{SearchBox.Text}|{_vodSort}";
            var priorOffset = CatalogPositionRetention.ShouldRestoreScroll(_displayedCatalogScope, scope, _pendingLibraryRestore is not null)
                ? FindChannelScrollViewer()?.VerticalOffset : null;
            ChannelList.ItemsSource = _visibleEntries;
            _displayedCatalogScope = scope;
            SelectChannelInVisibleList(selectedBeforeRefresh);
            if (priorOffset is { } retainedOffset)
                _ = Dispatcher.BeginInvoke(() => FindChannelScrollViewer()?.ScrollToVerticalOffset(retainedOffset), DispatcherPriority.Loaded);
            if (_pendingLibraryRestore is { } position)
            {
                _pendingLibraryRestore = null;
                var index = LibraryNavigationHistory.SelectedIndex(_visibleChannels, position.SelectedKey, ItemIdentity.For);
                if (index >= 0) ChannelList.SelectedIndex = index;
                _ = Dispatcher.BeginInvoke(() => FindChannelScrollViewer()?.ScrollToVerticalOffset(position.ScrollOffset), DispatcherPriority.Loaded);
            }
            UpdateEmptyState();
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

    private static FilterResult BuildHistoryFilterResult(AccountLibraryState library, IReadOnlyList<Channel> catalog,
        string search, MediaKind? kind, bool continueOnly, CancellationToken cancellationToken)
    {
        var recentItems = continueOnly ? ViewingHistory.Continue(library) : ViewingHistory.Recent(library);
        var keys = recentItems.Select(item => item.ItemKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalogByKey = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ItemIdentity.For(channel);
            if (keys.Contains(key)) catalogByKey.TryAdd(key, channel);
        }
        var query = SplitSearchQuery(search);
        var items = new List<Channel>();
        foreach (var recent in recentItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kind is not null && recent.MediaKind != kind) continue;
            var channel = ViewingHistory.Resolve(recent, catalogByKey);
            if (MatchesSearch(channel, query)) items.Add(channel);
        }
        return new FilterResult { FilteredChannels = items, VisibleChannels = items,
            CountText = $"{items.Count:N0} {(continueOnly ? "to continue" : "recent items")}" };
    }

    // A-Z, then 0-9, then "#" for anything else. Sort order for the letter folders below.
    private const string LetterBucketOrder = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#";

    private static FilterResult BuildFilterResult(
        IReadOnlyList<Channel> channels,
        MediaSearchIndex? searchIndex,
        string search,
        MediaKind? kind,
        string? activeFolder,
        string? activeLetter,
        int browseMode,
        HashSet<string>? favorites,
        HashSet<string>? recentUrls,
        IReadOnlyList<FavoriteFolder>? favoriteFolders,
        string? activeFavoriteFolderId,
        VodSort vodSort,
        IReadOnlyList<string> favoriteOrder,
        IReadOnlyDictionary<string, GroupOrganization> groupOrganization,
        CancellationToken cancellationToken)
    {
        var queryParts = SplitSearchQuery(search);
        // The index belongs to the active catalog; use it for every catalog
        // query while retaining the remaining folder/favorite semantics below.
        IEnumerable<Channel> candidates = searchIndex is null
            ? channels
            : searchIndex.SearchAll(search, kind, cancellationToken);
        var remainingQueryParts = searchIndex is null ? queryParts : [];
        var activeFolderName = NormalizeGroupName(activeFolder);

        // A folder drilled into from Folders mode stays in scope even after
        // switching to A-Z, so "A-Z" buckets only the channels inside that
        // folder instead of resetting to the whole library.
        bool InFolderScope(Channel channel) =>
            activeFolder is null || string.Equals(NormalizeGroupName(channel.Group), activeFolderName, StringComparison.OrdinalIgnoreCase);

        var filedIds = favoriteFolders is null
            ? null
            : favoriteFolders.SelectMany(folder => folder.ChannelIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? activeFavoriteIds = activeFavoriteFolderId is { Length: > 0 }
            ? favoriteFolders?.FirstOrDefault(folder => string.Equals(folder.Id, activeFavoriteFolderId, StringComparison.OrdinalIgnoreCase))?.ChannelIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        bool InFavoriteFolder(Channel channel) => activeFavoriteFolderId switch
        {
            null => true,
            "" => filedIds is null || !filedIds.Contains(ItemIdentity.For(channel)),
            _ => activeFavoriteIds?.Contains(ItemIdentity.For(channel)) == true
        };

        if (favorites is not null && browseMode == 0 && activeFavoriteFolderId is null)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var folderByChannelId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in favoriteFolders ?? [])
                foreach (var id in folder.ChannelIds)
                    folderByChannelId.TryAdd(id, folder.Id);
            foreach (var channel in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ChannelMatchesFilter(channel, kind, remainingQueryParts, favorites, null)) continue;
                var folderId = folderByChannelId.GetValueOrDefault(ItemIdentity.For(channel)) ?? "";
                counts.TryGetValue(folderId, out var count);
                counts[folderId] = count + 1;
            }
            var folders = (favoriteFolders ?? [])
                .Where(folder => queryParts.Length == 0 || counts.ContainsKey(folder.Id) || queryParts.All(part => ContainsIgnoreCase(folder.Name, part)))
                .Select(folder => new FolderResult(folder.Name, counts.GetValueOrDefault(folder.Id), folder.Id))
                .ToList();
            if (counts.TryGetValue("", out var unfiledCount))
                folders.Insert(0, new FolderResult("Unfiled", unfiledCount, ""));
            return new FilterResult { Folders = folders, CountText = $"{folders.Count:N0} folders" };
        }

        // Folders scope: browse by playlist category (the Group field).
        if (browseMode == 0 && activeFolder is null && activeFavoriteFolderId is null)
        {
            var folderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var channel in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ChannelMatchesFilter(channel, kind, remainingQueryParts, favorites, recentUrls)) continue;

                var folder = NormalizeGroupName(channel.Group);
                folderCounts.TryGetValue(folder, out var count);
                folderCounts[folder] = count + 1;
            }

            return new FilterResult
            {
                Folders = folderCounts
                    .OrderBy(pair => groupOrganization.FirstOrDefault(g => string.Equals(
                        string.IsNullOrWhiteSpace(g.Value.Name) ? g.Key : g.Value.Name, pair.Key,
                        StringComparison.OrdinalIgnoreCase)).Value?.Order is > 0 and var order ? order : int.MaxValue)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
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
            foreach (var channel in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!InFolderScope(channel)) continue;
                if (!InFavoriteFolder(channel)) continue;
                if (!ChannelMatchesFilter(channel, kind, remainingQueryParts, favorites, recentUrls)) continue;

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
                ShowBackButton = activeFolder is not null || activeFavoriteFolderId is not null,
                CountText = $"{letterCounts.Count:N0} folders"
            };
        }

        // Items scope (or a drill-down into a category/letter) shows the complete
        // matching list -- no grouping, no truncation.
        var filtered = new List<Channel>();
        foreach (var channel in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!InFolderScope(channel)) continue;
            if (!InFavoriteFolder(channel)) continue;
            if (activeLetter is not null && !string.Equals(GetNameBucketKey(channel.Name), activeLetter, StringComparison.Ordinal)) continue;
            if (!ChannelMatchesFilter(channel, kind, remainingQueryParts, favorites, recentUrls)) continue;

            filtered.Add(channel);
        }

        if (favorites is not null)
        {
            var positions = favoriteOrder.Select((id, index) => (id, index))
                .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.OrderBy(c => positions.GetValueOrDefault(ItemIdentity.For(c), int.MaxValue)).ToList();
        }
        else if (vodSort != VodSort.ProviderOrder && kind is MediaKind.Movie or MediaKind.Series)
            filtered = VodDiscovery.Sort(filtered, vodSort).ToList();
        else if (activeLetter is not null || activeFavoriteFolderId is not null)
        {
            filtered = filtered.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        return new FilterResult
        {
            FilteredChannels = filtered,
            VisibleChannels = filtered,
            ShowBackButton = activeFolder is not null || activeLetter is not null || activeFavoriteFolderId is not null,
            CountText = $"{filtered.Count:N0} items"
        };
    }

    // Episodes aren't part of the main catalog (fetched on demand per series), so
    // this bypasses the folder/letter/kind machinery entirely and just applies the
    // search box to whatever series is currently drilled into.
    private static FilterResult BuildSeriesFilterResult(IReadOnlyList<Channel> episodes, string search, int? season, CancellationToken cancellationToken)
    {
        var queryParts = SplitSearchQuery(search);
        var filtered = new List<Channel>(episodes.Count);
        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (season is not null && ViewingHistory.EpisodeOrder(episode).Season != season) continue;
            if (!MatchesSearch(episode, queryParts)) continue;
            filtered.Add(episode);
        }

        return new FilterResult
        {
            FilteredChannels = filtered,
            VisibleChannels = filtered,
            ShowBackButton = true,
            CountText = $"{filtered.Count:N0} episodes"
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
        if (favorites is not null && !favorites.Contains(ItemIdentity.For(channel))) return false;
        if (recentUrls is not null && !recentUrls.Contains(ItemIdentity.For(channel))) return false;
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

    private sealed record FolderResult(string Name, int Count, string? FavoriteFolderId = null);

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
        if (ClearSearchButton is not null) ClearSearchButton.IsEnabled = !string.IsNullOrEmpty(SearchBox.Text);
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
        ApplyFilters();
    }

    private void ResetFilters_Click(object sender, RoutedEventArgs e) => ResetFilters();
    private void EmptyStateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_channels.Count == 0) UpdatePlaylist_Click(sender, e);
        else ResetFilters();
    }

    private void ResetFilters()
    {
        _activeFolder = null;
        _activeFavoriteFolderId = null;
        _activeLetter = null;
        ClearActiveSeries();
        _browseMode = 0;
        _viewMode = 0;
        SearchBox.Clear();
        UpdateSearchScopeButtons();
        UpdateViewModeButtons();
        ApplyFilters();
    }

    private void UpdateFilterScope()
    {
        var kind = _mediaKindMode switch { 1 => "Live TV", 2 => "Movies", 3 => "Series", _ => "All media" };
        var view = _viewMode switch { 1 => "Favorites", 2 => "Recent", 3 => "Continue", _ => "All" };
        var browse = _browseMode switch { 0 => "Folders", 1 => "A-Z", _ => "Items" };
        var location = _activeSeriesName ?? _activeFolder ?? _activeLetter ?? (_activeFavoriteFolderId is null ? null : _activeFavoriteFolderId.Length == 0 ? "Unfiled" : _state.FavoriteFolders.FirstOrDefault(f => f.Id == _activeFavoriteFolderId)?.Name);
        FilterScopeText.Text = $"{kind} · {view} · {browse}" + (location is null ? "" : $" · {location}");
        SearchScopeText.Text = "Searching: " + FilterScopeText.Text;
        ResetFiltersButton.IsEnabled = LibraryNavigationPolicy.CanReset(_viewMode, _browseMode,
            _activeFolder is not null || _activeLetter is not null || _activeFavoriteFolderId is not null || _activeSeriesId is not null, SearchBox.Text);
    }

    private void UpdateEmptyState()
    {
        var empty = _visibleEntries.Count == 0;
        EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        EmptyStateText.Text = _viewMode == 3 && string.IsNullOrWhiteSpace(SearchBox.Text)
            ? "Nothing to continue. Watch part of a movie or episode to see it here."
            : LibraryNavigationPolicy.EmptyMessage(_channels.Count, SearchBox.Text, _viewMode);
        EmptyStateAction.Content = _channels.Count == 0 ? "Retry provider" : "Reset filters";
    }

    private ScrollViewer? FindChannelScrollViewer()
    {
        static ScrollViewer? Find(DependencyObject root)
        {
            if (root is ScrollViewer scroll) return scroll;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = Find(VisualTreeHelper.GetChild(root, i));
                if (found is not null) return found;
            }
            return null;
        }
        return Find(ChannelList);
    }

    private LibraryPosition CaptureLibraryPosition()
    {
        return new LibraryPosition(_activeFolder, _activeFavoriteFolderId, _activeLetter, _browseMode, _viewMode,
            ChannelList.SelectedItem is ChannelListEntry { Channel: { } channel } ? ItemIdentity.For(channel) : null,
            FindChannelScrollViewer()?.VerticalOffset ?? 0);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilters();

    private void AllMedia_Click(object sender, RoutedEventArgs e) => SetMediaKindMode(0);
    private void LiveTv_Click(object sender, RoutedEventArgs e) => SetMediaKindMode(1);
    private void Movies_Click(object sender, RoutedEventArgs e) => SetMediaKindMode(2);
    private void Series_Click(object sender, RoutedEventArgs e) => SetMediaKindMode(3);

    private void AllView_Click(object sender, RoutedEventArgs e) => SetViewMode(0);
    private void FavoritesView_Click(object sender, RoutedEventArgs e) => SetViewMode(1);
    private void RecentView_Click(object sender, RoutedEventArgs e)
    {
        SetMediaKindMode(0);
        SetViewMode(2);
    }
    private void ContinueView_Click(object sender, RoutedEventArgs e)
    {
        SetMediaKindMode(0);
        SetViewMode(3);
    }

    private void SearchFolders_Click(object sender, RoutedEventArgs e) => SetBrowseMode(0);
    private void SearchLetters_Click(object sender, RoutedEventArgs e) => SetBrowseMode(1);
    private void SearchItems_Click(object sender, RoutedEventArgs e) => SetBrowseMode(2);

    private void VodSortPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VodSortPicker is null || VodSortPicker.SelectedIndex < 0) return;
        _vodSort = (VodSort)VodSortPicker.SelectedIndex;
        if (ChannelList is not null) ApplyFilters();
    }

    private void VodDetails_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is not ChannelListEntry { Channel: { } channel }) return;
        ShowVodDetails(channel);
    }

    private void ShowVodDetails(Channel channel)
    {
        var dialog = new Window
        {
            Title = channel.Name + " — Details", Owner = this, Width = 540, Height = 420,
            MinWidth = 380, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.SetResourceReference(BackgroundProperty, "Bg1Brush");
        var content = new StackPanel { Margin = new Thickness(18) };
        var title = new TextBlock { Text = channel.Name, FontSize = 22, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text1Brush");
        content.Children.Add(title);
        if (Uri.TryCreate(channel.Logo, UriKind.Absolute, out var posterUri) && posterUri.Scheme is "http" or "https")
        {
            var poster = new System.Windows.Controls.Image { Height = 160, MaxWidth = 260, Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 8) };
            poster.Source = new System.Windows.Media.Imaging.BitmapImage(posterUri);
            poster.ImageFailed += (_, _) => poster.Visibility = Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(poster, "Provider poster for " + channel.Name);
            content.Children.Add(poster);
        }
        var details = new TextBlock { Text = VodDiscovery.Details(channel), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
        details.SetResourceReference(TextBlock.ForegroundProperty, "Text1Brush");
        content.Children.Add(details);
        var watch = new System.Windows.Controls.Button { Content = SeriesPlaceholder.TryGetSeriesId(channel, out _) ? "Open episodes" : "Watch",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Padding = new Thickness(16, 6, 16, 6) };
        watch.Click += (_, _) => { dialog.Close(); ActivateVodOrLive(channel); };
        content.Children.Add(watch);
        dialog.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }

    private void SetBrowseMode(int browseMode)
    {
        _browseMode = Math.Clamp(browseMode, 0, 2);
        // Keep whatever folder you've drilled into (e.g. via Folders mode) so
        // switching to A-Z buckets just that folder instead of everything.
        _activeLetter = null;
        ClearActiveSeries();
        UpdateSearchScopeButtons();
        ApplyFilters();
    }

    private void SetMediaKindMode(int mediaKindMode)
    {
        var nextMode = Math.Clamp(mediaKindMode, 0, 3);
        if (_mediaKindMode != nextMode)
        {
            _pendingLibraryRestore = _libraryHistory.Switch(_mediaKindMode, nextMode, CaptureLibraryPosition());
            // A folder/letter belongs to the previous media library. Switching
            // Live TV, Movies, or Series always starts at the new library root.
            _activeFolder = null;
            _activeLetter = null;
            _activeFavoriteFolderId = null;
        }

        ClearActiveSeries();
        _mediaKindMode = nextMode;
        if (_pendingLibraryRestore is { } saved)
        {
            _activeFolder = saved.Folder;
            _activeFavoriteFolderId = saved.FavoriteFolder;
            _activeLetter = saved.Letter;
            _browseMode = saved.BrowseMode;
            _viewMode = saved.ViewMode;
            UpdateSearchScopeButtons();
            UpdateViewModeButtons();
        }
        UpdateMediaKindButtons();
        ApplyFilters();
    }

    private void SetViewMode(int viewMode)
    {
        var nextMode = Math.Clamp(viewMode, 0, 3);
        if (_viewMode != nextMode)
        {
            _activeFolder = null;
            _activeLetter = null;
            _activeFavoriteFolderId = null;
            if (nextMode == 1) _browseMode = 0;
        }
        _viewMode = nextMode;
        ClearActiveSeries();
        UpdateSearchScopeButtons();
        UpdateViewModeButtons();
        ApplyFilters();
    }

    private void ClearActiveSeries()
    {
        _activeSeriesId = null;
        _activeSeriesName = null;
        _activeSeriesEpisodes = null;
        _activeSeason = null;
        SeasonPicker.Visibility = Visibility.Collapsed;
    }

    // Windows-Explorer-style clickable path (e.g. "Series / Persian Series Foreign /
    // Cape Fear 2026"). Lives on its own row below the title/Back button so a long
    // folder or series name can never crowd the Back button off to the side.
    private void UpdateBreadcrumb()
    {
        var segments = new List<(string Label, Action? OnClick)>();

        if (_activeFolder is not null || _activeFavoriteFolderId is not null || _activeLetter is not null || _activeSeriesId is not null)
        {
            var rootLabel = _viewMode == 1 ? "Favorites" : _mediaKindMode switch
            {
                1 => "Live TV",
                2 => "Movies",
                3 => "Series",
                _ => "All media"
            };
            segments.Add((rootLabel, () =>
            {
                _activeFolder = null;
                _activeFavoriteFolderId = null;
                _activeLetter = null;
                ClearActiveSeries();
                ApplyFilters();
            }));
        }

        if (_activeFolder is not null)
        {
            segments.Add((_activeFolder, () =>
            {
                _activeLetter = null;
                ClearActiveSeries();
                ApplyFilters();
            }));
        }

        if (_activeFavoriteFolderId is not null)
        {
            var name = _activeFavoriteFolderId.Length == 0 ? "Unfiled" :
                _state.FavoriteFolders.FirstOrDefault(folder => folder.Id == _activeFavoriteFolderId)?.Name ?? "Folder";
            segments.Add((name, () =>
            {
                _activeLetter = null;
                ClearActiveSeries();
                ApplyFilters();
            }));
        }

        if (_activeLetter is not null)
        {
            segments.Add((_activeLetter, () =>
            {
                ClearActiveSeries();
                ApplyFilters();
            }));
        }

        if (_activeSeriesId is not null)
        {
            // Current position -- not clickable, nothing deeper to collapse back from.
            segments.Add((_activeSeriesName ?? "Series", null));
        }

        BreadcrumbPanel.Children.Clear();
        if (segments.Count == 0)
        {
            BreadcrumbPanel.Visibility = Visibility.Collapsed;
            return;
        }

        BreadcrumbPanel.Visibility = Visibility.Visible;
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                var separator = new TextBlock { Text = " / ", VerticalAlignment = VerticalAlignment.Center };
                separator.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
                BreadcrumbPanel.Children.Add(separator);
            }

            var (label, onClick) = segments[i];
            var isCurrent = onClick is null;
            if (onClick is not null)
            {
                var segment = new System.Windows.Controls.Button { Content = label, Padding = new Thickness(4, 1, 4, 1), ToolTip = "Go to " + label };
                segment.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty, "Text1Brush");
                segment.Click += (_, _) => onClick();
                BreadcrumbPanel.Children.Add(segment);
            }
            else BreadcrumbPanel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        }
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
        SetViewButtonState(ContinueViewButton, _viewMode == 3);
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
        _playbackState.Select(ChannelList.SelectedItem is ChannelListEntry { Channel: { } channel } ? channel : null);
        UpdateFavoriteButton();
        UpdateBrowseGuide();
    }

    private void ChannelLogo_TargetUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
    {
        if (sender is System.Windows.Controls.Image { Parent: Border host } image)
        {
            host.Visibility = image.Source is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ChannelLogo_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Image { Parent: Border host })
        {
            host.Visibility = Visibility.Collapsed;
        }
    }

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ActivateSelectedListEntry();
    }

    private async void FolderBack_Click(object sender, RoutedEventArgs e)
    {
        // Pop one level at a time: out of a series' episode list first (back to
        // wherever that series was listed), then a letter drill-down (back to that
        // folder's letter buckets), then out of the folder itself. Remember exactly
        // what we're backing out of so the restored list can scroll/select back to
        // it, instead of resetting to the top of what can be a very long list.
        string? restoreFolderOrLetterName = null;
        string? restoreSeriesId = null;

        if (_activeSeriesId is not null)
        {
            restoreSeriesId = _activeSeriesId;
            ClearActiveSeries();
        }
        else if (_activeLetter is not null)
        {
            restoreFolderOrLetterName = _activeLetter;
            _activeLetter = null;
        }
        else if (_activeFavoriteFolderId is not null)
        {
            restoreFolderOrLetterName = _activeFavoriteFolderId;
            _activeFavoriteFolderId = null;
        }
        else
        {
            restoreFolderOrLetterName = _activeFolder;
            _activeFolder = null;
        }

        await ApplyFiltersAsync();
        RestoreListSelectionAfterBack(restoreFolderOrLetterName, restoreSeriesId);
        if (restoreSeriesId is not null && _seriesReturnOffset is { } offset)
        {
            _seriesReturnOffset = null;
            _ = Dispatcher.BeginInvoke(() => FindChannelScrollViewer()?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        }
    }

    private void RestoreListSelectionAfterBack(string? folderOrLetterName, string? seriesId)
    {
        ChannelListEntry? match = null;

        if (folderOrLetterName is not null)
        {
            match = _visibleEntries.FirstOrDefault(entry =>
                entry.IsFolder && (string.Equals(entry.FavoriteFolderId, folderOrLetterName, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(entry.FolderName, folderOrLetterName, StringComparison.OrdinalIgnoreCase)));
        }
        else if (seriesId is not null)
        {
            var index = _visibleChannels.FindIndex(channel =>
                SeriesPlaceholder.TryGetSeriesId(channel, out var id) &&
                string.Equals(id, seriesId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) match = _visibleEntries[index];
        }

        if (match is null) return;

        _suppressChannelSelectionChange = true;
        try
        {
            ChannelList.SelectedItem = match;
            ChannelList.ScrollIntoView(match);
            _playbackState.Select(match.Channel);
        }
        finally
        {
            _suppressChannelSelectionChange = false;
        }
    }

    private void ActivateSelectedListEntry()
    {
        if (ChannelList.SelectedItem is not ChannelListEntry entry)
        {
            // A freshly opened channel list may not have a highlighted item yet.
            // Make Select useful immediately by targeting the first visible entry.
            if (_visibleEntries.Count == 0) return;
            ChannelList.SelectedIndex = 0;
            entry = _visibleEntries[0];
            ChannelList.ScrollIntoView(entry);
        }

        if (entry.IsFolder)
        {
            if (entry.FavoriteFolderId is not null)
            {
                _activeFavoriteFolderId = entry.FavoriteFolderId;
                _activeLetter = null;
                ApplyFilters();
            }
            else EnterFolder(entry.FolderName);
            return;
        }

        if (entry.Channel is not null) ActivateVodOrLive(entry.Channel);
    }

    private void ActivateVodOrLive(Channel channel)
    {
        if (channel.MediaKind == MediaKind.Live || SeriesPlaceholder.TryGetSeriesId(channel, out _))
        {
            PlayChannel(channel);
            return;
        }
        _state.SelectedLibrary.ViewingProgress ??= new(StringComparer.OrdinalIgnoreCase);
        _state.SelectedLibrary.ViewingProgress.TryGetValue(ItemIdentity.For(channel), out var progress);
        if (progress is { PositionMs: > 0, Watched: false })
        {
            var choice = MessageBox.Show(this,
                $"Resume {channel.Name} at {FormatTime(progress.PositionMs)}?\n\nYes: Resume    No: Start Over    Cancel: Keep browsing",
                "Continue watching", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                PlayChannel(channel, null, progress.PositionMs);
                return;
            }
        }
        ViewingHistory.StartOver(_state.SelectedLibrary, channel, DateTime.UtcNow);
        _store.Save(_state);
        _skipNextProgressSave = true;
        PlayChannel(channel);
    }

    private async Task OfferNextEpisodeAsync(Channel finished)
    {
        if (finished.MediaKind != MediaKind.Series || _isChangingAccount) return;
        var accountId = _state.SelectedAccountId;
        var episodes = _playingSeriesEpisodes;
        if (episodes is null && string.IsNullOrWhiteSpace(finished.SeriesId))
            episodes = ViewingHistory.SeriesSiblings(_channels, finished).ToList();
        if (episodes is null && !string.IsNullOrWhiteSpace(finished.SeriesId))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                episodes = (await _playlistService.FetchSeriesEpisodesAsync(_state.Account.Clone(), finished.SeriesId, timeout.Token)).ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Next episode lookup failed: " + AppLogger.SanitizeText(ex.Message));
                return;
            }
        }
        if (episodes is null || accountId != _state.SelectedAccountId ||
            _currentChannel is null || ItemIdentity.For(_currentChannel) != ItemIdentity.For(finished)) return;
        var next = ViewingHistory.NextEpisode(_state.SelectedLibrary, episodes, finished);
        if (next is null) return;
        if (MessageBox.Show(this, $"Play next episode: {next.Name}?", "Series progression",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            ActivateVodOrLive(next);
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
        if (_viewMode == 1)
        {
            SelectChannelInVisibleList(channel);
            return;
        }
        if (channel.MediaKind == MediaKind.Series && !SeriesPlaceholder.TryGetSeriesId(channel, out _))
        {
            // Resolved episodes carry a synthetic "Season N" group that isn't part
            // of the main catalog -- deriving folder/letter scope from it would
            // filter the whole list down to nothing. If we're already showing this
            // series' episode list (via EnterSeriesAsync) just highlight the one
            // that's playing; otherwise (e.g. replayed from Recent) leave scope as-is.
            SelectChannelInVisibleList(channel);
            return;
        }

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
        SelectChannelInVisibleList(_currentChannel);
    }

    private void SelectChannelInVisibleList(Channel? channel)
    {
        if (channel is null) return;

        var index = _visibleChannels.FindIndex(visible =>
            string.Equals(visible.Id, channel.Id, StringComparison.OrdinalIgnoreCase));
        var match = index >= 0 ? _visibleEntries[index] : null;
        if (match is not null)
        {
            _suppressChannelSelectionChange = true;
            try
            {
                ChannelList.SelectedItem = match;
                ChannelList.ScrollIntoView(match);
                _playbackState.Select(match.Channel);
            }
            finally
            {
                _suppressChannelSelectionChange = false;
            }
        }
        UpdateFavoriteButton();
    }

    private static string NormalizeGroupName(string? group)
    {
        return string.IsNullOrWhiteSpace(group) ? "Uncategorized" : group.Trim();
    }

    private void PlayChannel(Channel channel, int? autoCandidateIndex = null)
    {
        PlayChannel(channel, autoCandidateIndex, resumeTimeMs: null);
    }

    private void PlayChannel(Channel channel, int? autoCandidateIndex, long? resumeTimeMs)
    {
        if (_recordingService?.ActiveCount > 0)
        {
            QueueAfterRecordingStops(() => PlayChannel(channel, autoCandidateIndex, resumeTimeMs));
            return;
        }
        // Series entries loaded via the Xtream API fallback are placeholders (one per
        // series, not per episode) since listing episodes requires a separate API call
        // per series. Route these into the episode list (like drilling into a folder)
        // instead of trying to play them.
        if (SeriesPlaceholder.TryGetSeriesId(channel, out var seriesId))
        {
            _ = EnterSeriesAsync(channel.Name, seriesId);
            return;
        }

        try
        {
            if (_skipNextProgressSave) _skipNextProgressSave = false;
            else SavePlaybackProgress(force: true);
            _offeredEndedRequest = null;
            if (channel.MediaKind != MediaKind.Live && resumeTimeMs is null && autoCandidateIndex is null &&
                _state.SelectedLibrary.ViewingProgress is not null &&
                _state.SelectedLibrary.ViewingProgress.TryGetValue(ItemIdentity.For(channel), out var previous) && previous.Watched)
                ViewingHistory.StartOver(_state.SelectedLibrary, channel, DateTime.UtcNow);
            AppLogger.Info("PlayChannel begin. requestedIndex=" + (autoCandidateIndex?.ToString() ?? "auto") + "; resumeTimeMs=" + (resumeTimeMs?.ToString() ?? "none") + "; " + AppLogger.DescribeChannel(channel));
            if (resumeTimeMs is null) ClearPauseResumeState();
            if (_currentChannel is null || !string.Equals(_currentChannel.Id, channel.Id, StringComparison.OrdinalIgnoreCase))
            {
                ClearLiveDelay();
            }

            var candidates = PlayerService.BuildPlaybackCandidates(channel, _state.Account);
            if (candidates.Count == 0)
            {
                AppLogger.Warn("PlayChannel aborted: no playable candidate. " + AppLogger.DescribeChannel(channel));
                StatusText.Text = "No playable URL found.";
                return;
            }

            if (_tuner is null)
            {
                AppLogger.Warn("PlayChannel aborted: player not initialized. " + AppLogger.DescribeChannel(channel));
                StatusText.Text = "Player is not initialized yet.";
                return;
            }

            var candidateIndex = Math.Clamp(autoCandidateIndex ?? 0, 0, candidates.Count - 1);
            var candidate = candidates[candidateIndex];
            ClearCatchup();
            var request = new TuneRequest(channel.Id, channel.Name, candidate.Url, candidate.Label,
                channel.MediaKind == MediaKind.Live, GetPlaybackBufferMs(), _state.SelectedAccountId);
            _playbackState.Start(channel, candidates, candidateIndex, request);
            _playingSeriesEpisodes = channel.MediaKind == MediaKind.Series && _activeSeriesEpisodes is not null &&
                _activeSeriesEpisodes.Any(episode => ItemIdentity.For(episode) == ItemIdentity.For(channel))
                ? _activeSeriesEpisodes : null;
            SelectChannelInVisibleList(channel);
            UpdateFavoriteButton();
            AppLogger.Info("Selected playback candidate. " + GetCandidateLogText(candidate, candidateIndex));
            _streamInfoTracker.ResetBandwidth();
            NowPlayingText.Text = channel.Name;
            UpdateRecordingControls();
            _lastEpgUiUpdateUtc = DateTime.UtcNow;
            UpdateEpgDisplay();
            StatusText.Text = "Opening: " + channel.Name;
            HideIdleBackground();
            AddRecent(channel);
            ShowControls();

            _tuner.Play(request);

            if (resumeTimeMs is > 0)
            {
                QueueResumeSeek(channel.Id, resumeTimeMs.Value);
            }
            StartBackgroundSourceProbe(channel, candidates);
        }
        catch (Exception ex)
        {
            AppLogger.Error("PlayChannel failed. " + AppLogger.DescribeChannel(channel), ex);
            StatusText.Text = "Play error: " + AppLogger.SanitizeText(ex.Message);
        }
    }

    // Drills into a series exactly like entering a folder: episodes replace the
    // current list and the existing folder-back button/gesture pops back out to
    // wherever the series was listed. Fetched on demand (one API call per series)
    // rather than up front, since this account has 46,000+ series.
    private async Task EnterSeriesAsync(string seriesName, string seriesId)
    {
        _seriesLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _seriesLoadCts = cts;
        var generation = ++_seriesLoadGeneration;
        var accountId = _state.SelectedAccountId;
        AppLogger.Info("Entering series. seriesId=" + seriesId + "; seriesName=" + seriesName);
        StatusText.Text = "Loading episodes for " + seriesName + "...";
        Cursor = System.Windows.Input.Cursors.Wait;

        try
        {
            var account = _state.Account.Clone();
            var episodes = await _playlistService.FetchSeriesEpisodesAsync(account, seriesId, cts.Token);
            if (generation != _seriesLoadGeneration || accountId != _state.SelectedAccountId || cts.IsCancellationRequested) return;
            if (episodes.Count == 0)
            {
                StatusText.Text = "No episodes were found for " + seriesName + ".";
                return;
            }

            _seriesReturnOffset = FindChannelScrollViewer()?.VerticalOffset;

            _activeSeriesId = seriesId;
            _activeSeriesName = seriesName;
            _activeSeriesEpisodes = episodes.OrderBy(episode => ViewingHistory.EpisodeOrder(episode).Season)
                .ThenBy(episode => ViewingHistory.EpisodeOrder(episode).Episode).ThenBy(episode => episode.Name).ToList();
            _activeSeason = null;
            SeasonPicker.Items.Clear();
            SeasonPicker.Items.Add("All seasons");
            foreach (var season in _activeSeriesEpisodes.Select(episode => ViewingHistory.EpisodeOrder(episode).Season)
                         .Where(value => value != int.MaxValue).Distinct().OrderBy(value => value))
                SeasonPicker.Items.Add("Season " + season);
            SeasonPicker.SelectedIndex = 0;
            SeasonPicker.Visibility = Visibility.Visible;
            await ApplyFiltersAsync();
            if (_visibleEntries.Count > 0)
            {
                ChannelList.SelectedIndex = 0;
                ChannelList.ScrollIntoView(ChannelList.SelectedItem);
            }
            StatusText.Text = $"Loaded {episodes.Count:N0} episodes for {seriesName}.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to load series episodes. seriesId=" + seriesId, ex);
            StatusText.Text = "Could not load episodes: " + AppLogger.SanitizeText(ex.Message);
        }
        finally
        {
            if (generation == _seriesLoadGeneration) Cursor = null;
            cts.Dispose();
        }
    }

    private void SeasonPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_activeSeriesEpisodes is null || SeasonPicker.SelectedIndex < 0) return;
        var selected = SeasonPicker.SelectedItem?.ToString();
        _activeSeason = selected is not null && selected.StartsWith("Season ", StringComparison.Ordinal) &&
            int.TryParse(selected.AsSpan(7), out var season) ? season : null;
        ApplyFilters();
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
            if (ReferenceEquals(_sourceProbeCts, cts)) StatusText.Text = "Source test failed: " + AppLogger.SanitizeText(ex.Message);
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

    private PlaybackCandidate? GetCurrentPlaybackCandidate() => _playbackState.PlayingCandidate;

    private static string GetCandidateLogText(PlaybackCandidate candidate, int index)
    {
        return $"candidateIndex={index}; label={candidate.Label}; url={AppLogger.SanitizeUrl(candidate.Url)}";
    }

    private void AddRecent(Channel channel)
    {
        ViewingHistory.RecordPlay(_state.SelectedLibrary, channel, DateTime.UtcNow);
        _store.Save(_state);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        if (_mediaPlayer is null) return;
        if (_mediaPlayer.IsPlaying) PauseCurrentPlayback();
        else if (_pausedPlayback is not null) ResumePausedPlayback();
        else if (_catchupPlayback is { } archive) _ = StartCatchupAsync(archive.Source, archive.Programme);
        else if (_currentChannel is not null) PlayChannel(_currentChannel);
        else if (ChannelList.SelectedItem is ChannelListEntry { Channel: { } channel }) PlayChannel(channel);
        UpdateStreamInfo();
    }

    private void PauseCurrentPlayback()
    {
        if (_mediaPlayer is null) return;
        SavePlaybackProgress(force: true);

        PausePlayerImmediately(_mediaPlayer);

        if (_currentChannel is not null)
        {
            var timeMs = Math.Max(0, _mediaPlayer.Time);
            _pausedPlayback = new PauseResumeSnapshot(
                _currentChannel,
                Math.Max(0, _currentCandidateIndex),
                timeMs);
        }

        _tuner?.NotifyUserPaused();

        StartLivePauseTracking();

        PlayPauseButton.Content = IconFactory.Create(IconFactory.Play);
        TimeText.Visibility = _pausedPlayback is null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = _pausedPlayback is null
            ? "Paused."
            : "Paused at " + FormatTime(_pausedPlayback.TimeMs) + ".";
        UpdateStreamInfo();
    }

    private async void ResumePausedPlayback()
    {
        if (_mediaPlayer is null || _pausedPlayback is null) return;

        var player = _mediaPlayer;
        var snapshot = _pausedPlayback;
        _tuner?.NotifyUserResumed();

        var resumed = false;
        try
        {
            // Play() may reopen the current media and restart at its beginning.
            // SetPause(false) explicitly resumes the existing paused input.
            player.SetPause(false);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline && ReferenceEquals(_mediaPlayer, player))
            {
                if (player.IsPlaying)
                {
                    resumed = true;
                    break;
                }
                await Task.Delay(50);
            }
        }
        catch
        {
            resumed = false;
        }

        if (resumed && ReferenceEquals(_mediaPlayer, player))
        {
            _pausedPlayback = null;
            FinishLivePauseTracking();
            PlayPauseButton.Content = IconFactory.Create(IconFactory.Pause);
            StatusText.Text = "Resumed: " + snapshot.Channel.Name;
            TimeText.Visibility = Visibility.Collapsed;
            UpdatePlaybackPosition();
            return;
        }

        // Retry with a new input only if explicit resume did not reach Playing.
        _pausedPlayback = null;
        FinishLivePauseTracking();
        if (_catchupPlayback is { } archive)
        {
            _ = ResumeCatchupAsync(archive, snapshot.TimeMs);
            return;
        }
        if (snapshot.Channel.MediaKind == MediaKind.Live)
        {
            // Never turn a failed live resume into an implicit fresh tune. That
            // silently loses the paused point; let the user choose Go Live.
            ClearPauseResumeState();
            FinishLivePauseTracking();
            PlayPauseButton.Content = IconFactory.Create(IconFactory.Play);
            StatusText.Text = "The paused live connection ended. Choose Go Live to reconnect at the live edge.";
            UpdatePlaybackPosition();
            return;
        }
        var canSeekBack = snapshot.Channel.MediaKind != MediaKind.Live && snapshot.TimeMs > 0;
        PlayChannel(snapshot.Channel, snapshot.CandidateIndex, canSeekBack ? snapshot.TimeMs : null);
        StatusText.Text = canSeekBack
            ? "Resuming: " + snapshot.Channel.Name
            : "Playback could not resume. Press Play to start again: " + snapshot.Channel.Name;
    }

    private static void PausePlayerImmediately(MediaPlayer player)
    {
        try { player.SetPause(true); }
        catch { /* Keep pause responsive even if LibVLC is shutting the input down. */ }
    }

    private void StartLivePauseTracking()
    {
        if (_catchupPlayback is not null) return;
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
        var show = _catchupPlayback is not null || behind >= TimeSpan.FromSeconds(1);
        var text = _catchupPlayback is not null ? "Watching archive" : show ? "Behind live " + FormatLiveDelay(behind) : string.Empty;
        LiveTimeshiftWindow? timeshiftWindow = null;
        var hasTimeshift = _currentChannel?.MediaKind == MediaKind.Live &&
            _tuner?.TryInspectLiveTimeshift(out timeshiftWindow, out _) == true;
        var canRewind = hasTimeshift && timeshiftWindow!.Duration >= TimeSpan.FromSeconds(30) &&
            _mediaPlayer?.State == VLCState.Playing;

        LiveDelayText.Text = text;
        LiveDelayText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GoLiveButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GoLiveMenuItem.IsEnabled = show;
        VideoGoLiveMenuItem.IsEnabled = show;
        RewindLiveButton.Visibility = hasTimeshift ? Visibility.Visible : Visibility.Collapsed;
        RewindLiveButton.IsEnabled = canRewind;
        RewindLiveMenuItem.IsEnabled = canRewind;
    }

    private static string FormatLiveDelay(TimeSpan delay)
    {
        if (delay.TotalHours >= 1) return delay.ToString(@"h\:mm\:ss");
        return delay.ToString(@"m\:ss");
    }

    private void ClearPauseResumeState()
    {
        _pausedPlayback = null;
        if (TimeText is not null) TimeText.Visibility = Visibility.Collapsed;
        _tuner?.NotifyUserResumed();
        _pendingResumeTimeMs = null;
        _pendingResumeChannelId = string.Empty;
        _pendingResumeSeekAttempts = 0;
        UpdateLiveDelayUi();
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
        if (_recordingService?.ActiveCount > 0)
        {
            QueueAfterRecordingStops(StopPlayback);
            return;
        }
        ClearCatchup();
        SavePlaybackProgress(force: true);
        ClearPauseResumeState();
        ClearLiveDelay();
        // The tuner supersedes any in-flight tune/retry cycle and retires the
        // active player, so nothing can restart playback after an explicit stop.
        _playbackState.Stop();
        UpdateRecordingControls();
        _sourceProbeCts?.Cancel();
        _sourceProbeCts = null;
        _tuner?.Stop();
        UpdateStreamInfo();
    }

    private void QueueAfterRecordingStops(Action action)
    {
        _afterRecordingStops = action;
        if (_recordingTransitionTask is { IsCompleted: false }) return;
        _recordingTransitionTask = StopRecordingThenContinueAsync();
    }

    private async Task StopRecordingThenContinueAsync()
    {
        await StopRecordingsSafelyAsync(RecordingStopReason.User);
        await Dispatcher.InvokeAsync(() =>
        {
            var action = _afterRecordingStops;
            _afterRecordingStops = null;
            _recordingTransitionTask = null;
            action?.Invoke();
        });
    }

    private async Task StopRecordingsSafelyAsync(RecordingStopReason reason)
    {
        if (_recordingStopTask is { IsCompleted: false } pending)
        {
            await pending;
            return;
        }
        var service = _recordingService;
        if (service is null || service.ActiveCount == 0) return;

        var stopTask = service.StopAllAsync(reason);
        _recordingStopTask = stopTask;
        try
        {
            await stopTask;
            SaveRecordingIndex(_state.SelectedAccountId);
        }
        catch (Exception exception)
        {
            AppLogger.Warn("Stopping recordings with playback failed. " + AppLogger.SanitizeText(exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_recordingStopTask, stopTask)) _recordingStopTask = null;
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => PlayRelative(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => PlayRelative(1);

    private void PlayRelative(int delta)
    {
        if (_currentChannel is { MediaKind: MediaKind.Series } currentEpisode)
        {
            var siblings = (IReadOnlyList<Channel>?)_playingSeriesEpisodes ?? ViewingHistory.SeriesSiblings(_channels, currentEpisode);
            if (siblings.Count > 1)
            {
                var ordered = siblings.OrderBy(episode => ViewingHistory.EpisodeOrder(episode).Season)
                    .ThenBy(episode => ViewingHistory.EpisodeOrder(episode).Episode)
                    .ThenBy(episode => episode.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var currentIndex = ordered.FindIndex(episode => ItemIdentity.For(episode) == ItemIdentity.For(currentEpisode));
                if (currentIndex >= 0)
                {
                    var targetIndex = Math.Clamp(currentIndex + delta, 0, ordered.Count - 1);
                    ActivateVodOrLive(ordered[targetIndex]);
                    return;
                }
            }
        }
        var playableChannels = _activeFolder is null && _activeLetter is null ? _filteredChannels : _visibleChannels;
        if (playableChannels.Count == 0) return;

        var index = _currentChannel is null
            ? -1
            : playableChannels.FindIndex(c => string.Equals(c.Id, _currentChannel.Id, StringComparison.OrdinalIgnoreCase));
        if (_currentChannel is not null && index < 0)
        {
            StatusText.Text = "The playing channel is outside the current list.";
            return;
        }
        if (index < 0 && _playbackState.SelectedChannel is { } selected)
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
            StatusText.Text = "Add subtitle failed: " + AppLogger.SanitizeText(ex.Message);
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
            _preferredSubtitleApplied = true;
            StatusText.Text = option.Id < 0 ? "Subtitles off." : "Subtitle selected: " + option.Name;
            UpdateSubtitleMenus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Subtitle selection failed: " + AppLogger.SanitizeText(ex.Message);
        }
    }

    private void UpdatePlaybackPosition()
    {
        if (_mediaPlayer is null || _isSeeking) return;
        ApplyPreferredTracks(_mediaPlayer);
        SavePlaybackProgress(force: false);
        if (TryGetLiveTimeshiftWindow(out var liveWindow) && liveWindow!.Duration > TimeSpan.Zero)
        {
            SeekSlider.IsEnabled = true;
            _updatingSeekSlider = true;
            try { SeekSlider.Value = LiveTimeshiftSliderValue(GetCurrentLiveBehind(), liveWindow.Duration); }
            finally { _updatingSeekSlider = false; }
            TimeText.Text = FormatLiveTimeshiftPosition(GetCurrentLiveBehind(), liveWindow.Duration);
            TimeText.Visibility = Visibility.Visible;
            return;
        }
        var length = _mediaPlayer.Length;
        var time = _mediaPlayer.Time;
        if (length > 0)
        {
            SeekSlider.IsEnabled = true;
            _updatingSeekSlider = true;
            try { SeekSlider.Value = Math.Clamp((double)time / length * 1000.0, 0, 1000); }
            finally { _updatingSeekSlider = false; }
            TimeText.Text = FormatTime(time) + " / " + FormatTime(length);
        }
        else
        {
            SeekSlider.IsEnabled = false;
            _updatingSeekSlider = true;
            try { SeekSlider.Value = 0; }
            finally { _updatingSeekSlider = false; }
            TimeText.Text = _mediaPlayer.IsPlaying ? "Live" : "00:00 / 00:00";
        }
        TimeText.Visibility = _pausedPlayback is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SavePlaybackProgress(bool force)
    {
        if (_currentChannel is not { MediaKind: not MediaKind.Live } channel || _mediaPlayer is null || _mediaPlayer.Time <= 0) return;
        var now = DateTime.UtcNow;
        if (!force && now - _lastProgressSaveUtc < TimeSpan.FromSeconds(5)) return;
        if (ViewingHistory.RecordPosition(_state.SelectedLibrary, channel, _mediaPlayer.Time, _mediaPlayer.Length, now))
        {
            _lastProgressSaveUtc = now;
            if (!_suspendProgressSave) _store.Save(_state);
        }
    }

    private static string FormatTime(long ms)
    {
        if (ms <= 0) return "00:00";
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isSeekingLiveTimeshift = TryGetLiveTimeshiftWindow(out var window) && window!.Duration > TimeSpan.Zero;
        _isSeeking = true;
    }

    private async void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        if (_isSeekingLiveTimeshift)
        {
            _isSeekingLiveTimeshift = false;
            await CommitLiveTimeshiftSliderAsync();
            return;
        }
        if (_mediaPlayer is null || _mediaPlayer.Length <= 0) return;
        _mediaPlayer.Time = PlaybackSeekMath.TimeForSlider(_mediaPlayer.Length, SeekSlider.Value);
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeekSlider || _mediaPlayer is null) return;
        if (TryGetLiveTimeshiftWindow(out var liveWindow) && liveWindow!.Duration > TimeSpan.Zero)
        {
            TimeText.Text = FormatLiveTimeshiftPosition(LiveTimeshiftBehindForSlider(SeekSlider.Value, liveWindow.Duration), liveWindow.Duration);
            TimeText.Visibility = Visibility.Visible;
            if (!_isSeeking && SeekSlider.IsKeyboardFocusWithin)
                _ = CommitLiveTimeshiftSliderAsync();
            return;
        }
        if (_mediaPlayer.Length <= 0) return;
        if (!_isSeeking && SeekSlider.IsKeyboardFocusWithin)
            _mediaPlayer.Time = PlaybackSeekMath.TimeForSlider(_mediaPlayer.Length, SeekSlider.Value);
        if (!_isSeeking && !SeekSlider.IsKeyboardFocusWithin) return;
        TimeText.Text = FormatTime(PlaybackSeekMath.TimeForSlider(_mediaPlayer.Length, SeekSlider.Value)) + " / " + FormatTime(_mediaPlayer.Length);
    }

    private bool TryGetLiveTimeshiftWindow(out LiveTimeshiftWindow? window)
    {
        window = null;
        return _currentChannel?.MediaKind == MediaKind.Live && _tuner?.TryInspectLiveTimeshift(out window, out _) == true;
    }

    private static double LiveTimeshiftSliderValue(TimeSpan behindLive, TimeSpan available) =>
        available <= TimeSpan.Zero ? 1000 : Math.Clamp(1000.0 * (1.0 - behindLive.TotalMilliseconds / available.TotalMilliseconds), 0, 1000);

    private static TimeSpan LiveTimeshiftBehindForSlider(double sliderValue, TimeSpan available) =>
        TimeSpan.FromMilliseconds(available.TotalMilliseconds * (1.0 - Math.Clamp(sliderValue, 0, 1000) / 1000.0));

    private static string FormatLiveTimeshiftPosition(TimeSpan behindLive, TimeSpan available) =>
        behindLive < TimeSpan.FromSeconds(1) ? "Live / " + FormatLiveDelay(available) : "Behind live " + FormatLiveDelay(behindLive) + " / " + FormatLiveDelay(available);

    private async Task CommitLiveTimeshiftSliderAsync()
    {
        if (_tuner is null || !TryGetLiveTimeshiftWindow(out var window) || window!.Duration <= TimeSpan.Zero) return;
        var targetBehindLive = LiveTimeshiftBehindForSlider(SeekSlider.Value, window.Duration);
        TimeshiftPlaybackSwitchResult result;
        if (targetBehindLive < TimeSpan.FromSeconds(1)) result = await _tuner.ReturnToLiveAsync();
        else result = await _tuner.SeekLiveTimeshiftAsync(targetBehindLive);
        if (result.Success)
        {
            ClearPauseResumeState();
            _livePauseStartedUtc = null;
            _liveBehind = targetBehindLive < TimeSpan.FromSeconds(1) ? TimeSpan.Zero : targetBehindLive;
            StatusText.Text = targetBehindLive < TimeSpan.FromSeconds(1)
                ? "Live: " + (_currentChannel?.Name ?? "")
                : "Timeshift position: " + FormatLiveDelay(targetBehindLive) + " behind live";
        }
        else
        {
            StatusText.Text = result.FailureReason ?? "The requested timeshift position is unavailable.";
        }
        UpdatePlaybackPosition();
        UpdateLiveDelayUi();
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
            AppLogger.Warn("Could not save audio state on exit. " + AppLogger.SanitizeText(ex.Message));
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
        if (_channelsVisible && SidebarColumn.ActualWidth >= 260) _savedSidebarWidth = SidebarColumn.ActualWidth;
        _channelsVisible = !_channelsVisible;
        SidebarColumn.MinWidth = _channelsVisible ? 260 : 0;
        SidebarColumn.Width = _channelsVisible ? new GridLength(_savedSidebarWidth) : new GridLength(0);
        Sidebar.Visibility = _channelsVisible ? Visibility.Visible : Visibility.Collapsed;
        SplitterColumn.Width = _channelsVisible ? new GridLength(5) : new GridLength(0);
        SidebarSplitter.Visibility = _channelsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        StreamInfoText.Visibility = sender is WpfMenuItem { IsChecked: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MorePlayback_Click(object sender, RoutedEventArgs e)
    {
        MorePlaybackButton.ContextMenu.PlacementTarget = MorePlaybackButton;
        MorePlaybackButton.ContextMenu.IsOpen = true;
    }

    private void PlayerGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 690;
        PreviousButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TimeText.Visibility = !compact && _pausedPlayback is not null ? Visibility.Visible : Visibility.Collapsed;
        VolumeSlider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NowPlayingText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToggleFullScreen()
    {
        if (_mediaPlayer is null) return;
        if (_isFullScreen) ExitFullScreen();
        else EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        if (_channelsVisible && SidebarColumn.ActualWidth >= 260) _savedSidebarWidth = SidebarColumn.ActualWidth;
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

        CapturePointerScreenPosition();
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
        TopMenuRow.Height = new GridLength(48);
        StatusRow.Height = new GridLength(28);
        RestoreControlsToPlayerGrid();
        ControlsRow.Height = GridLength.Auto;
        _channelsVisible = _channelsVisibleBeforeFullScreen;
        SidebarColumn.MinWidth = _channelsVisible ? 260 : 0;
        Sidebar.Visibility = _channelsVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = _channelsVisible ? new GridLength(_savedSidebarWidth) : new GridLength(0);
        SplitterColumn.Width = _channelsVisible ? new GridLength(5) : new GridLength(0);
        SidebarSplitter.Visibility = _channelsVisible ? Visibility.Visible : Visibility.Collapsed;

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

    private const int WM_MOUSEWHEEL = 0x020A;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(VideoWheelWndProc);
    }

    private IntPtr VideoWheelWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Covers the idle surface and any wheel message routed to the WPF window.
        // During playback the WinForms video child handles its own wheel event.
        if (msg == WM_MOUSEWHEEL)
        {
            var screenPoint = new Point(unchecked((short)(lParam.ToInt64() & 0xFFFF)), unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF)));
            var pos = VideoHost.PointFromScreen(screenPoint);
            if (pos.X >= 0 && pos.Y >= 0 && pos.X <= VideoHost.ActualWidth && pos.Y <= VideoHost.ActualHeight)
            {
                var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                ApplyVolumeWheel(delta);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void ApplyVolumeWheel(int delta)
    {
        SetVolume(_state.VolumeLevel + (delta > 0 ? 5 : -5));
        ShowControls();
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) => HandlePointerMovement();
    private void ControlsBar_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => HandlePointerMovement();

    private void HandlePointerMovement()
    {
        if (!_isFullScreen)
        {
            ShowControls();
            return;
        }

        var current = System.Windows.Forms.Cursor.Position;
        if (!_hasPointerScreenPosition)
        {
            _lastPointerScreenPosition = current;
            _hasPointerScreenPosition = true;
            return;
        }

        var deltaX = Math.Abs(current.X - _lastPointerScreenPosition.X);
        var deltaY = Math.Abs(current.Y - _lastPointerScreenPosition.Y);
        if (deltaX <= 2 && deltaY <= 2) return;

        _lastPointerScreenPosition = current;
        if (DateTime.UtcNow < _ignoreFullscreenPointerActivityUntilUtc) return;
        ShowControls();
    }

    private void CapturePointerScreenPosition()
    {
        _lastPointerScreenPosition = System.Windows.Forms.Cursor.Position;
        _hasPointerScreenPosition = true;
    }

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
            if (ControlsBar.IsMouseOver || ControlsBar.IsMouseCaptureWithin || ControlsBar.IsKeyboardFocusWithin ||
                SeekSlider.IsMouseCaptureWithin || VolumeSlider.IsMouseCaptureWithin ||
                VideoHost.ContextMenu?.IsOpen == true)
            {
                _controlsHideTimer.Stop();
                _controlsHideTimer.Start();
                return;
            }
            _controlsHideTimer.Stop();
            FullScreenControlsPopup.IsOpen = false;
            CapturePointerScreenPosition();
            _ignoreFullscreenPointerActivityUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
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
        if (hidden)
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
            // WPF's override does not cross into the native WinForms/LibVLC
            // child HWND. Hide the Win32 cursor for that surface as well.
            System.Windows.Forms.Cursor.Hide();
        }
        else
        {
            System.Windows.Forms.Cursor.Show();
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
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

    private async void ToggleEpg_Click(object sender, RoutedEventArgs e)
    {
        _state.EpgEnabled = EpgEnabledMenuItem.IsChecked;
        _store.Save(_state);
        UpdateEpgEnabledUi();

        if (!_state.EpgEnabled)
        {
            CancelEpgRefresh();
            _epgGuide = null;
            _epgFetchedAt = null;
            UpdateBrowseGuide();
            StatusText.Text = "Programme guide disabled.";
            return;
        }

        StatusText.Text = "Programme guide enabled.";
        if (_channels.Count > 0) await RefreshEpgAsync(showStatus: true);
    }

    private void UpdateEpgEnabledUi()
    {
        if (EpgPanel is null || RefreshEpgMenuItem is null) return;
        EpgPanel.Visibility = _state.EpgEnabled ? Visibility.Visible : Visibility.Collapsed;
        BrowseGuidePanel.Visibility = _state.EpgEnabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshEpgMenuItem.IsEnabled = _state.EpgEnabled;
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

    private async void RefreshEpg_Click(object sender, RoutedEventArgs e)
    {
        if (!_state.EpgEnabled)
        {
            StatusText.Text = "Enable Programme guide (EPG) in Settings first.";
            return;
        }
        await RefreshEpgAsync(showStatus: true);
    }

    private async Task RefreshEpgAsync(bool showStatus)
    {
        if (!_state.EpgEnabled || _isShuttingDown) return;
        CancelEpgRefresh();
        var cts = new CancellationTokenSource();
        _epgCts = cts;
        _epgLastAttempt = DateTimeOffset.UtcNow;
        var accountId = _state.SelectedAccountId;
        var account = _state.Account.Clone();

        try
        {
            if (_epgService.BuildEpgUrl(account) is null)
            {
                _epgRefreshFailed = true;
                UpdateEpgDisplay();
                if (showStatus) StatusText.Text = "No EPG URL is configured for this account.";
                return;
            }

            if (showStatus) StatusText.Text = "Refreshing programme guide...";
            EpgNowText.Text = "Programme guide loading...";
            EpgNextText.Text = "—";
            UpdateBrowseGuide();
            var guide = await _epgService.LoadAsync(account, _channels, cts.Token,
                _state.SelectedLibrary.GuideMappings.Values.ToArray());
            if (cts.IsCancellationRequested || !_state.EpgEnabled || _isShuttingDown ||
                !ReferenceEquals(_epgCts, cts) ||
                !string.Equals(accountId, _state.SelectedAccountId, StringComparison.OrdinalIgnoreCase)) return;
            _epgGuide = guide;
            _epgFetchedAt = DateTimeOffset.UtcNow;
            _epgRefreshFailed = false;
            if (guide is not null)
                await Task.Run(() => _store.SaveGuideCache(accountId, guide.Snapshot(_epgFetchedAt.Value)), cts.Token);
            UpdateEpgDisplay();
            if (showStatus) StatusText.Text = guide is null ? "No programme guide is available." : $"Programme guide updated: {guide.ProgrammeCount:N0} programmes";
        }
        catch (OperationCanceledException)
        {
            // Account changes and repeated refreshes cancel the older request.
            if (ReferenceEquals(_epgCts, cts) && showStatus) StatusText.Text = "Guide refresh canceled. Saved guide remains available.";
        }
        catch (Exception ex)
        {
            AppLogger.Warn("EPG update failed. " + AppLogger.SanitizeText(ex.Message));
            if (!ReferenceEquals(_epgCts, cts)) return;
            _epgRefreshFailed = true;
            UpdateEpgDisplay();
            if (showStatus) StatusText.Text = "EPG update failed: " + AppLogger.SanitizeText(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_epgCts, cts)) _epgCts = null;
            cts.Dispose();
            UpdateBrowseGuide();
        }
    }

    private void CancelEpgRefresh()
    {
        var cts = _epgCts;
        _epgCts = null;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void CancelEpg_Click(object sender, RoutedEventArgs e)
    {
        CancelEpgRefresh();
        StatusText.Text = "Guide refresh canceled. Saved guide remains available.";
        UpdateBrowseGuide();
    }

    private void UpdateBrowseGuide()
    {
        if (BrowseGuidePanel is null || !_state.EpgEnabled) return;
        GuideCancelButton.IsEnabled = _epgCts is not null;
        GuideGridButton.IsEnabled = _epgGuide is not null;
        var channel = _playbackState.SelectedChannel;
        _browseNow = null;
        _browseNext = null;
        if (channel is null || channel.MediaKind != MediaKind.Live)
            BrowseGuideState.Text = "Select a live channel to see its programmes.";
        else if (_epgGuide is null)
            BrowseGuideState.Text = _epgCts is not null ? "Loading guide. Cancel is available." :
                _epgRefreshFailed ? "Guide unavailable. Check the source URL, then Refresh." :
                "No guide loaded. Enable a source in account settings, then Refresh.";
        else
        {
            var key = ItemIdentity.For(channel);
            _state.SelectedLibrary.GuideMappings.TryGetValue(key, out var mappedId);
            var pair = _epgGuide.GetNowNext(channel, mappedId: mappedId,
                offsetMinutes: _state.SelectedLibrary.GuideOffsetMinutes);
            _browseNow = pair.Now;
            _browseNext = pair.Next;
            var age = _epgFetchedAt.HasValue ? DateTimeOffset.UtcNow - _epgFetchedAt.Value : TimeSpan.MaxValue;
            var freshness = _epgRefreshFailed || age >= GuideRefreshInterval ? "Saved guide is stale. Refresh to update. " :
                _epgCts is not null ? "Refreshing guide; saved programmes shown. " : "";
            BrowseGuideState.Text = freshness + (!_epgGuide.HasMatch(channel, mappedId) ?
                "No guide match for this channel. Use Map / time." :
                pair.Now is null && pair.Next is null ? "Channel matched, but no current or upcoming schedule. Refresh the guide." : channel.Name);
        }
        BrowseNowButton.Content = "Now: " + FormatProgramme(_browseNow, "No current programme");
        BrowseNextButton.Content = "Next: " + FormatProgramme(_browseNext, "No upcoming programme");
        BrowseNowButton.IsEnabled = _browseNow is not null;
        BrowseNextButton.IsEnabled = _browseNext is not null;
        UpdateCatchupButtons(channel);
    }

    private void BrowseProgramme_Click(object sender, RoutedEventArgs e)
    {
        var programme = ReferenceEquals(sender, BrowseNowButton) ? _browseNow : _browseNext;
        if (programme is null) return;
        if (_playbackState.SelectedChannel is { } channel) ShowProgrammeDetails(channel, programme);
    }

    private void GuideGrid_Click(object sender, RoutedEventArgs e)
    {
        if (_epgGuide is null) return;
        var guide = _epgGuide;
        var library = _state.SelectedLibrary;
        var stale = _epgRefreshFailed || !_epgFetchedAt.HasValue ||
            DateTimeOffset.UtcNow - _epgFetchedAt.Value >= GuideRefreshInterval;
        new GuideGridWindow(guide, _channels, _playbackState.SelectedChannel, _currentChannel,
            channel => library.GuideMappings.GetValueOrDefault(ItemIdentity.For(channel)),
            library.GuideOffsetMinutes, stale, (channel, programme) => _ = StartCatchupAsync(channel, programme),
            (channel, programme) => ScheduleProgramme(channel, programme)) { Owner = this }.ShowDialog();
    }

    private async void GuideMap_Click(object sender, RoutedEventArgs e)
    {
        var channel = _playbackState.SelectedChannel;
        if (channel is null || channel.MediaKind != MediaKind.Live)
        {
            StatusText.Text = "Select a live channel before mapping its guide.";
            return;
        }
        if (_epgGuide is null)
        {
            StatusText.Text = "Load a guide first to see available channel IDs.";
            return;
        }
        var library = _state.SelectedLibrary;
        var key = ItemIdentity.For(channel);
        library.GuideMappings.TryGetValue(key, out var existing);
        var choices = _epgGuide.Channels.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var dialog = new Window { Title = "Guide mapping and time", Owner = this, Width = 430, Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Guide channel for " + channel.Name, TextWrapping = TextWrapping.Wrap });
        var mapBox = new System.Windows.Controls.ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedValuePath = "Id",
            SelectedValue = existing, IsEditable = false, Margin = new Thickness(0, 5, 0, 12) };
        panel.Children.Add(mapBox);
        panel.Children.Add(new TextBlock { Text = "Time correction in minutes (−720 to +720; applies to this account)" , TextWrapping = TextWrapping.Wrap });
        var offsetBox = new System.Windows.Controls.TextBox { Text = library.GuideOffsetMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 5, 0, 12) };
        panel.Children.Add(offsetBox);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var save = new System.Windows.Controls.Button { Content = "Save", MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
        var clear = new System.Windows.Controls.Button { Content = "Clear mapping", MinWidth = 100 };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(offsetBox.Text, out var minutes) || minutes is < -720 or > 720)
            {
                MessageBox.Show(dialog, "Enter a time correction between −720 and +720 minutes.");
                return;
            }
            library.GuideOffsetMinutes = minutes;
            if (mapBox.SelectedValue is string id) library.GuideMappings[key] = id;
            _store.Save(_state);
            dialog.DialogResult = true;
        };
        clear.Click += (_, _) => { library.GuideMappings.Remove(key); _store.Save(_state); dialog.DialogResult = true; };
        actions.Children.Add(save);
        actions.Children.Add(clear);
        panel.Children.Add(actions);
        dialog.Content = panel;
        if (dialog.ShowDialog() == true)
        {
            UpdateBrowseGuide();
            if (library.GuideMappings.ContainsKey(key)) await RefreshEpgAsync(showStatus: true);
        }
    }

    private void UpdateEpgDisplay()
    {
        if (!_state.EpgEnabled || _isShuttingDown) return;
        UpdateBrowseGuide();
        if (_currentChannel is null)
        {
            EpgNowText.Text = _epgGuide is null ? "No programme selected" : "Select a live channel";
            EpgNextText.Text = "—";
            EpgNowText.ToolTip = null;
            EpgNextText.ToolTip = null;
            return;
        }

        if (_catchupPlayback is { } archive)
        {
            EpgNowText.Text = "Archive: " + FormatProgramme(archive.Programme, "Programme");
            EpgNextText.Text = "Go Live returns to the current broadcast.";
            EpgNowText.ToolTip = BuildProgrammeToolTip(archive.Programme);
            EpgNextText.ToolTip = null;
            return;
        }

        if (_currentChannel.MediaKind != MediaKind.Live)
        {
            EpgNowText.Text = "Programme guide is for live TV";
            EpgNextText.Text = "—";
            return;
        }

        _state.SelectedLibrary.GuideMappings.TryGetValue(ItemIdentity.For(_currentChannel), out var playingMap);
        var nowNext = _epgGuide?.GetNowNext(_currentChannel, mappedId: playingMap,
            offsetMinutes: _state.SelectedLibrary.GuideOffsetMinutes);
        EpgNowText.Text = FormatProgramme(nowNext?.Now, "No current programme information");
        EpgNextText.Text = FormatProgramme(nowNext?.Next, "No upcoming programme information");
        EpgNowText.ToolTip = BuildProgrammeToolTip(nowNext?.Now);
        EpgNextText.ToolTip = BuildProgrammeToolTip(nowNext?.Next);
    }

    private static string FormatProgramme(EpgProgramme? programme, string fallback)
    {
        if (programme is null) return fallback;
        return $"{programme.Start.LocalDateTime:t}–{programme.Stop.LocalDateTime:t}  {programme.Title}";
    }

    private static string? BuildProgrammeToolTip(EpgProgramme? programme)
    {
        if (programme is null) return null;
        var detail = FormatProgramme(programme, string.Empty);
        if (!string.IsNullOrWhiteSpace(programme.Category)) detail += Environment.NewLine + programme.Category;
        if (!string.IsNullOrWhiteSpace(programme.Description)) detail += Environment.NewLine + Environment.NewLine + programme.Description;
        return detail;
    }

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
            var selected = _state.EnsureSelectedAccount();
            var local = $"Account: {selected.DisplayName}\n" +
                $"Saved library: {(_store.HasChannelCacheForAccount(selected.Id) ? "Available" : "Not available")}\n" +
                $"Last successful refresh: {(selected.LastPlaylistUpdatedUtc is { } updated ? updated.ToLocalTime().ToString("g") : "Not yet recorded")}\n\n";
            if (string.IsNullOrWhiteSpace(_state.Account.ServerUrl) || string.IsNullOrWhiteSpace(_state.Account.Username))
            {
                MessageBox.Show(this, local + "Connection use and expiry are unavailable for this playlist account.", "Account information", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var info = await _playlistService.FetchAccountInfoSummaryAsync(_state.Account, timeout.Token);
                MessageBox.Show(this, local + info, "Account information", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            StatusText.Text = "Ready";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, AppLogger.SanitizeText(ex.Message), "Account information error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private const string GitHubProjectUrl = "https://github.com/cyrusthelittle/classic-windows-iptv-player";

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is null ? "unknown" : version.ToString(3);

        MessageBox.Show(
            this,
            $"Classic Windows IPTV Player\nVersion {versionText}",
            "About",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(GitHubProjectUrl) { UseShellExecute = true });
    }

    private void ToggleUpdateChecks_Click(object sender, RoutedEventArgs e)
    {
        _state.CheckForUpdatesOnStartup = UpdateChecksMenuItem.IsChecked;
        _store.Save(_state);
        StatusText.Text = _state.CheckForUpdatesOnStartup
            ? "Automatic update checks enabled."
            : "Automatic update checks disabled.";
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(showUpToDateMessage: true);
    }

    private async Task CheckForUpdatesAsync(bool showUpToDateMessage)
    {
        if (_isCheckingForUpdates)
        {
            if (showUpToDateMessage) StatusText.Text = "An update check is already in progress.";
            return;
        }

        _isCheckingForUpdates = true;
        CheckForUpdatesMenuItem.IsEnabled = false;
        if (showUpToDateMessage) StatusText.Text = "Checking GitHub for updates...";

        try
        {
            var release = await _updateService.GetAvailableUpdateAsync();
            if (release is null)
            {
                if (showUpToDateMessage)
                {
                    StatusText.Text = "You are using the latest version.";
                    MessageBox.Show(
                        this,
                        "You are using the latest version of Classic Windows IPTV Player.",
                        "No updates available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            AppLogger.Info($"Update available. current={_updateService.CurrentVersion}; latest={release.DisplayVersion}");
            var updateWindow = new UpdateWindow(_updateService.CurrentVersion, release) { Owner = this };
            updateWindow.ShowDialog();

            switch (updateWindow.PromptResult)
            {
                case UpdatePromptResult.NeverRemind:
                    _state.CheckForUpdatesOnStartup = false;
                    UpdateChecksMenuItem.IsChecked = false;
                    _store.Save(_state);
                    StatusText.Text = "Automatic update reminders disabled.";
                    break;
                case UpdatePromptResult.UpdateNow:
                    try
                    {
                        Process.Start(new ProcessStartInfo(release.PageUri.AbsoluteUri) { UseShellExecute = true });
                        StatusText.Text = "Opened the latest release on GitHub.";
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Could not open the GitHub release page.", ex);
                        StatusText.Text = "Could not open the GitHub release page.";
                        MessageBox.Show(
                            this,
                            "Windows could not open the release page. You can download the update manually from:\n\n" + release.PageUri.AbsoluteUri,
                            "Could not open update",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    break;
                default:
                    StatusText.Text = "Update postponed until a later startup.";
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("GitHub update check failed.", ex);
            if (showUpToDateMessage)
            {
                StatusText.Text = "Could not check for updates.";
                MessageBox.Show(
                    this,
                    "The update check could not reach GitHub. Please check your internet connection and try again later.\n\n" + AppLogger.SanitizeText(ex.Message),
                    "Update check failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            _isCheckingForUpdates = false;
            CheckForUpdatesMenuItem.IsEnabled = true;
        }
    }

    private async void ChangeAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_isChangingAccount) return;
        _isChangingAccount = true;

        SavePlaybackProgress(force: true);
        SaveCurrentAudioState();
        _store.Save(_state);
        var login = new LoginWindow { Owner = this, PlayingAccountId = _state.SelectedAccountId };
        _suspendProgressSave = true;
        try
        {
            var opened = login.ShowDialog() == true;
            ReloadAfterAccountEditor();
            _suspendProgressSave = false;
            if (!opened)
            {
                return;
            }

            var result = login.LoginResult;
            if (string.Equals(_state.SelectedAccountId, result.AccountId, StringComparison.OrdinalIgnoreCase))
            {
                // Saving edits to the current account does not interrupt its playing stream.
                StatusText.Text = "Account edits saved. Current playback continues; source changes apply on the next tune.";
                return;
            }
            AppLogger.Info("Changing account without restart. accountId=" + result.AccountId + "; updatePlaylist=" + result.UpdatePlaylist);

            _multiViewWindow?.Close();
            _multiViewWindow = null;
            StopPlayback();
            await StopRecordingsSafelyAsync(RecordingStopReason.Restarted);
            _scheduledRecordingTimer.Stop();
            _scheduledRecordings?.Dispose();
            _scheduledRecordings = null;
            _recordingService?.Dispose();
            SaveRecordingIndex(_state.SelectedAccountId);
            _activeRecordingId = null;
            _libraryLoadCts?.Cancel();
            _seriesLoadCts?.Cancel();
            ++_libraryLoadGeneration;
            ++_seriesLoadGeneration;
            ResetAccountView();

            // The account manager uses its own AppState instance and may have added
            // or removed accounts. Reload it so this window sees those changes.
            _state = _store.Load();
            _state.SelectedAccountId = result.AccountId;
            var selectedAccount = _state.EnsureSelectedAccount();
            selectedAccount.Settings = result.Account.Clone();
            _state.Account = selectedAccount.Settings.Clone();
            _store.Save(_state);
            InitializeRecordingService();

            InitializeBufferBox();
            InitializeVolumeControls();
            await LoadChannelsAsync(result.UpdatePlaylist);
            AppLogger.Info("Account changed successfully. accountId=" + result.AccountId);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Account change failed.", ex);
            StatusText.Text = "Account change failed: " + AppLogger.SanitizeText(ex.Message);
            MessageBox.Show(this, AppLogger.SanitizeText(ex.Message), "Account change error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _suspendProgressSave = false;
            _isChangingAccount = false;
        }
    }

    private void ReloadAfterAccountEditor()
    {
        SavePlaybackProgress(force: true);
        var playingAccountId = _state.SelectedAccountId;
        var currentLibrary = _state.SelectedLibrary;
        var updated = _store.Load();
        if (updated.Accounts.Any(account => string.Equals(account.Id, playingAccountId, StringComparison.OrdinalIgnoreCase)))
        {
            updated.SelectedAccountId = playingAccountId;
            ViewingHistory.Merge(updated.SelectedLibrary, currentLibrary);
        }
        _state = updated;
        _store.Save(_state);
    }

    private void ResetAccountView()
    {
        ClearCatchup();
        _filterCts?.Cancel();
        _sourceProbeCts?.Cancel();
        _sourceProbeCts?.Dispose();
        _sourceProbeCts = null;
        CancelEpgRefresh();
        _epgGuide = null;
        _epgFetchedAt = null;
        _epgLastAttempt = null;

        var previousMedia = _currentMedia;
        _currentMedia = null;
        previousMedia?.Dispose();
        _playbackState.Reset();
        _searchIndex = null;
        _channels = [];
        _sourceChannels = [];
        _filteredChannels = [];
        _visibleChannels = [];
        _visibleEntries = [];
        _activeFolder = null;
        _activeFavoriteFolderId = null;
        _activeLetter = null;
        _activeSeriesId = null;
        _activeSeriesName = null;
        _activeSeriesEpisodes = null;

        SearchBox.Clear();
        ChannelList.ItemsSource = null;
        FolderBackButton.Visibility = Visibility.Collapsed;
        BreadcrumbPanel.Children.Clear();
        BreadcrumbPanel.Visibility = Visibility.Collapsed;
        CountText.Text = "0 items";
        NowPlayingText.Text = "Select a channel to play";
        UpdateEpgDisplay();
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

    private void Reconnect5_Click(object sender, RoutedEventArgs e) => SetReconnectAttempts(5);
    private void Reconnect10_Click(object sender, RoutedEventArgs e) => SetReconnectAttempts(10);
    private void Reconnect15_Click(object sender, RoutedEventArgs e) => SetReconnectAttempts(15);
    private void Reconnect20_Click(object sender, RoutedEventArgs e) => SetReconnectAttempts(20);

    private void SetReconnectAttempts(int attempts)
    {
        _state.ReconnectAttempts = Math.Clamp(attempts, 1, 30);
        _store.Save(_state);
        if (_tuner is not null) _tuner.MaxAttempts = _state.ReconnectAttempts;
        StatusText.Text = $"Reconnect attempts set to {_state.ReconnectAttempts}.";
        AppLogger.Info("Reconnect attempts set to " + _state.ReconnectAttempts + ".");
    }

    private int GetReconnectAttempts()
    {
        // Older state files (saved before this setting existed) load as 0.
        var value = _state.ReconnectAttempts <= 0 ? 10 : _state.ReconnectAttempts;
        return Math.Clamp(value, 1, 30);
    }

    private static FeedbackOutbox CreateFeedbackOutbox(AppState state)
    {
        Uri? endpoint = AppState.TryValidateFeedbackEndpoint(state.FeedbackEndpoint, out var configured)
            ? configured
            : null;
        var directory = Path.Combine(AppContext.BaseDirectory, "cache");
        Directory.CreateDirectory(directory);
        return new FeedbackOutbox(Path.Combine(directory, "feedback-outbox.dat"), endpoint);
    }

    private void FeedbackEndpoint_Click(object sender, RoutedEventArgs e) => EditFeedbackEndpoint(this);

    private bool EditFeedbackEndpoint(Window owner)
    {
        var dialog = new FeedbackEndpointWindow(_state.FeedbackEndpoint) { Owner = owner };
        if (dialog.ShowDialog() != true) return false;

        _state.FeedbackEndpoint = dialog.Endpoint ?? string.Empty;
        _store.Save(_state);
        _feedbackOutbox.Dispose();
        _feedbackOutbox = CreateFeedbackOutbox(_state);
        StatusText.Text = string.IsNullOrEmpty(_state.FeedbackEndpoint)
            ? "Feedback delivery is not configured."
            : "Feedback endpoint saved in protected local settings.";
        return !string.IsNullOrEmpty(_state.FeedbackEndpoint);
    }

    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var queued = await _feedbackOutbox.GetPendingAsync();
            var previous = queued.FirstOrDefault(item => item.Type == "feedback");
            FeedbackWindow? dialog = null;
            dialog = new FeedbackWindow((message, includeLogs) => SendFeedbackAsync(message, includeLogs, dialog!), previous?.Message) { Owner = this };
            dialog.ShowDialog();
        }
        catch
        {
            MessageBox.Show(this, "The saved feedback queue could not be opened. Existing queued messages were left untouched.",
                "Feedback unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<FeedbackDeliveryResult> SendFeedbackAsync(string message, bool includeLogs, Window owner)
    {
        var queued = await _feedbackOutbox.GetPendingAsync();
        var existing = queued.FirstOrDefault(item => item.Type == "feedback" &&
            string.Equals(item.Message, message.Trim(), StringComparison.Ordinal));
        var log = includeLogs ? ReadRecentFeedbackLogs() : null;
        FeedbackQueueItem item;
        if (existing is not null && await _feedbackOutbox.UpdateFeedbackAsync(existing.Id, message, log))
            item = existing with { Message = message.Trim(), Log = log };
        else
            item = await _feedbackOutbox.EnqueueFeedbackAsync(message, log);
        if (!AppState.TryValidateFeedbackEndpoint(_state.FeedbackEndpoint, out _))
        {
            // The Send click is explicit consent to deliver this message. Ask for
            // the HTTPS destination only when delivery is actually requested.
            if (!EditFeedbackEndpoint(owner))
                return new(false, "Feedback is safely queued. Configure its HTTPS endpoint in Settings, then press Send again.", true);
        }

        var result = await _feedbackOutbox.SendAsync(item.Id);
        return result.Status switch
        {
            FeedbackDeliveryStatus.Sent => new(true, "Feedback sent."),
            FeedbackDeliveryStatus.EndpointNotConfigured => new(false, "Feedback is safely queued. Configure its HTTPS endpoint in Settings, then press Send again.", true),
            FeedbackDeliveryStatus.QueuedForRetry => new(false, "Delivery failed. The message is saved on this device; press Send to retry.", true),
            _ => new(false, "No message was sent. The queued message is still saved.", true)
        };
    }

    private static string ReadRecentFeedbackLogs()
    {
        var directory = Path.GetDirectoryName(AppLogger.CurrentLogPath);
        if (string.IsNullOrWhiteSpace(directory)) return string.Empty;

        var sections = new List<string>();
        foreach (var name in new[] { "app.log", "app.previous.log" })
        {
            var path = Path.Combine(directory, name);
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                const int maxRead = 128 * 1024;
                if (stream.Length > maxRead) stream.Seek(-maxRead, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(text)) sections.Add(text);
            }
            catch { /* Omit unreadable diagnostics; the feedback message can still be sent. */ }
        }
        return string.Join("\n", sections);
    }

    private void RestartStream_Click(object sender, RoutedEventArgs e)
    {
        if (_catchupPlayback is { } archive)
        {
            _ = StartCatchupAsync(archive.Source, archive.Programme);
            return;
        }
        if (_currentChannel?.MediaKind == MediaKind.Live) ClearLiveDelay();
        if (_currentChannel is not null) PlayChannel(_currentChannel);
        else StatusText.Text = "Select a channel and press Play first.";
    }

    private async void RewindLive_Click(object sender, RoutedEventArgs e)
    {
        if (_tuner is null || _currentChannel?.MediaKind != MediaKind.Live)
        {
            StatusText.Text = "Rewind is available only for a supported live HLS channel.";
            return;
        }

        RewindLiveButton.IsEnabled = false;
        var targetBehindLive = GetCurrentLiveBehind() + TimeSpan.FromSeconds(30);
        var result = await _tuner.SeekLiveTimeshiftAsync(targetBehindLive);
        if (result.Success)
        {
            ClearPauseResumeState();
            _livePauseStartedUtc = null;
            _liveBehind = targetBehindLive;
            UpdateLiveDelayUi();
            StatusText.Text = "Rewound to approximately " + FormatLiveDelay(targetBehindLive) + " behind live: " + _currentChannel.Name;
        }
        else
        {
            StatusText.Text = result.FailureReason ?? "The requested timeshift position is unavailable.";
        }
        UpdateLiveDelayUi();
    }

    private async void GoLive_Click(object sender, RoutedEventArgs e)
    {
        if (_currentChannel?.MediaKind != MediaKind.Live)
        {
            StatusText.Text = "Go Live is only available for live streams.";
            return;
        }

        if (_tuner is not null && _tuner.TryInspectLiveTimeshift(out _, out _))
        {
            var result = await _tuner.ReturnToLiveAsync();
            if (result.Success)
            {
                ClearPauseResumeState();
                ClearLiveDelay();
                StatusText.Text = "Live: " + _currentChannel.Name;
            }
            else
            {
                StatusText.Text = result.FailureReason ?? "Could not return to the live edge.";
            }
            UpdateLiveDelayUi();
            return;
        }

        ClearPauseResumeState();
        ClearLiveDelay();
        PlayChannel(_currentChannel);
        StatusText.Text = "Reconnecting at the live edge: " + _currentChannel.Name;
    }

    private void CopyStreamUrl_Click(object sender, RoutedEventArgs e)
    {
        var candidate = GetCurrentPlaybackCandidate();
        if (candidate is null)
        {
            StatusText.Text = "No playing stream URL to copy.";
            return;
        }
        Clipboard.SetText(candidate.Url);
        StatusText.Text = "Copied playing stream URL: " + AppLogger.SanitizeText(candidate.Label);
    }

    private void ShowStreamInfo_Click(object sender, RoutedEventArgs e)
    {
        UpdateStreamInfo();
        var selectedSource = GetCurrentPlaybackCandidate();
        var message = StreamInfoText.Text;
        if (_currentChannel is not null)
        {
            message += "\n\nPlaying channel: " + _currentChannel.Name;
            if (!string.IsNullOrWhiteSpace(_currentChannel.Group)) message += "\nGroup: " + _currentChannel.Group;
            message += "\nType: " + _currentChannel.MediaKind;
        }
        if (selectedSource is not null)
        {
            message += "\n\nPlaying source: " + selectedSource.Label + "\n" + AppLogger.SanitizeUrl(selectedSource.Url);
        }
        MessageBox.Show(this, AppLogger.SanitizeText(message), "Stream information", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackState.SelectedChannel is not { } channel)
        {
            StatusText.Text = "Select a channel first.";
            return;
        }

        var itemKey = ItemIdentity.For(channel);
        if (_state.FavoriteIds.Contains(itemKey, StringComparer.OrdinalIgnoreCase))
        {
            _state.FavoriteIds.RemoveAll(id => string.Equals(id, itemKey, StringComparison.OrdinalIgnoreCase));
            foreach (var folder in _state.FavoriteFolders)
                folder.ChannelIds.RemoveAll(id => string.Equals(id, itemKey, StringComparison.OrdinalIgnoreCase));
            StatusText.Text = "Removed from favorites: " + channel.Name;
        }
        else
        {
            _state.FavoriteIds.Add(itemKey);
            if (_viewMode == 1 && !string.IsNullOrEmpty(_activeFavoriteFolderId))
                _state.FavoriteFolders.FirstOrDefault(folder => folder.Id == _activeFavoriteFolderId)?.ChannelIds.Add(itemKey);
            StatusText.Text = "Added to favorites: " + channel.Name;
        }
        _store.Save(_state);
        UpdateFavoriteButton();
        if (_viewMode == 1) ApplyFilters();
    }

    private void NewFavoriteFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = CreateFavoriteFolder();
        if (folder is null) return;
        _browseMode = 0;
        _activeFavoriteFolderId = null;
        SearchBox.Clear();
        UpdateSearchScopeButtons();
        ApplyFilters();
    }

    private FavoriteFolder? CreateFavoriteFolder()
    {
        var name = PromptForFolderName("New favorite folder", "Folder name", string.Empty);
        if (name is null) return null;
        if (_state.FavoriteFolders.Any(folder => string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(name, "Unfiled", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "A folder with that name already exists.", "Favorite folders", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        var created = new FavoriteFolder { Name = name };
        _state.FavoriteFolders.Add(created);
        _store.Save(_state);
        return created;
    }

    private string? PromptForFolderName(string title, string label, string initial)
    {
        var dialog = new Window
        {
            Title = title, Owner = this, Width = 360, Height = 155,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 75, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new System.Windows.Controls.Button { Content = "Save", Width = 75, IsDefault = true };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5) });
        var input = new System.Windows.Controls.TextBox { Text = initial, MaxLength = 80 };
        field.Children.Add(input);
        panel.Children.Add(field);
        dialog.Content = panel;
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                input.Focus();
                return;
            }
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private void ChannelList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(ChannelList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is null || item.DataContext is not ChannelListEntry entry)
        {
            e.Handled = true;
            return;
        }
        ChannelList.SelectedItem = entry;
        var menu = ChannelList.ContextMenu!;
        menu.Items.Clear();
        if (entry.FavoriteFolderId is { Length: > 0 } favoriteFolderId)
        {
            var folder = _state.FavoriteFolders.FirstOrDefault(f => f.Id == favoriteFolderId);
            if (folder is not null)
            {
                var rename = new WpfMenuItem { Header = "Rename folder" };
                rename.Click += (_, _) =>
                {
                    var name = PromptForFolderName("Rename favorite folder", "Folder name", folder.Name);
                    if (name is null) return;
                    if (_state.FavoriteFolders.Any(other => other.Id != folder.Id && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(name, "Unfiled", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(this, "A folder with that name already exists.", "Favorite folders", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    folder.Name = name;
                    _store.Save(_state);
                    ApplyFilters();
                };
                menu.Items.Add(rename);
                var moveUp = new WpfMenuItem { Header = "Move folder up" };
                moveUp.Click += (_, _) => MoveFavoriteFolder(folder, -1);
                menu.Items.Add(moveUp);
                var moveDown = new WpfMenuItem { Header = "Move folder down" };
                moveDown.Click += (_, _) => MoveFavoriteFolder(folder, 1);
                menu.Items.Add(moveDown);
                var delete = new WpfMenuItem { Header = "Delete folder" };
                delete.Click += (_, _) =>
                {
                    if (MessageBox.Show(this, $"Delete '{folder.Name}'? Its channels will stay in Favorites under Unfiled.",
                            "Delete favorite folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    _state.FavoriteFolders.Remove(folder);
                    _store.Save(_state);
                    _activeFavoriteFolderId = null;
                    ApplyFilters();
                };
                menu.Items.Add(delete);
            }
        }
        else if (entry.IsFolder && _viewMode != 1 && _browseMode == 0)
        {
            var sourceGroup = _sourceChannels.Select(c => c.Group).Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(name => string.Equals(DisplayGroupName(name),
                    entry.FolderName, StringComparison.OrdinalIgnoreCase));
            if (sourceGroup is not null)
            {
                AddAction("Rename group...", () =>
                {
                    var name = PromptForFolderName("Rename group", "Group name", entry.FolderName);
                    if (name is null) return;
                    if (_sourceChannels.Select(c => c.Group).Distinct(StringComparer.OrdinalIgnoreCase).Any(g =>
                        !string.Equals(g, sourceGroup, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(DisplayGroupName(g), name, StringComparison.OrdinalIgnoreCase)))
                    {
                        MessageBox.Show(this, "A group with that name already exists.", "Groups", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    GetGroupRule(sourceGroup).Name = name;
                    SaveOrganization();
                });
                AddAction("Hide group", () => { GetGroupRule(sourceGroup).Hidden = true; SaveOrganization(); });
                AddAction("Move group up", () => MoveGroup(sourceGroup, -1));
                AddAction("Move group down", () => MoveGroup(sourceGroup, 1));
            }
        }
        else if (entry.Channel is { } channel)
        {
            var itemKey = ItemIdentity.For(channel);
            AddAction("Rename item...", () =>
            {
                var name = PromptForFolderName("Rename item", "Display name", channel.Name);
                if (name is null) return;
                GetChannelRule(itemKey).Name = name;
                SaveOrganization();
            });
            AddAction("Hide item", () => { GetChannelRule(itemKey).Hidden = true; SaveOrganization(); });
            AddAction("Move item up", () => MoveChannel(channel, -1));
            AddAction("Move item down", () => MoveChannel(channel, 1));
            if (_viewMode == 1)
            {
                AddAction("Move favorite up", () => MoveFavorite(itemKey, -1));
                AddAction("Move favorite down", () => MoveFavorite(itemKey, 1));
            }
            menu.Items.Add(new Separator());
            if (channel.MediaKind != MediaKind.Live)
            {
                var details = new WpfMenuItem { Header = "Details" };
                details.Click += (_, _) => ShowVodDetails(channel);
                menu.Items.Add(details);
            }
            if (channel.MediaKind != MediaKind.Live && !SeriesPlaceholder.TryGetSeriesId(channel, out _))
            {
                _state.SelectedLibrary.ViewingProgress ??= new(StringComparer.OrdinalIgnoreCase);
                _state.SelectedLibrary.ViewingProgress.TryGetValue(ItemIdentity.For(channel), out var progress);
                if (progress is { PositionMs: > 0, Watched: false })
                {
                    var resume = new WpfMenuItem { Header = "Resume at " + FormatTime(progress.PositionMs) };
                    resume.Click += (_, _) => PlayChannel(channel, null, progress.PositionMs);
                    menu.Items.Add(resume);
                }
                var startOver = new WpfMenuItem { Header = "Start Over" };
                startOver.Click += (_, _) =>
                {
                    SavePlaybackProgress(force: true);
                    ViewingHistory.StartOver(_state.SelectedLibrary, channel, DateTime.UtcNow);
                    _store.Save(_state);
                    _skipNextProgressSave = true;
                    PlayChannel(channel);
                };
                menu.Items.Add(startOver);
                if (_viewMode == 3)
                {
                    var dismiss = new WpfMenuItem { Header = "Dismiss from Continue" };
                    dismiss.Click += (_, _) =>
                    {
                        ViewingHistory.Dismiss(_state.SelectedLibrary, channel);
                        _store.Save(_state);
                        ApplyFilters();
                    };
                    menu.Items.Add(dismiss);
                }
                menu.Items.Add(new Separator());
            }
            var move = new WpfMenuItem { Header = "Add to favorite folder" };
            var unfiled = new WpfMenuItem { Header = "Unfiled" };
            unfiled.Click += (_, _) => PutInFavoriteFolder(channel, null);
            move.Items.Add(unfiled);
            foreach (var folder in _state.FavoriteFolders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var destination = folder;
                var option = new WpfMenuItem { Header = destination.Name };
                option.Click += (_, _) => PutInFavoriteFolder(channel, destination);
                move.Items.Add(option);
            }
            move.Items.Add(new Separator());
            var create = new WpfMenuItem { Header = "New folder..." };
            create.Click += (_, _) =>
            {
                var folder = CreateFavoriteFolder();
                if (folder is not null) PutInFavoriteFolder(channel, folder);
            };
            move.Items.Add(create);
            menu.Items.Add(move);
        }
        if (menu.Items.Count == 0)
        {
            e.Handled = true;
            return;
        }
        void AddAction(string label, Action action)
        {
            var option = new WpfMenuItem { Header = label };
            option.Click += (_, _) => action();
            menu.Items.Add(option);
        }
    }

    private ChannelOrganization GetChannelRule(string key)
    {
        var rules = _state.SelectedLibrary.ChannelOrganization;
        if (!rules.TryGetValue(key, out var rule)) rules[key] = rule = new ChannelOrganization();
        return rule;
    }

    private GroupOrganization GetGroupRule(string key)
    {
        var rules = _state.SelectedLibrary.GroupOrganization;
        if (!rules.TryGetValue(key, out var rule)) rules[key] = rule = new GroupOrganization();
        return rule;
    }

    private string DisplayGroupName(string sourceGroup)
    {
        var name = _state.SelectedLibrary.GroupOrganization.GetValueOrDefault(sourceGroup)?.Name;
        return string.IsNullOrWhiteSpace(name) ? sourceGroup : name;
    }

    private void SaveOrganization()
    {
        _store.Save(_state);
        RefreshOrganizedLibrary();
    }

    private void MoveChannel(Channel channel, int direction)
    {
        var peers = _channels.Where(c => string.Equals(c.Group, channel.Group, StringComparison.OrdinalIgnoreCase)).ToList();
        var index = peers.FindIndex(c => ItemIdentity.For(c) == ItemIdentity.For(channel));
        if (index < 0 || index + direction < 0 || index + direction >= peers.Count) return;
        (peers[index], peers[index + direction]) = (peers[index + direction], peers[index]);
        for (var i = 0; i < peers.Count; i++) GetChannelRule(ItemIdentity.For(peers[i])).Order = i + 1;
        SaveOrganization();
    }

    private void MoveGroup(string sourceGroup, int direction)
    {
        var groups = _visibleEntries.Where(e => e.IsFolder && e.FavoriteFolderId is null)
            .Select(e => _sourceChannels.Select(c => c.Group).Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => string.Equals(DisplayGroupName(g),
                    e.FolderName, StringComparison.OrdinalIgnoreCase)))
            .Where(g => g is not null).Select(g => g!).ToList();
        var index = groups.FindIndex(g => string.Equals(g, sourceGroup, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + direction < 0 || index + direction >= groups.Count) return;
        (groups[index], groups[index + direction]) = (groups[index + direction], groups[index]);
        for (var i = 0; i < groups.Count; i++) GetGroupRule(groups[i]).Order = i + 1;
        SaveOrganization();
    }

    private void MoveFavorite(string key, int direction)
    {
        LibraryOrganization.MoveFavorite(_state.FavoriteIds, key, direction);
        _store.Save(_state);
        ApplyFilters();
    }

    private void MoveFavoriteFolder(FavoriteFolder folder, int direction)
    {
        var folders = _state.FavoriteFolders;
        var index = folders.FindIndex(f => f.Id == folder.Id);
        if (index < 0 || index + direction < 0 || index + direction >= folders.Count) return;
        (folders[index], folders[index + direction]) = (folders[index + direction], folders[index]);
        _store.Save(_state);
        ApplyFilters();
    }

    private void PutInFavoriteFolder(Channel channel, FavoriteFolder? destination)
    {
        var itemKey = ItemIdentity.For(channel);
        if (!_state.FavoriteIds.Contains(itemKey, StringComparer.OrdinalIgnoreCase))
            _state.FavoriteIds.Add(itemKey);
        foreach (var folder in _state.FavoriteFolders)
            folder.ChannelIds.RemoveAll(id => string.Equals(id, itemKey, StringComparison.OrdinalIgnoreCase));
        destination?.ChannelIds.Add(itemKey);
        _store.Save(_state);
        StatusText.Text = destination is null ? $"Added to Unfiled: {channel.Name}" : $"Added to {destination.Name}: {channel.Name}";
        UpdateFavoriteButton();
        if (_viewMode == 1) ApplyFilters();
    }

    private void UpdateFavoriteButton()
    {
        var channel = _playbackState.SelectedChannel;
        var isFavorite = channel is not null && _state.FavoriteIds.Contains(ItemIdentity.For(channel), StringComparer.OrdinalIgnoreCase);
        FavoriteButton.Content = IconFactory.Create(isFavorite ? IconFactory.Star : IconFactory.StarHollow);
        FavoriteButton.IsEnabled = channel is not null;
        var action = isFavorite ? "Remove from favorites" : "Add to favorites";
        var label = channel is null ? "Select a channel to change favorites" : action + ": selected " + channel.Name;
        FavoriteButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(FavoriteButton, label);
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
        var generation = ++_remoteGeneration;
        try
        {
            if (_state.RemoteControlEnabled)
            {
                _remoteControlService.Start(_state.RemoteControlPort, command => HandleRemoteCommand(command, generation), GetRemoteControlState);
                if (_state.RemoteControlPort != _remoteControlService.Port)
                {
                    _state.RemoteControlPort = _remoteControlService.Port;
                    _store.Save(_state);
                }
                var urls = string.Join("\n", RemoteControlService.GetLocalUrls(_remoteControlService.Port));
                if (showStatus)
                    MessageBox.Show(this, "Open one of these addresses in your phone's browser while both devices are on the same trusted Wi-Fi network:\n\n" + urls +
                        "\n\nThis controls the desktop player; it does not mirror or show the PC screen on your phone.",
                        "Phone Remote", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "Phone remote enabled on the local network.";
            }
            else
            {
                _remoteControlService.Stop();
                if (showStatus) StatusText.Text = "Phone remote disabled.";
            }
        }
        catch (Exception ex)
        {
            _remoteControlService.Stop();
            _state.RemoteControlEnabled = false;
            _store.Save(_state);
            MessageBox.Show(this, "Phone remote could not start.\n\n" + AppLogger.SanitizeText(ex.Message) +
                "\n\nTry another port or allow the app through Windows Firewall.", "Phone Remote", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Phone remote failed to start.";
        }
    }

    private void HandleRemoteCommand(string command, int generation)
    {
        Dispatcher.Invoke(new Action(() =>
        {
            if (generation != _remoteGeneration || _isShuttingDown || !_remoteControlService.IsRunning || !_state.RemoteControlEnabled || Dispatcher.HasShutdownStarted) return;
            if (command.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
            {
                SearchBox.Text = command["search:".Length..];
                SearchBox.CaretIndex = SearchBox.Text.Length;
                return;
            }

            switch (command)
            {
                case "media-all": SetMediaKindMode(0); break;
                case "media-live": SetMediaKindMode(1); break;
                case "media-movies": SetMediaKindMode(2); break;
                case "media-series": SetMediaKindMode(3); break;
                case "view-all": SetViewMode(0); break;
                case "view-favorites": SetViewMode(1); break;
                case "view-recent": SetViewMode(2); break;
                case "browse-folders": SetBrowseMode(0); break;
                case "browse-letters": SetBrowseMode(1); break;
                case "browse-items": SetBrowseMode(2); break;
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
                case "select": ActivateSelectedListEntry(); break;
                case "back":
                    if (_isFullScreen) ToggleFullScreen();
                    else if (_activeSeriesId is not null || _activeFolder is not null || _activeLetter is not null)
                        FolderBack_Click(this, new RoutedEventArgs());
                    else if (!_isFullScreen && !_channelsVisible)
                        ToggleChannels_Click(this, new RoutedEventArgs());
                    break;
            }
        }));
    }

    private RemoteControlState GetRemoteControlState() => new(
        Volatile.Read(ref _browseMode), Volatile.Read(ref _mediaKindMode), Volatile.Read(ref _viewMode));

    private void MoveSelection(int delta)
    {
        if (_visibleEntries.Count == 0) return;
        var index = ChannelList.SelectedIndex;
        if (index < 0) index = delta > 0 ? -1 : 1;
        index = Math.Clamp(index + delta, 0, _visibleEntries.Count - 1);
        ChannelList.SelectedIndex = index;
        ChannelList.ScrollIntoView(ChannelList.SelectedItem);
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_channelsVisible) ToggleChannels_Click(this, new RoutedEventArgs());
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }
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
            case Key.Enter:
                ActivateSelectedListEntry();
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
                if (_isFullScreen) SetVolume(_state.VolumeLevel + 5);
                else MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                if (_isFullScreen) SetVolume(_state.VolumeLevel - 5);
                else MoveSelection(1);
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
        public string Logo { get; private init; } = string.Empty;
        public Viewbox Icon { get; private init; } = IconFactory.Create(IconFactory.Tv, 18);
        public bool IsFolder { get; private init; }
        public string FolderName { get; private init; } = string.Empty;
        public string? FavoriteFolderId { get; private init; }
        public Channel? Channel { get; private init; }

        public static ChannelListEntry ForFolder(string folderName, int itemCount, string? favoriteFolderId = null)
        {
            return new ChannelListEntry
            {
                Name = folderName,
                Group = $"{itemCount:N0} channels - double-click to open",
                Icon = IconFactory.Create(IconFactory.Folder, 18),
                IsFolder = true,
                FolderName = folderName,
                FavoriteFolderId = favoriteFolderId
            };
        }

        public static ChannelListEntry ForChannel(Channel channel, ViewingProgress? progress)
        {
            return new ChannelListEntry
            {
                Name = channel.Name,
                Group = channel.Group + (progress is { Watched: true } ? " · Watched" :
                    progress is { PositionMs: > 0 } ? " · Resume " + FormatTime(progress.PositionMs) : ""),
                Logo = NormalizeLogoUrl(channel.Logo),
                Icon = IconFactory.Create(channel.MediaKind == MediaKind.Live ? IconFactory.Tv : IconFactory.Cinema, 18),
                FolderName = NormalizeGroupName(channel.Group),
                Channel = channel
            };
        }

        private static string NormalizeLogoUrl(string logo)
        {
            if (!Uri.TryCreate(logo?.Trim(), UriKind.Absolute, out var uri)) return string.Empty;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
                ? uri.AbsoluteUri
                : string.Empty;
        }
    }

    private sealed record SubtitleOption(int Id, string Name);

    private sealed record PauseResumeSnapshot(Channel Channel, int CandidateIndex, long TimeMs);

    private void CleanupPlayer()
    {
        ClearCatchup();
        _isShuttingDown = true;
        _remoteControlService.Dispose();
        try
        {
            _libraryLoadCts?.Cancel();
            _seriesLoadCts?.Cancel();
            ++_libraryLoadGeneration;
            ++_seriesLoadGeneration;
            PrepareWindowForShutdown();
            try { _mediaPlayer?.Stop(); }
            catch { }
            _videoView.MediaPlayer = null;
            _sourceProbeCts?.Cancel();
            CancelEpgRefresh();
            _sourceProbeCts?.Dispose();
            _sourceProbeCts = null;
            _positionTimer.Stop();
            _controlsHideTimer.Stop();
            _volumeOsdTimer.Stop();
            // The tuner owns every player/media pair; disposing it stops the active
            // stream and waits for background teardown before LibVLC goes away.
            _tuner?.Dispose();
            _mediaPlayer = null;
            _currentMedia = null;
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
        try { SetCursorHidden(false); }
        catch { }

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
