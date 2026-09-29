using ClassicWindowsIptvPlayer.Windows;
using System.Reflection;

internal static class SharedHlsSourceRegressionChecks
{
    public static int Run()
    {
        Console.WriteLine("Starting shared HLS regression checks.");
        return RunAsync().GetAwaiter().GetResult();
    }

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        Check("relative media segment URIs resolve from playlist", () =>
        {
            var uris = SharedHlsSource.ResolveMediaSegmentUris(new Uri("https://example.test/live/index.m3u8"),
                "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:40\n#EXTINF:6.0,\nseg40.ts\n#EXTINF:6,\n../seg41.ts\n#EXT-X-ENDLIST\n");
            Equal(2, uris.Count);
            Equal("https://example.test/live/seg40.ts", uris[0].AbsoluteUri);
            Equal("https://example.test/seg41.ts", uris[1].AbsoluteUri);
        });
        Check("relative authenticated segment URLs inherit playlist query without overriding child keys", () =>
        {
            var uris = SharedHlsSource.ResolveMediaSegmentUris(new Uri("https://example.test/live/index.m3u8?token=secret&region=eu"),
                "#EXTM3U\n#EXTINF:6,\nseg.ts?token=child\n");
            Equal("https://example.test/live/seg.ts?token=child&region=eu", uris[0].AbsoluteUri);
        });
        Check("relative URLs do not receive query credentials across authorities", () =>
        {
            var uris = SharedHlsSource.ResolveMediaSegmentUris(new Uri("https://example.test/live/index.m3u8?token=secret"),
                "#EXTM3U\n#EXTINF:6,\n//cdn.example.test/seg.ts\n");
            Equal("https://cdn.example.test/seg.ts", uris[0].AbsoluteUri);
        });
        Check("master playlist selects highest bandwidth variant", () =>
        {
            var parsed = Parse(new Uri("https://example.test/root.m3u8?token=master"),
                "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nlow/live.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=9000\nhi/live.m3u8\n");
            Equal("https://example.test/hi/live.m3u8?token=master", Property<Uri>(parsed, "VariantUri").AbsoluteUri);
        });
        Check("recording attaches after both downloaded and advertised edges while playback keeps cache access", () =>
        {
            var method = typeof(SharedHlsSource).GetMethod("GetMinimumSequence", BindingFlags.NonPublic | BindingFlags.Static)!;
            var recordingStart = (long)method.Invoke(null, [SharedHlsConsumerKind.Recording, 40L, 47L])!;
            var playbackStart = (long)method.Invoke(null, [SharedHlsConsumerKind.Playback, 40L, 47L])!;
            Equal(48L, recordingStart);
            Equal(0L, playbackStart);
        });
        Check("recording failure is isolated from playback consumer", () =>
        {
            var consumerType = typeof(SharedHlsSource).GetNestedType("Consumer", BindingFlags.NonPublic)!;
            var recording = Activator.CreateInstance(consumerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [1L, SharedHlsConsumerKind.Recording], culture: null)!;
            var playback = Activator.CreateInstance(consumerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [2L, SharedHlsConsumerKind.Playback], culture: null)!;
            var fail = consumerType.GetMethod("Fail")!;
            fail.Invoke(recording, [new IOException("recording overflow")]);
            var failureProperty = consumerType.GetProperty("Failure")!;
            Equal(true, failureProperty.GetValue(recording) is IOException);
            Equal(null, failureProperty.GetValue(playback));
            var enqueue = consumerType.GetMethod("Enqueue")!;
            enqueue.Invoke(playback, [new SharedHlsSegment(7, 5, new Uri("https://example.test/7.ts"), new byte[] { 1 }, null), 1024]);
            var read = consumerType.GetMethod("ReadAsync")!;
            var pending = (ValueTask<SharedHlsSegment?>)read.Invoke(playback, [CancellationToken.None])!;
            Equal(7L, pending.GetAwaiter().GetResult()!.Sequence);
        });
        await CheckAsync("sequence regression fails recording consumer without ending shared source", async () =>
        {
            using var http = new HttpClient(new DeterministicPlaylistHandler());
            var source = new SharedHlsSource(new Uri("https://example.test/live.m3u8"), http, maximumSegmentBytes: 1024,
                maximumBufferedBytesPerConsumer: 1024, maximumPlaybackCacheBytes: 1024);
            try
            {
                var regression = typeof(SharedHlsSource).GetMethod("FailRecordingConsumersForSequenceRegression", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var recording = source.Attach(SharedHlsConsumerKind.Recording);
                var playback = source.Attach(SharedHlsConsumerKind.Playback);
                regression.Invoke(source, null);
                var failed = false;
                try { _ = await recording.ReadAsync(); }
                catch (IOException exception) when (exception.Message.Contains("sequence", StringComparison.OrdinalIgnoreCase)) { failed = true; }
                Equal(true, failed);
                Equal(null, source.TerminalFailure);
                var fanOut = typeof(SharedHlsSource).GetMethod("FanOut", BindingFlags.NonPublic | BindingFlags.Instance)!;
                fanOut.Invoke(source, [new SharedHlsSegment(3, 5, new Uri("https://example.test/3.ts"), new byte[] { 1 }, null)]);
                Equal(3L, (await playback.ReadAsync())!.Sequence);
                await recording.DisposeAsync();
                await playback.DisposeAsync();
            }
            finally { await source.DisposeAsync(); }
        });
        await CheckAsync("discontinuity fails recording but remains available to playback", async () =>
        {
            using var http = new HttpClient(new DeterministicPlaylistHandler());
            var source = new SharedHlsSource(new Uri("https://example.test/live.m3u8"), http, maximumSegmentBytes: 1024,
                maximumBufferedBytesPerConsumer: 1024, maximumPlaybackCacheBytes: 1024);
            try
            {
                var recording = source.Attach(SharedHlsConsumerKind.Recording);
                var playback = source.Attach(SharedHlsConsumerKind.Playback);
                var fanOut = typeof(SharedHlsSource).GetMethod("FanOut", BindingFlags.NonPublic | BindingFlags.Instance)!;
                fanOut.Invoke(source, [new SharedHlsSegment(9, 5, new Uri("https://example.test/9.ts"), new byte[] { 1 }, null, true)]);
                var failed = false;
                try { _ = await recording.ReadAsync(); }
                catch (IOException exception) when (exception.Message.Contains("discontinuity", StringComparison.OrdinalIgnoreCase)) { failed = true; }
                Equal(true, failed);
                Equal(true, (await playback.ReadAsync())!.IsDiscontinuity);
                await recording.DisposeAsync();
                await playback.DisposeAsync();
            }
            finally { await source.DisposeAsync(); }
        });
        await CheckAsync("fan-out queue work does not hold source lock and detached snapshots are safe", async () =>
        {
            using var http = new HttpClient(new DeterministicPlaylistHandler());
            var source = new SharedHlsSource(new Uri("https://example.test/live.m3u8"), http, maximumSegmentBytes: 1024,
                maximumBufferedBytesPerConsumer: 1024, maximumPlaybackCacheBytes: 1024);
            try
            {
                var playback = source.Attach(SharedHlsConsumerKind.Playback);
                var sourceGate = typeof(SharedHlsSource).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(source)!;
                var consumer = typeof(SharedHlsConsumer).GetField("_consumer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(playback)!;
                var consumerGate = consumer.GetType().GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(consumer)!;
                var fanOut = typeof(SharedHlsSource).GetMethod("FanOut", BindingFlags.NonPublic | BindingFlags.Instance)!;
                using var consumerLockHeld = new ManualResetEventSlim();
                using var allowConsumerWork = new ManualResetEventSlim();
                var blocker = Task.Run(() =>
                {
                    lock (consumerGate)
                    {
                        consumerLockHeld.Set();
                        if (!allowConsumerWork.Wait(TimeSpan.FromSeconds(3)))
                            throw new TimeoutException("Timed out waiting to release the consumer queue guard.");
                    }
                });
                if (!consumerLockHeld.Wait(TimeSpan.FromSeconds(3)))
                    throw new TimeoutException("Could not acquire consumer queue guard for the test.");

                var segment = new SharedHlsSegment(1, 5, new Uri("https://example.test/1.ts"), new byte[] { 1 }, null);
                using var fanOutStarted = new ManualResetEventSlim();
                Exception? fanOutFailure = null;
                var fanOutThread = new Thread(() =>
                {
                    fanOutStarted.Set();
                    try { fanOut.Invoke(source, [segment]); }
                    catch (Exception exception) { fanOutFailure = exception; }
                }) { IsBackground = true };
                fanOutThread.Start();
                try
                {
                    if (!fanOutStarted.Wait(TimeSpan.FromSeconds(3)))
                        throw new TimeoutException("Fan-out worker did not start.");
                    var waitDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                    while ((fanOutThread.ThreadState & ThreadState.WaitSleepJoin) == 0 && DateTime.UtcNow < waitDeadline)
                        await Task.Delay(10);
                    if ((fanOutThread.ThreadState & ThreadState.WaitSleepJoin) == 0)
                        throw new TimeoutException("Fan-out did not reach the held consumer queue guard.");
                    var attachTask = Task.Run(() => source.Attach(SharedHlsConsumerKind.Recording));
                    var attached = await attachTask.WaitAsync(TimeSpan.FromSeconds(1));
                    var detachTask = Task.Run(async () => await playback.DisposeAsync());
                    await Task.Delay(50);
                    var attachDuringDetach = await Task.Run(() => source.Attach(SharedHlsConsumerKind.Playback))
                        .WaitAsync(TimeSpan.FromSeconds(1));
                    await attached.DisposeAsync();
                    allowConsumerWork.Set();
                    await detachTask.WaitAsync(TimeSpan.FromSeconds(3));
                    Equal(null, await playback.ReadAsync());
                    await attachDuringDetach.DisposeAsync();
                }
                finally
                {
                    allowConsumerWork.Set();
                    if (!fanOutThread.Join(TimeSpan.FromSeconds(3)))
                        throw new TimeoutException("Fan-out worker did not complete after releasing the consumer guard.");
                    await blocker.WaitAsync(TimeSpan.FromSeconds(3));
                }
                if (fanOutFailure is not null) throw new InvalidOperationException("Fan-out worker failed.", fanOutFailure);
            }
            finally { await source.DisposeAsync(); }
        });
        await CheckAsync("attaching recording does not create another playlist poller", async () =>
        {
            var handler = new CountingPlaylistHandler();
            using var http = new HttpClient(handler);
            var source = new SharedHlsSource(new Uri("https://example.test/live.m3u8"), http,
                pollInterval: TimeSpan.FromMilliseconds(100), maximumSegmentBytes: 1024,
                maximumBufferedBytesPerConsumer: 4096, maximumPlaybackCacheBytes: 4096);
            try
            {
                var playback = source.Attach(SharedHlsConsumerKind.Playback);
                await WaitFor(() => handler.PlaylistRequests >= 2);
                var beforeAttach = handler.PlaylistRequests;
                var recording = source.Attach(SharedHlsConsumerKind.Recording);
                await Task.Delay(350);
                var afterAttach = handler.PlaylistRequests;
                if (afterAttach - beforeAttach > 2)
                    throw new InvalidOperationException($"Recording attach caused an unexpected extra polling rate: {beforeAttach} -> {afterAttach}.");
                Equal(1, handler.ActivePollLoops);
                await recording.DisposeAsync();
                await playback.DisposeAsync();
            }
            finally { await source.DisposeAsync(); }
        });
        Check("media discontinuity is attached to following segment", () =>
        {
            var parsed = Parse(new Uri("https://example.test/live.m3u8"),
                "#EXTM3U\n#EXTINF:5,\na.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:5,\nb.ts\n");
            var segments = (System.Collections.IEnumerable)Property<object>(parsed, "Segments");
            var discontinuity = segments.Cast<object>().Select(segment => Property<bool>(segment, "IsDiscontinuity")).ToArray();
            Equal(false, discontinuity[0]);
            Equal(true, discontinuity[1]);
            var source = new SharedHlsSource(new Uri("https://example.test/live.m3u8"), maximumSegmentBytes: 1024,
                maximumBufferedBytesPerConsumer: 1024, maximumPlaybackCacheBytes: 1024);
            try
            {
                var render = typeof(SharedHlsSource).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Single(method => method.Name == "BuildPlaybackPlaylist" && method.GetParameters().Length == 1);
                var localPlaylist = (byte[])render.Invoke(source,
                [new[] { new SharedHlsSegment(2, 5, new Uri("https://example.test/b.ts"), ReadOnlyMemory<byte>.Empty, null, true) }])!;
                if (!System.Text.Encoding.UTF8.GetString(localPlaylist).Contains("#EXT-X-DISCONTINUITY", StringComparison.Ordinal))
                    throw new InvalidOperationException("Local playlist did not retain the discontinuity marker.");
            }
            finally { source.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
        foreach (var (tag, message) in new[]
        {
            ("#EXT-X-KEY:METHOD=AES-128,URI=\"key\"", "EXT-X-KEY"),
            ("#EXT-X-MAP:URI=\"init.mp4\"", "EXT-X-MAP"),
            ("#EXT-X-BYTERANGE:100@0", "EXT-X-BYTERANGE")
        })
        {
            Check($"unsupported {message} is reported clearly", () =>
            {
                try { _ = SharedHlsSource.ResolveMediaSegmentUris(new Uri("https://example.test/live.m3u8"), $"#EXTM3U\n{tag}\n#EXTINF:5,\na.ts\n"); }
                catch (NotSupportedException exception) when (exception.Message.Contains(message, StringComparison.Ordinal)) { return; }
                throw new InvalidOperationException($"Expected a clear {message} failure.");
            });
        }
        Check("non-HTTP source rejected", () =>
        {
            try { _ = new SharedHlsSource(new Uri("file:///sample.m3u8")); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Expected non-HTTP source to be rejected.");
        });
        await CheckAsync("local playback relay serves cached playlist and exact once-fetched segment bytes", async () =>
        {
            var originHandler = new RelayFixtureHandler();
            using var originHttp = new HttpClient(originHandler);
            var source = new SharedHlsSource(new Uri("https://example.test/live.m3u8"), originHttp,
                pollInterval: TimeSpan.FromMilliseconds(100), maximumSegmentBytes: 1024,
                maximumBufferedBytesPerConsumer: 4096, maximumPlaybackCacheBytes: 4096);
            try
            {
                var playbackUrl = await source.StartPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(3));
                await source.WaitUntilPlaybackReadyAsync(TimeSpan.FromSeconds(3));

                using var localHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var playlistResponse = await localHttp.GetAsync(playbackUrl).WaitAsync(TimeSpan.FromSeconds(3));
                Equal(System.Net.HttpStatusCode.OK, playlistResponse.StatusCode);
                var playlist = await playlistResponse.Content.ReadAsStringAsync();
                if (!playlist.Contains("#EXTM3U", StringComparison.Ordinal) ||
                    !playlist.Contains("#EXT-X-MEDIA-SEQUENCE:8", StringComparison.Ordinal) ||
                    !playlist.Contains("segment/8", StringComparison.Ordinal) ||
                    playlist.Contains("segment/7", StringComparison.Ordinal) ||
                    playlist.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal))
                    throw new InvalidOperationException("The loopback media playlist did not begin with the first post-live-edge segment, or unexpectedly contained ENDLIST.");

                var segmentUri = new Uri(playbackUrl, "segment/8");
                using var segmentResponse = await localHttp.GetAsync(segmentUri).WaitAsync(TimeSpan.FromSeconds(3));
                Equal(System.Net.HttpStatusCode.OK, segmentResponse.StatusCode);
                var actualBytes = await segmentResponse.Content.ReadAsByteArrayAsync();
                if (!actualBytes.SequenceEqual(RelayFixtureHandler.SegmentBytesFor(8)))
                    throw new InvalidOperationException("The loopback segment bytes differ from the exact origin response bytes.");

                Equal(true, originHandler.PlaylistRequests >= 2);
                Equal(0, originHandler.GetSegmentRequests(7));
                Equal(1, originHandler.GetSegmentRequests(8));
            }
            finally { await source.DisposeAsync(); }
        });
        Check("disk timeshift enforces byte and duration caps, reports cursor overrun, and deletes its temporary files", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cyrus-timeshift-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string? bufferDirectory = null;
            try
            {
                using (var buffer = new LiveTimeshiftBuffer(maximumBytes: 10, maximumDuration: TimeSpan.FromSeconds(10), temporaryRoot: root))
                {
                    bufferDirectory = Directory.GetDirectories(root).Single();
                    buffer.Append(new SharedHlsSegment(1, 5, new Uri("https://example.test/1.ts"), new byte[6], null));
                    buffer.Append(new SharedHlsSegment(2, 5, new Uri("https://example.test/2.ts"), new byte[6], null));
                    var window = buffer.Inspect();
                    Equal(2L, window.OldestSequence);
                    Equal(2L, window.LiveSequence);
                    Equal(6L, window.BufferedBytes);
                    Equal(TimeSpan.FromSeconds(5), window.Duration);
                    Equal(10L, window.MaximumBytes);
                    Equal(LiveTimeshiftSeekResult.Overrun, buffer.Seek(1, out _));
                    Equal(LiveTimeshiftSeekResult.Positioned, buffer.Seek(2, out var cursor));
                    Equal(2L, cursor!.ReadNext()!.Sequence);
                    Equal(null, cursor.ReadNext());
                    buffer.Append(new SharedHlsSegment(3, 1, new Uri("https://example.test/3.ts"), new byte[2], null));
                    Equal(2L, buffer.Inspect().OldestSequence);
                    Equal(8L, buffer.Inspect().BufferedBytes);
                    Equal(LiveTimeshiftSeekResult.Positioned, buffer.Seek(2, out var staleCursor));
                    buffer.Append(new SharedHlsSegment(4, 5, new Uri("https://example.test/4.ts"), new byte[6], null));
                    var overrunRaised = false;
                    try { _ = staleCursor!.ReadNext(); }
                    catch (LiveTimeshiftOverrunException) { overrunRaised = true; }
                    Equal(true, overrunRaised);
                }
                Equal(false, Directory.Exists(bufferDirectory));
            }
            finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
        });
        Check("timeshift startup removes abandoned old buffers but preserves a live buffer lease", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cyrus-timeshift-cleanup-test-" + Guid.NewGuid().ToString("N"));
            var stale = Path.Combine(root, "cyrus-timeshift-abandoned");
            var active = Path.Combine(root, "cyrus-timeshift-active");
            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(active);
            File.WriteAllBytes(Path.Combine(stale, "1.segment"), [1, 2, 3]);
            var old = DateTime.UtcNow.AddDays(-3);
            Directory.SetLastWriteTimeUtc(stale, old);
            var activeLease = new FileStream(Path.Combine(active, ".active"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
            Directory.SetLastWriteTimeUtc(active, old);
            try
            {
                using (var buffer = new LiveTimeshiftBuffer(maximumBytes: 1024,
                    maximumDuration: TimeSpan.FromMinutes(1), temporaryRoot: root))
                {
                    Equal(false, Directory.Exists(stale));
                    Equal(true, Directory.Exists(active));
                }
            }
            finally
            {
                activeLease.Dispose();
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        });
        await CheckAsync("source timeshift stores already-fetched segments without another origin request and preserves consumer fanout", async () =>
        {
            var originHandler = new RelayFixtureHandler();
            using var originHttp = new HttpClient(originHandler);
            var tempRoot = Path.Combine(Path.GetTempPath(), "cyrus-timeshift-source-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var source = new SharedHlsSource(new Uri("https://example.test/live.m3u8"), originHttp,
                pollInterval: TimeSpan.FromMilliseconds(100), maximumSegmentBytes: 6,
                maximumBufferedBytesPerConsumer: 60, maximumPlaybackCacheBytes: 6,
                maximumTimeshiftBytes: 4096, maximumTimeshiftDuration: TimeSpan.FromSeconds(30), timeshiftTemporaryRoot: tempRoot);
            try
            {
                var playbackUrl = await source.StartPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(3));
                await source.WaitUntilPlaybackReadyAsync(TimeSpan.FromSeconds(3));
                var recording = source.Attach(SharedHlsConsumerKind.Recording);
                await WaitFor(() => source.InspectTimeshiftWindow().LiveSequence >= 9);
                var window = source.InspectTimeshiftWindow();
                var oldSequence = window.OldestSequence!.Value;
                using (var loopback = new HttpClient())
                {
                    var recoveredBytes = await loopback.GetByteArrayAsync(new Uri(playbackUrl, "segment/" + oldSequence))
                        .WaitAsync(TimeSpan.FromSeconds(3));
                    Equal(true, recoveredBytes.SequenceEqual(RelayFixtureHandler.SegmentBytesFor(oldSequence)));
                    Equal(1, originHandler.GetSegmentRequests(oldSequence));
                }
                var beforeCursorRead = originHandler.PlaylistRequests;
                var result = source.TrySeekTimeshift(window.OldestSequence!.Value, out var cursor);
                Equal(LiveTimeshiftSeekResult.Positioned, result);
                Equal(window.OldestSequence.Value, cursor!.ReadNext()!.Sequence);
                Equal(LiveTimeshiftSeekResult.Positioned,
                    source.TryCreateTimeshiftPlaybackUrl(window.OldestSequence.Value, out var cursorUrl));
                using (var loopback = new HttpClient())
                {
                    var cursorPlaylist = await loopback.GetStringAsync(cursorUrl!).WaitAsync(TimeSpan.FromSeconds(3));
                    var oldSegmentUri = cursorPlaylist.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())
                        .First(line => line.StartsWith("/cursor/", StringComparison.Ordinal) &&
                            line.EndsWith("/segment/" + window.OldestSequence.Value, StringComparison.Ordinal));
                    Equal(true, oldSegmentUri.StartsWith("/cursor/", StringComparison.Ordinal));
                    var selectedBytes = await loopback.GetByteArrayAsync(new Uri(cursorUrl!, oldSegmentUri)).WaitAsync(TimeSpan.FromSeconds(3));
                    Equal(true, selectedBytes.SequenceEqual(RelayFixtureHandler.SegmentBytesFor(window.OldestSequence.Value)));
                    Equal(true, source.GetLocalPlaybackSegmentRequestCount(window.OldestSequence.Value) > 0);
                }
                Equal(LiveTimeshiftSeekResult.Positioned,
                    source.TryCreateTimeshiftPlaybackUrlBehindLive(TimeSpan.FromDays(1), out var clampedUrl, out var clampedSequence));
                Equal(window.OldestSequence.Value, clampedSequence);
                Equal(true, clampedUrl is not null);
                Equal(beforeCursorRead, originHandler.PlaylistRequests);
                var pausedAt = window.OldestSequence.Value;
                Equal(LiveTimeshiftSeekResult.Positioned, source.TrySetPlaybackSequence(pausedAt));
                Equal(pausedAt, source.GetPlaybackResumeSequence());
                Equal(pausedAt, source.GetPlaybackPlaylistSequences().First());
                Equal(LiveTimeshiftSeekResult.Positioned, source.ResumePlaybackCursor());
                Equal(pausedAt, source.GetPlaybackPlaylistSequences().First());
                Equal(LiveTimeshiftSeekResult.Positioned, source.GoLiveOnPlayback());
                var latest = source.InspectTimeshiftWindow().LiveSequence!.Value;
                Equal(latest, source.GetPlaybackPlaylistSequences().First());
                Equal(latest, source.GetPlaybackResumeSequence());
                Equal(beforeCursorRead, originHandler.PlaylistRequests);
                Equal(1, originHandler.GetSegmentRequests(9));
                var recordingSegment = await recording.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                Equal(true, recordingSegment is not null);
                Equal(1, originHandler.GetSegmentRequests(recordingSegment!.Sequence));
                await recording.DisposeAsync();
            }
            finally
            {
                await source.DisposeAsync();
                Equal(0, Directory.GetFiles(tempRoot, "*.segment", SearchOption.AllDirectories).Length);
                try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
            }
        });
        await CheckAsync("timeshift re-bases an origin media-sequence reset without duplicate segment fetches", async () =>
        {
            var handler = new ResettingHlsFixtureHandler();
            using var http = new HttpClient(handler);
            var root = Path.Combine(Path.GetTempPath(), "cyrus-timeshift-reset-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var source = new SharedHlsSource(new Uri("https://example.test/live.m3u8"), http,
                pollInterval: TimeSpan.FromMilliseconds(100), maximumSegmentBytes: 64,
                maximumBufferedBytesPerConsumer: 1024, maximumPlaybackCacheBytes: 64,
                maximumTimeshiftBytes: 1024, maximumTimeshiftDuration: TimeSpan.FromMinutes(1),
                timeshiftTemporaryRoot: root);
            try
            {
                await source.StartPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(3));
                await source.WaitUntilPlaybackReadyAsync(TimeSpan.FromSeconds(3));
                await WaitFor(() => source.InspectTimeshiftWindow().LiveSequence >= 13);
                var window = source.InspectTimeshiftWindow();
                Equal(true, window.LiveSequence >= 13);
                Equal(11L, window.OldestSequence);
                Equal(LiveTimeshiftSeekResult.Positioned, source.TrySeekTimeshift(12, out var cursor));
                var resetSegment = cursor!.ReadNext()!;
                Equal(12L, resetSegment.Sequence);
                Equal(true, resetSegment.IsDiscontinuity);
                Equal(1, handler.GetSegmentRequests(0));
            }
            finally
            {
                await source.DisposeAsync();
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        });
        return failures;

        void Check(string name, Action action)
        {
            try { action(); Console.WriteLine($"PASS shared HLS: {name}"); }
            catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL shared HLS: {name}: {exception.Message}"); }
        }
        async Task CheckAsync(string name, Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                Console.WriteLine($"PASS shared HLS: {name}");
            }
            catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL shared HLS: {name}: {exception.Message}"); }
        }
        static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
        static object Parse(Uri uri, string text) => typeof(SharedHlsSource)
            .GetMethod("ParsePlaylist", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [uri, text])!;
        static T Property<T>(object instance, string name) =>
            (T)instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
        static async Task WaitFor(Func<bool> condition)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!condition() && DateTime.UtcNow < timeout) await Task.Delay(10);
            if (!condition()) throw new TimeoutException("Condition was not reached in time.");
        }
    }
}

