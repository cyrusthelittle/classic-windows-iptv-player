using System.Globalization;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>
/// One HTTP HLS source shared by independent consumers. It polls one media playlist
/// and downloads each segment once. Each consumer receives a bounded, independent
/// queue; playback queues are protected from recording pressure.
/// </summary>
public sealed class SharedHlsSource : IAsyncDisposable
{
    private readonly Uri _playlistUri;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _pollInterval;
    private readonly int _maximumSegmentBytes;
    private readonly int _maximumBufferedBytesPerConsumer;
    private readonly int _maximumPlaybackCacheBytes;
    private readonly LiveTimeshiftBuffer _timeshiftBuffer;
    private Exception? _timeshiftFailure;
    private readonly object _gate = new();
    private readonly List<Consumer> _consumers = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _runTask;
    private long _nextId;
    private long _liveEdgeSequence = -1;
    private long _advertisedEdgeSequence = -1;
    private long _lastPlaylistMediaSequence = -1;
    private long _sourceSequenceOffset;
    private readonly LinkedList<SharedHlsSegment> _playbackCache = [];
    private int _playbackCacheBytes;
    private long? _playbackStartSequence;
    private long? _playbackResumeSequence;
    private TcpListener? _playbackListener;
    private Task? _playbackServerTask;
    private readonly HashSet<Task> _playbackHandlers = [];
    private readonly Dictionary<string, PlaybackCursor> _playbackCursors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, int> _localPlaybackSegmentRequests = new();
    private const int MaximumPlaybackClients = 32;
    private const int MaximumPlaybackCursors = 128;
    private static readonly TimeSpan PlaybackCursorLifetime = TimeSpan.FromMinutes(30);
    private Uri? _playbackUrl;
    private Exception? _terminalFailure;
    private bool _disposed;

