using ClassicWindowsIptvPlayer.Core;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreOutcome = ClassicWindowsIptvPlayer.Core.RecordingOutcome;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>Lifecycle of a single capture. A capture never returns to <see cref="Starting"/>.</summary>
public enum RecordingCaptureState
{
    Idle = 0,
    Starting,
    Recording,
    Stopping,
    Failed
}

/// <summary>
/// One capture request. Sources are provided either by the already-playing tuner's
/// output or by an attached shared HLS consumer.
/// </summary>
public sealed record RecordingStartRequest
{
    public string StreamUrl { get; init; } = string.Empty;
    /// <summary>Existing live HLS source consumer used by shared-source capture.</summary>
    public SharedHlsConsumer? SharedHlsConsumer { get; init; }

    /// <summary>Absolute path of the file to write. The extension is corrected to the container the capture actually uses.</summary>
    public string DestinationPath { get; init; } = string.Empty;

    public string AccountId { get; init; } = string.Empty;
    public string ChannelId { get; init; } = string.Empty;
    public string ChannelName { get; init; } = string.Empty;

    /// <summary>Stable provider-independent channel key, used by the index for per-account name collisions.</summary>
    public string ChannelKey { get; init; } = string.Empty;

    public AccountProfile? Profile { get; init; }
    public EpgProgramme? Programme { get; init; }

    /// <summary>Requested capture length. Zero means open-ended: it then ends only on a stop request or an error.</summary>
    public TimeSpan Duration { get; init; }

    public RecordingContainerKind Container { get; init; } = RecordingContainerKind.Ts;
    public StreamLeaseKind LeaseKind { get; init; } = StreamLeaseKind.InstantRecording;
    public bool ConfirmUnknownConnection { get; init; }
    public int BufferMs { get; init; } = 1000;
    public DateTimeOffset RequestedStartUtc { get; init; }
    public bool HasRequestedEnd => Duration > TimeSpan.Zero;

    public override string ToString() =>
        $"RecordingStartRequest {{ AccountId={AccountId}, Channel={ChannelName}, Container={Container}, Duration={Duration}, SharedHls={SharedHlsConsumer is not null} }}";
}

/// <summary>What the host renders for one capture. It carries no source URL, so it is safe to bind to a control.</summary>
public sealed record RecordingStatusSnapshot(
    string RecordingId,
    RecordingCaptureState State,
    string AccountId,
    string ChannelId,
    string ChannelName,
    string FilePath,
    TimeSpan Elapsed,
    long ByteSize,
    string Note)
{
    public string SizeText => RecordingPolicy.FormatBytes(ByteSize);
    public bool IsLive => State is RecordingCaptureState.Starting or RecordingCaptureState.Recording or RecordingCaptureState.Stopping;
    public string StateText => State switch
    {
        RecordingCaptureState.Starting => "Starting",
        RecordingCaptureState.Recording => "Recording",
        RecordingCaptureState.Stopping => "Stopping",
        RecordingCaptureState.Failed => "Failed",
        _ => "Idle"
    };
}

/// <summary>
/// How a capture ended, plus the index row for it. A refusal never opened a
/// connection, so it carries no <see cref="Entry"/>; anything that reached the disk
/// does, already finished through <see cref="RecordingIndex.Finish"/>.
/// </summary>
public sealed record RecordingOutcome
{
    public string RecordingId { get; init; } = string.Empty;

    /// <summary>True once the capture passed validation and was allowed to open its connection.</summary>
    public bool Accepted { get; init; }

    public RecordingEntry? Entry { get; init; }
    public CoreOutcome Result { get; init; } = CoreOutcome.Failed;
    public RecordingFailure Failure { get; init; } = RecordingFailure.None;
    public string FailureReason { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string FileName => string.IsNullOrEmpty(FilePath) ? string.Empty : Path.GetFileName(FilePath);
    public RecordingContainerKind Container { get; init; }
    public long ByteSize { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Succeeded => Accepted && Failure == RecordingFailure.None && ByteSize > 0 &&
        Result is CoreOutcome.Completed or CoreOutcome.Stopped;
    public bool HasPlayableFile => !string.IsNullOrEmpty(FilePath) && ByteSize > 0;
}

/// <summary>
/// Owns live capture. Captures use the tuner's current LibVLC input or may attach to an
/// existing shared HLS source.
/// <para>
/// This build carries no H.264/HEVC/VP8/VP9/AV1/AAC/Vorbis/Opus/FLAC encoder, so a
/// capture is pass-through remux and nothing else. There is no transcode path here and
/// none may be added: the muxer is chosen from the codecs the source actually carries,
/// and the file is verified afterwards because a muxer silently drops what it cannot
/// carry and still reports success.
/// </para>
/// </summary>
public sealed class RecordingService : IDisposable
{
    public const int MaxConcurrentCeiling = 4;
    public const int RecentSnapshotLimit = 32;

    private static readonly TimeSpan SourceProbeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetunedOutputStartTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan OutputInspectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DiskRecheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HlsRefreshFloor = TimeSpan.FromMilliseconds(500);
    private const int MaximumHlsPlaylistCharacters = 1_048_576;
    private static readonly HttpClient CaptureHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    // The muxer keeps appending for about a second after Stop() returns, so the size is
    // only final once the output has held still for a moment beyond that floor.
    private static readonly TimeSpan MuxerFlushFloor = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan MuxerFlushCap = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MuxerFlushPoll = TimeSpan.FromMilliseconds(400);

    // The stream-output value is a small option language: these characters are chain
    // syntax, so a destination containing one cannot be addressed at all.
    private static readonly char[] StreamOutputReserved = [',', '{', '}', '\'', '"', '\r', '\n', '\t'];

    // What MPEG-TS can carry: exactly the shapes an IPTV live stream is already in. Any
    // other codec goes to Matroska, which is a superset for everything IPTV carries.
    private static readonly HashSet<string> TsCarriedCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "MPGV", "MP1V", "MP2V", "MP4V", "H264", "AVC1", "AVC3", "HEVC", "HEV1", "HVC1",
        "MP2A", "MP3", "MPGA", "MP4A", "AAC", "AC3", "EAC3"
    };

    private readonly RecordingIndex _index;
    private readonly object _gate = new();
    private readonly List<CaptureSession> _sessions = [];
    private readonly List<RecordingStatusSnapshot> _recent = [];
    // Emergency stop path retains native captures rather than invoking LibVLC teardown
    // while its stream-output pipeline is active. Process shutdown reclaims the handles.
    private readonly SemaphoreSlim _slots;
    private readonly int _maxConcurrent;
    /// <summary>Optional volume facts seam for deterministic tests; production uses RecordingPolicy's DriveInfo probe.</summary>
    public Func<string, VolumeSpace>? FreeSpaceProbe { get; set; }
    private int _disposed;
    private int _active;

    /// <summary>Raised on background threads as a capture changes state or grows. The host marshals to its UI thread.</summary>
    public event Action<RecordingStatusSnapshot>? StateChanged;

    /// <summary>Raised once per capture that reached the disk, after the file is flushed, verified and the lease released.</summary>
    public event Action<RecordingOutcome>? CaptureFinished;

    public RecordingService(ConnectionBudget budget, RecordingIndex index, int maxConcurrentCaptures = 2)
    {
        ArgumentNullException.ThrowIfNull(budget);
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _maxConcurrent = Math.Clamp(maxConcurrentCaptures, 1, MaxConcurrentCeiling);
        _slots = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);
    }

    public int MaxConcurrentCaptures => _maxConcurrent;
    public int ActiveCount => Volatile.Read(ref _active);

    /// <summary>True when every capture slot is taken. A host uses this to disable its record button.</summary>
    public bool IsAtCapacity => ActiveCount >= _maxConcurrent;

    /// <summary>Live captures, in the order they started.</summary>
    public IReadOnlyList<RecordingStatusSnapshot> ActiveCaptures
    {
        get
        {
            lock (_gate) return [.. _sessions.Select(session => session.Snapshot())];
        }
    }

