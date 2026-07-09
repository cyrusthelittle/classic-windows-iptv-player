using CyrusIptv.Core;
using LibVLCSharp.Shared;
using System;
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
        var generation = Interlocked.Increment(ref _generation);
        AppLogger.Info("Tuner: play requested. generation=" + generation + "; channel=" + request.ChannelName + "; url=" + AppLogger.SanitizeUrl(request.Url));
        _ = Task.Run(async () =>
        {
            try
            {
                await RunTuneCycleAsync(generation, request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Tuner: tune cycle crashed. generation=" + generation, ex);
                RaiseState(new TunerStateSnapshot(TunerStatus.Failed, request, 0, MaxAttempts, ex.Message));
            }
        });
    }

    public void Stop()
    {
        var generation = Interlocked.Increment(ref _generation);
        AppLogger.Info("Tuner: stop requested. generation=" + generation);
        RetireActivePlayer();
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
        RetireActivePlayer();
        try
        {
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

    private async Task RunTuneCycleAsync(int generation, TuneRequest request)
    {
        var maxAttempts = MaxAttempts;
        var attempt = 0;
        while (attempt < maxAttempts)
        {
            attempt++;
            if (!IsCurrent(generation)) return;

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

            // Surface handoff protocol: the host releases the old player's binding
            // while that player is still alive (rebinding/detaching touches its
            // native handle), then binds the new player, and only then is the old
            // player torn down. A detached player can never be rebound, because
            // hosts only ever bind the tuner's current player.
            if (oldPlayer is not null) RaiseDetaching(oldPlayer);

            try
            {
                PlayerAttached?.Invoke(player, media, request);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Tuner: PlayerAttached handler failed.", ex);
            }

            Retire(oldPlayer, oldMedia);

            var opened = await OpenAsync(player, media).ConfigureAwait(false);
            // If a newer request took over while this one was opening, the
            // successor has already retired this player; just bow out.
            if (!IsCurrent(generation)) return;

            if (opened)
            {
                RaiseState(new TunerStateSnapshot(TunerStatus.Playing, request, attempt, maxAttempts, null));
                AppLogger.Info("Tuner: playing. generation=" + generation + "; attempt=" + attempt + "; channel=" + request.ChannelName);

                var playingSince = DateTime.UtcNow;
                var end = await MonitorAsync(player).ConfigureAwait(false);
                if (!IsCurrent(generation)) return;

                if (end == StreamEnd.EndedNormally && !request.IsLive)
                {
                    AppLogger.Info("Tuner: media finished. generation=" + generation + "; channel=" + request.ChannelName);
                    RaiseState(new TunerStateSnapshot(TunerStatus.Ended, request, attempt, maxAttempts, null));
                    return;
                }

                if (end == StreamEnd.StoppedExternally)
                {
                    // The host stopped the player deliberately (user stop/shutdown).
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

            // This attempt's player is dead weight; make the host release the
            // video surface first, then retire it before the next try.
            lock (_gate)
            {
                if (ReferenceEquals(_activePlayer, player))
                {
                    _activePlayer = null;
                    _activeMedia = null;
                }
            }

            RaiseDetaching(player);
            Retire(player, media);

            if (attempt < maxAttempts)
            {
                var delay = RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length) - 1];
                await Task.Delay(delay).ConfigureAwait(false);
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
    private static async Task<bool> OpenAsync(MediaPlayer player, Media media)
    {
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPlaying(object? s, EventArgs e) => outcome.TrySetResult(true);
        void OnFailure(object? s, EventArgs e) => outcome.TrySetResult(false);

        player.Playing += OnPlaying;
        player.EncounteredError += OnFailure;
        player.Stopped += OnFailure;
        player.EndReached += OnFailure;
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
    private static async Task<StreamEnd> MonitorAsync(MediaPlayer player)
    {
        var outcome = new TaskCompletionSource<StreamEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnError(object? s, EventArgs e) => outcome.TrySetResult(StreamEnd.Error);
        void OnEnd(object? s, EventArgs e) => outcome.TrySetResult(StreamEnd.EndedNormally);
        void OnStopped(object? s, EventArgs e) => outcome.TrySetResult(StreamEnd.StoppedExternally);

        player.EncounteredError += OnError;
        player.EndReached += OnEnd;
        player.Stopped += OnStopped;
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

    private void RetireActivePlayer()
    {
        MediaPlayer? player;
        Media? media;
        lock (_gate)
        {
            player = _activePlayer;
            media = _activeMedia;
            _activePlayer = null;
            _activeMedia = null;
        }

        if (player is not null) RaiseDetaching(player);
        Retire(player, media);
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
                    player?.Stop();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Tuner: retired player Stop failed. " + ex.Message);
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
