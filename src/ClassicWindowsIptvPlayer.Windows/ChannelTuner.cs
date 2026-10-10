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
    string AccountId = "",
    int TimeshiftSeconds = 30);

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
    public bool TimeshiftEnabled { get; set; }
    private int _timeshiftRetentionSeconds = 30;
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
    private ContinuousTimeshiftSource? _activeFileSource;
    private TuneRequest? _activeRequest;
    private string? _recordingOutputPath;
    private CancellationTokenSource? _activeTuneCts;
    private Task? _activeTuneTask;
    private readonly List<Task> _tuneCycles = [];
    private Task _retireChain = Task.CompletedTask;
    private volatile bool _disposed;
    private volatile bool _userPaused;
    private long? _inputStartSequence;
    private (ContinuousTimeshiftSource Source, long Sequence, long OffsetMs)? _pausedLivePosition;
    private long? _lastLivePositionMs, _lastLivePlayerTimeMs;
    private long _lastLivePositionTick;
    private bool _livePositionClockFallback;
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

    public void SetTimeshiftRetention(int seconds)
    {
        var retention = seconds is >= 1 and <= 120 ? seconds : 30;
        Volatile.Write(ref _timeshiftRetentionSeconds, retention);
        lock (_gate) _activeFileSource?.SetRetentionSeconds(retention);
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

    public bool TryAttachSharedHlsRecording(out SharedHlsConsumer? consumer)
    {
        lock (_gate)
        {
            consumer = _activeRequest is { IsLive: true } && _activeFileSource is { TerminalFailure: null } source
                ? source.AttachRecording() : null;
            return consumer is not null;
        }
    }
    public bool TryAttachSharedHlsRecording(string expectedAccountId, string expectedChannelId,
        out SharedHlsConsumer? consumer)
    {
        lock (_gate)
        {
            consumer = null;
            if (string.IsNullOrWhiteSpace(expectedAccountId) || string.IsNullOrWhiteSpace(expectedChannelId) ||
                !string.Equals(_activeRequest?.AccountId, expectedAccountId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_activeRequest?.ChannelId, expectedChannelId, StringComparison.OrdinalIgnoreCase)) return false;
            return TryAttachSharedHlsRecording(out consumer);
        }
    }
    /// <summary>Whether the current channel has a retained recording to pause or seek.</summary>
    public bool HasBufferedLivePlayback
    {
        get { lock (_gate) return _activeRequest is { IsLive: true } && _activeFileSource?.InspectTimeshiftWindow().HasData == true; }
    }

    public bool TryGetLivePlaybackPosition(out TimeSpan position, out TimeSpan duration)
    {
        lock (_gate)
        {
            position = duration = TimeSpan.Zero;
            if (_activeFileSource is not { } source || _activePlayer is not { } player) return false;
            var window = source.InspectTimeshiftWindow();
            duration = window.Duration;
            var absoluteMs = _pausedLivePosition is { } paused
                ? paused.Sequence + paused.OffsetMs
                : GetAbsoluteLivePositionLocked(player);
            position = TimeSpan.FromMilliseconds(Math.Clamp(absoluteMs - (window.OldestSequence ?? 0), 0, duration.TotalMilliseconds));
            source.UpdatePlaybackPosition(absoluteMs, _userPaused);
            return true;
        }
    }

    private long GetAbsoluteLivePositionLocked(MediaPlayer player)
    {
        var playerTime = Math.Max(0, player.Time);
        var candidate = (_inputStartSequence ?? 0) + playerTime;
        var now = Environment.TickCount64;
        var position = candidate;
        if (_lastLivePositionMs is long previous)
        {
            var elapsed = Math.Max(0, now - _lastLivePositionTick);
            var expected = previous + elapsed;
            position = Math.Max(candidate, expected);
            if (_lastLivePlayerTimeMs is not long previousPlayerTime || playerTime > previousPlayerTime + 250)
                _lastLivePositionTick = now;
            if (_livePositionClockFallback)
            {
                position = expected;
            }
            else if (_lastLivePlayerTimeMs is long resetPlayerTime &&
                     (playerTime + 1000 < resetPlayerTime || candidate + 1000 < previous ||
                      (player.State == VLCState.Playing && now - _lastLivePositionTick > 2000)))
            {
                // Some live TS muxers reset or stall LibVLC's playback clock even
                // though the independent continuous reader is still moving. Keep
                // the recorded position monotonic instead of pinning history.
                _livePositionClockFallback = true;
                position = expected;
            }
        }
        else
        {
            _lastLivePositionTick = now;
        }
        _lastLivePositionMs = position;
        _lastLivePlayerTimeMs = playerTime;
        _lastLivePositionTick = now;
        return position;
    }
    public bool TryInspectLiveTimeshift(out LiveTimeshiftWindow? window, out string? unsupportedReason)
    {
        lock (_gate)
        {
            var source = _activeFileSource;
            if (_activeRequest is not { IsLive: true } || source is null)
            {
                window = null;
                unsupportedReason = "Live timeshift is available only for supported continuous-file channels.";
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

    /// <summary>Open another reading position in the same recording without restarting the writer.</summary>
    public async Task<TimeshiftPlaybackSwitchResult> SeekLiveTimeshiftAsync(TimeSpan behindLive, CancellationToken cancellationToken = default)
    {
        if (behindLive < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(behindLive));
        CancellationToken tuneToken;
        lock (_gate) tuneToken = _activeTuneCts?.Token ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tuneToken);
        await _timeshiftTransitionGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ContinuousTimeshiftSource? source;
            MediaPlayer? player;
            lock (_gate)
            {
                source = _activeFileSource;
                player = _activePlayer;
                if (_activeRequest is not { IsLive: true } || source is null || player is null ||
                    player.State is not (VLCState.Playing or VLCState.Paused))
                    return new(false, null, false, "The active live continuous-file player is not playing.");
            }
            var status = source.TryCreateTimeshiftPlaybackUrlBehindLive(behindLive, out var url, out var sequence);
            if (status != LiveTimeshiftSeekResult.Positioned || url is null)
                return new(false, null, false, "The requested point is outside the available timeshift window (" + status + ").");
            source.UpdatePlaybackPosition(sequence, _userPaused);
            return await SwitchActivePlayerInputAsync(player, source, url, sequence, linked.Token).ConfigureAwait(false);
        }
        finally { _timeshiftTransitionGate.Release(); }
    }

    /// <summary>Switch the existing player to a fresh local input at the current continuous-file live edge.</summary>
    public async Task<TimeshiftPlaybackSwitchResult> ReturnToLiveAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken tuneToken;
        lock (_gate) tuneToken = _activeTuneCts?.Token ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tuneToken);
        await _timeshiftTransitionGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ContinuousTimeshiftSource? source;
            MediaPlayer? player;
            lock (_gate)
            {
                source = _activeFileSource;
                player = _activePlayer;
                if (_activeRequest is not { IsLive: true } || source is null || player is null)
                    return new(false, null, false, "The active channel has no continuous-file playback source.");
            }
            var status = source.TryCreateTimeshiftPlaybackUrlBehindLive(TimeSpan.Zero, out var url, out var sequence);
            if (status != LiveTimeshiftSeekResult.Positioned || url is null)
                return new(false, null, false, "The continuous-file live edge is not available (" + status + ").");
            source.UpdatePlaybackPosition(sequence, false);
            return await SwitchActivePlayerInputAsync(player, source, url, sequence, linked.Token, preservePause: false).ConfigureAwait(false);
        }
        finally { _timeshiftTransitionGate.Release(); }
    }

    private async Task<TimeshiftPlaybackSwitchResult> SwitchActivePlayerInputAsync(MediaPlayer player,
        ContinuousTimeshiftSource source, Uri url, long sequence, CancellationToken cancellationToken, bool preservePause = true)
    {
        Media? previousMedia;
        lock (_gate)
        {
            if (!ReferenceEquals(_activePlayer, player) || !ReferenceEquals(_activeFileSource, source) || _activeMedia is null)
                return new(false, sequence, false, "The active channel changed before the timeshift switch began.");
            previousMedia = _activeMedia;
            _timeshiftSwitchingPlayer = player;
        }

        Media? replacement = null;
        var resumePausedState = preservePause && _userPaused;
        var needsVideo = player.VoutCount > 0 || previousMedia.Tracks.Any(track => track.TrackType == TrackType.Video);
        try
        {
            replacement = CreateTimeshiftMedia(url);
            StopPlayer(player);
            var started = await StartExistingPlayerInputAsync(player, replacement, cancellationToken).ConfigureAwait(false);
            if (started)
            {
                // Playing is raised before the decoder has produced a picture. Pausing
                // there can leave a freshly sought input with no video output at all.
                if (resumePausedState)
                {
                    var frameDeadline = DateTime.UtcNow.AddSeconds(6);
                    while (DateTime.UtcNow < frameDeadline &&
                        (player.Time <= 0 || (needsVideo && player.VoutCount == 0)))
                        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
                if (resumePausedState) player.SetPause(true);
                TuneRequest request;
                lock (_gate)
                {
                    if (!ReferenceEquals(_activePlayer, player) || !ReferenceEquals(_activeFileSource, source))
                        return new(false, sequence, false, "The active channel changed while LibVLC was opening the buffered input.");
                    _activeMedia = replacement;
                    _inputStartSequence = sequence;
                    _pausedLivePosition = resumePausedState ? (source, sequence, Math.Max(0, player.Time)) : null;
                    _userPaused = resumePausedState;
                    _lastLivePositionMs = sequence + Math.Max(0, player.Time);
                    _lastLivePlayerTimeMs = Math.Max(0, player.Time);
                    _lastLivePositionTick = Environment.TickCount64;
                    _livePositionClockFallback = false;
                    source.UpdatePlaybackPosition(_lastLivePositionMs.Value, resumePausedState);
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
        (_recordingOutputPath is not null || _activeFileSource is null || _activeFileSource.TerminalFailure is null);

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
    public void NotifyUserPaused()
    {
        lock (_gate)
        {
            if (_activeRequest is { IsLive: true } && _activeFileSource is not null && _activePlayer is { } activePlayer)
                _lastLivePositionMs = GetAbsoluteLivePositionLocked(activePlayer);
            _userPaused = true;
            _pausedLivePosition = null;
            if (_activeFileSource is { } source && _activePlayer is { } player &&
                _lastLivePositionMs is long absolutePosition &&
                source.TryLocatePlaybackPosition(0, absolutePosition, out var sequence, out var offset))
            {
                _pausedLivePosition = (source, sequence, offset);
                source.UpdatePlaybackPosition(sequence + offset, paused: true);
            }
        }
    }

    public async Task<TimeshiftPlaybackSwitchResult> ResumeBufferedPauseAsync()
    {
        CancellationToken token;
        lock (_gate) token = _activeTuneCts?.Token ?? CancellationToken.None;
        await _timeshiftTransitionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            MediaPlayer? player;
            (ContinuousTimeshiftSource Source, long Sequence, long OffsetMs)? paused;
            lock (_gate) { player = _activePlayer; paused = _pausedLivePosition; }
            if (paused is not { } position || player is null)
                return new(false, null, false, "The paused position is not buffered.");
            if (position.Source.TryCreateTimeshiftPlaybackUrl(position.Sequence, out var url) != LiveTimeshiftSeekResult.Positioned || url is null)
                return new(false, null, false, "The recorded pause position is unavailable. Choose Go Live.");
            // Continue the same file reader and decoder, retaining the paused frame.
            if (player.State == VLCState.Paused)
            {
                player.SetPause(false);
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline && !player.IsPlaying)
                    await Task.Delay(50, token).ConfigureAwait(false);
                if (player.IsPlaying)
                {
                    lock (_gate)
                    {
                        _userPaused = false;
                        _pausedLivePosition = null;
                        _lastLivePositionMs = position.Sequence + position.OffsetMs;
                        _lastLivePlayerTimeMs = Math.Max(0, player.Time);
                        _lastLivePositionTick = Environment.TickCount64;
                        _livePositionClockFallback = false;
                        position.Source.UpdatePlaybackPosition(position.Sequence + position.OffsetMs, paused: false);
                    }
                    return new(true, position.Sequence, false, null);
                }
            }
            var result = await SwitchActivePlayerInputAsync(player, position.Source, url, position.Sequence, token).ConfigureAwait(false);
            if (result.Success)
            {
                player.SetPause(false);
                lock (_gate)
                {
                    _userPaused = false;
                    _pausedLivePosition = null;
                    _lastLivePositionMs = position.Sequence + position.OffsetMs;
                    _lastLivePlayerTimeMs = Math.Max(0, player.Time);
                    _lastLivePositionTick = Environment.TickCount64;
                    _livePositionClockFallback = false;
                    position.Source.UpdatePlaybackPosition(position.Sequence + position.OffsetMs, paused: false);
                }
            }
            return result;
        }
        finally { _timeshiftTransitionGate.Release(); }
    }

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
            ContinuousTimeshiftSource? fileSource = null;
            long? inputStartSequence = null;
            string? recordingOutputPath;
            lock (_gate) recordingOutputPath = _recordingOutputPath;
            try
            {
                var mediaUrl = request.Url;
                if (recordingOutputPath is null && request.IsLive)
                {
                    try
                    {
                        fileSource = new ContinuousTimeshiftSource(_libVlc, request.Url, request.BufferMs,
                            request.TimeshiftSeconds is >= 1 and <= 120 ? request.TimeshiftSeconds : Volatile.Read(ref _timeshiftRetentionSeconds));
                        mediaUrl = (await fileSource.StartPlaybackAsync(cancellationToken).ConfigureAwait(false)).AbsoluteUri;
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (fileSource is not null) await fileSource.DisposeAsync().ConfigureAwait(false);
                        fileSource = null;
                        AppLogger.Warn("Tuner: continuous recording unavailable; opening direct playback. " + AppLogger.SanitizeText(exception.Message));
                    }
                }
                if (fileSource is not null &&
                    fileSource.TryCreateTimeshiftPlaybackUrlBehindLive(TimeSpan.Zero, out var initialUrl, out var initialSequence) == LiveTimeshiftSeekResult.Positioned)
                {
                    mediaUrl = initialUrl!.AbsoluteUri;
                    inputStartSequence = initialSequence;
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
                if (fileSource is not null)
                {
                    try { await fileSource.DisposeAsync().ConfigureAwait(false); } catch { }
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
                    _activeFileSource = fileSource;
                    _inputStartSequence = inputStartSequence;
                    _pausedLivePosition = null;
                    _userPaused = false;
                    _lastLivePositionMs = null;
                    _lastLivePlayerTimeMs = null;
                    _livePositionClockFallback = false;
                    _activeRequest = request;
                    installed = true;
                }
            }
            if (!installed)
            {
                player.Dispose();
                media.Dispose();
                if (fileSource is not null) await fileSource.DisposeAsync().ConfigureAwait(false);
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
                await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
                RaiseState(new TunerStateSnapshot(TunerStatus.Failed, request, attempt, maxAttempts, "The embedded video surface is unavailable."));
                return;
            }

            // The previous cycle owns and retires its own player after its native
            // callbacks have been detached. Cancelling it is safe; disposing it
            // here would race OpenAsync/MonitorAsync event cleanup.
            var opened = await OpenAsync(player, media, cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
            {
                await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
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
                    await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
                    return;
                }

                if (PlaybackCompletionPolicy.IsNormalVodCompletion(request.IsLive, end == StreamEnd.EndedNormally))
                {
                    AppLogger.Info("Tuner: media finished. generation=" + generation + "; channel=" + request.ChannelName);
                    RaiseState(new TunerStateSnapshot(TunerStatus.Ended, request, attempt, maxAttempts, null));
                    await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
                    return;
                }

                if (end == StreamEnd.StoppedExternally)
                {
                    await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
                    return;
                }

                if (_userPaused)
                {
                    // Keep ownership of the recording until resume or cancellation.
                    while (_userPaused && !cancellationToken.IsCancellationRequested)
                        await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                    if (!cancellationToken.IsCancellationRequested && player.IsPlaying)
                        await MonitorAsync(player, cancellationToken, null).ConfigureAwait(false);
                }
                var duration = DateTime.UtcNow - playingSince;
                playedFor = duration;
                AppLogger.Warn("Tuner: stream dropped after " + duration.TotalSeconds.ToString("F0") + "s. generation=" + generation + "; channel=" + request.ChannelName);
            }
            else
            {
                AppLogger.Warn("Tuner: attempt " + attempt + " did not reach Playing. generation=" + generation + "; channel=" + request.ChannelName);
            }

            await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);

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
        void OnError(object? s, EventArgs e) { if (!IsTimeshiftSwitchInProgress(player)) outcome.TrySetResult(StreamEnd.Error); }
        void OnEnd(object? s, EventArgs e) { if (!IsTimeshiftSwitchInProgress(player)) outcome.TrySetResult(StreamEnd.EndedNormally); }
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
                if (player.State == VLCState.Error && !IsTimeshiftSwitchInProgress(player)) outcome.TrySetResult(StreamEnd.Error);
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

    private async Task ReleaseAttemptAsync(MediaPlayer player, Media media, ContinuousTimeshiftSource? fileSource)
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
        ContinuousTimeshiftSource? ownedSource = fileSource;
        lock (_gate)
        {
            if (ReferenceEquals(_activePlayer, player))
            {
                ownedMedia = _activeMedia ?? media;
                ownedSource = _activeFileSource ?? fileSource;
                _activePlayer = null;
                _activeMedia = null;
                _activeFileSource = null;
                _activeRequest = null;
                wasActive = true;
            }
        }

        if (wasActive) RaiseDetaching(player);
        Retire(player, ownedMedia);
        if (ownedSource is not null)
        {
            try { await ownedSource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { AppLogger.Warn("Tuner: timeshift file cleanup failed. " + AppLogger.SanitizeText(exception.Message)); }
        }
        }
        finally { _timeshiftTransitionGate.Release(); }
    }

    private void RaiseActiveMediaChanged(MediaPlayer player, Media media, TuneRequest request)
    {
        try { ActiveMediaChanged?.Invoke(player, media, request); }
        catch (Exception exception) { AppLogger.Error("Tuner: ActiveMediaChanged handler failed.", exception); }
    }

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
