using ClassicWindowsIptvPlayer.Windows;
using LibVLCSharp.Shared;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

var fixturePath = Path.Combine(Path.GetTempPath(), "cyrus-native-fixture-" + Guid.NewGuid().ToString("N") + ".wav");
var isolatedRoot = Path.Combine(Path.GetTempPath(), "cyrus-native-data-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("CYRUS_IPTV_DATA_ROOT", isolatedRoot);
try
{
    CreateSilentWave(fixturePath);
    Core.Initialize();
    using var vlc = new LibVLC("--no-video", "--quiet");
    using var tuner = new ChannelTuner(vlc) { MaxAttempts = 1 };
    var states = new List<TunerStatus>();
    var gate = new object();
    tuner.StateChanged += state => { lock (gate) states.Add(state.Status); };
    var request = new TuneRequest("synthetic", "Generated silence", new Uri(fixturePath).AbsoluteUri,
        "local fixture", false, 200);

    for (var i = 0; i < 8; i++)
    {
        tuner.Play(request);
        await Task.Delay(80);
        tuner.Stop();
    }

    tuner.Play(request);
    await WaitUntilAsync(() => { lock (gate) return states.Contains(TunerStatus.Playing); }, TimeSpan.FromSeconds(10));
    tuner.Stop();
    await WaitUntilAsync(() => tuner.CurrentPlayer is null, TimeSpan.FromSeconds(5));
    var countAfterStop = 0;
    lock (gate) countAfterStop = states.Count;
    await Task.Delay(1200);
    lock (gate)
    {
        if (states.Skip(countAfterStop).Any(status => status is TunerStatus.Tuning or TunerStatus.Playing))
            throw new InvalidOperationException("Playback reopened after explicit Stop.");
    }

    var shutdown = Task.Run(() => tuner.Dispose());
    await shutdown.WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine("PASS native generated-WAV rapid tune/Stop, no reopen, bounded shutdown");
    await RunStalledHttpFixtureAsync(vlc, File.ReadAllBytes(fixturePath));
    await RunLiveTimeshiftFixtureAsync(vlc, fixturePath);
}
finally
{
    try { File.Delete(fixturePath); } catch { }
}

static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (!predicate())
    {
        if (DateTime.UtcNow >= until) throw new TimeoutException("Native fixture did not reach expected state.");
        await Task.Delay(50);
    }
}