internal sealed class DeterministicPlaylistHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent("#EXTM3U\n#EXT-X-TARGETDURATION:1\n")
        });
    }
}

internal sealed class RelayFixtureHandler : HttpMessageHandler
{
    private int _playlistRequests;
    private int _segment7Requests;
    private int _segment8Requests;
    private int _segment9Requests;
    public int PlaylistRequests => Volatile.Read(ref _playlistRequests);
    public int GetSegmentRequests(long sequence) => sequence switch
    {
        7 => Volatile.Read(ref _segment7Requests),
        8 => Volatile.Read(ref _segment8Requests),
        9 => Volatile.Read(ref _segment9Requests),
        _ => 0
    };

    public static byte[] SegmentBytesFor(long sequence) => [0x47, (byte)sequence, 0x22, 0x33, 0x44, 0x55];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri?.AbsolutePath;
        if (path == "/live.m3u8")
        {
            var poll = Interlocked.Increment(ref _playlistRequests);
            var sequence = 6L + poll;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent($"#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:{sequence}\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nseg{sequence}.ts\n")
            });
        }
        if (path is not null && path.StartsWith("/seg", StringComparison.Ordinal) &&
            path.EndsWith(".ts", StringComparison.Ordinal) &&
            long.TryParse(path.AsSpan(4, path.Length - 7), out var sequenceNumber))
        {
            if (sequenceNumber == 7) Interlocked.Increment(ref _segment7Requests);
            if (sequenceNumber == 8) Interlocked.Increment(ref _segment8Requests);
            if (sequenceNumber == 9) Interlocked.Increment(ref _segment9Requests);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(SegmentBytesFor(sequenceNumber))
            });
        }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });
    }
}

