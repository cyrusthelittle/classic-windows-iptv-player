using ClassicWindowsIptvPlayer.Core;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClassicWindowsIptvPlayer.Windows;

public enum TunerStatus
{
    Idle,
    Tuning,
    Playing,
    Ended,
    Failed
}

public sealed record TuneRequest(
    string ChannelId,
    string ChannelName,
    string Url,
    string SourceLabel,
    bool IsLive,
    int BufferMs,
    string AccountId = "");

public sealed record TunerStateSnapshot(
    TunerStatus Status,
    TuneRequest? Request,
    int Attempt,
    int MaxAttempts,
    string? Detail);

public sealed record TimeshiftPlaybackSwitchResult(bool Success, long? SelectedSequence,
    bool RecoverySucceeded, string? FailureReason);

/// <summary>
/// Owns stream startup, monitoring and recovery, modeled on how set-top boxes
/// and players like TiviMate/Kodi tune live TV: a single entry point where the
/// latest request always wins, every attempt runs on a disposable player, open
/// attempts are bounded by a watchdog instead of hanging, transient failures
/// retry silently behind a "Tuning" state, and a stream that dies mid-play is
/// re-tuned automatically. The host only ever renders the reported state.
/// </summary>
public sealed class ChannelTuner : IDisposable
{
    private const int DefaultMaxAttempts = 10;
    private const int MaxAttemptsCeiling = 30;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
    // LibVLC's native Stop/Dispose returns before the remote HTTP peer always sees
    // the socket close. This short quiet gap prevents overlapping provider leases.
    private static readonly TimeSpan ProviderConnectionReleaseGracePeriod = TimeSpan.FromMilliseconds(250);

    private readonly LibVLC _libVlc;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _recordingTransitionGate = new(1, 1);
    private readonly SemaphoreSlim _timeshiftTransitionGate = new(1, 1);
    private int _generation;
    private MediaPlayer? _activePlayer;
    private Media? _activeMedia;
    private SharedHlsSource? _activeSharedHlsSource;
    private TuneRequest? _activeRequest;
    private string? _recordingOutputPath;
    private CancellationTokenSource? _activeTuneCts;
    private Task? _activeTuneTask;
    private readonly List<Task> _tuneCycles = [];
    private Task _retireChain = Task.CompletedTask;
    private volatile bool _disposed;
    private volatile bool _userPaused;
    private MediaPlayer? _timeshiftSwitchingPlayer;
    private volatile int _maxAttempts = DefaultMaxAttempts;

    /// <summary>
    /// How many open attempts a tune request gets before it is reported as
    /// Failed. Adjustable at runtime (a running cycle keeps the budget it
    /// started with); out-of-range values are clamped.
    /// </summary>
    public int MaxAttempts
    {
        get => _maxAttempts;
        set => _maxAttempts = Math.Clamp(value, 1, MaxAttemptsCeiling);
    }

    /// <summary>
    /// Raised synchronously on the tuning thread just before Play, so the host
    /// can attach the player to its video surface and apply audio state.
    /// </summary>
    public event Action<MediaPlayer, Media, TuneRequest>? PlayerAttached;

    /// <summary>Raised synchronously after an active player's Media changes and before its prior Media is disposed.</summary>
    public event Action<MediaPlayer, Media, TuneRequest>? ActiveMediaChanged;

    /// <summary>
    /// Raised synchronously before a player that may still own the video surface
    /// is torn down. The host MUST drop every reference to it (including the
    /// video view binding) before this returns: the view touches the player's
    /// native handle when it detaches, which crashes on a disposed player.
    /// </summary>
    public event Action<MediaPlayer>? PlayerDetaching;

    /// <summary>Raised on arbitrary threads; the host marshals to its UI thread.</summary>
    public event Action<TunerStateSnapshot>? StateChanged;

    public ChannelTuner(LibVLC libVlc)
    {
        _libVlc = libVlc;
    }

    /// <summary>The player of the newest tune attempt, or null when idle.</summary>
    public MediaPlayer? CurrentPlayer
    {
        get
        {
            lock (_gate)
            {
                return _activePlayer;
            }
        }
    }

    /// <summary>Attach recording to the current live HLS fetch without opening another provider request.</summary>
    public bool TryAttachSharedHlsRecording(out SharedHlsConsumer? consumer)
    {
        lock (_gate)
        {
            var source = _activeSharedHlsSource;
            if (_activeRequest is { IsLive: true } && _activePlayer is { IsPlaying: true } && source is { TerminalFailure: null })
            {
                consumer = source.Attach(SharedHlsConsumerKind.Recording);
                return true;
            }
        }
        consumer = null;
        return false;
    }