static async Task RunLiveTimeshiftFixtureAsync(LibVLC vlc, string wavePath)
{
    var transportPath = Path.ChangeExtension(wavePath, ".ts");
    try
    {
        using (var media = new Media(vlc, new Uri(wavePath).AbsoluteUri, FromType.FromLocation))
        using (var encoder = new MediaPlayer(vlc))
        {
            media.AddOption(":sout=#transcode{acodec=mpga,ab=128}:std{access=file,mux=ts,dst='" + transportPath.Replace('\\', '/') + "'}");
            if (!encoder.Play(media)) throw new InvalidOperationException("Fixture TS encoder failed.");
            await WaitUntilAsync(() => encoder.State == VLCState.Ended, TimeSpan.FromSeconds(25));
            encoder.Stop();
        }
        var bytes = await File.ReadAllBytesAsync(transportPath);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stopping = new CancellationTokenSource();
        var connections = 0;
        var stall = 0;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(stopping.Token);
            Interlocked.Increment(ref connections);
            using var stream = client.GetStream();
            var request = new byte[4096];
            await stream.ReadAtLeastAsync(request, 1, throwOnEndOfStream: true, stopping.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nConnection: close\r\n\r\n"), stopping.Token);
            while (!stopping.IsCancellationRequested)
                for (var offset = 0; offset < bytes.Length; offset += 188 * 8)
                {
                    while (Volatile.Read(ref stall) != 0) await Task.Delay(25, stopping.Token);
                    await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(188 * 8, bytes.Length - offset)), stopping.Token);
                    await Task.Delay(80, stopping.Token);
                }
        });
        try
        {
            using var live = new ChannelTuner(vlc) { TimeshiftEnabled = true, MaxAttempts = 1 };
        live.SetTimeshiftRetention(6);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            live.Play(new TuneRequest("local-ts", "Continuous HTTP TS", $"http://127.0.0.1:{port}/stream", "local fixture", true, 200,
                TimeshiftSeconds: 6));
            await WaitUntilAsync(() => live.CurrentPlayer?.IsPlaying == true, TimeSpan.FromSeconds(35));
            await WaitUntilAsync(() => live.TryInspectLiveTimeshift(out var buffer, out _) && buffer!.LiveSequence >= 18000,
                TimeSpan.FromSeconds(20));
            if (!live.TryInspectLiveTimeshift(out var boundedWindow, out _) || boundedWindow is null ||
                boundedWindow.Duration > TimeSpan.FromSeconds(8) || boundedWindow.OldestSequence <= 0)
                throw new InvalidOperationException("The configured timeshift duration did not bound the retained timeline.");
            var rewind = await live.SeekLiveTimeshiftAsync(TimeSpan.FromSeconds(4));
            if (!rewind.Success) throw new InvalidOperationException(rewind.FailureReason);
            await Task.Delay(700);
            live.CurrentPlayer!.SetPause(true);
            await WaitUntilAsync(() => live.CurrentPlayer.State == VLCState.Paused, TimeSpan.FromSeconds(3));
            live.NotifyUserPaused();
            var pausedTime = live.CurrentPlayer.Time;
            var pausedInput = live.CurrentPlayer.Media!.Mrl;
            var recording = Directory.GetFiles(ClassicWindowsIptvPlayer.Core.AppDataPaths.Temp, "session.ts", SearchOption.AllDirectories).Single();
            byte[] prefix;
            using (var file = new FileStream(recording, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            { prefix = new byte[188 * 8]; file.ReadExactly(prefix); }
            if (prefix.Any(value => value != 0))
                throw new InvalidOperationException("Expired TS bytes were not released from the sparse timeshift file.");
            var allocatedBeforePause = NativeFileStorage.GetAllocatedBytes(recording);
            if (allocatedBeforePause >= new FileInfo(recording).Length * 0.8)
                throw new InvalidOperationException($"Expired history still occupies disk (allocated={allocatedBeforePause}; logical={new FileInfo(recording).Length}).");
            var recordedBefore = new FileInfo(recording).Length;
            live.TryGetLivePlaybackPosition(out var positionBefore, out var durationBefore);
            var behindBeforePause = durationBefore - positionBefore;
            live.TryInspectLiveTimeshift(out var pauseWindow, out _);
            var absolutePausedPosition = (pauseWindow!.OldestSequence ?? 0) + (long)positionBefore.TotalMilliseconds;
            await Task.Delay(18000);
            live.TryGetLivePlaybackPosition(out var positionAfter, out var durationAfter);
            var behindAfterPause = durationAfter - positionAfter;
            if (behindAfterPause - behindBeforePause < TimeSpan.FromSeconds(12) ||
                durationAfter - durationBefore < TimeSpan.FromSeconds(12) || new FileInfo(recording).Length <= recordedBefore)
                throw new InvalidOperationException("Pause must freeze the read timestamp while the same TS file and timeline grow.");
            live.TryInspectLiveTimeshift(out var expandedPausedWindow, out _);
            var allocatedAfterPause = NativeFileStorage.GetAllocatedBytes(recording);
            if (expandedPausedWindow!.OldestSequence > absolutePausedPosition || allocatedAfterPause <= allocatedBeforePause)
                throw new InvalidOperationException("The paused read position was not retained as the disk buffer expanded.");
            var resumed = await live.ResumeBufferedPauseAsync();
            if (!resumed.Success) throw new InvalidOperationException(resumed.FailureReason);
            await WaitUntilAsync(() => live.CurrentPlayer!.IsPlaying, TimeSpan.FromSeconds(5));
            if (live.CurrentPlayer!.Media!.Mrl != pausedInput || Math.Abs(live.CurrentPlayer.Time - pausedTime) > 1000)
                throw new InvalidOperationException("Resume did not retain the paused input and playback time.");
            var liveEdge = await live.ReturnToLiveAsync();
            if (!liveEdge.Success) throw new InvalidOperationException(liveEdge.FailureReason);
            await WaitUntilAsync(() => live.TryInspectLiveTimeshift(out var window, out _) && window!.Duration <= TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(5));
            var allocatedAfterLive = NativeFileStorage.GetAllocatedBytes(recording);
            if (allocatedAfterLive >= allocatedAfterPause)
                throw new InvalidOperationException("Moving the reader to the live edge did not release the paused history.");
            if (connections != 1 || listener.Pending()) throw new InvalidOperationException("Timeshift reopened the provider input.");
            var backward = await live.SeekLiveTimeshiftAsync(TimeSpan.FromSeconds(4));
            var forward = await live.SeekLiveTimeshiftAsync(TimeSpan.FromSeconds(1));
            if (!backward.Success || !forward.Success || forward.SelectedSequence - backward.SelectedSequence < 2000)
                throw new InvalidOperationException("Forward/backward seeks did not select recorded timestamps.");
            live.CurrentPlayer!.SetPause(true); live.NotifyUserPaused();
            var goLive = await live.ReturnToLiveAsync();
            if (!goLive.Success || !live.CurrentPlayer.IsPlaying) throw new InvalidOperationException("Go Live must resume playback at the file's current edge.");
            live.TryGetLivePlaybackPosition(out var livePosition, out var liveDuration);
            if (liveDuration - livePosition > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("Go Live stayed at the old pause position.");
            if (!live.TryAttachSharedHlsRecording(out var capture)) throw new InvalidOperationException("Recording cannot attach to the existing TS writer.");
            await using (capture!)
            {
                var recorded = await capture!.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (recorded is null || recorded.Data.Span[0] != 0x47) throw new InvalidOperationException("Attached recording did not read MPEG-TS bytes.");
            }
            Volatile.Write(ref stall, 1);
            await Task.Delay(8000);
            if (live.CurrentPlayer is null || live.CurrentPlayer.State is VLCState.Ended or VLCState.Error)
                throw new InvalidOperationException("A temporary recording tail was treated as end of file.");
            if (connections != 1 || listener.Pending()) throw new InvalidOperationException("Seeking/recording reopened the provider input.");
            await live.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
            if (File.Exists(recording)) throw new InvalidOperationException("Stop left the timeshift recording on disk.");
            Console.WriteLine($"PASS native continuous HTTP TS: {boundedWindow.Duration.TotalSeconds:F1}s bounded window ({allocatedBeforePause:N0} allocated bytes), 18-second pause retention ({allocatedAfterPause:N0} bytes), live-edge shrink ({allocatedAfterLive:N0} bytes), same-input resume, forward/backward seek, Go Live, recording attachment, stalled tail, one upstream, cleanup");
        }
        finally
        {
            stopping.Cancel();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }
    finally
    {
        try { File.Delete(transportPath); } catch (IOException) { }
    }
}

static void CreateSilentWave(string path)
{
    const int sampleRate = 16_000;
    const int seconds = 180;
    const int sampleCount = sampleRate * seconds;
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write("RIFF"u8);
    writer.Write(36 + sampleCount * 2);
    writer.Write("WAVEfmt "u8);
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(sampleRate);
    writer.Write(sampleRate * 2);
    writer.Write((short)2);
    writer.Write((short)16);
    writer.Write("data"u8);
    writer.Write(sampleCount * 2);
    for (var i = 0; i < sampleCount; i++) writer.Write((short)(8000 * Math.Sin(i * 2 * Math.PI * (220 + i / sampleRate) / sampleRate)));
}

static async Task RunStalledHttpFixtureAsync(LibVLC vlc, byte[] wave)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var serverCts = new CancellationTokenSource();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(serverCts.Token);
        using var stream = client.GetStream();
        var request = new byte[4096];
        await stream.ReadAtLeastAsync(request, 1, throwOnEndOfStream: true, serverCts.Token);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {wave.Length}\r\nConnection: keep-alive\r\n\r\n");
        await stream.WriteAsync(header, serverCts.Token);
        await stream.WriteAsync(wave.AsMemory(0, Math.Min(wave.Length, 160_044)), serverCts.Token);
        await stream.FlushAsync(serverCts.Token);
        await Task.Delay(Timeout.Infinite, serverCts.Token);
    });

    try
    {
        using var tuner = new ChannelTuner(vlc) { MaxAttempts = 1 };
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tuner.StateChanged += state => { if (state.Status == TunerStatus.Playing) playing.TrySetResult(); };
        tuner.Play(new TuneRequest("stalled", "Stalled local WAV", $"http://127.0.0.1:{port}/stall.wav",
            "local stalled fixture", false, 200));
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1200); // The server keeps the response open but sends no more bytes.
        tuner.Stop();
        await WaitUntilAsync(() => tuner.CurrentPlayer is null, TimeSpan.FromSeconds(5));
        await Task.Run(() => tuner.Dispose()).WaitAsync(TimeSpan.FromSeconds(8));
        Console.WriteLine("PASS native stalled local response stops and retires without hanging");
    }
    finally
    {
        serverCts.Cancel();
        listener.Stop();
        try { await server; } catch (OperationCanceledException) { }
        catch (SocketException) { }
    }
}

static class NativeFileStorage
{
    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    public static long GetAllocatedBytes(string path)
    {
        var low = GetCompressedFileSize(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return ((long)high << 32) | low;
    }
}