    public SharedHlsSource(Uri playlistUri, HttpClient? httpClient = null,
        TimeSpan? pollInterval = null, int maximumSegmentBytes = 32 *  1024 * 1024,
        int maximumBufferedBytesPerConsumer = 16 * 1024 * 1024,
        int maximumPlaybackCacheBytes = 128 * 1024 * 1024,
        long maximumTimeshiftBytes = 2L * 1024 * 1024 * 1024,
        TimeSpan? maximumTimeshiftDuration = null, string? timeshiftTemporaryRoot = null)
    {
        if (playlistUri is null || !playlistUri.IsAbsoluteUri ||
            playlistUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("An absolute HTTP or HTTPS HLS playlist URL is required.", nameof(playlistUri));
        if (maximumSegmentBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSegmentBytes));
        if (maximumBufferedBytesPerConsumer < maximumSegmentBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBufferedBytesPerConsumer), "The buffer must fit at least one maximum-sized segment.");
        if (maximumPlaybackCacheBytes < maximumSegmentBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPlaybackCacheBytes), "The playback cache must fit at least one maximum-sized segment.");

        _playlistUri = playlistUri;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = httpClient is null;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        if (_pollInterval < TimeSpan.FromMilliseconds(100))
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Playlist polling cannot be faster than 100 ms.");
        _maximumSegmentBytes = maximumSegmentBytes;
        _maximumBufferedBytesPerConsumer = maximumBufferedBytesPerConsumer;
        _maximumPlaybackCacheBytes = maximumPlaybackCacheBytes;
        _timeshiftBuffer = new LiveTimeshiftBuffer(maximumTimeshiftBytes, maximumTimeshiftDuration, timeshiftTemporaryRoot);
    }

    /// <summary>Raised for background fetch errors. Handlers must not throw.</summary>
    public event EventHandler<Exception>? SourceError;

    /// <summary>Set when the source cannot be safely relayed. Consumers should fall back to the origin for playback and disable shared recording.</summary>
    public Exception? TerminalFailure { get { lock (_gate) return _terminalFailure; } }

    /// <summary>Loopback HLS URL for LibVLC after <see cref="StartPlaybackAsync"/> completes.</summary>
    public Uri? PlaybackUrl { get { lock (_gate) return _playbackUrl; } }

    /// <summary>Failure disables only disk timeshift; shared playback/recording fanout continues.</summary>
    public Exception? TimeshiftFailure { get { lock (_gate) return _timeshiftFailure; } }

    /// <summary>Local relay segment request count for diagnostics and playback integration checks.</summary>
    public int GetLocalPlaybackSegmentRequestCount(long sequence) =>
        _localPlaybackSegmentRequests.TryGetValue(sequence, out var count) ? count : 0;

    public LiveTimeshiftWindow InspectTimeshiftWindow()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timeshiftFailure is not null) throw new NotSupportedException("Disk timeshift is unavailable for this HLS source.", _timeshiftFailure);
            return _timeshiftBuffer.Inspect();
        }
    }

    /// <summary>Creates a read cursor over already-downloaded segments; it never performs provider I/O.</summary>
    public LiveTimeshiftSeekResult TrySeekTimeshift(long sequence, out LiveTimeshiftCursor? cursor)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timeshiftFailure is not null) { cursor = null; return LiveTimeshiftSeekResult.Unsupported; }
            return _timeshiftBuffer.Seek(sequence, out cursor);
        }
    }

    /// <summary>
    /// Creates a distinct local HLS input rooted at an already-buffered segment.
    /// Unlike changing the current playlist, this URL has an immutable cursor identity
    /// so LibVLC opens it as a fresh input. It never contacts the provider.
    /// </summary>
    public LiveTimeshiftSeekResult TryCreateTimeshiftPlaybackUrl(long sequence, out Uri? playbackUrl)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            playbackUrl = null;
            if (_timeshiftFailure is not null) return LiveTimeshiftSeekResult.Unsupported;
            var result = _timeshiftBuffer.Seek(sequence, out _);
            if (result != LiveTimeshiftSeekResult.Positioned) return result;
            var cursorId = Guid.NewGuid().ToString("N");
            RegisterPlaybackCursor(cursorId, sequence);
            var origin = _playbackUrl ?? throw new InvalidOperationException("The shared playback endpoint has not started.");
            playbackUrl = new Uri(origin, $"cursor/{cursorId}/{sequence.ToString(CultureInfo.InvariantCulture)}/index.m3u8");
            return LiveTimeshiftSeekResult.Positioned;
        }
    }

    public LiveTimeshiftSeekResult TryCreateTimeshiftPlaybackUrlBehindLive(TimeSpan behindLive, out Uri? playbackUrl, out long selectedSequence)
    {
        if (behindLive < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(behindLive));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            playbackUrl = null;
            selectedSequence = -1;
            if (_timeshiftFailure is not null) return LiveTimeshiftSeekResult.Unsupported;
            var window = _timeshiftBuffer.Inspect();
            if (!window.HasData || window.OldestSequence is not long oldest || window.LiveSequence is not long live)
                return LiveTimeshiftSeekResult.Empty;
            selectedSequence = live;
            var remaining = behindLive.TotalSeconds;
            var entries = _timeshiftBuffer.ReadWindowFrom(oldest);
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                selectedSequence = entries[index].Sequence;
                remaining -= entries[index].DurationSeconds;
                if (remaining <= 0) break;
            }
            var result = _timeshiftBuffer.Seek(selectedSequence, out _);
            if (result != LiveTimeshiftSeekResult.Positioned) return result;
            var cursorId = Guid.NewGuid().ToString("N");
            RegisterPlaybackCursor(cursorId, selectedSequence);
            var origin = _playbackUrl ?? throw new InvalidOperationException("The shared playback endpoint has not started.");
            playbackUrl = new Uri(origin, $"cursor/{cursorId}/{selectedSequence.ToString(CultureInfo.InvariantCulture)}/index.m3u8");
            return result;
        }
    }

    private void RegisterPlaybackCursor(string cursorId, long sequence)
    {
        var expiredBefore = DateTimeOffset.UtcNow - PlaybackCursorLifetime;
        foreach (var expired in _playbackCursors.Where(pair => pair.Value.LastAccessUtc < expiredBefore).Select(pair => pair.Key).ToArray())
            _playbackCursors.Remove(expired);
        while (_playbackCursors.Count >= MaximumPlaybackCursors)
        {
            var oldest = _playbackCursors.MinBy(pair => pair.Value.LastAccessUtc).Key;
            _playbackCursors.Remove(oldest);
        }
        _playbackCursors.Add(cursorId, new PlaybackCursor(sequence));
    }

    /// <summary>Move the existing loopback playback playlist to a buffered sequence without opening another origin request.</summary>
    public LiveTimeshiftSeekResult TrySetPlaybackSequence(long sequence)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timeshiftFailure is not null) return LiveTimeshiftSeekResult.Unsupported;
            var result = _timeshiftBuffer.Seek(sequence, out _);
            if (result != LiveTimeshiftSeekResult.Positioned) return result;
            _playbackStartSequence = sequence;
            _playbackResumeSequence = sequence;
            return result;
        }
    }

    /// <summary>Resume playlist advancement from the pinned buffered sequence, not the live edge.</summary>
    public LiveTimeshiftSeekResult ResumePlaybackCursor()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_playbackResumeSequence is not long sequence) return LiveTimeshiftSeekResult.Empty;
            var result = _timeshiftBuffer.Seek(sequence, out _);
            if (result != LiveTimeshiftSeekResult.Positioned) return result;
            _playbackStartSequence = sequence;
            return result;
        }
    }

    /// <summary>Restore the live playlist mode after a transient source-window change.</summary>
    public LiveTimeshiftSeekResult CancelPlaybackCursor()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _playbackStartSequence = null;
            _playbackResumeSequence = null;
            return _liveEdgeSequence >= 0 ? LiveTimeshiftSeekResult.Positioned : LiveTimeshiftSeekResult.Empty;
        }
    }

    /// <summary>Return the existing loopback playlist to the newest fetched segment.</summary>
    public LiveTimeshiftSeekResult GoLiveOnPlayback()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_liveEdgeSequence < 0) return LiveTimeshiftSeekResult.Empty;
            _playbackStartSequence = _liveEdgeSequence;
            _playbackResumeSequence = _liveEdgeSequence;
            return LiveTimeshiftSeekResult.Positioned;
        }
    }

    /// <summary>Starts the local HLS endpoint and shared upstream poll; completes when the loopback listener is ready.</summary>
    public Task<Uri> StartPlaybackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_playbackUrl is not null) return Task.FromResult(_playbackUrl);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _playbackListener = listener;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _playbackUrl = new Uri($"http://127.0.0.1:{port}/index.m3u8");
            _playbackServerTask = Task.Run(() => ServePlaybackAsync(listener, _lifetime.Token));
            _runTask ??= Task.Run(() => RunAsync(_lifetime.Token));
            return Task.FromResult(_playbackUrl);
        }
    }

    /// <summary>Waits until at least one segment is cached for playback, or fails on source error/timeout/cancellation.</summary>
    public async Task WaitUntilPlaybackReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(timeout);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_terminalFailure is not null) throw new InvalidOperationException("Shared HLS relay is unavailable; use direct playback and disable shared recording.", _terminalFailure);
                if (_playbackCache.Count > 0) return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Attach at the latest known live edge. No prior segment is enqueued.</summary>
    public SharedHlsConsumer Attach(SharedHlsConsumerKind kind)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var consumer = new Consumer(++_nextId, kind);
            consumer.MinimumSequence = GetMinimumSequence(kind, _liveEdgeSequence, _advertisedEdgeSequence);
            _consumers.Add(consumer);
            _runTask ??= Task.Run(() => RunAsync(_lifetime.Token));
            return new SharedHlsConsumer(this, consumer);
        }
    }

    private static long GetMinimumSequence(SharedHlsConsumerKind kind, long downloadedEdge, long advertisedEdge) =>
        kind == SharedHlsConsumerKind.Recording ? Math.Max(downloadedEdge, advertisedEdge) + 1 : 0;

    /// <summary>Detach immediately and discard that consumer's queued segments.</summary>
    internal void Detach(Consumer consumer)
    {
        lock (_gate)
            _consumers.Remove(consumer);
        consumer.Detach();
    }

    internal ValueTask<SharedHlsSegment?> ReadAsync(Consumer consumer, CancellationToken cancellationToken) =>
        consumer.ReadAsync(cancellationToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var seen = new HashSet<(long Sequence, string Uri)>();
        var nextSequence = (long?)null;
        var playlist = _playlistUri;
        var pollInterval = TimeSpan.FromMilliseconds(Math.Clamp(_pollInterval.TotalMilliseconds, 500, 2000));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var playlistResponse = await _http.GetAsync(playlist, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                playlistResponse.EnsureSuccessStatusCode();
                var effectivePlaylistUri = playlistResponse.RequestMessage?.RequestUri ?? playlist;
                var text = await playlistResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var parsed = ParsePlaylist(effectivePlaylistUri, text);
                if (parsed.VariantUri is not null)
                {
                    playlist = parsed.VariantUri;
                    seen.Clear();
                    nextSequence = null;
                    pollInterval = TimeSpan.FromMilliseconds(Math.Clamp(_pollInterval.TotalMilliseconds, 500, 2000));
                }
                else if (parsed.Segments.Count > 0)
                {
                    var sequenceRegressed = false;
                    lock (_gate)
                    {
                        sequenceRegressed = _lastPlaylistMediaSequence >= 0 && parsed.MediaSequence < _lastPlaylistMediaSequence;
                        if (sequenceRegressed)
                        {
                            // Keep the relay's public sequence strictly increasing even
                            // when an origin restarts its HLS media-sequence counter.
                            _sourceSequenceOffset = _liveEdgeSequence + 1 - parsed.MediaSequence;
                            nextSequence = parsed.MediaSequence;
                            seen.Clear();
                            FailRecordingConsumersForSequenceRegression();
                        }
                        _lastPlaylistMediaSequence = parsed.MediaSequence;
                        _advertisedEdgeSequence = Math.Max(_advertisedEdgeSequence,
                            parsed.Segments[^1].Sequence + _sourceSequenceOffset);
                    }
                    if (parsed.TargetDurationSeconds is double targetDuration)
                        pollInterval = TimeSpan.FromMilliseconds(Math.Clamp(targetDuration * 500, 500, 2000));
                    nextSequence ??= parsed.MediaSequence + parsed.Segments.Count;
                    var firstAfterSequenceReset = sequenceRegressed;
                    foreach (var item in parsed.Segments)
                    {
                        var identity = (item.Sequence, item.Uri.AbsoluteUri);
                        if (item.Sequence < nextSequence.Value || !seen.Add(identity)) continue;
                        var bytes = await DownloadSegmentAsync(item.Uri, cancellationToken).ConfigureAwait(false);
                        var internalSequence = item.Sequence + _sourceSequenceOffset;
                        var segment = new SharedHlsSegment(internalSequence, item.Duration, item.Uri, bytes,
                            item.ProgramDateTimeUtc, item.IsDiscontinuity || firstAfterSequenceReset);
                        firstAfterSequenceReset = false;
                        lock (_gate)
                        {
                            _liveEdgeSequence = Math.Max(_liveEdgeSequence, item.Sequence);
                            CachePlaybackSegment(segment);
                        }
                        bool timeshiftAvailable;
                        lock (_gate) timeshiftAvailable = _timeshiftFailure is null;
                        try
                        {
                            if (timeshiftAvailable) _timeshiftBuffer.Append(segment);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                        {
                            lock (_gate) _timeshiftFailure ??= exception;
                            try { SourceError?.Invoke(this, exception); } catch { }
                        }
                        FanOut(segment);
                        nextSequence = item.Sequence + 1;
                    }
                    var floor = parsed.MediaSequence - 4;
                    if (seen.Count > 4096)
                        seen.RemoveWhere(key => key.Sequence < floor);
                }
                if (parsed.EndList) break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                try { SourceError?.Invoke(this, exception); } catch { }
                if (exception is InvalidDataException or NotSupportedException)
                {
                    lock (_gate)
                    {
                        _terminalFailure = exception;
                        foreach (var consumer in _consumers) consumer.Fail(exception);
                    }
                    break;
                }
            }

            try { await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        lock (_gate)
            foreach (var consumer in _consumers) consumer.Complete();
    }

    private async Task<byte[]> DownloadSegmentAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > _maximumSegmentBytes)
            throw new InvalidDataException("An HLS segment exceeds the configured size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > _maximumSegmentBytes)
                throw new InvalidDataException("An HLS segment exceeds the configured size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private void FanOut(SharedHlsSegment segment)
    {
        Consumer[] consumers;
        lock (_gate)
            consumers = _consumers.ToArray();

        foreach (var consumer in consumers)
        {
            if (segment.IsDiscontinuity && consumer.Kind == SharedHlsConsumerKind.Recording)
            {
                consumer.Fail(new InvalidDataException("The HLS stream signaled a discontinuity; recording stopped to avoid joining incompatible media timelines."));
                continue;
            }
            consumer.Enqueue(segment, _maximumBufferedBytesPerConsumer);
        }
    }

    private void FailRecordingConsumersForSequenceRegression()
    {
        var failure = new InvalidDataException("The HLS media sequence regressed; recording stopped because segment identity can no longer be guaranteed.");
        foreach (var consumer in _consumers.Where(consumer => consumer.Kind == SharedHlsConsumerKind.Recording))
            consumer.Fail(failure);
    }

    private void CachePlaybackSegment(SharedHlsSegment segment)
    {
        _playbackCache.AddLast(segment);
        _playbackCacheBytes += segment.Data.Length;
        while (_playbackCache.Count > 1 && _playbackCacheBytes > _maximumPlaybackCacheBytes)
        {
            _playbackCacheBytes -= _playbackCache.First!.Value.Data.Length;
            _playbackCache.RemoveFirst();
        }
    }

    private async Task ServePlaybackAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
                client.NoDelay = true;
                lock (_gate)
                {
                    if (_playbackHandlers.Count >= MaximumPlaybackClients)
                    {
                        client.Dispose();
                        continue;
                    }
                }
                Task handler = HandlePlaybackClientAsync(client, cancellationToken);
                lock (_gate) _playbackHandlers.Add(handler);
                _ = handler.ContinueWith(completed =>
                {
                    lock (_gate) _playbackHandlers.Remove(completed);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandlePlaybackClientAsync(TcpClient client, CancellationToken lifetimeToken)
    {
        using var ownedClient = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var cancellationToken = timeout.Token;
        try
        {
            var stream = client.GetStream();
            var request = await ReadPlaybackRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            if (request is null) return;
            var (method, path) = request.Value;
            if (method is not ("GET" or "HEAD"))
            {
                await WritePlaybackResponseAsync(stream, 405, "Method Not Allowed", "text/plain", ReadOnlyMemory<byte>.Empty, method == "HEAD", cancellationToken).ConfigureAwait(false);
                return;
            }
            string? cursorId = null;
            long cursorStart = 0;
            var isCursorPlaylist = TryParseCursorPlaylistPath(path, out cursorId, out cursorStart);
            if (path == "/index.m3u8" || isCursorPlaylist)
            {
                SharedHlsSegment[] snapshot;
                byte[] body;
                try
                {
                    lock (_gate)
                    {
                        long? start;
                        if (cursorId is not null)
                        {
                            if (!_playbackCursors.TryGetValue(cursorId, out var cursor) || cursor.InitialSequence != cursorStart)
                                throw new LiveTimeshiftOverrunException();
                            var window = _timeshiftBuffer.Inspect();
                            if (!window.HasData || window.OldestSequence is not long oldest)
                                throw new LiveTimeshiftOverrunException();
                            // If playback falls behind retention, advance it to the oldest
                            // surviving sequence. Sequence numbers stay monotonic.
                            cursor.StartSequence = Math.Max(cursor.StartSequence, oldest);
                            cursor.LastAccessUtc = DateTimeOffset.UtcNow;
                            start = cursor.StartSequence;
                        }
                        else start = _playbackStartSequence;
                        snapshot = start is long selectedStart
                            ? _timeshiftBuffer.ReadWindowFrom(selectedStart).ToArray()
                            : _playbackCache.ToArray();
                        if (cursorId is not null && snapshot.Length > 0)
                            cursorStart = _playbackCursors[cursorId].InitialSequence;
                    }
                    body = cursorId is null
                        ? BuildPlaybackPlaylist(snapshot)
                        : BuildCursorPlaybackPlaylist(snapshot, $"/cursor/{cursorId}/{cursorStart.ToString(CultureInfo.InvariantCulture)}/segment/");
                }
                catch (LiveTimeshiftOverrunException)
                {
                    snapshot = [];
                    body = Encoding.UTF8.GetBytes("#EXTM3U\n");
                }
                await WritePlaybackResponseAsync(stream, snapshot.Length == 0 ? 503 : 200,
                    snapshot.Length == 0 ? "Service Unavailable" : "OK", "application/vnd.apple.mpegurl",
                    body.AsMemory(), method == "HEAD", cancellationToken).ConfigureAwait(false);
                return;
            }

            const string cursorPrefix = "/cursor/";
            var segmentPath = path;
            if (path.StartsWith(cursorPrefix, StringComparison.Ordinal))
            {
                var cursorParts = path[cursorPrefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (cursorParts.Length != 4 || !long.TryParse(cursorParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var cursorSequence) ||
                    cursorParts[2] != "segment" || !long.TryParse(cursorParts[3], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    await WritePlaybackResponseAsync(stream, 404, "Not Found", "text/plain", ReadOnlyMemory<byte>.Empty, method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }
                bool validCursor;
                lock (_gate) validCursor = _playbackCursors.TryGetValue(cursorParts[0], out var cursor) && cursor.InitialSequence == cursorSequence;
                if (validCursor)
                {
                    lock (_gate)
                    {
                        if (_playbackCursors.TryGetValue(cursorParts[0], out var cursor)) cursor.LastAccessUtc = DateTimeOffset.UtcNow;
                    }
                }
                if (!validCursor)
                {
                    await WritePlaybackResponseAsync(stream, 404, "Not Found", "text/plain", ReadOnlyMemory<byte>.Empty, method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }
                segmentPath = "/segment/" + cursorParts[3];
            }
            const string prefix = "/segment/";
            if (segmentPath.StartsWith(prefix, StringComparison.Ordinal) &&
                long.TryParse(segmentPath.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
            {
                _localPlaybackSegmentRequests.AddOrUpdate(sequence, 1, (_, count) => checked(count + 1));
                SharedHlsSegment? segment;
                lock (_gate)
                {
                    segment = _playbackCache.FirstOrDefault(item => item.Sequence == sequence);
                    if (segment is null)
                    {
                        try { segment = _timeshiftBuffer.Read(sequence); }
                        catch (LiveTimeshiftOverrunException) { }
                    }
                }
                if (segment is null)
                {
                    await WritePlaybackResponseAsync(stream, 404, "Not Found", "text/plain", ReadOnlyMemory<byte>.Empty, method == "HEAD", cancellationToken).ConfigureAwait(false);
                    return;
                }
                await WritePlaybackResponseAsync(stream, 200, "OK", "video/mp2t", segment.Data,
                    method == "HEAD", cancellationToken).ConfigureAwait(false);
                return;
            }
            await WritePlaybackResponseAsync(stream, 404, "Not Found", "text/plain", ReadOnlyMemory<byte>.Empty, method == "HEAD", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
    }

    private static async Task<(string Method, string Path)?> ReadPlaybackRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        const int maxHeaderBytes = 8192;
        var bytes = new byte[maxHeaderBytes];
        var count = 0;
        var matched = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            var value = bytes[count++];
            matched = (matched, value) switch
            {
                (0, 13) => 1,
                (1, 10) => 2,
                (2, 13) => 3,
                (3, 10) => 4,
                (_, 13) => 1,
                _ => 0
            };
            if (matched == 4) break;
        }
        if (matched != 4) return ("", "");
        var header = Encoding.ASCII.GetString(bytes, 0, count - 4);
        var firstLineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLine = firstLineEnd < 0 ? header : header[..firstLineEnd];
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return ("", "");
        var path = parts[1];
        if (path.Length == 0 || path[0] != '/' || path.Contains('#') || path.Contains('?')) return ("", "");
        return (parts[0], path);
    }

    private static async Task WritePlaybackResponseAsync(NetworkStream stream, int status, string reason,
        string contentType, ReadOnlyMemory<byte> body, bool headOnly, CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!headOnly && body.Length > 0)
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal byte[] BuildPlaybackPlaylist(IReadOnlyList<SharedHlsSegment> segments) =>
        BuildPlaybackPlaylistCore(segments, "segment/");

    private byte[] BuildCursorPlaybackPlaylist(IReadOnlyList<SharedHlsSegment> segments, string segmentPrefix) =>
        BuildPlaybackPlaylistCore(segments, segmentPrefix);

    private byte[] BuildPlaybackPlaylistCore(IReadOnlyList<SharedHlsSegment> segments, string segmentPrefix)
    {
        var targetDuration = Math.Max(1, (int)Math.Ceiling(segments.Count == 0 ? _pollInterval.TotalSeconds : segments.Max(item => item.DurationSeconds)));
        var firstSequence = segments.Count == 0 ? Math.Max(0, _liveEdgeSequence + 1) : segments[0].Sequence;
        var builder = new StringBuilder().AppendLine("#EXTM3U").AppendLine("#EXT-X-VERSION:3")
            .Append("#EXT-X-TARGETDURATION:").AppendLine(targetDuration.ToString(CultureInfo.InvariantCulture))
            .Append("#EXT-X-MEDIA-SEQUENCE:").AppendLine(firstSequence.ToString(CultureInfo.InvariantCulture));
        foreach (var item in segments)
        {
            if (item.ProgramDateTimeUtc is DateTimeOffset programTime)
                builder.Append("#EXT-X-PROGRAM-DATE-TIME:").AppendLine(programTime.ToString("O", CultureInfo.InvariantCulture));
            if (item.IsDiscontinuity) builder.AppendLine("#EXT-X-DISCONTINUITY");
            builder.Append("#EXTINF:").Append(item.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine(",")
                .Append(segmentPrefix).Append(item.Sequence.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static bool TryParseCursorPlaylistPath(string path, out string? cursorId, out long cursorStart)
    {
        cursorId = null;
        cursorStart = 0;
        const string prefix = "/cursor/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[2] != "index.m3u8" || parts[0].Length != 32 ||
            !Guid.TryParseExact(parts[0], "N", out _) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out cursorStart)) return false;
        cursorId = parts[0];
        return true;
    }

    private sealed class PlaybackCursor(long startSequence)
    {
        public long InitialSequence { get; } = startSequence;
        public long StartSequence = startSequence;
        public DateTimeOffset LastAccessUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Testing seam: snapshot the exact playlist currently advertised to LibVLC.</summary>
    internal IReadOnlyList<long> GetPlaybackPlaylistSequences()
    {
        lock (_gate)
        {
            if (_playbackStartSequence is long start)
                return _timeshiftBuffer.ReadWindowFrom(start).Select(item => item.Sequence).ToArray();
            return _playbackCache.Select(item => item.Sequence).ToArray();
        }
    }

    internal long? GetPlaybackResumeSequence()
    {
        lock (_gate) return _playbackResumeSequence;
    }

    private static ParsedPlaylist ParsePlaylist(Uri baseUri, string text)
    {
        if (text.Length > 1_048_576) throw new InvalidDataException("The HLS playlist exceeds the size limit.");
        var lines = text.Split('\n').Select(line => line.Trim().TrimEnd('\r')).ToArray();
        if (lines.Length == 0 || lines[0] != "#EXTM3U") throw new InvalidDataException("The response is not an HLS playlist.");
        var variantPending = false;
        Uri? bestVariant = null;
        long bestBandwidth = -1;
        var sequence = 0L;
        var duration = 0d;
        double? targetDuration = null;
        DateTimeOffset? programTime = null;
        var nextSegmentDiscontinuity = false;
        var segments = new List<PlaylistSegment>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                var bandwidth = ParseBandwidth(line);
                var variantUri = i + 1 < lines.Length && !lines[i + 1].StartsWith('#') && lines[i + 1].Length > 0
                    ? ResolvePlaylistUri(baseUri, lines[i + 1]) : null;
                if (variantUri is not null && (bestVariant is null || bandwidth > bestBandwidth))
                {
                    bestVariant = variantUri;
                    bestBandwidth = bandwidth;
                }
                variantPending = true;
                continue;
            }
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
                throw new NotSupportedException("This shared HLS relay does not support EXT-X-KEY encryption; direct playback can be used instead.");
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                throw new NotSupportedException("This shared HLS relay does not support EXT-X-MAP initialization sections; direct playback can be used instead.");
            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                throw new NotSupportedException("This shared HLS relay does not support EXT-X-BYTERANGE segments; direct playback can be used instead.");
            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-PART", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-PRELOAD-HINT:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-SKIP:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-GAP", StringComparison.Ordinal))
                throw new NotSupportedException("Encrypted, fragmented, byte-range, or alternate-rendition HLS playlists are not supported by the local relay.");
            if (line == "#EXT-X-DISCONTINUITY")
            {
                nextSegmentDiscontinuity = true;
                continue;
            }
            if (line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE:", StringComparison.Ordinal))
                throw new NotSupportedException("This shared HLS relay does not support EXT-X-DISCONTINUITY-SEQUENCE.");
            if (line.StartsWith("#EXT-X-", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-INDEPENDENT-SEGMENTS", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal))
                throw new NotSupportedException("The HLS playlist contains an unsupported extension tag.");
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                if (double.TryParse(line.AsSpan("#EXT-X-TARGETDURATION:".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTarget) && parsedTarget > 0)
                    targetDuration = parsedTarget;
                continue;
            }
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                _ = long.TryParse(line.AsSpan("#EXT-X-MEDIA-SEQUENCE:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
                continue;
            }
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line.AsSpan(8);
                var comma = value.IndexOf(',');
                if (comma >= 0) value = value[..comma];
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out duration) || duration < 0)
                    duration = 0;
                continue;
            }
            if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal))
            {
                if (DateTimeOffset.TryParse(line.AsSpan(25), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTime))
                    programTime = parsedTime;
                continue;
            }
            if (line.StartsWith('#') || line.Length == 0) continue;
            var resolved = ResolvePlaylistUri(baseUri, line);
            if (variantPending) { variantPending = false; continue; }
            segments.Add(new PlaylistSegment(sequence++, duration, resolved, programTime, nextSegmentDiscontinuity));
            nextSegmentDiscontinuity = false;
            if (programTime is not null) programTime += TimeSpan.FromSeconds(duration);
            duration = 0;
        }
        if (bestVariant is not null) return new ParsedPlaylist(bestVariant, sequence, [], false, null);
        return new ParsedPlaylist(null, segments.Count == 0 ? sequence : segments[0].Sequence, segments,
            lines.Any(line => line == "#EXT-X-ENDLIST"), targetDuration);
    }

    private static long ParseBandwidth(string streamInfo)
    {
        foreach (var attribute in streamInfo.AsSpan("#EXT-X-STREAM-INF:".Length).ToString().Split(','))
        {
            var pair = attribute.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals("BANDWIDTH", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bandwidth))
                return bandwidth;
        }
        return 0;
    }

    private static Uri ResolvePlaylistUri(Uri baseUri, string reference)
    {
        var isAbsolute = Uri.TryCreate(reference, UriKind.Absolute, out _);
        var resolved = new Uri(baseUri, reference);
        if (resolved.Scheme is not ("http" or "https"))
            throw new NotSupportedException("Only HTTP(S) HLS URLs are supported by the local relay.");
        if (!isAbsolute && resolved.Authority.Equals(baseUri.Authority, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(baseUri.Query))
        {
            var referenceKeys = resolved.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => Uri.UnescapeDataString(pair.Split('=', 2)[0])).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inherited = baseUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(pair => !referenceKeys.Contains(Uri.UnescapeDataString(pair.Split('=', 2)[0]))).ToArray();
            if (inherited.Length > 0)
            {
                var builder = new UriBuilder(resolved);
                builder.Query = string.Join('&', new[] { resolved.Query.TrimStart('?'), string.Join('&', inherited) }
                    .Where(query => !string.IsNullOrEmpty(query)));
                resolved = builder.Uri;
            }
        }
        return resolved;
    }

    /// <summary>Parser seam for deterministic tests and callers that inspect playlist behavior.</summary>
    public static IReadOnlyList<Uri> ResolveMediaSegmentUris(Uri playlistUri, string playlistText) =>
        ParsePlaylist(playlistUri, playlistText).Segments.Select(segment => segment.Uri).ToArray();

    public async ValueTask DisposeAsync()
    {
        Task? runTask;
        Task? playbackServerTask;
        TcpListener? listener;
        Task[] handlers;
        Consumer[] consumers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            runTask = _runTask;
            playbackServerTask = _playbackServerTask;
            listener = _playbackListener;
            _playbackListener = null;
            _lifetime.Cancel();
            try { listener?.Stop(); } catch { }
            handlers = _playbackHandlers.ToArray();
            consumers = _consumers.ToArray();
            _consumers.Clear();
        }
        foreach (var consumer in consumers) consumer.Complete();
        if (runTask is not null)
            try { await runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (playbackServerTask is not null)
            try { await playbackServerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (handlers.Length > 0)
            try { await Task.WhenAll(handlers).ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (_ownsHttp) _http.Dispose();
        _timeshiftBuffer.Dispose();
        _lifetime.Dispose();
    }

    private sealed record PlaylistSegment(long Sequence, double Duration, Uri Uri, DateTimeOffset? ProgramDateTimeUtc, bool IsDiscontinuity);
    private sealed record ParsedPlaylist(Uri? VariantUri, long MediaSequence, IReadOnlyList<PlaylistSegment> Segments, bool EndList, double? TargetDurationSeconds);

    internal sealed class Consumer(long id, SharedHlsConsumerKind kind)
    {
        private readonly object _gate = new();
        private readonly Queue<SharedHlsSegment> _queue = new();
        private readonly SemaphoreSlim _available = new(0);
        private int _bufferedBytes;
        private bool _detached;
        private bool _completed;

        public long Id { get; } = id;
        public SharedHlsConsumerKind Kind { get; } = kind;
        public long MinimumSequence { get; set; }
        public Exception? Failure { get { lock (_gate) return _failure; } }

        public void Enqueue(SharedHlsSegment segment, int limit)
        {
            lock (_gate)
            {
                if (_detached || _completed) return;
                if (segment.Sequence < MinimumSequence) return;
                if (Kind == SharedHlsConsumerKind.Playback)
                {
                    while (_queue.Count > 0 && _bufferedBytes + segment.Data.Length > limit)
                    {
                        _bufferedBytes -= _queue.Dequeue().Data.Length;
                        _ = _available.Wait(0);
                    }
                }
                else if (_bufferedBytes + segment.Data.Length > limit)
                {
                    _failure = new IOException("The recording consumer exceeded its bounded HLS buffer; capture stopped to avoid missing media.");
                    _completed = true;
                    _available.Release();
                    return;
                }
                _queue.Enqueue(segment);
                _bufferedBytes += segment.Data.Length;
                _available.Release();
            }
        }

        public async ValueTask<SharedHlsSegment?> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_queue.TryDequeue(out var segment))
                    {
                        _bufferedBytes -= segment.Data.Length;
                        _ = _available.Wait(0);
                        return segment;
                    }
                    if (_failure is not null) throw new IOException(_failure.Message, _failure);
                    if (_completed || _detached) return null;
                }
                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public bool Detach()
        {
            lock (_gate)
            {
                if (_detached) return false;
                _detached = true;
                _queue.Clear();
                _bufferedBytes = 0;
                _completed = true;
                _available.Release();
                return true;
            }
        }

        private Exception? _failure;

        public void Fail(Exception exception)
        {
            lock (_gate)
            {
                if (_completed || _detached) return;
                _failure = exception;
                _completed = true;
                _available.Release();
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_completed) return;
                _completed = true;
                _available.Release();
            }
        }
    }
}

/// <summary>Bounded disk-backed cache of complete HLS media segments.</summary>
public sealed class LiveTimeshiftBuffer : IDisposable
{
    private static readonly object CleanupGate = new();
    private static readonly HashSet<string> CleanedRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan StaleBufferAge = TimeSpan.FromDays(2);
    private readonly object _gate = new();
    private readonly LinkedList<Entry> _entries = [];
    private readonly string _directory;
    private readonly FileStream _lease;
    private readonly long _maximumBytes;
    private readonly TimeSpan _maximumDuration;
    private long _bytes;
    private double _duration;
    private bool _disposed;

    public LiveTimeshiftBuffer(long maximumBytes = 2L * 1024 * 1024 * 1024,
        TimeSpan? maximumDuration = null, string? temporaryRoot = null)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
        _maximumDuration = maximumDuration ?? TimeSpan.FromMinutes(30);
        if (_maximumDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        var root = Path.GetFullPath(temporaryRoot ?? Path.GetTempPath());
        Directory.CreateDirectory(root);
        CleanupStaleDirectoriesOnce(root);
        _directory = Path.Combine(root, "cyrus-timeshift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        try
        {
            _lease = new FileStream(Path.Combine(_directory, ".active"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch
        {
            try { Directory.Delete(_directory, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public LiveTimeshiftWindow Inspect()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _entries.Count == 0
                ? new LiveTimeshiftWindow(false, null, null, TimeSpan.Zero, _bytes, _maximumBytes, null)
                : new LiveTimeshiftWindow(true, _entries.First!.Value.Sequence, _entries.Last!.Value.Sequence,
                    TimeSpan.FromSeconds(_duration), _bytes, _maximumBytes, _entries.Last!.Value.ProgramDateTimeUtc);
        }
    }

    public void Append(SharedHlsSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.DurationSeconds <= 0 || double.IsNaN(segment.DurationSeconds) || double.IsInfinity(segment.DurationSeconds))
            throw new InvalidDataException("The HLS segment has an invalid duration.");
        if (segment.Data.Length == 0 || segment.Data.Length > _maximumBytes)
            throw new InvalidDataException("The HLS segment cannot fit in the configured timeshift buffer.");
        if (segment.DurationSeconds > _maximumDuration.TotalSeconds)
            throw new InvalidDataException("The HLS segment is longer than the configured timeshift retention window.");

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.Last is { } last && segment.Sequence <= last.Value.Sequence)
            {
                if (segment.Sequence == last.Value.Sequence) return;
                throw new InvalidDataException("The HLS media sequence regressed; timeshift is unavailable for this source.");
            }

            while (_entries.Count > 0 && (_bytes + segment.Data.Length > _maximumBytes ||
                _duration + segment.DurationSeconds > _maximumDuration.TotalSeconds))
                EvictFirst();

            var path = Path.Combine(_directory, segment.Sequence.ToString(CultureInfo.InvariantCulture) + ".segment");
            File.WriteAllBytes(path, segment.Data.ToArray());
            var entry = new Entry(segment.Sequence, segment.DurationSeconds, path, segment.Data.Length,
                segment.ProgramDateTimeUtc, segment.IsDiscontinuity);
            _entries.AddLast(entry);
            _bytes += entry.Length;
            _duration += entry.DurationSeconds;
        }
    }

    public LiveTimeshiftSeekResult Seek(long sequence, out LiveTimeshiftCursor? cursor)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            cursor = null;
            if (_entries.Count == 0) return LiveTimeshiftSeekResult.Empty;
            if (sequence < _entries.First!.Value.Sequence) return LiveTimeshiftSeekResult.Overrun;
            if (sequence > _entries.Last!.Value.Sequence) return LiveTimeshiftSeekResult.AfterLiveEdge;
            if (!_entries.Any(entry => entry.Sequence == sequence)) return LiveTimeshiftSeekResult.Unsupported;
            cursor = new LiveTimeshiftCursor(this, sequence);
            return LiveTimeshiftSeekResult.Positioned;
        }
    }

    internal SharedHlsSegment? Read(long sequence)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var first = _entries.First?.Value.Sequence;
            if (first is null || sequence < first.Value) throw new LiveTimeshiftOverrunException();
            var entry = _entries.FirstOrDefault(item => item.Sequence == sequence);
            if (entry is null) return null;
            return new SharedHlsSegment(entry.Sequence, entry.DurationSeconds,
                new Uri("timeshift://segment/" + entry.Sequence), File.ReadAllBytes(entry.Path),
                entry.ProgramDateTimeUtc, entry.IsDiscontinuity);
        }
    }

    internal IReadOnlyList<SharedHlsSegment> ReadWindowFrom(long sequence)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var first = _entries.First?.Value.Sequence;
            if (first is null || sequence < first.Value) throw new LiveTimeshiftOverrunException();
            return _entries.Where(entry => entry.Sequence >= sequence)
                .Select(entry => new SharedHlsSegment(entry.Sequence, entry.DurationSeconds,
                    new Uri("timeshift://segment/" + entry.Sequence), File.ReadAllBytes(entry.Path),
                    entry.ProgramDateTimeUtc, entry.IsDiscontinuity)).ToArray();
        }
    }

    private void EvictFirst()
    {
        var entry = _entries.First!.Value;
        try
        {
            if (File.Exists(entry.Path)) File.Delete(entry.Path);
            if (File.Exists(entry.Path)) throw new IOException("The expired timeshift segment could not be removed from disk.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The timeshift buffer could not remove an expired segment; disk buffering has stopped to protect the configured limit.", exception);
        }
        _entries.RemoveFirst();
        _bytes -= entry.Length;
        _duration -= entry.DurationSeconds;
    }

    private static void CleanupStaleDirectoriesOnce(string root)
    {
        lock (CleanupGate)
        {
            if (!CleanedRoots.Add(root)) return;
            var staleBeforeUtc = DateTime.UtcNow - StaleBufferAge;
            try
            {
                foreach (var path in Directory.EnumerateDirectories(root, "cyrus-timeshift-*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var info = new DirectoryInfo(path);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LastWriteTimeUtc >= staleBeforeUtc) continue;

                        // Each live buffer holds this file exclusively. A crashed process
                        // releases the handle, so only abandoned, old buffers are removed.
                        using (new FileStream(Path.Combine(path, ".active"), FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None)) { }
                        Directory.Delete(path, recursive: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _entries.Clear();
            _bytes = 0;
            _duration = 0;
            _lease.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record Entry(long Sequence, double DurationSeconds, string Path, int Length,
        DateTimeOffset? ProgramDateTimeUtc, bool IsDiscontinuity);
}

public sealed record LiveTimeshiftWindow(bool HasData, long? OldestSequence, long? LiveSequence,
    TimeSpan Duration, long BufferedBytes, long MaximumBytes, DateTimeOffset? LiveProgramTimeUtc);

public enum LiveTimeshiftSeekResult { Positioned, Empty, Overrun, AfterLiveEdge, Unsupported }

public sealed class LiveTimeshiftCursor
{
    private readonly LiveTimeshiftBuffer _buffer;
    private long _nextSequence;
    internal LiveTimeshiftCursor(LiveTimeshiftBuffer buffer, long sequence) { _buffer = buffer; _nextSequence = sequence; }
    public long NextSequence => _nextSequence;
    public SharedHlsSegment? ReadNext()
    {
        var segment = _buffer.Read(_nextSequence);
        if (segment is not null) _nextSequence++;
        return segment;
    }
}

public sealed class LiveTimeshiftOverrunException() : IOException("The timeshift cursor was overtaken by buffer retention; seek to the oldest buffered segment.");

public enum SharedHlsConsumerKind { Playback, Recording }

/// <summary>A byte-preserving HLS media segment delivered by a shared source.</summary>
public sealed record SharedHlsSegment(long Sequence, double DurationSeconds, Uri SourceUri,
    ReadOnlyMemory<byte> Data, DateTimeOffset? ProgramDateTimeUtc, bool IsDiscontinuity = false);

/// <summary>Independent bounded reader; dispose it at the exact recording stop action.</summary>
public sealed class SharedHlsConsumer : IAsyncDisposable
{
    private readonly SharedHlsSource _source;
    private readonly SharedHlsSource.Consumer _consumer;
    private int _disposed;

    internal SharedHlsConsumer(SharedHlsSource source, SharedHlsSource.Consumer consumer)
    { _source = source; _consumer = consumer; }

    public SharedHlsConsumerKind Kind => _consumer.Kind;
    public ValueTask<SharedHlsSegment?> ReadAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _disposed) == 0 ? _source.ReadAsync(_consumer, cancellationToken) : ValueTask.FromResult<SharedHlsSegment?>(null);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _source.Detach(_consumer);
        return ValueTask.CompletedTask;
    }
}