    /// <summary>
    /// Atomically attaches to the currently playing live HLS source only when
    /// the active account and stable channel identity match. Never tunes or
    /// otherwise changes playback.
    /// </summary>
    public bool TryAttachSharedHlsRecording(string expectedAccountId, string expectedChannelId,
        out SharedHlsConsumer? consumer)
    {
        lock (_gate)
        {
            var request = _activeRequest;
            var source = _activeSharedHlsSource;
            if (request is { IsLive: true } && _activePlayer is { IsPlaying: true } &&
                source is { TerminalFailure: null } &&
                !string.IsNullOrWhiteSpace(expectedAccountId) &&
                string.Equals(request.AccountId, expectedAccountId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(expectedChannelId) &&
                string.Equals(request.ChannelId, expectedChannelId, StringComparison.OrdinalIgnoreCase))
            {
                consumer = source.Attach(SharedHlsConsumerKind.Recording);
                return true;
            }
        }
        consumer = null;
        return false;
    }

    /// <summary>Inspect the rolling disk window for the active, supported shared-HLS input.</summary>
    public bool TryInspectLiveTimeshift(out LiveTimeshiftWindow? window, out string? unsupportedReason)
    {
        lock (_gate)
        {
            var source = _activeSharedHlsSource;
            if (_activeRequest is not { IsLive: true } || source is null)
            {
                window = null;
                unsupportedReason = "Live timeshift is available only for supported shared-HLS channels.";
                return false;
            }
            try
            {
                window = source.InspectTimeshiftWindow();
                unsupportedReason = null;
                return true;
            }
            catch (NotSupportedException exception)
            {
                window = null;
                unsupportedReason = exception.Message;
                return false;
            }
        }
    }

    /// <summary>Switch the existing player to a distinct buffered HLS input at a duration before live.</summary>
    public async Task<TimeshiftPlaybackSwitchResult> SeekLiveTimeshiftAsync(TimeSpan behindLive, CancellationToken cancellationToken = default)
    {
        if (behindLive < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(behindLive));
        CancellationToken tuneToken;
        lock (_gate) tuneToken = _activeTuneCts?.Token ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tuneToken);
        await _timeshiftTransitionGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            SharedHlsSource? source;
            MediaPlayer? player;
            lock (_gate)
            {
                source = _activeSharedHlsSource;
                player = _activePlayer;
                if (_activeRequest is not { IsLive: true } || source is null || player is null || player.State != VLCState.Playing)
                    return new(false, null, false, "The active live shared-HLS player is not playing.");
            }
            var status = source.TryCreateTimeshiftPlaybackUrlBehindLive(behindLive, out var url, out var sequence);
            if (status != LiveTimeshiftSeekResult.Positioned || url is null)
                return new(false, null, false, "The requested point is outside the available timeshift window (" + status + ").");
            return await SwitchActivePlayerInputAsync(player, source, url, sequence, linked.Token).ConfigureAwait(false);
        }
        finally { _timeshiftTransitionGate.Release(); }
    }

    /// <summary>Switch the existing player to a fresh local input at the current shared-HLS live edge.</summary>
    public async Task<TimeshiftPlaybackSwitchResult> ReturnToLiveAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken tuneToken;
        lock (_gate) tuneToken = _activeTuneCts?.Token ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tuneToken);
        await _timeshiftTransitionGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            SharedHlsSource? source;
            MediaPlayer? player;
            lock (_gate)
            {
                source = _activeSharedHlsSource;
                player = _activePlayer;
                if (_activeRequest is not { IsLive: true } || source is null || player is null)
                    return new(false, null, false, "The active channel has no shared-HLS playback source.");
            }
            var status = source.TryCreateTimeshiftPlaybackUrlBehindLive(TimeSpan.Zero, out var url, out var sequence);
            if (status != LiveTimeshiftSeekResult.Positioned || url is null)
                return new(false, null, false, "The shared-HLS live edge is not available (" + status + ").");
            return await SwitchActivePlayerInputAsync(player, source, url, sequence, linked.Token).ConfigureAwait(false);
        }
        finally { _timeshiftTransitionGate.Release(); }
    }

    private async Task<TimeshiftPlaybackSwitchResult> SwitchActivePlayerInputAsync(MediaPlayer player,
        SharedHlsSource source, Uri url, long sequence, CancellationToken cancellationToken)
    {
        Media? previousMedia;
        lock (_gate)
        {
            if (!ReferenceEquals(_activePlayer, player) || !ReferenceEquals(_activeSharedHlsSource, source) || _activeMedia is null)
                return new(false, sequence, false, "The active channel changed before the timeshift switch began.");
            previousMedia = _activeMedia;
            _timeshiftSwitchingPlayer = player;
        }

        Media? replacement = null;
        try
        {
            replacement = CreateTimeshiftMedia(url);
            StopPlayer(player);
            var started = await StartExistingPlayerInputAsync(player, replacement, cancellationToken).ConfigureAwait(false);
            if (started)
            {
                TuneRequest request;
                lock (_gate)
                {
                    if (!ReferenceEquals(_activePlayer, player) || !ReferenceEquals(_activeSharedHlsSource, source))
                        return new(false, sequence, false, "The active channel changed while LibVLC was opening the buffered input.");
                    _activeMedia = replacement;
                    request = _activeRequest!;
                }
                RaiseActiveMediaChanged(player, replacement, request);
                replacement = null;
                Retire(null, previousMedia);
                return new(true, sequence, true, null);
            }

            StopPlayer(player);
            var restored = await StartExistingPlayerInputAsync(player, previousMedia, CancellationToken.None).ConfigureAwait(false);
            return new(false, sequence, restored, restored
                ? "LibVLC rejected the buffered input; the previous playback input was restored."
                : "LibVLC rejected the buffered input and could not restore the previous playback input.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopPlayer(player);
            bool tuneIsEnding;
            lock (_gate) tuneIsEnding = _activeTuneCts is null || _activeTuneCts.IsCancellationRequested;
            var restored = !tuneIsEnding && previousMedia is not null &&
                await StartExistingPlayerInputAsync(player, previousMedia, CancellationToken.None).ConfigureAwait(false);
            return new(false, sequence, restored, tuneIsEnding
                ? "The timeshift switch was cancelled because playback was stopped or retuned."
                : restored ? "The timeshift switch was cancelled; previous playback was restored." : "The timeshift switch was cancelled and previous playback recovery failed.");
        }
        catch (Exception exception)
        {
            try
            {
                StopPlayer(player);
                if (previousMedia is not null)
                    await StartExistingPlayerInputAsync(player, previousMedia, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            var recovered = player.State == VLCState.Playing && ReferenceEquals(_activeMedia, previousMedia);
            return new(false, sequence, recovered, AppLogger.SanitizeText(exception.Message) + (recovered ? " Previous playback was restored." : " Previous playback recovery failed."));
        }
        finally
        {
            replacement?.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_timeshiftSwitchingPlayer, player)) _timeshiftSwitchingPlayer = null;
            }
        }
    }

    private Media CreateTimeshiftMedia(Uri url)
    {
        var media = new Media(_libVlc, url.AbsoluteUri, FromType.FromLocation);
        media.AddOption(":network-caching=200");
        media.AddOption(":live-caching=200");
        media.AddOption(":http-reconnect");
        return media;
    }

    private static async Task<bool> StartExistingPlayerInputAsync(MediaPlayer player, Media media, CancellationToken cancellationToken)
    {
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPlaying(object? sender, EventArgs args) => outcome.TrySetResult(true);
        void OnFailure(object? sender, EventArgs args) => outcome.TrySetResult(false);
        player.Playing += OnPlaying;
        player.EncounteredError += OnFailure;
        player.Stopped += OnFailure;
        player.EndReached += OnFailure;
        using var registration = cancellationToken.Register(() => outcome.TrySetCanceled(cancellationToken));
        try
        {
            if (!player.Play(media)) return false;
            var winner = await Task.WhenAny(outcome.Task, Task.Delay(TimeSpan.FromSeconds(12), cancellationToken)).ConfigureAwait(false);
            if (winner != outcome.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
            return await outcome.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            AppLogger.Warn("Tuner: local timeshift input failed to start. " + AppLogger.SanitizeText(exception.Message));
            return false;
        }
        finally
        {
            player.Playing -= OnPlaying;
            player.EncounteredError -= OnFailure;
            player.Stopped -= OnFailure;
            player.EndReached -= OnFailure;
        }
    }

    /// <summary>Positions a local disk-buffer cursor only; it does not retune or alter LibVLC playback.</summary>
    public LiveTimeshiftSeekResult TrySeekLiveTimeshift(long sequence, out LiveTimeshiftCursor? cursor)
    {
        lock (_gate)
        {
            if (_activeRequest is not { IsLive: true } || _activeSharedHlsSource is null)
            {
                cursor = null;
                return LiveTimeshiftSeekResult.Unsupported;
            }
            return _activeSharedHlsSource.TrySeekTimeshift(sequence, out cursor);
        }
    }

    /// <summary>Switch LibVLC's existing loopback HLS playlist to a cached segment. No provider request is made.</summary>
    public LiveTimeshiftSeekResult TryPauseLiveAtSequence(long sequence)
    {
        lock (_gate)
        {
            if (_activeRequest is not { IsLive: true } || _activeSharedHlsSource is null)
                return LiveTimeshiftSeekResult.Unsupported;
            return _activeSharedHlsSource.TrySetPlaybackSequence(sequence);
        }
    }

    /// <summary>Resume the active local timeshift cursor without retuning or provider I/O.</summary>
    public void ResumeLiveTimeshiftCursor()
    {
        lock (_gate) _activeSharedHlsSource?.ResumePlaybackCursor();
    }

    public LiveTimeshiftSeekResult ResumeLiveTimeshiftCursorWithResult()
    {
        lock (_gate)
        {
            if (_activeRequest is not { IsLive: true } || _activeSharedHlsSource is null)
                return LiveTimeshiftSeekResult.Unsupported;
            return _activeSharedHlsSource.ResumePlaybackCursor();
        }
    }

    /// <summary>Switch the existing loopback playlist to its latest fetched edge. No retune or provider request is made.</summary>
    public LiveTimeshiftSeekResult TryGoLiveOnCurrentSource()
    {
        lock (_gate)
        {
            if (_activeRequest is not { IsLive: true } || _activeSharedHlsSource is null)
                return LiveTimeshiftSeekResult.Unsupported;
            return _activeSharedHlsSource.GoLiveOnPlayback();
        }
    }

    public LiveTimeshiftSeekResult CancelLiveTimeshiftPosition()
    {
        lock (_gate)
        {
            if (_activeRequest is not { IsLive: true } || _activeSharedHlsSource is null)
                return LiveTimeshiftSeekResult.Unsupported;
            return _activeSharedHlsSource.CancelPlaybackCursor();
        }
    }

    public bool CanRecordActiveStream
    {
        get { lock (_gate) return CanRecordActiveStreamLocked(); }
    }

    /// <summary>Choose a container suited to the already-open media without probing/reopening its URL.</summary>
    public RecordingContainerKind GetActiveRecordingContainer()
    {
        Media? media;
        lock (_gate)
        {
            if (!CanRecordActiveStreamLocked()) return RecordingContainerKind.Ts;
            media = _activeMedia;
        }

        var video = new List<string>();
        var audio = new List<string>();
        try
        {
            foreach (var track in media?.Tracks ?? [])
            {
                var codec = new string(BitConverter.GetBytes(track.Codec).Where(value => value != 0)
                    .Select(value => value is >= 0x20 and < 0x7F ? (char)value : '?').ToArray()).Trim().ToUpperInvariant();
                if (codec.Length == 0) continue;
                if (track.TrackType == TrackType.Video) video.Add(codec);
                else if (track.TrackType == TrackType.Audio) audio.Add(codec);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warn("Tuner: active tracks were unavailable for recording-container selection. " + AppLogger.SanitizeText(exception.Message));
        }
        // Matroska is a safer fallback if LibVLC has not exposed tracks yet; it
        // carries a wider range of passthrough codecs than MPEG-TS.
        return video.Count + audio.Count == 0
            ? RecordingContainerKind.Mkv
            : RecordingService.ChooseContainer(video, audio);
    }

    private bool CanRecordActiveStreamLocked() =>
        _activeRequest is { IsLive: true } && _activePlayer is { IsPlaying: true } &&
        (_recordingOutputPath is not null || _activeSharedHlsSource is null || _activeSharedHlsSource.TerminalFailure is null);

    /// <summary>
    /// Retunes the current live channel once with a LibVLC display+file sout chain.
    /// The old player is fully retired before the new input is opened, so an account
    /// with a one-connection limit still sees at most one provider stream.
    /// </summary>
    public async Task<bool> StartRecordingOutputAsync(string destinationPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) return false;
        await _recordingTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TuneRequest? request = null;
        try
        {
            lock (_gate)
            {
                request = _activeRequest;
                if (request is not { IsLive: true } || _activePlayer is not { IsPlaying: true } || _recordingOutputPath is not null)
                    return false;
            }

            await StopAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(ProviderConnectionReleaseGracePeriod, cancellationToken).ConfigureAwait(false);
            lock (_gate) _recordingOutputPath = Path.GetFullPath(destinationPath);
            if (await RetuneAndWaitForPlayingAsync(request, cancellationToken).ConfigureAwait(false)) return true;

            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate) _recordingOutputPath = null;
            await RestoreOrdinaryPlaybackAsync(request, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate) _recordingOutputPath = null;
            if (request is { IsLive: true } && !_disposed)
                await RestoreOrdinaryPlaybackAsync(request, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _recordingTransitionGate.Release();
        }
    }

    /// <summary>Closes the recording output by retiring it, then restores ordinary playback.</summary>
    public async Task<bool> StopRecordingOutputAsync(CancellationToken cancellationToken)
    {
        await _recordingTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TuneRequest? request;
            lock (_gate)
            {
                if (_recordingOutputPath is null) return true;
                request = _activeRequest;
            }

            await StopAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) _recordingOutputPath = null;
            if (request is not { IsLive: true }) return false;
            return await RestoreOrdinaryPlaybackAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _recordingTransitionGate.Release();
        }
    }

    private async Task<bool> RestoreOrdinaryPlaybackAsync(TuneRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(ProviderConnectionReleaseGracePeriod, cancellationToken).ConfigureAwait(false);
        return await RetuneAndWaitForPlayingAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RetuneAndWaitForPlayingAsync(TuneRequest request, CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(TunerStateSnapshot snapshot)
        {
            if (snapshot.Request != request) return;
            if (snapshot.Status == TunerStatus.Playing) started.TrySetResult(true);
            else if (snapshot.Status is TunerStatus.Failed or TunerStatus.Ended) started.TrySetResult(false);
        }

        StateChanged += OnState;
        try
        {
            Play(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try { return await started.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                AppLogger.Warn("Tuner: timed out waiting for recording retune to play.");
                return false;
            }
        }
        finally
        {
            StateChanged -= OnState;
        }
    }

    public void Play(TuneRequest request)
    {
        if (_disposed) return;
        var generation = Interlocked.Increment(ref _generation);
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previousCts;
        Task? previousCycle;
        lock (_gate)
        {
            previousCts = _activeTuneCts;
            previousCycle = _activeTuneTask;
            _activeTuneCts = cts;
            _tuneCycles.RemoveAll(task => task.IsCompleted);
        }
        try { previousCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        AppLogger.Info("Tuner: play requested. generation=" + generation + "; channel=" + request.ChannelName + "; url=" + AppLogger.SanitizeUrl(request.Url));
        var cycle = Task.Run(async () =>
        {
            try
            {
                if (previousCycle is not null)
                {
                    try { await previousCycle.ConfigureAwait(false); }
                    catch { }
                }
                if (cts.IsCancellationRequested || !IsCurrent(generation)) return;
                await RunTuneCycleAsync(generation, request, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Tuner: tune cycle crashed. generation=" + generation, ex);
                if (IsCurrent(generation))
                    RaiseState(new TunerStateSnapshot(TunerStatus.Failed, request, 0, MaxAttempts, AppLogger.SanitizeText(ex.Message)));
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeTuneCts, cts))
                    {
                        _activeTuneCts = null;
                        _activeTuneTask = null;
                    }
                }
                cts.Dispose();
            }
        });
        lock (_gate)
        {
            _tuneCycles.Add(cycle);
            if (ReferenceEquals(_activeTuneCts, cts)) _activeTuneTask = cycle;
        }
    }

    public void Stop()
    {
        if (_disposed) return;
        var generation = Interlocked.Increment(ref _generation);
        _userPaused = false;
        AppLogger.Info("Tuner: stop requested. generation=" + generation);
        CancelAndDetachActivePlayer();
        RaiseState(new TunerStateSnapshot(TunerStatus.Idle, null, 0, MaxAttempts, null));
    }

    // Callers that must release a provider connection before another HTTP
    // request await retirement without blocking the WPF dispatcher.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        Task? cycle;
        lock (_gate) cycle = _activeTuneTask;
        if (cycle is not null) await cycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task retirement;
        lock (_gate) retirement = _retireChain;
        await retirement.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// While the user has playback paused, a dying connection (providers drop
    /// idle streams) must not trigger a re-tune that yanks them out of pause;
    /// the host re-tunes on resume if the player can't continue.
    /// </summary>
    public void NotifyUserPaused() => _userPaused = true;

    public void NotifyUserResumed() => _userPaused = false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _generation);
        CancelAndDetachActivePlayer();
        try
        {
            Task[] cycles;
            lock (_gate) cycles = [.. _tuneCycles];
            Task.WaitAll(cycles, TimeSpan.FromSeconds(3));
            // Give background teardown a moment to finish; disposing LibVLC while
            // a player is still alive can crash the native engine.
            _retireChain.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Teardown failures are logged inside the chain; shutdown continues.
        }
    }

    private bool IsCurrent(int generation)
    {
        return !_disposed && generation == Volatile.Read(ref _generation);
    }

    private async Task RunTuneCycleAsync(int generation, TuneRequest request, CancellationToken cancellationToken)
    {
        var maxAttempts = MaxAttempts;
        var retryBudget = new TunerRetryBudget(maxAttempts);
        var vodPosition = new VodRecoveryPosition();
        while (retryBudget.CanAttempt)
        {
            var attempt = retryBudget.BeginAttempt();
            if (!IsCurrent(generation) || cancellationToken.IsCancellationRequested) return;

            RaiseState(new TunerStateSnapshot(TunerStatus.Tuning, request, attempt, maxAttempts, null));
            AppLogger.Info("Tuner: attempt " + attempt + "/" + maxAttempts + ". generation=" + generation + "; channel=" + request.ChannelName);

            MediaPlayer player;
            Media media;
            SharedHlsSource? sharedHlsSource = null;
            string? recordingOutputPath;
            lock (_gate) recordingOutputPath = _recordingOutputPath;
            try
            {
                var mediaUrl = request.Url;
                if (recordingOutputPath is null && request.IsLive && IsHlsPlaylistUrl(request.Url))
                {
                    try
                    {
                        sharedHlsSource = new SharedHlsSource(new Uri(request.Url, UriKind.Absolute),
                            maximumSegmentBytes: 16 * 1024 * 1024,
                            maximumBufferedBytesPerConsumer: 64 * 1024 * 1024,
                            maximumPlaybackCacheBytes: 32 * 1024 * 1024);
                        sharedHlsSource.SourceError += (_, exception) => AppLogger.Warn(
                            "Shared HLS source failed: " + AppLogger.SanitizeText(exception.Message));
                        mediaUrl = (await sharedHlsSource.StartPlaybackAsync(cancellationToken).ConfigureAwait(false)).AbsoluteUri;
                        await sharedHlsSource.WaitUntilPlaybackReadyAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
                        AppLogger.Info("Tuner: live HLS playback is using the shared upstream source.");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (sharedHlsSource is not null) await sharedHlsSource.DisposeAsync().ConfigureAwait(false);
                        return;
                    }
                    catch (Exception exception)
                    {
                        if (sharedHlsSource is not null)
                        {
                            try { await sharedHlsSource.DisposeAsync().ConfigureAwait(false); } catch { }
                        }
                        sharedHlsSource = null;
                        mediaUrl = request.Url;
                        AppLogger.Warn("Tuner: shared HLS playback is unavailable; opening the original URL for viewing, with recording disabled. " +
                            AppLogger.SanitizeText(exception.Message));
                    }
                }

                media = new Media(_libVlc, mediaUrl, FromType.FromLocation);
                media.AddOption(":network-caching=" + request.BufferMs);
                media.AddOption(":live-caching=" + request.BufferMs);
                media.AddOption(":file-caching=" + request.BufferMs);
                media.AddOption(":http-reconnect");
                if (recordingOutputPath is not null)
                {
                    var muxer = Path.GetExtension(recordingOutputPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? "mkv" : "ts";
                    media.AddOption(":sout=#duplicate{dst=display,dst=std{access=file,mux=" + muxer + ",dst='" +
                        EscapeSoutPath(recordingOutputPath) + "'}}");
                    media.AddOption(":sout-all");
                }
                player = new MediaPlayer(_libVlc)
                {
                    // LibVLC's own video output consumes mouse/keyboard input before
                    // the host window sees it; the host does its own input handling.
                    EnableMouseInput = false,
                    EnableKeyInput = false
                };
            }
            catch (Exception ex)
            {
                if (sharedHlsSource is not null)
                {
                    try { await sharedHlsSource.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                AppLogger.Error("Tuner: failed to create player/media. generation=" + generation, ex);
                RaiseState(new TunerStateSnapshot(TunerStatus.Failed, request, attempt, maxAttempts, AppLogger.SanitizeText(ex.Message)));
                return;
            }

            MediaPlayer? oldPlayer = null;
            Media? oldMedia = null;
            var installed = false;
            lock (_gate)
            {
                if (IsCurrent(generation))
                {
                    oldPlayer = _activePlayer;
                    oldMedia = _activeMedia;
                    _activePlayer = player;
                    _activeMedia = media;
                    _activeSharedHlsSource = sharedHlsSource;
                    _activeRequest = request;
                    installed = true;
                }
            }
            if (!installed)
            {
                player.Dispose();
                media.Dispose();
                if (sharedHlsSource is not null) await sharedHlsSource.DisposeAsync().ConfigureAwait(false);
                return;
            }

            // Surface handoff protocol: release the old player's binding while it
            // is alive, then bind the new player. Its owning cycle will tear the
            // old player down only after native callback cleanup has completed.
            if (oldPlayer is not null)
            {
                StopPlayer(oldPlayer);
                RaiseDetaching(oldPlayer);
                Retire(oldPlayer, oldMedia);
            }

            try
            {
                PlayerAttached?.Invoke(player, media, request);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Tuner: PlayerAttached handler failed.", ex);
                await ReleaseAttemptAsync(player, media, sharedHlsSource).ConfigureAwait(false);
                RaiseState(new TunerStateSnapshot(TunerStatus.Failed, request, attempt, maxAttempts, "The embedded video surface is unavailable."));
                return;
            }

            // The previous cycle owns and retires its own player after its native
            // callbacks have been detached. Cancelling it is safe; disposing it
            // here would race OpenAsync/MonitorAsync event cleanup.
            var opened = await OpenAsync(player, media, cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
            {
                await ReleaseAttemptAsync(player, media, sharedHlsSource).ConfigureAwait(false);
                return;
            }

            TimeSpan? playedFor = null;
            if (opened)
            {
                RaiseState(new TunerStateSnapshot(TunerStatus.Playing, request, attempt, maxAttempts, null));
                AppLogger.Info("Tuner: playing. generation=" + generation + "; attempt=" + attempt + "; channel=" + request.ChannelName);

                var playingSince = DateTime.UtcNow;
                var end = await MonitorAsync(player, cancellationToken,
                    request.IsLive ? null : vodPosition).ConfigureAwait(false);
                if (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
                {
                    await ReleaseAttemptAsync(player, media, sharedHlsSource).ConfigureAwait(false);
                    return;
                }

                if (PlaybackCompletionPolicy.IsNormalVodCompletion(request.IsLive, end == StreamEnd.EndedNormally))
                {
                    AppLogger.Info("Tuner: media finished. generation=" + generation + "; channel=" + request.ChannelName);
                    RaiseState(new TunerStateSnapshot(TunerStatus.Ended, request, attempt, maxAttempts, null));
                    await ReleaseAttemptAsync(player, media, sharedHlsSource).ConfigureAwait(false);
                    return;
                }

                if (end == StreamEnd.StoppedExternally)
                {
                    await ReleaseAttemptAsync(player, media, sharedHlsSource).ConfigureAwait(false);
                    return;
                }

                if (_userPaused)
                {
                    AppLogger.Info("Tuner: stream failed while paused; leaving recovery to the resume path. generation=" + generation);
                    return;
                }

                var duration = DateTime.UtcNow - playingSince;
                playedFor = duration;
                AppLogger.Warn("Tuner: stream dropped after " + duration.TotalSeconds.ToString("F0") + "s. generation=" + generation + "; channel=" + request.ChannelName);
            }
            else
            {
                AppLogger.Warn("Tuner: attempt " + attempt + " did not reach Playing. generation=" + generation + "; channel=" + request.ChannelName);
            }

            await ReleaseAttemptAsync(player, media, sharedHlsSource).ConfigureAwait(false);

            if (retryBudget.NextDelayMs(playedFor) is int delay)
            {
                if (!await TunerRetryBudget.WaitForRetryAsync(delay, cancellationToken, () => IsCurrent(generation)).ConfigureAwait(false)) return;
            }
        }

        if (!IsCurrent(generation)) return;
        AppLogger.Warn("Tuner: all attempts failed. generation=" + generation + "; channel=" + request.ChannelName);
        RaiseState(new TunerStateSnapshot(TunerStatus.Failed, request, maxAttempts, maxAttempts, "The stream did not start."));
    }

    /// <summary>
    /// Starts playback and waits until the stream is confirmed playing, fails,
    /// or the open watchdog expires. Never throws.
    /// </summary>
    private static async Task<bool> OpenAsync(MediaPlayer player, Media media, CancellationToken cancellationToken)
    {
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPlaying(object? s, EventArgs e) => outcome.TrySetResult(true);
        void OnFailure(object? s, EventArgs e) => outcome.TrySetResult(false);

        player.Playing += OnPlaying;
        player.EncounteredError += OnFailure;
        player.Stopped += OnFailure;
        player.EndReached += OnFailure;
        using var cancellationRegistration = cancellationToken.Register(() => outcome.TrySetResult(false));
        try
        {
            if (!player.Play(media))
            {
                AppLogger.Warn("Tuner: Play() refused to start.");
                return false;
            }

            var winner = await Task.WhenAny(outcome.Task, Task.Delay(OpenTimeout)).ConfigureAwait(false);
            if (winner != outcome.Task)
            {
                AppLogger.Warn("Tuner: open watchdog expired after " + OpenTimeout.TotalSeconds + "s.");
                return false;
            }

            return await outcome.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Tuner: open failed.", ex);
            return false;
        }
        finally
        {
            player.Playing -= OnPlaying;
            player.EncounteredError -= OnFailure;
            player.Stopped -= OnFailure;
            player.EndReached -= OnFailure;
        }
    }

    private enum StreamEnd
    {
        Error,
        EndedNormally,
        StoppedExternally
    }

    /// <summary>
    /// Waits until the playing stream errors out, ends, or is stopped. Retiring
    /// a player always calls Stop first, so this completes for superseded
    /// players too and never leaks.
    /// </summary>
    private async Task<StreamEnd> MonitorAsync(MediaPlayer player, CancellationToken cancellationToken,
        VodRecoveryPosition? vodPosition)
    {
        var outcome = new TaskCompletionSource<StreamEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnError(object? s, EventArgs e) => outcome.TrySetResult(StreamEnd.Error);
        void OnEnd(object? s, EventArgs e) => outcome.TrySetResult(StreamEnd.EndedNormally);
        void OnStopped(object? s, EventArgs e)
        {
            if (!IsTimeshiftSwitchInProgress(player)) outcome.TrySetResult(StreamEnd.StoppedExternally);
        }

        player.EncounteredError += OnError;
        player.EndReached += OnEnd;
        player.Stopped += OnStopped;
        using var cancellationRegistration = cancellationToken.Register(() => outcome.TrySetResult(StreamEnd.StoppedExternally));
        try
        {
            var recoveryTarget = vodPosition?.RecoveryTargetMs;
            var seekDeadline = DateTime.UtcNow.AddSeconds(4);
            var awaitingSeekConfirmationMs = 0L;
            while (!outcome.Task.IsCompleted)
            {
                if (player.State == VLCState.Error) outcome.TrySetResult(StreamEnd.Error);
                else if (player.State == VLCState.Stopped && !IsTimeshiftSwitchInProgress(player))
                    outcome.TrySetResult(StreamEnd.StoppedExternally);
                if (outcome.Task.IsCompleted) break;
                if (recoveryTarget is > 0 && DateTime.UtcNow < seekDeadline)
                {
                    try
                    {
                        if (player.IsSeekable && player.Length > recoveryTarget)
                        {
                            player.Time = recoveryTarget.Value;
                            awaitingSeekConfirmationMs = recoveryTarget.Value;
                            recoveryTarget = null;
                        }
                    }
                    catch { /* Metadata may not be ready yet. */ }
                }

                try
                {
                    if (vodPosition is not null && recoveryTarget is null)
                    {
                        var time = player.Time;
                        if (awaitingSeekConfirmationMs == 0 || time >= awaitingSeekConfirmationMs - 1500)
                        {
                            awaitingSeekConfirmationMs = 0;
                            vodPosition.Observe(time, player.Length);
                        }
                    }
                }
                catch { /* Native teardown can race a position sample. */ }

                await Task.WhenAny(outcome.Task, Task.Delay(250, cancellationToken)).ConfigureAwait(false);
            }
            return await outcome.Task.ConfigureAwait(false);
        }
        finally
        {
            player.EncounteredError -= OnError;
            player.EndReached -= OnEnd;
            player.Stopped -= OnStopped;
        }
    }

    private bool IsTimeshiftSwitchInProgress(MediaPlayer player)
    {
        lock (_gate) return ReferenceEquals(_timeshiftSwitchingPlayer, player);
    }

    private void CancelAndDetachActivePlayer()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _activeTuneCts;
        }

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task ReleaseAttemptAsync(MediaPlayer player, Media media, SharedHlsSource? sharedHlsSource)
    {
        await _timeshiftTransitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
        // Callback subscriptions have already been removed when this is called.
        // Stop while the drawable is still attached; clearing a live player's
        // HWND lets LibVLC fall back to a top-level Direct3D11 output window.
        StopPlayer(player);

        var wasActive = false;
        Media ownedMedia = media;
        SharedHlsSource? ownedSource = sharedHlsSource;
        lock (_gate)
        {
            if (ReferenceEquals(_activePlayer, player))
            {
                ownedMedia = _activeMedia ?? media;
                ownedSource = _activeSharedHlsSource ?? sharedHlsSource;
                _activePlayer = null;
                _activeMedia = null;
                _activeSharedHlsSource = null;
                _activeRequest = null;
                wasActive = true;
            }
        }

        if (wasActive) RaiseDetaching(player);
        Retire(player, ownedMedia);
        if (ownedSource is not null)
        {
            try { await ownedSource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { AppLogger.Warn("Tuner: shared HLS source cleanup failed. " + AppLogger.SanitizeText(exception.Message)); }
        }
        }
        finally { _timeshiftTransitionGate.Release(); }
    }

    private void RaiseActiveMediaChanged(MediaPlayer player, Media media, TuneRequest request)
    {
        try { ActiveMediaChanged?.Invoke(player, media, request); }
        catch (Exception exception) { AppLogger.Error("Tuner: ActiveMediaChanged handler failed.", exception); }
    }

    private static bool IsHlsPlaylistUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static string EscapeSoutPath(string path) =>
        path.Replace('\\', '/').Replace("'", "\\'", StringComparison.Ordinal);

    private static void StopPlayer(MediaPlayer player)
    {
        try { player.Stop(); }
        catch (Exception ex) { AppLogger.Warn("Tuner: player Stop failed. " + AppLogger.SanitizeText(ex.Message)); }
    }

    private void RaiseDetaching(MediaPlayer player)
    {
        try
        {
            PlayerDetaching?.Invoke(player);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Tuner: PlayerDetaching handler failed.", ex);
        }
    }

    private void Retire(MediaPlayer? player, Media? media)
    {
        if (player is null && media is null) return;

        lock (_gate)
        {
            var previous = _retireChain;
            _retireChain = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                }
                catch
                {
                    // A failed earlier retirement must not leak this player too.
                }

                try
                {
                    player?.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Tuner: retired player Dispose failed. " + AppLogger.SanitizeText(ex.Message));
                }

                try
                {
                    media?.Dispose();
                }
                catch
                {
                    // Ignore native cleanup races while switching streams.
                }
            });
        }
    }

    private void RaiseState(TunerStateSnapshot snapshot)
    {
        try
        {
            StateChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Tuner: StateChanged handler failed.", ex);
        }
    }
}