    /// <summary>Starts capture from the tuner-owned shared HLS source.</summary>
    private async Task<RecordingOutcome> StartSharedHlsCoreAsync(RecordingStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        async Task<RecordingOutcome> RefuseAsync(string id, RecordingFailure failure, string message)
        {
            if (request.SharedHlsConsumer is not null)
                await request.SharedHlsConsumer.DisposeAsync().ConfigureAwait(false);
            return Refused(id, failure, message);
        }
        if (Volatile.Read(ref _disposed) != 0)
            return await RefuseAsync(string.Empty, RecordingFailure.NoDestination, "The recording service is shutting down.").ConfigureAwait(false);

        var requestedStart = request.RequestedStartUtc == default ? DateTimeOffset.UtcNow : request.RequestedStartUtc;

        // Nothing opens a connection before the destination and the drive are cleared.
        var fullPath = ResolvePath(request.DestinationPath);
        if (fullPath is null)
            return await RefuseAsync(string.Empty, RecordingFailure.NoDestination, "Choose where the recording should be saved.").ConfigureAwait(false);
        var freeSpaceProbe = FreeSpaceProbe;
        var destination = RecordingPolicy.ValidateDestination(Path.GetDirectoryName(fullPath));
        if (destination.IsValid && freeSpaceProbe is not null)
        {
            VolumeSpace measured;
            try { measured = freeSpaceProbe(destination.Folder); }
            catch { measured = VolumeSpace.Unknown; }
            destination = destination with { Space = measured ?? VolumeSpace.Unknown };
        }
        if (!destination.IsValid) return await RefuseAsync(string.Empty, destination.Failure, destination.Message).ConfigureAwait(false);
        var space = RecordingPolicy.CheckFreeSpace(destination, request.Duration);
        if (space.Status == RecordingSpaceStatus.Low) return await RefuseAsync(string.Empty, RecordingFailure.LowDiskSpace, space.Message).ConfigureAwait(false);
        if (StreamOutputReserved.Any(request.DestinationPath.Contains))
            return await RefuseAsync(string.Empty, RecordingFailure.DestinationNotWritable,
                "That recording file name contains characters a capture cannot write to. Choose another name.").ConfigureAwait(false);

        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (request.SharedHlsConsumer is not null)
                await request.SharedHlsConsumer.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var session = new CaptureSession(this, "rec-" + Guid.NewGuid().ToString("N"), request, fullPath,
            destination.Folder, space.RequiredBytes, requestedStart);
        var serviceStopping = false;
        lock (_gate)
        {
            if (_disposed != 0)
            {
                _slots.Release();
                serviceStopping = true;
            }
            else _sessions.Add(session);
        }
        if (serviceStopping)
            return await RefuseAsync(session.Id, RecordingFailure.NoDestination, "The recording service is shutting down.").ConfigureAwait(false);

        Interlocked.Increment(ref _active);
        AppLogger.Info("Capture: shared-HLS start requested. id=" + session.Id + "; account=" + request.AccountId +
            "; channel=" + request.ChannelName);

        _ = Task.Run(() => RunSharedHlsAsync(session, space.Message, freeSpaceProbe));
        try
        {
            return await session.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller token governs startup only. If the capture is still waiting
            // for its first shared-HLS segment, detach it now and wait for finalization
            // before returning so cancellation cannot strand a consumer or output file.
            session.RequestStop(RecordingStopReason.User);
            await session.Finished.Task.ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts a TS or Matroska recording from the tuner's already-owned LibVLC player output.
    /// This method never opens a media, player, URL, or provider connection. The
    /// callbacks attach and detach the stream-output chain on the tuner-owned player.
    /// </summary>
    public async Task<RecordingOutcome> StartRetunedAsync(RecordingStartRequest request,
        Func<string, CancellationToken, Task<bool>> startOutput,
        Func<CancellationToken, Task<bool>> stopOutput,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(startOutput);
        ArgumentNullException.ThrowIfNull(stopOutput);

        RecordingOutcome Refuse(RecordingFailure failure, string message) =>
            Refused(string.Empty, failure, message);

        if (Volatile.Read(ref _disposed) != 0)
            return Refuse(RecordingFailure.NoDestination, "The recording service is shutting down.");
        if (request.SharedHlsConsumer is not null)
            return Refuse(RecordingFailure.UnsupportedSource, "A retuned output capture cannot own a shared HLS consumer.");
        if (string.IsNullOrWhiteSpace(request.AccountId))
            return Refuse(RecordingFailure.NoAccount, "Choose an account before starting a recording.");

        var requestedStart = request.RequestedStartUtc == default ? DateTimeOffset.UtcNow : request.RequestedStartUtc;
        var fullPath = ResolvePath(request.DestinationPath);
        if (fullPath is null)
            return Refuse(RecordingFailure.NoDestination, "Choose where the recording should be saved.");
        if (StreamOutputReserved.Any(fullPath.Contains))
            return Refuse(RecordingFailure.DestinationNotWritable,
                "That recording file name contains characters the tuner output cannot write to. Choose another name.");

        var freeSpaceProbe = FreeSpaceProbe;
        var destination = RecordingPolicy.ValidateDestination(Path.GetDirectoryName(fullPath));
        if (destination.IsValid && freeSpaceProbe is not null)
        {
            VolumeSpace measured;
            try { measured = freeSpaceProbe(destination.Folder); }
            catch { measured = VolumeSpace.Unknown; }
            destination = destination with { Space = measured ?? VolumeSpace.Unknown };
        }
        if (!destination.IsValid) return Refuse(destination.Failure, destination.Message);
        var space = RecordingPolicy.CheckFreeSpace(destination, request.Duration);
        if (space.Status == RecordingSpaceStatus.Low)
            return Refuse(RecordingFailure.LowDiskSpace, space.Message);

        try { await _slots.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }

        // Claim a collision-free TS name atomically. Remove the placeholder before
        // returning it to the tuner; FileMode.CreateNew ensures we never overwrite an
        // existing recording while choosing a candidate.
        var container = request.Container == RecordingContainerKind.Mkv ? RecordingContainerKind.Mkv : RecordingContainerKind.Ts;
        var chosen = Path.ChangeExtension(fullPath, RecordingPolicy.Extension(container));
        var reserved = false;
        try
        {
            var stem = Path.Combine(Path.GetDirectoryName(chosen)!, Path.GetFileNameWithoutExtension(chosen));
            var extension = Path.GetExtension(chosen);
            for (var suffix = 0; suffix < 10_000; suffix++)
            {
                var candidate = suffix == 0 ? chosen : $"{stem} ({suffix + 1}){extension}";
                try
                {
                    using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1)) { }
                    chosen = candidate;
                    File.Delete(chosen);
                    reserved = true;
                    break;
                }
                catch (IOException) when (suffix < 9_999)
                {
                    // Existing/inaccessible name: try the next deterministic suffix.
                }
            }
            if (!reserved)
            {
                _slots.Release();
                return Refuse(RecordingFailure.NameCollision, "A unique recording file name could not be reserved.");
            }
        }
        catch (Exception exception)
        {
            _slots.Release();
            return Refuse(RecordingFailure.DestinationNotWritable,
                "The recording file could not be reserved: " + AppLogger.SanitizeText(exception.Message));
        }

        var sessionRequest = request with
        {
            StreamUrl = string.Empty,
            SharedHlsConsumer = null,
            Container = container
        };
        var session = new CaptureSession(this, "rec-" + Guid.NewGuid().ToString("N"), sessionRequest,
            chosen, destination.Folder, space.RequiredBytes, requestedStart);
        session.ConfigureRetunedOutput(startOutput, stopOutput);
        var serviceStopping = false;
        lock (_gate)
        {
            if (_disposed != 0)
            {
                _slots.Release();
                serviceStopping = true;
            }
            else _sessions.Add(session);
        }
        if (serviceStopping)
        {
            DeletePartial(chosen);
            return Refuse(RecordingFailure.NoDestination, "The recording service is shutting down.");
        }

        Interlocked.Increment(ref _active);
        AppLogger.Info("Capture: tuner-owned output start requested. id=" + session.Id + "; channel=" + request.ChannelName);
        _ = Task.Run(() => RunRetunedAsync(session, freeSpaceProbe));
        try
        {
            return await session.Ready.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            session.RequestStop(RecordingStopReason.User);
            await session.Finished.Task.ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunRetunedAsync(CaptureSession session, Func<string, VolumeSpace>? freeSpaceProbe)
    {
        var verdict = new Verdict { Note = "Recording from the tuner-owned playback output." };
        try
        {
            await DriveRetunedAsync(session, verdict, freeSpaceProbe).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Capture: tuner-owned output session failed. id=" + session.Id, exception);
            Fail(verdict, "The tuner output recording failed: " + AppLogger.SanitizeText(exception.Message));
        }
        try { await FinalizeAsync(session, verdict).ConfigureAwait(false); }
        catch (Exception exception)
        {
            AppLogger.Error("Capture: tuner-owned output finalization failed. id=" + session.Id, exception);
            Fail(verdict, "The recording could not be finalized: " + AppLogger.SanitizeText(exception.Message));
        }

        var outcome = session.Complete(verdict);
        lock (_gate)
        {
            _sessions.Remove(session);
            _recent.Insert(0, session.Snapshot());
            if (_recent.Count > RecentSnapshotLimit) _recent.RemoveRange(RecentSnapshotLimit, _recent.Count - RecentSnapshotLimit);
        }
        _slots.Release();
        Interlocked.Decrement(ref _active);
        try { CaptureFinished?.Invoke(outcome); }
        catch (Exception exception) { AppLogger.Error("Capture: CaptureFinished handler failed. id=" + session.Id, exception); }
    }

    private async Task DriveRetunedAsync(CaptureSession session, Verdict verdict,
        Func<string, VolumeSpace>? freeSpaceProbe)
    {
        session.SetState(RecordingCaptureState.Starting);
        session.Raise();
        session.FilePath = session.RequestedPath;
        session.FileName = Path.GetFileName(session.FilePath);
        session.Container = session.Request.Container;
        session.Muxer = session.Container == RecordingContainerKind.Mkv ? "mkv" : "ts";
        verdict.FilePath = session.FilePath;

        var startedOutput = false;
        try
        {
            session.Token.ThrowIfCancellationRequested();
            var startTask = session.StartRetunedOutputAsync(session.FilePath, session.Token);
            var startWinner = await Task.WhenAny(startTask, Task.Delay(RetunedOutputStartTimeout, session.Token)).ConfigureAwait(false);
            if (startWinner != startTask)
                throw new TimeoutException("The tuner did not confirm playback start before the recording timeout.");
            startedOutput = await startTask.ConfigureAwait(false);
            if (!startedOutput)
                throw new IOException("The tuner did not start playback with recording output enabled.");

            var deadline = DateTime.UtcNow + RetunedOutputStartTimeout;
            while (SafeLength(session.FilePath) <= 0 && DateTime.UtcNow < deadline)
            {
                session.Token.ThrowIfCancellationRequested();
                await Task.Delay(100, session.Token).ConfigureAwait(false);
            }
            if (SafeLength(session.FilePath) <= 0)
                throw new TimeoutException("The tuner started playback but the recording output file received no data.");
            if (session.StopReason != RecordingStopReason.None)
                throw new OperationCanceledException("Recording was stopped while its output was starting.");

            if (!session.TryMarkRetunedOutputPayloadReady(DateTimeOffset.UtcNow))
                throw new OperationCanceledException("Recording stopped before the tuner output was committed.", session.Token);
            verdict.Started = true;
            verdict.Result = CoreOutcome.Recording;
            verdict.Reason = RecordingStopReason.None;
            verdict.Failure = RecordingFailure.None;
            verdict.Note = "Recording the active tuner playback output to " + session.Muxer.ToUpperInvariant() + ".";
            session.Note = verdict.Note;
            session.SetState(RecordingCaptureState.Recording);
            session.BeginRow();
            session.Raise();
            session.Ready.TrySetResult(session.StartedOutcome());
            await MonitorAsync(session, verdict, freeSpaceProbe).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.CancellationRequested || session.StopReason != RecordingStopReason.None)
        {
            if (verdict.Started)
                ApplyStopReason(verdict, session.StopReason == RecordingStopReason.None ? RecordingStopReason.User : session.StopReason);
            else
                Fail(verdict, "Recording was stopped before the tuner output produced data.");
        }
        catch (Exception exception)
        {
            Fail(verdict, exception is TimeoutException ? RecordingFailure.UnsupportedSource : RecordingFailure.DestinationNotWritable,
                AppLogger.SanitizeText(exception.Message));
        }
        finally
        {
            if (startedOutput || session.RetunedOutputMayBeActive)
            {
                var stopOk = await session.StopRetunedOutputOnceAsync(CancellationToken.None).ConfigureAwait(false);
                if (!stopOk && verdict.Started)
                    Fail(verdict, RecordingFailure.UnsupportedSource, "The tuner could not confirm that recording output was stopped and flushed.");
            }
            verdict.ByteSize = SafeLength(session.FilePath);
            session.SetByteSize(verdict.ByteSize);
            verdict.Elapsed = session.RecordedDuration;
            if (!verdict.Started)
            {
                verdict.FilePath = string.Empty;
                verdict.ByteSize = 0;
                session.ForgetFile();
                DeletePartial(session.FilePath);
            }
        }
    }

    /// <summary>Starts recording from the tuner-owned HLS consumer without opening a second stream URL.</summary>
    public Task<RecordingOutcome> StartSharedHlsAsync(RecordingStartRequest request, SharedHlsConsumer consumer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(consumer);
        if (string.IsNullOrWhiteSpace(request.AccountId))
            return RefuseSharedConsumerAsync(consumer, RecordingFailure.NoAccount,
                "Choose an account before starting a shared-stream recording.");
        if (consumer.Kind != SharedHlsConsumerKind.Recording)
            return RefuseSharedConsumerAsync(consumer, RecordingFailure.UnsupportedSource,
                "The attached shared-stream consumer is not a recording consumer.");
        return StartSharedHlsCoreAsync(request with { StreamUrl = string.Empty, SharedHlsConsumer = consumer }, cancellationToken);
    }

    private static async Task<RecordingOutcome> RefuseSharedConsumerAsync(SharedHlsConsumer consumer,
        RecordingFailure failure, string message)
    {
        await consumer.DisposeAsync().ConfigureAwait(false);
        return Refused(string.Empty, failure, message);
    }

    /// <summary>
    /// Ends one capture and waits for its file to be flushed and verified. The reason
    /// decides how the capture is recorded; a shutdown abort uses the same path, so both
    /// a graceful stop and a hard abort leave a playable file behind.
    /// </summary>
    public async Task<RecordingOutcome?> StopAsync(string recordingId, RecordingStopReason reason, CancellationToken cancellationToken = default)
    {
        var session = FindSession(recordingId);
        if (session is null) return null;
        session.RequestStop(reason);
        return await session.Finished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ends every live capture and waits for all of them to be flushed and verified.</summary>
    public async Task<IReadOnlyList<RecordingOutcome>> StopAllAsync(RecordingStopReason reason, CancellationToken cancellationToken = default)
    {
        var sessions = LiveSessions();
        foreach (var session in sessions) session.RequestStop(reason);
        var results = new List<RecordingOutcome>(sessions.Length);
        foreach (var session in sessions)
        {
            try { results.Add(await session.Finished.Task.WaitAsync(cancellationToken).ConfigureAwait(false)); }
            catch (OperationCanceledException) { throw; }
            catch { /* One stubborn capture must not strand the rest of a shutdown. */ }
        }
        return results;
    }

    /// <summary>The live snapshot for a capture, or the last one it published after it ended.</summary>
    public RecordingStatusSnapshot? Find(string recordingId)
    {
        if (string.IsNullOrEmpty(recordingId)) return null;
        lock (_gate)
        {
            var live = _sessions.FirstOrDefault(session => string.Equals(session.Id, recordingId, StringComparison.Ordinal));
            if (live is not null) return live.Snapshot();
            return _recent.FirstOrDefault(snapshot => string.Equals(snapshot.RecordingId, recordingId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The container a capture will actually write for these source codecs. MPEG-TS is
    /// the default because an IPTV live stream is already shaped like it and it is the
    /// only one of the two that stays playable while it is being written. Matroska is
    /// chosen only for a codec TS cannot carry (Opus, FLAC, Vorbis, AV1 and the like).
    /// MP4 is never chosen: its moov atom is written at close, so a capture that is
    /// killed leaves a file with no tracks at all.
    /// </summary>
    public static RecordingContainerKind ChooseContainer(IReadOnlyList<string>? videoCodecs, IReadOnlyList<string>? audioCodecs)
    {
        var video = videoCodecs ?? [];
        var audio = audioCodecs ?? [];
        if (video.Count == 0 && audio.Count == 0) return RecordingContainerKind.Ts;
        return video.Concat(audio).All(TsCarriedCodecs.Contains) ? RecordingContainerKind.Ts : RecordingContainerKind.Mkv;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var sessions = LiveSessions();
        foreach (var session in sessions) session.RequestStop(RecordingStopReason.Shutdown);
        try { Task.WaitAll([.. sessions.Select(session => session.Finished.Task)], TimeSpan.FromSeconds(12)); }
        catch { /* A capture that ignored the stop is cancelled below so its slot is still freed. */ }
        foreach (var session in sessions) session.Cancel();
        try { Task.WaitAll([.. sessions.Select(session => session.Finished.Task)], TimeSpan.FromSeconds(5)); }
        catch { /* Shutdown continues; every failure is already logged. */ }
        // The semaphore is deliberately left undisposed: a shared-source start already waiting for
        // a slot would surface ObjectDisposedException out of the caller's await instead
        // of a plain refusal, and the object is negligible.
    }

    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var text = path.Trim();
            if (!Path.IsPathRooted(text)) return null;
            var full = Path.GetFullPath(text);
            return string.IsNullOrEmpty(Path.GetExtension(full)) ? null : full;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string MuxerName(RecordingContainerKind container) => container switch
    {
        RecordingContainerKind.Ts => "ts",
        RecordingContainerKind.Mkv => "mkv",
        RecordingContainerKind.Mp4 => "mp4",
        _ => "ts"
    };

    // The only capture form this LibVLC build accepts is a MEDIA option whose value
    // starts with '#', naming the file access output module and the muxer explicitly.
    // ':sout=file{...}' and a sout without the leading '#' are both read as the legacy
    // 'standard' muxer and write nothing, a '--sout' constructor argument is silently
    // ignored, and a path inside the chain must use forward slashes. 'no-overwrite'
    // belongs to the file access module, so it nests inside access=file{}: the muxer
    // then refuses a name that is taken instead of truncating an existing recording.
    private static string BuildStreamOutput(RecordingContainerKind container, string path) =>
        $":sout=#std{{access=file{{no-overwrite}},mux={MuxerName(container)},dst={path.Replace('\\', '/')}}}";

    private static RecordingOutcome Refused(string recordingId, RecordingFailure failure, string message) => new()
    {
        RecordingId = recordingId, Accepted = false, Result = CoreOutcome.Failed,
        Failure = failure, FailureReason = AppLogger.SanitizeText(message)
    };

    private CaptureSession[] LiveSessions()
    {
        lock (_gate) return [.. _sessions];
    }

    private CaptureSession? FindSession(string? recordingId)
    {
        if (string.IsNullOrEmpty(recordingId)) return null;
        lock (_gate) return _sessions.FirstOrDefault(session => string.Equals(session.Id, recordingId, StringComparison.Ordinal));
    }

    private async Task RunSharedHlsAsync(CaptureSession session, string spaceNote,
        Func<string, VolumeSpace>? freeSpaceProbe)
    {
        var verdict = new Verdict { Note = spaceNote };
        try
        {
            if (session.Request.SharedHlsConsumer is { } consumer)
                await DriveSharedHlsAsync(session, verdict, consumer, freeSpaceProbe).ConfigureAwait(false);
            else
            Fail(verdict, RecordingFailure.UnsupportedSource, "The shared HLS source is unavailable.");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Capture: session crashed. id=" + session.Id + "; channel=" + session.Request.ChannelName, exception);
            Fail(verdict, "The capture ended with an internal error: " + AppLogger.SanitizeText(exception.Message));
        }
        try { await FinalizeAsync(session, verdict).ConfigureAwait(false); }
        catch (Exception exception)
        {
            AppLogger.Error("Capture: finalization crashed. id=" + session.Id, exception);
            Fail(verdict, "The recording could not be finalized: " + AppLogger.SanitizeText(exception.Message));
        }

        var outcome = session.Complete(verdict);
        lock (_gate)
        {
            _sessions.Remove(session);
            _recent.Insert(0, session.Snapshot());
            if (_recent.Count > RecentSnapshotLimit) _recent.RemoveRange(RecentSnapshotLimit, _recent.Count - RecentSnapshotLimit);
        }
        _slots.Release();
        Interlocked.Decrement(ref _active);

        AppLogger.Info("Capture: finished. id=" + session.Id + "; channel=" + session.Request.ChannelName +
            "; container=" + session.Muxer + "; result=" + outcome.Result + "; failure=" + outcome.Failure +
            "; bytes=" + outcome.ByteSize.ToString(CultureInfo.InvariantCulture));
        try { CaptureFinished?.Invoke(outcome); }
        catch (Exception exception) { AppLogger.Error("Capture: CaptureFinished handler failed. id=" + session.Id, exception); }
    }

    /// <summary>
    /// Copies complete transport-stream segments from the tuner's existing HLS
    /// session. Network fetching is owned by SharedHlsSource; this task only performs
    /// bounded-queue reads and sequential disk writes, so it cannot stall playback.
    /// </summary>
    private async Task DriveSharedHlsAsync(CaptureSession session, Verdict verdict, SharedHlsConsumer consumer,
        Func<string, VolumeSpace>? freeSpaceProbe)
    {
        var chosen = Path.ChangeExtension(session.RequestedPath, RecordingPolicy.Extension(RecordingContainerKind.Ts));
        if (File.Exists(chosen))
        {
            Fail(verdict, RecordingFailure.NameCollision, "A file with that name is already there, so this capture did not start.");
            await consumer.DisposeAsync().ConfigureAwait(false);
            return;
        }

        FileStream? output = null;
        var diskCheckAt = Environment.TickCount64 + (long)DiskRecheckInterval.TotalMilliseconds;
        try
        {
            output = new FileStream(chosen, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            session.MarkManagedHttp();
            session.AttachHttpOutput(output);
            output = null;
            session.FilePath = chosen;
            session.FileName = Path.GetFileName(chosen);
            session.Container = RecordingContainerKind.Ts;
            session.Muxer = "ts";
            verdict.FilePath = chosen;
            verdict.Note = "Recording segments from the active shared HLS playback source; file boundaries align to whole HLS segments.";
            session.Note = verdict.Note;
            while (true)
            {
                session.Token.ThrowIfCancellationRequested();
                var segment = await consumer.ReadAsync(session.Token).ConfigureAwait(false);
                if (segment is null)
                {
                    verdict.Result = CoreOutcome.Completed;
                    verdict.Reason = RecordingStopReason.ProviderEnded;
                    break;
                }
                // ReadAsync can return an already queued item even as Stop is pressed.
                // Check again before writing so no later queued segment crosses Stop.
                session.Token.ThrowIfCancellationRequested();
                if (segment.ProgramDateTimeUtc is DateTimeOffset mediaStart && mediaStart < session.RequestedStartUtc)
                    continue;

                var stream = session.HttpOutput ?? throw new IOException("The recording file is no longer writable.");
                await stream.WriteAsync(segment.Data, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                session.SetByteSize(stream.Length);
                if (!verdict.Started && stream.Length > 0)
                {
                    // Serialize the first-payload transition with Stop. If Stop won
                    // while the first segment was being written, this attempt is still
                    // a failed start; finalization removes even a partial payload.
                    if (!session.TryMarkSharedHlsPayloadReady(session.RequestedStartUtc))
                        throw new InvalidOperationException("Recording was stopped before its first shared HLS payload.");

                    verdict.Started = true;
                    verdict.Result = CoreOutcome.Recording;
                    verdict.Failure = RecordingFailure.None;
                    verdict.Reason = RecordingStopReason.None;
                    session.SetState(RecordingCaptureState.Recording);
                    session.BeginRow();
                    session.SetEntryStarted(session.RequestedStartUtc);
                    session.Note = verdict.Note;
                    session.Raise();
                    session.Ready.TrySetResult(session.StartedOutcome());
                }
                session.Raise();

                if (Environment.TickCount64 >= diskCheckAt)
                {
                    diskCheckAt = Environment.TickCount64 + (long)DiskRecheckInterval.TotalMilliseconds;
                    if (!HasRoomLeft(session.DestinationFolder, freeSpaceProbe))
                    {
                        verdict.Result = CoreOutcome.DiskFull;
                        verdict.Reason = RecordingStopReason.DiskFull;
                        verdict.Failure = RecordingFailure.LowDiskSpace;
                        verdict.Note = "The recording drive filled up, so capture stopped at an HLS segment boundary.";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (session.CancellationRequested)
        {
            if (verdict.Started)
                ApplyStopReason(verdict, session.StopReason == RecordingStopReason.None ? RecordingStopReason.User : session.StopReason);
            else
                Fail(verdict, "Recording stopped before the first HLS segment arrived; no recording was created.");
        }
        catch (Exception exception)
        {
            if (verdict.Started)
            {
                verdict.Result = CoreOutcome.Failed;
                verdict.Reason = RecordingStopReason.Error;
                verdict.Failure = RecordingFailure.UnsupportedSource;
                verdict.Note = "Shared HLS recording failed: " + AppLogger.SanitizeText(exception.Message);
            }
            else Fail(verdict, "The shared HLS source could not start recording: " + AppLogger.SanitizeText(exception.Message));
        }
        finally
        {
            try { if (output is not null) await output.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await session.DisposeHttpAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                verdict.Result = CoreOutcome.Failed;
                verdict.Reason = RecordingStopReason.Error;
                verdict.Failure = RecordingFailure.DestinationNotWritable;
                verdict.Note = "The shared HLS recording file could not be flushed: " + AppLogger.SanitizeText(exception.Message);
            }
            try { await consumer.DisposeAsync().ConfigureAwait(false); } catch { }
            verdict.ByteSize = SafeLength(chosen);
            if (!verdict.Started)
            {
                // A cancelled/failing start is not a partial recording. In particular,
                // Stop may win after a first segment was written but before Ready was
                // published; remove that short file instead of leaving an orphan.
                try { if (File.Exists(chosen)) File.Delete(chosen); } catch { }
                verdict.FilePath = string.Empty;
                verdict.ByteSize = 0;
            }
            verdict.Elapsed = session.RecordedDuration;
            session.SetByteSize(verdict.ByteSize);
        }
    }

    private async Task DriveHlsAsync(CaptureSession session, Verdict verdict, Uri playlistUri)
    {
        var chosen = Path.ChangeExtension(session.RequestedPath, RecordingPolicy.Extension(session.Request.Container));
        if (File.Exists(chosen))
        {
            Fail(verdict, RecordingFailure.NameCollision, "A file with that name is already there, so this capture did not start.");
            return;
        }
        session.MarkManagedHttp();
        session.Container = session.Request.Container;
        session.Muxer = session.Container == RecordingContainerKind.Mkv ? "mkv" : "ts";
        session.FilePath = chosen;
        session.FileName = Path.GetFileName(chosen);
        verdict.FilePath = chosen;

        FileStream? output = null;
        var seenSegments = new HashSet<string>(StringComparer.Ordinal);
        var seenMaps = new HashSet<string>(StringComparer.Ordinal);
        long? firstCaptureSequence = null;
        long nextDiskCheck = Environment.TickCount64 + (long)DiskRecheckInterval.TotalMilliseconds;
        var deadline = DateTimeOffset.UtcNow + (session.Request.HasRequestedEnd ? session.Request.Duration : TimeSpan.FromMinutes(2));
        var startupDeadline = DateTimeOffset.UtcNow + OpenTimeout;
        var started = false;
        try
        {
            output = new FileStream(chosen, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            session.AttachHttpOutput(output);
            output = null;
            var currentPlaylist = playlistUri;
            while (true)
            {
                session.Token.ThrowIfCancellationRequested();
                var loaded = await DownloadHlsPlaylistAsync(currentPlaylist, session.Token).ConfigureAwait(false);
                currentPlaylist = loaded.FinalUri;
                if (HasUnsupportedHlsEncryption(loaded.Content))
                    throw new NotSupportedException("Encrypted HLS recording is not supported by the direct segment capture path.");

                var variant = SelectHlsVariant(loaded.Content, loaded.FinalUri);
                if (variant is not null)
                {
                    currentPlaylist = variant;
                    continue;
                }

                var media = ParseHlsMediaPlaylist(loaded.Content, loaded.FinalUri);
                if (media.HasByteRanges)
                    throw new NotSupportedException("HLS byte-range segments are not supported by the direct capture path.");

                if (firstCaptureSequence is null)
                {
                    // The first live playlist is only a baseline. Its complete current
                    // window is pre-roll; begin at the next media sequence advertised
                    // after recording was requested.
                    firstCaptureSequence = media.MediaSequence + media.Segments.Count;
                    started = true;
                    verdict.Started = true;
                    verdict.Result = CoreOutcome.Recording;
                    verdict.Reason = RecordingStopReason.None;
                    verdict.Failure = RecordingFailure.None;
                    verdict.Note = "Waiting at the live HLS edge for new media segments.";
                    session.Note = verdict.Note;
                    session.SetState(RecordingCaptureState.Recording);
                    session.BeginRow();
                    session.Raise();
                    session.Ready.TrySetResult(session.StartedOutcome());
                }

                foreach (var segment in media.Segments)
                {
                    session.Token.ThrowIfCancellationRequested();
                    if (segment.Sequence < firstCaptureSequence.Value) continue;
                    var identity = segment.DiscontinuitySequence.ToString(CultureInfo.InvariantCulture) + ":" +
                        segment.Sequence.ToString(CultureInfo.InvariantCulture) + ":" + segment.Uri.AbsoluteUri;
                    if (!seenSegments.Add(identity)) continue;

                    if (media.InitMap is not null && seenMaps.Add(media.InitMap.AbsoluteUri))
                        await AppendHttpResourceAsync(media.InitMap, session.HttpOutput!, session.Token).ConfigureAwait(false);
                    session.Token.ThrowIfCancellationRequested();
                    await AppendHttpResourceAsync(segment.Uri, session.HttpOutput!, session.Token).ConfigureAwait(false);
                    session.Token.ThrowIfCancellationRequested();
                    await session.HttpOutput!.FlushAsync(session.Token).ConfigureAwait(false);
                    if (!session.HasRecordedStart)
                    {
                        session.MarkStarted();
                        session.SetEntryStarted(DateTimeOffset.UtcNow);
                        verdict.Note = "Capturing new HLS media segments.";
                        session.Note = verdict.Note;
                    }
                    session.SetByteSize(session.HttpOutput.Length);
                    session.Raise();
                    if (session.Request.HasRequestedEnd && DateTimeOffset.UtcNow >= deadline)
                    {
                        verdict.Result = CoreOutcome.Completed;
                        verdict.Reason = RecordingStopReason.RequestedEndUtc;
                        return;
                    }
                }

                if (media.EndList)
                {
                    verdict.Result = CoreOutcome.Completed;
                    verdict.Reason = RecordingStopReason.ProviderEnded;
                    return;
                }
                if (!started && DateTimeOffset.UtcNow >= startupDeadline)
                    throw new TimeoutException("The HLS playlist did not provide a media segment before the startup deadline.");

                if (Environment.TickCount64 >= nextDiskCheck)
                {
                    nextDiskCheck = Environment.TickCount64 + (long)DiskRecheckInterval.TotalMilliseconds;
                    if (!HasRoomLeft(session.DestinationFolder))
                    {
                        verdict.Result = CoreOutcome.DiskFull;
                        verdict.Reason = RecordingStopReason.DiskFull;
                        verdict.Failure = RecordingFailure.LowDiskSpace;
                        verdict.Note = "The recording drive filled up, so the capture was stopped.";
                        return;
                    }
                }
                var refresh = TimeSpan.FromSeconds(Math.Clamp(media.TargetDurationSeconds / 2d, HlsRefreshFloor.TotalSeconds, 10d));
                await Task.Delay(refresh, session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (session.CancellationRequested)
        {
            if (started)
                ApplyStopReason(verdict, session.StopReason == RecordingStopReason.None ? RecordingStopReason.User : session.StopReason);
            else
                Fail(verdict, "The HLS capture was cancelled before its first segment arrived.");
        }
        catch (Exception exception)
        {
            if (started)
            {
                verdict.Result = CoreOutcome.Failed;
                verdict.Reason = RecordingStopReason.Error;
                verdict.Failure = RecordingFailure.UnsupportedSource;
                verdict.Note = "The HLS capture failed: " + AppLogger.SanitizeText(exception.Message);
            }
            else
                Fail(verdict, "The HLS stream did not start: " + AppLogger.SanitizeText(exception.Message));
        }
        finally
        {
            try { await session.DisposeHttpAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                verdict.Result = CoreOutcome.Failed;
                verdict.Reason = RecordingStopReason.Error;
                verdict.Failure = RecordingFailure.DestinationNotWritable;
                verdict.Note = "The HLS capture file could not be flushed: " + AppLogger.SanitizeText(exception.Message);
            }
            verdict.ByteSize = SafeLength(session.FilePath);
            session.SetByteSize(verdict.ByteSize);
            verdict.Elapsed = session.RecordedDuration;
        }
    }

    private static async Task<(Uri FinalUri, string Content)> DownloadHlsPlaylistAsync(Uri uri, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await CaptureHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (response.Content.Headers.ContentLength is long length && length > MaximumHlsPlaylistCharacters)
            throw new InvalidDataException("The HLS playlist exceeds the supported size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var content = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            content.Append(buffer, 0, read);
            if (content.Length > MaximumHlsPlaylistCharacters)
                throw new InvalidDataException("The HLS playlist exceeds the supported size limit.");
        }
        return (finalUri, content.ToString());
    }

    private static Uri? SelectHlsVariant(string content, Uri baseUri)
    {
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Uri? selected = null;
        long highestBandwidth = -1;
        long pendingBandwidth = -1;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingBandwidth = ParseAttributeLong(line, "BANDWIDTH");
                continue;
            }
            if (pendingBandwidth < 0 || line.StartsWith('#')) continue;
            if (Uri.TryCreate(baseUri, line, out var variant) && pendingBandwidth > highestBandwidth)
            {
                selected = variant;
                highestBandwidth = pendingBandwidth;
            }
            pendingBandwidth = -1;
        }
        if (lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase)) && selected is null)
            throw new InvalidDataException("The HLS master playlist contains no usable media variant.");
        return selected;
    }

    private static HlsMediaPlaylist ParseHlsMediaPlaylist(string content, Uri baseUri)
    {
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!lines.Any(line => line.Equals("#EXTM3U", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The HTTP response is not an HLS media playlist.");
        long sequence = 0;
        long discontinuitySequence = 0;
        long discontinuities = 0;
        double targetDuration = 2;
        Uri? initMap = null;
        var segments = new List<HlsSegment>();
        var hasByteRanges = false;
        var endList = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
                long.TryParse(line[(line.IndexOf(':') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence);
            else if (line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
                long.TryParse(line[(line.IndexOf(':') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out discontinuitySequence);
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
                double.TryParse(line[(line.IndexOf(':') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out targetDuration);
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var mapUri = ParseAttribute(line, "URI");
                if (mapUri is not null && Uri.TryCreate(baseUri, mapUri, out var resolvedMap)) initMap = resolvedMap;
            }
            else if (line.Equals("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase)) discontinuities++;
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase)) hasByteRanges = true;
            else if (line.Equals("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase)) endList = true;
            else if (!line.StartsWith('#') && Uri.TryCreate(baseUri, line, out var segment))
                segments.Add(new HlsSegment(segment, sequence + segments.Count, discontinuitySequence + discontinuities));
        }
        return new HlsMediaPlaylist(sequence, Math.Max(0.5, targetDuration), initMap, segments, hasByteRanges, endList);
    }

    private static string? ParseAttribute(string line, string name)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return null;
        foreach (var item in line[(colon + 1)..].Split(','))
        {
            var equals = item.IndexOf('=');
            if (equals <= 0 || !item[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return item[(equals + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private static bool HasUnsupportedHlsEncryption(string content)
    {
        var method = "NONE";
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase)) continue;
            method = ParseAttribute(line, "METHOD") ?? string.Empty;
            if (!method.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static long ParseAttributeLong(string line, string name) =>
        long.TryParse(ParseAttribute(line, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

    private static async Task AppendHttpResourceAsync(Uri uri, FileStream output, CancellationToken token)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await CaptureHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(requestCts.Token).ConfigureAwait(false);
        await input.CopyToAsync(output, 128 * 1024, requestCts.Token).ConfigureAwait(false);
    }

    private sealed record HlsSegment(Uri Uri, long Sequence, long DiscontinuitySequence);
    private sealed record HlsMediaPlaylist(long MediaSequence, double TargetDurationSeconds, Uri? InitMap,
        IReadOnlyList<HlsSegment> Segments, bool HasByteRanges, bool EndList);

    private async Task CopyHttpStreamAsync(CaptureSession session, Verdict verdict)
    {
        var input = session.HttpInput ?? throw new InvalidOperationException("HTTP capture input is unavailable.");
        var output = session.HttpOutput ?? throw new InvalidOperationException("HTTP capture output is unavailable.");
        var buffer = new byte[128 * 1024];
        var nextDiskCheck = DateTimeOffset.UtcNow + DiskRecheckInterval;
        try
        {
            while (true)
            {
                if (session.StopReason != RecordingStopReason.None)
                {
                    ApplyStopReason(verdict, session.StopReason);
                    return;
                }
                if (session.CancellationRequested)
                {
                    ApplyStopReason(verdict, session.StopReason == RecordingStopReason.None ? RecordingStopReason.User : session.StopReason);
                    return;
                }
                if (session.Request.HasRequestedEnd && DateTimeOffset.UtcNow >= session.Request.RequestedStartUtc + session.Request.Duration)
                {
                    verdict.Result = CoreOutcome.Completed;
                    verdict.Reason = RecordingStopReason.RequestedEndUtc;
                    return;
                }
                if (DateTimeOffset.UtcNow >= nextDiskCheck)
                {
                    nextDiskCheck = DateTimeOffset.UtcNow + DiskRecheckInterval;
                    if (!HasRoomLeft(session.DestinationFolder))
                    {
                        verdict.Result = CoreOutcome.DiskFull;
                        verdict.Reason = RecordingStopReason.DiskFull;
                        verdict.Failure = RecordingFailure.LowDiskSpace;
                        verdict.Note = "The recording drive filled up, so the capture was stopped.";
                        return;
                    }
                }

                var read = await input.ReadAsync(buffer, session.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    verdict.Result = CoreOutcome.Completed;
                    verdict.Reason = RecordingStopReason.ProviderEnded;
                    return;
                }
                await output.WriteAsync(buffer.AsMemory(0, read), session.Token).ConfigureAwait(false);
                session.SetByteSize(output.Length);
                session.Raise();
            }
        }
        catch (OperationCanceledException) when (session.CancellationRequested)
        {
            ApplyStopReason(verdict, session.StopReason == RecordingStopReason.None ? RecordingStopReason.User : session.StopReason);
        }
        finally
        {
            // DisposeAsync flushes the managed file stream and releases its handle before
            // the completion/index path runs. No LibVLC native stop is involved.
            try { await session.DisposeHttpAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                verdict.Result = CoreOutcome.Failed;
                verdict.Reason = RecordingStopReason.Error;
                verdict.Failure = RecordingFailure.DestinationNotWritable;
                verdict.Note = "The captured file could not be flushed: " + AppLogger.SanitizeText(exception.Message);
            }
            verdict.ByteSize = SafeLength(session.FilePath);
            session.SetByteSize(verdict.ByteSize);
            verdict.Elapsed = session.RecordedDuration;
        }
    }

    private static EventHandler<EventArgs> HandleError(CaptureSession session) => (_, _) =>
    {
        if (session.MarkError()) session.MarkWake();
    };

    private static EventHandler<EventArgs> HandleEnd(CaptureSession session) => (_, _) =>
    {
        session.MarkEnd();
        session.MarkWake();
    };

    private async Task MonitorAsync(CaptureSession session, Verdict verdict, Func<string, VolumeSpace>? freeSpaceProbe = null)
    {
        var request = session.Request;
        var endAt = request.HasRequestedEnd ? DateTimeOffset.UtcNow + request.Duration : (DateTimeOffset?)null;
        var nextDiskCheck = DateTimeOffset.UtcNow + DiskRecheckInterval;
        using var registration = session.Token.Register(() => session.Wake.TrySetResult());

        while (true)
        {
            session.SetByteSize(SafeLength(verdict.FilePath));
            session.Raise();

            var stop = session.StopReason;
            if (stop != RecordingStopReason.None)
            {
                ApplyStopReason(verdict, stop);
                return;
            }
            if (session.ErrorRequested)
            {
                Fail(verdict, "The provider stream stopped before the capture was finished.");
                return;
            }
            if (session.EndReached)
            {
                verdict.Result = CoreOutcome.Completed;
                verdict.Reason = RecordingStopReason.ProviderEnded;
                return;
            }
            if (endAt is DateTimeOffset stopAt && DateTimeOffset.UtcNow >= stopAt)
            {
                verdict.Result = CoreOutcome.Completed;
                verdict.Reason = RecordingStopReason.RequestedEndUtc;
                return;
            }
            if (DateTimeOffset.UtcNow >= nextDiskCheck)
            {
                nextDiskCheck = DateTimeOffset.UtcNow + DiskRecheckInterval;
                    if (!HasRoomLeft(session.DestinationFolder, freeSpaceProbe))
                {
                    verdict.Result = CoreOutcome.DiskFull;
                    verdict.Reason = RecordingStopReason.DiskFull;
                    verdict.Failure = RecordingFailure.LowDiskSpace;
                    verdict.Note = "The recording drive filled up, so the capture was stopped.";
                    return;
                }
            }

            await Task.WhenAny(Task.Delay(StatusPollInterval), session.Wake.Task).ConfigureAwait(false);
            if (session.CancellationRequested)
            {
                Fail(verdict, "The capture was cancelled.");
                return;
            }
        }
    }

    private static void ApplyStopReason(Verdict verdict, RecordingStopReason reason)
    {
        verdict.Reason = reason;
        switch (reason)
        {
            case RecordingStopReason.RequestedEndUtc:
            case RecordingStopReason.ProviderEnded:
                verdict.Result = CoreOutcome.Completed;
                verdict.Failure = RecordingFailure.None;
                break;
            case RecordingStopReason.DiskFull:
                verdict.Result = CoreOutcome.DiskFull;
                verdict.Failure = RecordingFailure.LowDiskSpace;
                break;
            case RecordingStopReason.User:
            case RecordingStopReason.Superseded:
            case RecordingStopReason.Restarted:
                verdict.Result = CoreOutcome.Stopped;
                verdict.Failure = RecordingFailure.None;
                break;
            default:
                verdict.Result = CoreOutcome.Failed;
                verdict.Failure = RecordingFailure.UnsupportedSource;
                break;
        }
    }

    private async Task FinalizeAsync(CaptureSession session, Verdict verdict)
    {
        session.SetState(RecordingCaptureState.Stopping);
        try
        {
            // Only a path this capture committed to writing is ever read or removed, so a
            // name that already held a recording is never touched by a capture that
            // refused to start.
            var path = verdict.FilePath;
            if (session.HasRetunedOutput)
            {
                // The tuner callback has already detached the output chain and
                // flushed its file before this shared finalization path is entered.
                verdict.ByteSize = string.IsNullOrEmpty(path) ? 0 : await WaitForMuxerFlushAsync(path).ConfigureAwait(false);
            }
            else if (session.UsesManagedHttp)
            {
                // The managed copy loop already flushed/closed its FileStream.
                verdict.ByteSize = string.IsNullOrEmpty(path) ? 0 : SafeLength(path);
            }
            else
            {
                // Preserve the LibVLC finalization path for non-HTTP sources only.
                StopPlayer(session);
                verdict.ByteSize = string.IsNullOrEmpty(path) ? 0 : await WaitForMuxerFlushAsync(path).ConfigureAwait(false);
            }
            session.MarkStopped();
            verdict.Elapsed = session.RecordedDuration;
            session.SetByteSize(verdict.ByteSize);

            if (!verdict.Started || verdict.ByteSize <= 0)
            {
                // A start that never delivered a complete payload is not a recording.
                // Remove even a partial first write and never expose it as successful.
                if (verdict.Failure == RecordingFailure.None)
                {
                    verdict.Result = CoreOutcome.Failed;
                    verdict.Failure = RecordingFailure.UnsupportedSource;
                }
                if (verdict.Reason == RecordingStopReason.None) verdict.Reason = RecordingStopReason.Error;
                verdict.FilePath = string.Empty;
                session.ForgetFile();
                DeletePartial(path);
            }
            else if (verdict.Failure == RecordingFailure.None)
            {
                // A nonempty, flushed pass-through file is retained as an unverified
                // partial. Opening it in another LibVLC instance during stop adds native
                // lifetime risk and is not required to release/finalize the capture.
                verdict.Result = CoreOutcome.Stopped;
                verdict.Failure = RecordingFailure.None;
                if (verdict.Reason == RecordingStopReason.None)
                    verdict.Reason = RecordingStopReason.User;
                verdict.Note = session.UsesManagedHttp
                    ? "The HTTP response ended and the destination file was flushed and closed."
                    : "The LibVLC capture was stopped and its output settled; codec inspection is unavailable.";
            }
        }
        finally
        {
            session.MarkStopped();
            if (!session.UsesManagedHttp)
                session.DisposeNativeCapture();
        }
    }

    // A dropped, unplayable or wrong-codec stream is a failed capture, and the reason
    // has to name what the source carried and what the file actually kept.
    private static string? Verify(SourceTracks source, OutputReport report)
    {
        if (!report.Parsed) return "The recorded file could not be opened after the capture ended, so it is not playable.";
        if (report.VideoCodecs.Count == 0 && report.AudioCodecs.Count == 0)
            return "The recorded file has no audio or video track, so it is not playable.";
        if (!source.IsKnown)
            return "The source codecs could not be read, so the recording was only checked for playable tracks (" + report.Describe() + ").";

        // The probe is a snapshot of a live stream that is still settling, so gaining a
        // track is not a fault; losing one is exactly the silent muxer drop this check
        // exists for.
        if (report.VideoCodecs.Count < source.VideoCodecs.Count)
            return $"The capture kept {report.VideoCodecs.Count} of {source.VideoCodecs.Count} video streams ({source.Describe()} in, {report.Describe()} out), so the recording is incomplete.";
        if (report.AudioCodecs.Count < source.AudioCodecs.Count)
            return $"The capture kept {report.AudioCodecs.Count} of {source.AudioCodecs.Count} audio streams ({source.Describe()} in, {report.Describe()} out), so the recording is incomplete.";
        if (source.VideoCodecs.Count > 0 && report.VideoCodecs.Count > 0 &&
            !string.Equals(source.VideoCodecs[0], report.VideoCodecs[0], StringComparison.OrdinalIgnoreCase))
            return $"The recorded video is {report.VideoCodecs[0]} but the source was {source.VideoCodecs[0]}. This build cannot transcode, so the capture is not a faithful copy.";
        return null;
    }

    private async Task<SourceTracks> ProbeAsync(LibVLC? libVlc, string url, int bufferMs, CancellationToken token)
    {
        if (libVlc is null) return SourceTracks.Unknown;
        Media? probe = null;
        try
        {
            var cache = Math.Max(200, bufferMs).ToString(CultureInfo.InvariantCulture);
            probe = new Media(libVlc, url, FromType.FromLocation);
            probe.AddOption(":network-caching=" + cache);
            probe.AddOption(":live-caching=" + cache);
            probe.AddOption(":http-reconnect");
            // A live stream never finishes parsing, so the timeout is what ends the
            // probe and the track list is read from whatever the demuxer reported by then.
            await probe.Parse(MediaParseOptions.ParseNetwork, (int)SourceProbeTimeout.TotalMilliseconds, token).ConfigureAwait(false);
            return ReadTracks(probe);
        }
        catch (OperationCanceledException)
        {
            return SourceTracks.Unknown;
        }
        catch (Exception exception)
        {
            AppLogger.Warn("Capture: source probe failed. " + AppLogger.SanitizeText(exception.Message));
            return SourceTracks.Unknown;
        }
        finally
        {
            try { probe?.ParseStop(); } catch { /* The probe is abandoned either way. */ }
            try { probe?.Dispose(); } catch { /* Native cleanup races are not worth reporting. */ }
        }
    }

    private static SourceTracks ReadTracks(Media media)
    {
        var video = new List<string>();
        var audio = new List<string>();
        try
        {
            foreach (var track in media.Tracks)
            {
                var codec = FourCc(track.Codec);
                if (codec is null) continue;
                if (track.TrackType == TrackType.Video) video.Add(codec);
                else if (track.TrackType == TrackType.Audio) audio.Add(codec);
            }
        }
        catch (Exception exception)
        {
            // LibVLCSharp has small API differences between builds; an unreadable track
            // list means an unknown source, not a broken capture.
            AppLogger.Warn("Capture: the track list could not be read. " + AppLogger.SanitizeText(exception.Message));
        }
        return new SourceTracks(video, audio);
    }

    // A track's own description and language are rarely filled in for video and audio, so
    // the codec comes from the FourCC identifier instead (for example 'h264', 'mp4a').
    private static string? FourCc(uint value)
    {
        if (value == 0) return null;
        var text = new string(BitConverter.GetBytes(value)
            .Where(b => b != 0)
            .Select(b => b is >= 0x20 and < 0x7F ? (char)b : '?')
            .ToArray()).Trim();
        return text.Length == 0 ? null : text.ToUpperInvariant();
    }

    private static async Task<long> WaitForMuxerFlushAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        var started = DateTime.UtcNow;
        var deadline = started + MuxerFlushCap;
        long last = -1;
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (DateTime.UtcNow - started >= MuxerFlushFloor && stable >= 2) break;
            var current = SafeLength(path);
            if (current == last) stable++;
            else { stable = 0; last = current; }
            await Task.Delay(MuxerFlushPoll).ConfigureAwait(false);
        }
        return Math.Max(0, SafeLength(path));
    }

    private static async Task<bool> OpenAsync(CaptureSession session, MediaPlayer player, Media media)
    {
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPlaying(object? sender, EventArgs args) => opened.TrySetResult(true);
        void OnFailure(object? sender, EventArgs args) => opened.TrySetResult(false);

        player.Playing += OnPlaying;
        player.EncounteredError += OnFailure;
        player.Stopped += OnFailure;
        player.EndReached += OnFailure;
        using var registration = session.Token.Register(() => opened.TrySetResult(false));
        try
        {
            if (!player.Play(media))
            {
                AppLogger.Warn("Capture: Play() refused to start. id=" + session.Id);
                return false;
            }
            var winner = await Task.WhenAny(opened.Task, Task.Delay(OpenTimeout, session.Token)).ConfigureAwait(false);
            return winner == opened.Task && await opened.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Capture: open failed. id=" + session.Id, exception);
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

    private static bool HasRoomLeft(string folder, Func<string, VolumeSpace>? freeSpaceProbe = null)
    {
        var live = RecordingPolicy.ValidateDestination(folder);
        var space = live.Space;
        if (live.IsValid && freeSpaceProbe is not null)
        {
            try { space = freeSpaceProbe(live.Folder) ?? VolumeSpace.Unknown; }
            catch { space = VolumeSpace.Unknown; }
        }
        return !space.IsKnown || space.AvailableBytes >= RecordingPolicy.SafetyMarginBytes;
    }

    private static void StopPlayer(CaptureSession session)
    {
        var player = session.Player;
        if (player is null) return;
        session.DetachCaptureEvents();
        try { player.Stop(); }
        catch (Exception exception)
        {
            AppLogger.Warn("Capture: player Stop failed. " + AppLogger.SanitizeText(exception.Message));
        }
    }

    private static long SafeLength(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0L;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0L;
        }
    }

    private static void DeletePartial(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) { AppLogger.Warn("Capture: the partial file could not be removed. " + AppLogger.SanitizeText(exception.Message)); }
    }

    private static void Fail(Verdict verdict, string message) => Fail(verdict, RecordingFailure.UnsupportedSource, message);

    private static void Fail(Verdict verdict, RecordingFailure failure, string message)
    {
        verdict.Result = CoreOutcome.Failed;
        if (verdict.Reason == RecordingStopReason.None) verdict.Reason = RecordingStopReason.Error;
        verdict.Failure = failure;
        verdict.Note = AppLogger.SanitizeText(message);
    }

    /// <summary>
    /// Holds one budget slot and gives it back exactly once whatever the holder does.
    /// Core has no RAII for a lease, so that discipline lives here.
    /// </summary>
    private sealed class LeaseScope(ConnectionBudget budget, StreamLease lease) : IDisposable
    {
        private StreamLease? _lease = lease;
        private int _released;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            var held = Interlocked.Exchange(ref _lease, null);
            if (held is not null) budget.Release(held);
        }

        public void Dispose() => Release();
    }

    private sealed class Verdict
    {
        public bool Started;
        public CoreOutcome Result = CoreOutcome.Failed;
        public RecordingStopReason Reason = RecordingStopReason.None;
        public RecordingFailure Failure = RecordingFailure.UnsupportedSource;
        public string Note = string.Empty;
        public string FilePath = string.Empty;
        public long ByteSize;
        public TimeSpan Elapsed;
    }

    private readonly record struct SourceTracks(IReadOnlyList<string> VideoCodecs, IReadOnlyList<string> AudioCodecs)
    {
        public static SourceTracks Unknown { get; } = new([], []);
        public bool IsKnown => VideoCodecs.Count > 0 || AudioCodecs.Count > 0;
        public string Describe() => DescribeTracks(VideoCodecs, AudioCodecs);
    }

    private readonly record struct OutputReport(bool Parsed, IReadOnlyList<string> VideoCodecs, IReadOnlyList<string> AudioCodecs)
    {
        public static OutputReport Unreadable { get; } = new(false, [], []);
        public string Describe() => DescribeTracks(VideoCodecs, AudioCodecs);
    }

    private static string DescribeTracks(IReadOnlyList<string> video, IReadOnlyList<string> audio)
    {
        if (video.Count == 0 && audio.Count == 0) return "no tracks";
        var parts = new List<string>(2);
        if (video.Count > 0) parts.Add(string.Join("/", video) + " video");
        if (audio.Count > 0) parts.Add(string.Join("/", audio) + " audio");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// One capture. Its state, stop request, error flag, byte count and clock are all
    /// <see cref="Interlocked"/> fields so the host can read a snapshot from any thread
    /// without taking a lock, and nothing here ever blocks a caller or a dispatcher.
    /// </summary>
    private sealed class CaptureSession
    {
        private readonly RecordingService _owner;
        private int _state;
        private int _stopReason;
        private int _errorRequested;
        private int _endReached;
        private int _fileForgotten;
        private long _byteSize;
        private long _startedTicks;
        private long _stoppedTicks;
        private long _stopRequestedTicks;
        private readonly object _readyGate = new();
        private EventHandler<EventArgs>? _onError;
        private EventHandler<EventArgs>? _onEnd;
        private Func<string, CancellationToken, Task<bool>>? _startRetunedOutput;
        private Func<CancellationToken, Task<bool>>? _stopRetunedOutput;
        private Task<bool>? _stopRetunedTask;

        public CaptureSession(RecordingService owner, string id, RecordingStartRequest request, string requestedPath,
            string destinationFolder, long requiredBytes, DateTimeOffset requestedStart)
        {
            _owner = owner;
            Id = id;
            Request = request;
            RequestedPath = requestedPath;
            FilePath = requestedPath;
            DestinationFolder = destinationFolder;
            RequiredBytes = requiredBytes;
            RequestedStartUtc = requestedStart;
            Cts = new CancellationTokenSource();
            Wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ready = new TaskCompletionSource<RecordingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            Finished = new TaskCompletionSource<RecordingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string Id { get; }
        public RecordingStartRequest Request { get; }
        public string RequestedPath { get; }
        public string DestinationFolder { get; }
        public long RequiredBytes { get; }
        public DateTimeOffset RequestedStartUtc { get; }
        public CancellationTokenSource Cts { get; }
        public CancellationToken Token => Cts.Token;
        public bool CancellationRequested => Cts.IsCancellationRequested;
        public TaskCompletionSource Wake { get; }
        public TaskCompletionSource<RecordingOutcome> Ready { get; }
        public TaskCompletionSource<RecordingOutcome> Finished { get; }

        public string FilePath { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string Muxer { get; set; } = "ts";
        public string Note { get; set; } = string.Empty;
        public RecordingContainerKind Container { get; set; } = RecordingContainerKind.Ts;
        public SourceTracks Source { get; set; } = SourceTracks.Unknown;
        public RecordingEntry? Entry { get; private set; }
        public LibVLC? LibVlc { get; private set; }
        public MediaPlayer? Player { get; private set; }
        public Media? Media { get; private set; }
        public HttpResponseMessage? HttpResponse { get; private set; }
        public Stream? HttpInput { get; private set; }
        public FileStream? HttpOutput { get; private set; }
        public bool UsesManagedHttp { get; private set; }
        public bool HasRetunedOutput => _startRetunedOutput is not null;
        public bool RetunedOutputMayBeActive { get; private set; }
        public bool HasRecordedStart => Interlocked.Read(ref _startedTicks) > 0;

        public RecordingCaptureState State => (RecordingCaptureState)Volatile.Read(ref _state);
        public RecordingStopReason StopReason => (RecordingStopReason)Volatile.Read(ref _stopReason);
        public DateTimeOffset? StopRequestedUtc
        {
            get
            {
                var ticks = Interlocked.Read(ref _stopRequestedTicks);
                return ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
            }
        }
        public bool ErrorRequested => Volatile.Read(ref _errorRequested) != 0;
        public bool EndReached => Volatile.Read(ref _endReached) != 0;
        public bool FileForgotten => Volatile.Read(ref _fileForgotten) != 0;
        public long ByteSize => Interlocked.Read(ref _byteSize);
        public TimeSpan RecordedDuration
        {
            get
            {
                var start = Interlocked.Read(ref _startedTicks);
                var stop = Interlocked.Read(ref _stoppedTicks);
                if (start <= 0)
                    return TimeSpan.Zero;

                var end = stop > 0 ? stop : DateTimeOffset.UtcNow.UtcDateTime.Ticks;
                return TimeSpan.FromTicks(Math.Max(0, end - start));
            }
        }

        public void SetState(RecordingCaptureState value) => Interlocked.Exchange(ref _state, (int)value);
        public void SetByteSize(long value) => Interlocked.Exchange(ref _byteSize, value);
        public void MarkStarted() => MarkStarted(DateTimeOffset.UtcNow);
        public void MarkStarted(DateTimeOffset startedUtc) => Interlocked.Exchange(ref _startedTicks, startedUtc.UtcDateTime.Ticks);
        public bool TryMarkSharedHlsPayloadReady(DateTimeOffset startedUtc)
        {
            lock (_readyGate)
            {
                if (StopReason != RecordingStopReason.None || CancellationRequested)
                    return false;
                MarkStarted(startedUtc);
                return true;
            }
        }
        public bool TryMarkRetunedOutputPayloadReady(DateTimeOffset startedUtc)
        {
            lock (_readyGate)
            {
                if (StopReason != RecordingStopReason.None || CancellationRequested)
                    return false;
                MarkStarted(startedUtc);
                return true;
            }
        }
        public void MarkStopped() => MarkStopped(StopRequestedUtc ?? DateTimeOffset.UtcNow);
        public void MarkStopped(DateTimeOffset stoppedUtc) => Interlocked.Exchange(ref _stoppedTicks, stoppedUtc.UtcDateTime.Ticks);
        public bool MarkError() => Interlocked.Exchange(ref _errorRequested, 1) == 0;
        public void MarkEnd() => Interlocked.Exchange(ref _endReached, 1);
        public void ForgetFile() => Interlocked.Exchange(ref _fileForgotten, 1);
        public void MarkWake() => Wake.TrySetResult();
        public void RequestStop(RecordingStopReason reason)
        {
            var requestedStopUtc = DateTimeOffset.UtcNow;
            lock (_readyGate)
            {
                if (Interlocked.CompareExchange(ref _stopReason, (int)reason, (int)RecordingStopReason.None) == (int)RecordingStopReason.None)
                    Interlocked.Exchange(ref _stopRequestedTicks, requestedStopUtc.UtcDateTime.Ticks);
            }
            if (UsesManagedHttp || Request.SharedHlsConsumer is not null || HasRetunedOutput)
            {
                try { Cts.Cancel(); } catch (ObjectDisposedException) { }
            }
            Wake.TrySetResult();
        }

        public void Cancel()
        {
            try { Cts.Cancel(); }
            catch (ObjectDisposedException) { /* The capture already ended. */ }
            Wake.TrySetResult();
        }

        public void Adopt(MediaPlayer player, LibVLC libVlc, EventHandler<EventArgs> onError, EventHandler<EventArgs> onEnd)
        {
            Player = player;
            LibVlc = libVlc;
            // The instances are kept so the native subscriptions can be removed again:
            // a lambda rebuilt at teardown would not compare equal to the one added here.
            _onError = onError;
            _onEnd = onEnd;
            player.EncounteredError += onError;
            player.EndReached += onEnd;
        }

        public void Attach(Media media) => Media = media;

        public void ConfigureRetunedOutput(Func<string, CancellationToken, Task<bool>> startOutput,
            Func<CancellationToken, Task<bool>> stopOutput)
        {
            _startRetunedOutput = startOutput;
            _stopRetunedOutput = stopOutput;
        }

        public async Task<bool> StartRetunedOutputAsync(string path, CancellationToken token)
        {
            var start = _startRetunedOutput ?? throw new InvalidOperationException("No tuner output start callback is configured.");
            RetunedOutputMayBeActive = true;
            return await start(path, token).ConfigureAwait(false);
        }

        public Task<bool> StopRetunedOutputOnceAsync(CancellationToken token)
        {
            lock (_readyGate)
            {
                return _stopRetunedTask ??= StopRetunedOutputCoreAsync(token);
            }
        }

        private async Task<bool> StopRetunedOutputCoreAsync(CancellationToken token)
        {
            var stop = _stopRetunedOutput;
            if (stop is null) return true;
            try
            {
                var result = await stop(token).WaitAsync(RetunedOutputStartTimeout).ConfigureAwait(false);
                RetunedOutputMayBeActive = false;
                return result;
            }
            catch (Exception exception)
            {
                AppLogger.Warn("Capture: tuner output stop/flush failed. id=" + Id + "; " + AppLogger.SanitizeText(exception.Message));
                return false;
            }
        }

        public void AttachHttp(HttpResponseMessage response, Stream input, FileStream output)
        {
            HttpResponse = response;
            HttpInput = input;
            HttpOutput = output;
            UsesManagedHttp = true;
        }

        public void AttachHttpOutput(FileStream output)
        {
            HttpOutput = output;
            MarkManagedHttp();
        }

        public void MarkManagedHttp() => UsesManagedHttp = true;

        public async Task DisposeHttpAsync()
        {
            Exception? outputError = null;
            try { if (HttpInput is not null) await HttpInput.DisposeAsync().ConfigureAwait(false); } catch { }
            try
            {
                if (HttpOutput is not null)
                {
                    await HttpOutput.FlushAsync().ConfigureAwait(false);
                    await HttpOutput.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                outputError = exception;
                try { HttpOutput?.Dispose(); } catch { }
            }
            try { HttpResponse?.Dispose(); } catch { }
            HttpInput = null;
            HttpOutput = null;
            HttpResponse = null;
            if (outputError is not null) throw outputError;
        }

        public void DetachCaptureEvents()
        {
            if (Player is not null && _onError is not null && _onEnd is not null)
            {
                try { Player.EncounteredError -= _onError; } catch { }
                try { Player.EndReached -= _onEnd; } catch { }
            }
            _onError = null;
            _onEnd = null;
        }

        public void DisposeNativeCapture()
        {
            DetachCaptureEvents();
            try { Player?.Dispose(); } catch { }
            try { Media?.Dispose(); } catch { }
            try { LibVlc?.Dispose(); } catch { }
            Player = null;
            Media = null;
            LibVlc = null;
        }

        public bool HasNativeObjects => Player is not null || Media is not null || LibVlc is not null;

        /// <summary>Opens the index row here rather than in the host, so a capture that dies with the app leaves a finished row.</summary>
        public void BeginRow()
        {
            var plan = new RecordingPlan
            {
                DestinationFolder = DestinationFolder, ChannelName = Request.ChannelName, FileName = FileName,
                FilePath = FilePath, Container = Container, RequestedStartUtc = RequestedStartUtc,
                RequestedStopUtc = RequestedStartUtc + (Request.HasRequestedEnd ? Request.Duration : TimeSpan.Zero),
                ActualStartUtc = Request.SharedHlsConsumer is null ? DateTimeOffset.UtcNow : RequestedStartUtc,
                Programme = RecordedProgrammeMetadata.From(Request.Programme),
                RequiredBytes = RequiredBytes
            };
            Entry = _owner._index.Begin(plan, Request.AccountId, Request.ChannelId, Request.ChannelKey, FilePath);
        }

        public void SetEntryStarted(DateTimeOffset startedUtc)
        {
            Entry = _owner._index.SetStarted(Entry, startedUtc) ?? Entry;
            Raise();
        }

        public RecordingOutcome StartedOutcome() => new()
        {
            RecordingId = Id, Accepted = Request.SharedHlsConsumer is null || ByteSize > 0,
            Entry = Entry, Result = CoreOutcome.Recording,
            FilePath = FilePath, Container = Container, ByteSize = ByteSize, Elapsed = TimeSpan.Zero
        };

        public RecordingOutcome Complete(Verdict verdict)
        {
            // A capture that somehow reached here without a terminal result is reported as
            // a failure rather than leaving an index row that claims to be running.
            var result = verdict.Result == CoreOutcome.Recording ? CoreOutcome.Failed : verdict.Result;
            // RecordingIndex turns a completed capture with no reason into RequestedEndUtc;
            // any other terminal result needs a reason it can store.
            var reason = verdict.Reason == RecordingStopReason.None && result != CoreOutcome.Completed
                ? RecordingStopReason.Error
                : verdict.Reason;
            var note = AppLogger.SanitizeText(verdict.Note).Trim();
            var filePath = FileForgotten ? string.Empty : verdict.FilePath;
            if (Entry is not null)
                Entry = _owner._index.Finish(Entry, new RecordingFinish(StopRequestedUtc ?? DateTimeOffset.UtcNow, result, reason, verdict.ByteSize, note)) ?? Entry;
            var outcome = new RecordingOutcome
            {
                RecordingId = Id, Accepted = verdict.Started, Entry = Entry, Result = result,
                Failure = verdict.Started ? verdict.Failure :
                    (verdict.Failure == RecordingFailure.None ? RecordingFailure.UnsupportedSource : verdict.Failure),
                FailureReason = verdict.Started || string.IsNullOrEmpty(note) ? string.Empty : note,
                FilePath = filePath, Container = Container, ByteSize = verdict.ByteSize, Elapsed = verdict.Elapsed
            };
            SetState(result == CoreOutcome.Failed ? RecordingCaptureState.Failed : RecordingCaptureState.Idle);
            Note = outcome.FailureReason;
            Raise();
            // A refusal inside the capture task never reached a running state, so the
            // start attempt completes here too rather than leaving the host waiting.
            Ready.TrySetResult(outcome);
            Finished.TrySetResult(outcome);
            Cts.Dispose();
            return outcome;
        }

        public RecordingStatusSnapshot Snapshot() => new(Id, State, Request.AccountId, Request.ChannelId,
            Request.ChannelName, FileForgotten ? string.Empty : FilePath, RecordedDuration, ByteSize, Note);

        public void Raise()
        {
            try { _owner.StateChanged?.Invoke(Snapshot()); }
            catch (Exception exception) { AppLogger.Error("Capture: StateChanged handler failed. id=" + Id, exception); }
        }
    }
}
