using ClassicWindowsIptvPlayer.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>
/// Account-scoped schedule runner. It can only attach captures to a matching stream
/// that the user is already watching; it never asks the tuner to start playback.
/// The host is responsible for periodically calling <see cref="ProcessDueAsync"/>.
/// </summary>
public sealed class ScheduledRecordingCoordinator : IDisposable
{
    private const string OverlapConflictReason = "Capture window overlaps another active job for this account.";
    private const string StreamUnavailableReason = "No healthy supported shared HLS stream for this account and channel is already playing; playback was not changed.";
    private readonly string _accountId;
    private readonly ConfigStore _store;
    private readonly ChannelTuner _tuner;
    private readonly RecordingService _recordings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Task<RecordingOutcome>> _starts = new(StringComparer.OrdinalIgnoreCase);
    // Tracks start reservations independently of the gate: StartSharedHlsAsync may
    // await media with the gate released while another due pass is running.
    private readonly HashSet<string> _startsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _startCancellation = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string RecordingId, Task<RecordingOutcome?> Stop)> _stops = new(StringComparer.OrdinalIgnoreCase);
    private ScheduledRecordingIndex _index;
    private int _disposed;

    public event EventHandler? Changed;

    public ScheduledRecordingCoordinator(string accountId, ConfigStore store, ChannelTuner tuner, RecordingService recordings)
    {
        if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("An account ID is required.", nameof(accountId));
        _accountId = accountId;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tuner = tuner ?? throw new ArgumentNullException(nameof(tuner));
        _recordings = recordings ?? throw new ArgumentNullException(nameof(recordings));
        _index = _store.LoadScheduledRecordingIndex(_accountId) ?? new ScheduledRecordingIndex();
        _index.Normalize();
        var now = DateTimeOffset.UtcNow;
        _index.RecoverActive(now);
        foreach (var job in _index.Jobs.Where(job => IsOwned(job) && job.Status == ScheduledRecordingStatus.Scheduled && job.RequestedCaptureEndUtc <= now).ToArray())
            Replace(job with { Status = ScheduledRecordingStatus.Missed, Reason = "The capture window expired while the app was not running." });
        ReclassifyConflicts();
        PersistAndNotify();
        _recordings.CaptureFinished += OnCaptureFinished;
    }

    public IReadOnlyList<ScheduledRecordingJob> Jobs
    {
        get
        {
            lock (_index) return _index.Jobs.Where(IsOwned).OrderBy(job => job.RequestedCaptureStartUtc).ToArray();
        }
    }

    /// <summary>Validates and durably adds an account-scoped schedule, classifying any overlap.</summary>
    public async Task<ScheduledRecordingJob> AddAsync(ScheduledRecordingJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!string.Equals(job.AccountId, _accountId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The scheduled job belongs to a different account.", nameof(job));
            if (!ScheduledRecordingPolicy.Validate(job).IsValid || !ValidDestination(job.DestinationPath))
                throw new ArgumentException("The scheduled job or destination path is invalid.", nameof(job));
            var normalized = job with { Status = ScheduledRecordingStatus.Scheduled, RecordingId = null, Reason = string.Empty };
            if (!_index.Add(normalized)) throw new ArgumentException("A job with this ID already exists or the job is invalid.", nameof(job));
            ReclassifyConflicts();
            PersistAndNotify();
            return Find(job.Id)!;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Serializes due-job decisions and saves all lifecycle changes durably.</summary>
    public async Task ProcessDueAsync(DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            ReclassifyConflicts();
            foreach (var job in Jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (job.Status == ScheduledRecordingStatus.Recording)
                {
                    if (!string.IsNullOrWhiteSpace(job.RecordingId) && now >= job.RequestedCaptureEndUtc)
                        await StopAtRequestedEndAsync(job, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (job.Status is not (ScheduledRecordingStatus.Scheduled or ScheduledRecordingStatus.Conflict) ||
                    now < job.RequestedCaptureStartUtc) continue;
                if (_startsInFlight.Contains(job.Id)) continue;
                if (now >= job.RequestedCaptureEndUtc)
                {
                    Update(job, ScheduledRecordingStatus.Missed, "The requested capture window expired before a matching stream was available.");
                    continue;
                }
                if (job.Status == ScheduledRecordingStatus.Conflict && string.Equals(job.Reason, OverlapConflictReason, StringComparison.Ordinal))
                    continue;
                await StartDueAsync(job, now, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var job = Find(jobId);
            if (job is null || job.Status is ScheduledRecordingStatus.Completed or ScheduledRecordingStatus.Missed or
                ScheduledRecordingStatus.Failed or ScheduledRecordingStatus.Cancelled) return false;

            _starts.TryGetValue(job.Id, out var pendingStart);
            Task<RecordingOutcome?>? pendingStop = null;
            Update(job, ScheduledRecordingStatus.Cancelled, "Cancelled by the user.");
            if (pendingStart is not null && _startCancellation.TryGetValue(job.Id, out var startCancellation))
                startCancellation.Cancel();
            if (!string.IsNullOrWhiteSpace(job.RecordingId))
                pendingStop = _recordings.StopAsync(job.RecordingId, RecordingStopReason.User, CancellationToken.None);
            ReclassifyConflicts();
            PersistAndNotify();

            // Do not report cancellation as finished until a start that was already
            // opening its first HLS segment (or an active capture) has flushed and
            // detached. Release the coordinator gate so its start continuation and
            // capture-finished callback can finish their own state updates.
            if (pendingStart is not null || pendingStop is not null)
            {
                _gate.Release();
                try
                {
                    if (pendingStart is not null)
                    {
                        RecordingOutcome? started = null;
                        try { started = await pendingStart.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        if (started is { Accepted: true } && !string.IsNullOrWhiteSpace(started.RecordingId))
                            pendingStop ??= _recordings.StopAsync(started.RecordingId, RecordingStopReason.User, CancellationToken.None);
                    }
                    if (pendingStop is not null) await pendingStop.ConfigureAwait(false);
                }
                finally { await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false); }
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task StartDueAsync(ScheduledRecordingJob job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_startsInFlight.Add(job.Id)) return;
        if (!IsOwned(job))
        {
            Update(job, ScheduledRecordingStatus.Failed, "The scheduled job belongs to a different account.");
            _startsInFlight.Remove(job.Id);
            return;
        }
        if (!ScheduledRecordingPolicy.Validate(job).IsValid || !ValidDestination(job.DestinationPath))
        {
            Update(job, ScheduledRecordingStatus.Failed, "The saved destination or job data is invalid.");
            _startsInFlight.Remove(job.Id);
            return;
        }
        if (!_tuner.TryAttachSharedHlsRecording(_accountId, job.ChannelId, out var consumer) || consumer is null)
        {
            Update(job, ScheduledRecordingStatus.Conflict, StreamUnavailableReason);
            _startsInFlight.Remove(job.Id);
            return;
        }

        var programme = new EpgProgramme(job.ChannelId, job.ProgrammeTitle, string.Empty, string.Empty,
            job.ProgrammeStartUtc, job.ProgrammeEndUtc);
        var request = new RecordingStartRequest
        {
            AccountId = _accountId,
            ChannelId = job.ChannelId,
            ChannelName = job.ChannelName,
            ChannelKey = job.ChannelId,
            Programme = programme,
            DestinationPath = job.DestinationPath,
            Container = RecordingContainerKind.Ts,
            RequestedStartUtc = now,
            Duration = job.RequestedCaptureEndUtc > now ? job.RequestedCaptureEndUtc - now : TimeSpan.Zero
        };

        var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<RecordingOutcome> startTask;
        try { startTask = _recordings.StartSharedHlsAsync(request, consumer, startCancellation.Token); }
        catch (Exception exception)
        {
            startCancellation.Dispose();
            Update(job, ScheduledRecordingStatus.Failed, "Could not start the shared-stream capture: " + AppLogger.SanitizeText(exception.Message));
            _startsInFlight.Remove(job.Id);
            return;
        }

        _starts[job.Id] = startTask;
        _startCancellation[job.Id] = startCancellation;
        // StartSharedHlsAsync waits for initial media. Release the gate so cancellation
        // and capture-finished callbacks can progress during that wait.
        _gate.Release();
        RecordingOutcome outcome;
        Exception? startError = null;
        try { outcome = await startTask.ConfigureAwait(false); }
        catch (Exception exception) { outcome = default!; startError = exception; }
        finally { await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false); }

        _starts.Remove(job.Id);
        _startCancellation.Remove(job.Id);
        _startsInFlight.Remove(job.Id);
        startCancellation.Dispose();
        var current = Find(job.Id);
        if (current is null || current.Status == ScheduledRecordingStatus.Cancelled || Volatile.Read(ref _disposed) != 0)
        {
            if (startError is null && outcome is not null && outcome.Accepted && !string.IsNullOrWhiteSpace(outcome.RecordingId))
                _ = _recordings.StopAsync(outcome.RecordingId, RecordingStopReason.User, CancellationToken.None);
            return;
        }
        if (startError is not null)
        {
            if (startError is OperationCanceledException && cancellationToken.IsCancellationRequested) throw startError;
            if (startError is OperationCanceledException)
                Update(current, ScheduledRecordingStatus.Cancelled, "Cancelled before the shared stream produced media.");
            else Update(current, ScheduledRecordingStatus.Failed, "Shared-stream capture failed to start: " + AppLogger.SanitizeText(startError.Message));
            ReclassifyConflicts();
            return;
        }
        if (!outcome.Accepted || string.IsNullOrWhiteSpace(outcome.RecordingId))
        {
            Update(current, ScheduledRecordingStatus.Failed, string.IsNullOrWhiteSpace(outcome.FailureReason)
                ? "The recording service rejected the scheduled capture."
                : AppLogger.SanitizeText(outcome.FailureReason));
            ReclassifyConflicts();
            return;
        }
        Update(current with { RecordingId = outcome.RecordingId }, ScheduledRecordingStatus.Recording, string.Empty);
    }

    private async Task StopAtRequestedEndAsync(ScheduledRecordingJob job, CancellationToken cancellationToken)
    {
        if (_stops.TryGetValue(job.Id, out var activeStop) && activeStop.RecordingId == job.RecordingId)
        {
            if (!activeStop.Stop.IsCompleted) return;
            try { MapOutcome(job, await activeStop.Stop.ConfigureAwait(false)); }
            catch (Exception exception) { Update(job, ScheduledRecordingStatus.Failed, "Scheduled stop failed: " + AppLogger.SanitizeText(exception.Message)); }
            _stops.Remove(job.Id);
            return;
        }
        var id = job.RecordingId!;
        var stopTask = _recordings.StopAsync(id, RecordingStopReason.RequestedEndUtc, cancellationToken);
        _stops[job.Id] = (id, stopTask);
        try { MapOutcome(job, await stopTask.ConfigureAwait(false)); }
        catch (Exception exception) { Update(job, ScheduledRecordingStatus.Failed, "Scheduled stop failed: " + AppLogger.SanitizeText(exception.Message)); }
        finally { _stops.Remove(job.Id); }
    }

    private void OnCaptureFinished(RecordingOutcome outcome)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ = HandleCaptureFinishedAsync(outcome);
    }

    private async Task HandleCaptureFinishedAsync(RecordingOutcome outcome)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                var job = Jobs.FirstOrDefault(item => string.Equals(item.RecordingId, outcome.RecordingId, StringComparison.Ordinal));
                if (job is not null)
                {
                    MapOutcome(job, outcome);
                    ReclassifyConflicts();
                }
            }
            finally { _gate.Release(); }
        }
        catch (ObjectDisposedException) { }
    }

    private void MapOutcome(ScheduledRecordingJob job, RecordingOutcome? outcome)
    {
        if (outcome is null || job.Status == ScheduledRecordingStatus.Cancelled) return;
        var requestedEnd = outcome.Entry?.StopReason == RecordingStopReason.RequestedEndUtc;
        var status = outcome.Result switch
        {
            Core.RecordingOutcome.Completed when requestedEnd => ScheduledRecordingStatus.Completed,
            Core.RecordingOutcome.Stopped when requestedEnd => ScheduledRecordingStatus.Completed,
            _ => ScheduledRecordingStatus.Failed
        };
        var reason = status == ScheduledRecordingStatus.Completed ? string.Empty : outcome.Result switch
        {
            Core.RecordingOutcome.DiskFull => "Recording stopped because the destination ran out of space.",
            Core.RecordingOutcome.Discarded => string.IsNullOrWhiteSpace(outcome.FailureReason)
                ? "The shared stream ended before usable media was written." : AppLogger.SanitizeText(outcome.FailureReason),
            Core.RecordingOutcome.Failed => string.IsNullOrWhiteSpace(outcome.FailureReason)
                ? "The shared-stream capture failed." : AppLogger.SanitizeText(outcome.FailureReason),
            Core.RecordingOutcome.Stopped => "The capture ended before its requested window (for example, playback stopped or the provider ended the stream).",
            _ => "The capture ended without reaching its requested end time."
        };
        Update(job, status, reason);
        ReclassifyConflicts();
    }

    private void ReclassifyConflicts()
    {
        var prior = Jobs.ToDictionary(job => job.Id, StringComparer.OrdinalIgnoreCase);
        var classified = ScheduledRecordingPolicy.ClassifyConflicts(_index.Jobs);
        foreach (var job in classified)
        {
            if (prior.TryGetValue(job.Id, out var old) && old.Status == ScheduledRecordingStatus.Conflict &&
                !string.Equals(old.Reason, OverlapConflictReason, StringComparison.Ordinal) &&
                !string.Equals(old.Reason, StreamUnavailableReason, StringComparison.Ordinal))
                continue;
            Replace(job);
        }
    }

    private ScheduledRecordingJob? Find(string id) => Jobs.FirstOrDefault(job => string.Equals(job.Id, id, StringComparison.OrdinalIgnoreCase));
    private bool IsOwned(ScheduledRecordingJob job) => string.Equals(job.AccountId, _accountId, StringComparison.OrdinalIgnoreCase);

    private void Update(ScheduledRecordingJob job, ScheduledRecordingStatus status, string reason)
    {
        var safeReason = AppLogger.SanitizeText(reason);
        if (job.Status == status && string.Equals(job.Reason, safeReason, StringComparison.Ordinal)) return;
        Replace(job with { Status = status, Reason = safeReason });
        PersistAndNotify();
    }

    private void Replace(ScheduledRecordingJob job)
    {
        var position = _index.Jobs.FindIndex(item => string.Equals(item.AccountId, job.AccountId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Id, job.Id, StringComparison.OrdinalIgnoreCase));
        if (position < 0) return;
        _index.Jobs[position] = job;
    }

    private void PersistAndNotify()
    {
        _index.Normalize();
        _store.SaveScheduledRecordingIndex(_accountId, _index);
        if (Volatile.Read(ref _disposed) != 0) return;
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            if (Volatile.Read(ref _disposed) != 0) break;
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { AppLogger.Error("Scheduled recording: Changed event handler failed.", exception); }
        }
    }

    private static bool ValidDestination(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        !path.Contains("://", StringComparison.Ordinal) && !path.Contains('?', StringComparison.Ordinal) &&
        !path.Contains('#') && !path.Contains("token", StringComparison.OrdinalIgnoreCase) &&
        !path.Contains("password", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _recordings.CaptureFinished -= OnCaptureFinished;
        foreach (var cancellation in _startCancellation.Values)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        // Do not dispose the semaphore here: an in-flight start/callback may still
        // need to reacquire it to observe disposal and safely finish its cleanup.
    }
}
