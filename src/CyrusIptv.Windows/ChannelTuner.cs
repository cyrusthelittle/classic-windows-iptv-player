using CyrusIptv.Core;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CyrusIptv.Windows;

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
    int BufferMs);

public sealed record TunerStateSnapshot(
    TunerStatus Status,
    TuneRequest? Request,
    int Attempt,
    int MaxAttempts,
    string? Detail);

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
    // A stream that survived this long was healthy; if it dies afterwards the
    // outage gets a fresh retry budget instead of inheriting old failures.
    private static readonly TimeSpan HealthyPlayThreshold = TimeSpan.FromSeconds(30);
    // Failed opens on this provider are instant refusals while the server spins
    // the channel up on demand (~2s). Keep the cadence tight so playback starts
    // the moment the stream becomes available, instead of backing off past it.
    private static readonly int[] RetryDelaysMs = [250, 300, 400, 500];

    private readonly LibVLC _libVlc;
    private readonly object _gate = new();
    private int _generation;
    private MediaPlayer? _activePlayer;
    private Media? _activeMedia;
    private CancellationTokenSource? _activeTuneCts;
    private Task? _activeTuneTask;
    private readonly List<Task> _tuneCycles = [];
    private Task _retireChain = Task.CompletedTask;
    private volatile bool _disposed;
    private volatile bool _userPaused;
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
                RaiseState(new TunerStateSnapshot(TunerStatus.Failed, request, 0, MaxAttempts, ex.Message));
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
        var generation = Interlocked.Increment(ref _generation);
        AppLogger.Info("Tuner: stop requested. generation=" + generation);
        CancelAndDetachActivePlayer();
        RaiseState(new TunerStateSnapshot(TunerStatus.Idle, null, 0, MaxAttempts, null));
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
        var attempt = 0;
        while (attempt < maxAttempts)
        {
            attempt++;
            if (!IsCurrent(generation) || cancellationToken.IsCancellationRequested) return;

            RaiseState(new TunerStateSnapshot(TunerStatus.Tuning, request, attempt, maxAttempts, null));
            AppLogger.Info("Tuner: attempt " + attempt + "/" + maxAttempts + ". generation=" + generation + "; channel=" + request.ChannelName);

            MediaPlayer player;
            Media media;
            try
            {
                media = new Media(_libVlc, request.Url, FromType.FromLocation);
                media.AddOption(":network-caching=" + request.BufferMs);
                media.AddOption(":live-caching=" + request.BufferMs);
                media.AddOption(":file-caching=" + request.BufferMs);
                media.AddOption(":http-reconnect");
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
                AppLogger.Error("Tuner: failed to create player/media. generation=" + generation, ex);
                RaiseState(new TunerStateSnapshot(TunerStatus.Failed, request, attempt, maxAttempts, ex.Message));
                return;
            }

            MediaPlayer? oldPlayer;
            Media? oldMedia;
            lock (_gate)
            {
                if (!IsCurrent(generation))
                {
                    player.Dispose();
                    media.Dispose();
                    return;
                }

                oldPlayer = _activePlayer;
                oldMedia = _activeMedia;
                _activePlayer = player;
                _activeMedia = media;
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
                ReleaseAttempt(player, media);
                RaiseState(new TunerStateSnapshot(TunerStatus.Failed, request, attempt, maxAttempts, "The embedded video surface is unavailable."));
                return;
            }

            // The previous cycle owns and retires its own player after its native
            // callbacks have been detached. Cancelling it is safe; disposing it
            // here would race OpenAsync/MonitorAsync event cleanup.
            var opened = await OpenAsync(player, media, cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
            {
                ReleaseAttempt(player, media);
                return;
            }

            if (opened)
            {
                RaiseState(new TunerStateSnapshot(TunerStatus.Playing, request, attempt, maxAttempts, null));
                AppLogger.Info("Tuner: playing. generation=" + generation + "; attempt=" + attempt + "; channel=" + request.ChannelName);

                var playingSince = DateTime.UtcNow;
                var end = await MonitorAsync(player, cancellationToken).ConfigureAwait(false);
                if (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
                {
                    ReleaseAttempt(player, media);
                    return;
                }

                if (end == StreamEnd.EndedNormally && !request.IsLive)
                {
                    AppLogger.Info("Tuner: media finished. generation=" + generation + "; channel=" + request.ChannelName);
                    RaiseState(new TunerStateSnapshot(TunerStatus.Ended, request, attempt, maxAttempts, null));
                    ReleaseAttempt(player, media);
                    return;
                }

                if (end == StreamEnd.StoppedExternally)
                {
                    ReleaseAttempt(player, media);
                    return;
                }

                if (_userPaused)
                {
                    AppLogger.Info("Tuner: stream failed while paused; leaving recovery to the resume path. generation=" + generation);
                    return;
                }

                var playedFor = DateTime.UtcNow - playingSince;
                AppLogger.Warn("Tuner: stream dropped after " + playedFor.TotalSeconds.ToString("F0") + "s. generation=" + generation + "; channel=" + request.ChannelName);
                if (playedFor >= HealthyPlayThreshold) attempt = 0;
            }
            else
            {
                AppLogger.Warn("Tuner: attempt " + attempt + " did not reach Playing. generation=" + generation + "; channel=" + request.ChannelName);
            }

            ReleaseAttempt(player, media);

            if (attempt < maxAttempts)
            {
                var delay = RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length) - 1];
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
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
    private static async Task<StreamEnd> MonitorAsync(MediaPlayer player, CancellationToken cancellationToken)
    {
        var outcome = new TaskCompletionSource<StreamEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnError(object? s, EventArgs e) => outcome.TrySetResult(StreamEnd.Error);
        void OnEnd(object? s, EventArgs e) => outcome.TrySetResult(StreamEnd.EndedNormally);
        void OnStopped(object? s, EventArgs e) => outcome.TrySetResult(StreamEnd.StoppedExternally);

        player.EncounteredError += OnError;
        player.EndReached += OnEnd;
        player.Stopped += OnStopped;
        using var cancellationRegistration = cancellationToken.Register(() => outcome.TrySetResult(StreamEnd.StoppedExternally));
        try
        {
            return await outcome.Task.ConfigureAwait(false);
        }
        finally
        {
            player.EncounteredError -= OnError;
            player.EndReached -= OnEnd;
            player.Stopped -= OnStopped;
        }
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

    private void ReleaseAttempt(MediaPlayer player, Media media)
    {
        // Callback subscriptions have already been removed when this is called.
        // Stop while the drawable is still attached; clearing a live player's
        // HWND lets LibVLC fall back to a top-level Direct3D11 output window.
        StopPlayer(player);

        var wasActive = false;
        lock (_gate)
        {
            if (ReferenceEquals(_activePlayer, player))
            {
                _activePlayer = null;
                _activeMedia = null;
                wasActive = true;
            }
        }

        if (wasActive) RaiseDetaching(player);
        Retire(player, media);
    }

    private static void StopPlayer(MediaPlayer player)
    {
        try { player.Stop(); }
        catch (Exception ex) { AppLogger.Warn("Tuner: player Stop failed. " + ex.Message); }
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
                    AppLogger.Warn("Tuner: retired player Dispose failed. " + ex.Message);
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