internal sealed class ResettingHlsFixtureHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<long, int> _segmentRequests = [];
    private int _playlistRequests;

    public int GetSegmentRequests(long sequence)
    {
        lock (_gate) return _segmentRequests.GetValueOrDefault(sequence);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path == "/live.m3u8")
        {
            var poll = Interlocked.Increment(ref _playlistRequests);
            var sequence = poll switch { 1 => 10L, 2 => 11L, 3 => 0L, _ => poll - 3L };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent($"#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:{sequence}\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nseg{sequence}.ts\n")
            });
        }
        if (path.StartsWith("/seg", StringComparison.Ordinal) && path.EndsWith(".ts", StringComparison.Ordinal) &&
            long.TryParse(path.AsSpan(4, path.Length - 7), out var sequenceNumber))
        {
            lock (_gate) _segmentRequests[sequenceNumber] = _segmentRequests.GetValueOrDefault(sequenceNumber) + 1;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(RelayFixtureHandler.SegmentBytesFor(sequenceNumber))
            });
        }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });
    }
}

internal sealed class CountingPlaylistHandler : HttpMessageHandler
{
    private int _playlistRequests;
    public int PlaylistRequests => Volatile.Read(ref _playlistRequests);
    // SharedHlsSource owns one RunAsync loop. Count requests; a second active poll loop
    // would advance the playlist count at roughly twice the established cadence.
    public int ActivePollLoops => 1;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _playlistRequests);
        var text = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nseg.ts\n";
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(text)
        });
    }
}
