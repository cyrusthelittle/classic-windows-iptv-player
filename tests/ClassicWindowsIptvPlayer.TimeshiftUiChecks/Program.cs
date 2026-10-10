using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;
using LibVLCSharp.Shared;

internal static class Program
{
    private static MainWindow window = null!;
    private static string root = "";
    private static object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
    private static object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(window, args);
    private static string Status => ((TextBlock)window.FindName("StatusText")).Text;
    private static void Check(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException(description);
        File.AppendAllText(Path.Combine(root, "evidence.txt"), "PASS " + description + Environment.NewLine);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("CYRUS_IPTV_DATA_ROOT", AppContext.BaseDirectory);
        if (!args.Contains("--child"))
        {
            root = Path.Combine(Path.GetTempPath(), "cyrus-timeshift-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var source in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, source);
                if (relative.StartsWith("cache\\", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("logs\\", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("accounts.", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            Console.WriteLine(root);
            using var child = Process.Start(new ProcessStartInfo(Path.Combine(root, Path.GetFileName(Environment.ProcessPath!)), "--child")
            { WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden });
            if (!child!.WaitForExit(120000)) { child.Kill(true); Console.WriteLine("FAIL fixture deadline"); Environment.ExitCode = 1; return; }
            Environment.ExitCode = child.ExitCode;
            var evidence = Path.Combine(root, "evidence.txt");
            Console.WriteLine(File.Exists(evidence) ? File.ReadAllText(evidence) : "FAIL fixture exited before checks");
            return;
        }

        root = AppContext.BaseDirectory;
        GenerateAvi.Run(Path.Combine(root, "segment.avi"));
        LibVLCSharp.Shared.Core.Initialize();
        using (var encoderVlc = new LibVLC("--quiet"))
        using (var input = new Media(encoderVlc, new Uri(Path.Combine(root, "segment.avi")).AbsoluteUri, FromType.FromLocation))
        using (var encoder = new MediaPlayer(encoderVlc))
        {
            var directory = Path.Combine(root, "origin");
            Directory.CreateDirectory(directory);
            var output = directory.Replace('\\', '/');
            input.AddOption(":sout=#transcode{vcodec=mp2v,vb=600,fps=10}:std{access=livehttp{seglen=2,splitanywhere=true,delsegs=false,numsegs=0,index='" + output + "/index.m3u8',index-url='segment-########.ts'},mux=ts,dst='" + output + "/segment-########.ts'}");
            if (!encoder.Play(input)) throw new InvalidOperationException("Video fixture encoder failed.");
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (encoder.State != VLCState.Ended && DateTime.UtcNow < deadline) Thread.Sleep(50);
            if (encoder.State != VLCState.Ended) throw new TimeoutException("Video fixture encoder timeout.");
            encoder.Stop();
        }
        using var origin = new HlsFixture(Path.Combine(root, "origin"));
        var store = new ConfigStore();
        var state = store.Load();
        var saved = state.EnsureSelectedAccount();
        var account = new AccountSettings { ServerUrl = origin.Origin, Username = "local-fixture", Password = "local-fixture" };
        saved.Settings = account.Clone(); saved.Name = "Local timeshift fixture";
        state.TimeshiftEnabled = true;
        state.Account = account.Clone(); state.CheckForUpdatesOnStartup = false; state.RemoteControlEnabled = false;
        store.Save(state);
        var channel = new Channel
        {
            Id = "local-live-timeshift", Name = "Local HLS Timeshift", Group = "Fixture",
            Url = origin.Origin + "/live.m3u8", MediaKind = MediaKind.Live
        };
        store.SaveChannelCache(saved.Id, new[] { channel });
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var startup = typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(object), typeof(StartupEventArgs) }, null)!;
        app.Startup -= (StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler), app);
        window = new MainWindow(new LoginResult { Account = account, AccountId = saved.Id, UpdatePlaylist = false });
        app.MainWindow = window;
        window.ContentRendered += async (_, _) =>
        {
            try { await ExerciseProductionSlider(channel, origin); File.AppendAllText(Path.Combine(root, "evidence.txt"), "ALL PASS" + Environment.NewLine); }
            catch (Exception exception) { File.AppendAllText(Path.Combine(root, "evidence.txt"), "FAIL " + exception + Environment.NewLine); Environment.ExitCode = 1; }
            finally
            {
                try { Call("StopPlayback"); } catch { }
                await Task.Delay(800);
                try { window.Close(); } catch { }
                await Task.Delay(300);
                app.Shutdown();
            }
        };
        window.Show();
        app.Run();
    }

    private static async Task ExerciseProductionSlider(Channel channel, HlsFixture origin)
    {
        await WaitUntil(() => Field("_tuner") is ChannelTuner, TimeSpan.FromSeconds(20), "MainWindow tuner was not initialized; status=" + Status);
        Call("PlayChannel", channel, null);
        await WaitUntil(() => Field("_mediaPlayer") is MediaPlayer { IsPlaying: true }, TimeSpan.FromSeconds(25), "local live playback did not start; status=" + Status);
        await WaitUntil(() => Field("_tuner") is ChannelTuner tuner && tuner.TryInspectLiveTimeshift(out var snapshot, out _) && snapshot is { Duration.TotalSeconds: > 8 },
            TimeSpan.FromSeconds(25), "local live buffer did not become available");

        var slider = (Slider)window.FindName("SeekSlider");
        var timeText = (TextBlock)window.FindName("TimeText");
        var goLive = (Button)window.FindName("GoLiveButton");
        var player = (MediaPlayer)Field("_mediaPlayer")!;
        Call("UpdatePlaybackPosition");
        Check(!slider.IsVisible && !goLive.IsVisible && !timeText.IsVisible, "normal stream latency does not display delay controls");

        Call("PauseCurrentPlayback");
        await WaitUntil(() => player.State == VLCState.Paused && slider.IsVisible && goLive.IsVisible,
            TimeSpan.FromSeconds(5), "pause did not render the live controls");
        await Task.Delay(2500);
        var pausedTime = player.Time;
        var pausedMrl = player.Media!.Mrl;
        var pausedMarker = await SnapshotMarker(player, "paused-frame.png");
        await Task.Delay(1000);
        var stillPausedMarker = await SnapshotMarker(player, "still-paused-frame.png");
        Check(Math.Abs(pausedMarker - stillPausedMarker) < 0.1, "decoded frame stays fixed during pause");
        Check(timeText.Text.StartsWith("Paused ", StringComparison.Ordinal), "pause displays the pause clock and behind-live timer");

        Call("ResumePausedPlayback");
        await WaitUntil(() => Field("_pausedPlayback") is null && player.IsPlaying,
            TimeSpan.FromSeconds(15), "pause did not resume; status=" + Status);
        Check(player.Media!.Mrl == pausedMrl, "resume keeps the existing buffered reader");
        var resumedMarker = await SnapshotMarker(player, "resumed-frame.png");
        var markerDelta = Math.Abs(resumedMarker - pausedMarker);
        Check(Math.Min(markerDelta, Math.Abs(100 - markerDelta)) < 12,
            $"decoded frame continues from pause (marker {pausedMarker:0.0} -> {resumedMarker:0.0}, VLC clock {pausedTime} -> {player.Time}ms)");
        Call("UpdatePlaybackPosition");
        Check(slider.IsVisible && goLive.IsVisible && timeText.IsVisible, "Go Live, seek bar and timer remain rendered immediately after resume");

        // Playback at normal speed must not subtract one second of delay per second.
        // Wait longer than the pause to expose the former countdown hiding both controls.
        var until = DateTime.UtcNow.AddSeconds(7);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(100);
            if (!slider.IsVisible || !goLive.IsVisible || !goLive.IsEnabled || !timeText.IsVisible)
                throw new InvalidOperationException("resumed delayed playback lost live controls: " + timeText.Text);
        }
        Check(true, "resumed delayed playback retains all live controls longer than the pause duration");
        Check(timeText.Text.StartsWith("Behind live ", StringComparison.Ordinal), "resumed timer displays the buffered playback distance");

        await DragSlider(slider, 500);
        await WaitUntil(() => !(bool)Field("_committingLiveSeek")! && player.IsPlaying,
            TimeSpan.FromSeconds(15), "delayed playback seek did not finish; status=" + Status);
        Check(slider.IsVisible && goLive.IsVisible, "seeking while delayed keeps both live controls");
        Call("EnterFullScreen");
        Call("UpdatePlaybackPosition");
        Check(slider.IsVisible && goLive.IsVisible, "full screen retains both controls after resume");
        Call("ExitFullScreen");

        Call("GoLive_Click", goLive, new RoutedEventArgs());
        await WaitUntil(() => player.IsPlaying && !goLive.IsVisible && !slider.IsVisible && !timeText.IsVisible,
            TimeSpan.FromSeconds(15), "Go Live did not resume at the edge and hide the controls");
        Check(Status.StartsWith("Live:", StringComparison.Ordinal), "Go Live reports the live edge");
        await Task.Delay(2000);
        Check(!goLive.IsVisible && !slider.IsVisible, "ordinary live buffering after Go Live keeps the controls hidden");

        Call("PauseCurrentPlayback");
        await WaitUntil(() => player.State == VLCState.Paused, TimeSpan.FromSeconds(5), "second pause failed");
        Call("ResumePausedPlayback");
        await WaitUntil(() => Field("_pausedPlayback") is null && player.IsPlaying, TimeSpan.FromSeconds(15), "short pause did not resume");
        await Task.Delay(2000);
        Check(slider.IsVisible && goLive.IsVisible, "even a short pause retains live controls after resume");

        var recordButton = (Button)window.FindName("RecordButton");
        Check(recordButton.IsEnabled, "the local stream supports recording");
        Call("Record_Click", recordButton, new RoutedEventArgs());
        await WaitUntil(() => Field("_activeRecordingId") is string && Field("_mediaPlayer") is MediaPlayer { IsPlaying: true } &&
            ((TextBlock)window.FindName("RecordingStatusText")).Text.StartsWith("Recording:", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20), "local recording did not start; status=" + Status);
        Call("UpdatePlaybackPosition");
        Check(!slider.IsVisible && !slider.IsEnabled && !goLive.IsVisible && !goLive.IsEnabled,
            "recording immediately hides and disables both live controls");
        Check(timeText.Text == "Recording live TV", "recording displays its status without a timeshift-unavailable message");
        var recordingPlayer = (MediaPlayer)Field("_mediaPlayer")!;
        var recordingMrl = recordingPlayer.Media!.Mrl;
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = slider };
        Call("SeekSlider_PreviewMouseDown", slider, down);
        Check(down.Handled && !(bool)Field("_isSeeking")!, "recording rejects seek interaction");
        slider.Value = 500;
        await (Task)Call("CommitLiveTimeshiftSliderAsync")!;
        Call("GoLive_Click", goLive, new RoutedEventArgs());
        await Task.Delay(2000);
        Check(recordingPlayer.IsPlaying && ReferenceEquals(recordingPlayer, Field("_mediaPlayer")) && recordingPlayer.Media!.Mrl == recordingMrl,
            "seek and Go Live cannot switch the recording input");
        Call("StopRecording_Click", window.FindName("StopRecordingButton"), new RoutedEventArgs());
        await WaitUntil(() => Field("_activeRecordingId") is null && Field("_mediaPlayer") is MediaPlayer { IsPlaying: true } restored && !ReferenceEquals(restored, recordingPlayer),
            TimeSpan.FromSeconds(20), "recording did not stop and restore live playback; status=" + Status);
        Check(Directory.GetFiles(Path.Combine(root, "Recordings"), "*", SearchOption.AllDirectories).Any(path => new FileInfo(path).Length > 1880),
            "the guarded recording produced a nonempty media file");
        Check(origin.PlaylistRequests > 0 && origin.SegmentRequests > 0, "playback used only the invented local HLS origin");
    }

    private static async Task DragSlider(Slider slider, double value)
    {
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = slider };
        Call("SeekSlider_PreviewMouseDown", slider, down);
        slider.Value = value;
        var preview = (TextBlock)window.FindName("TimeText");
        Check(preview.Visibility == Visibility.Visible && preview.Text.Contains("Behind live ", StringComparison.Ordinal),
            $"scrub preview at {value:0} shows useful live times: {preview.Text}");
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseUpEvent, Source = slider };
        Call("SeekSlider_PreviewMouseUp", slider, up);
        await Task.Delay(50);
    }

    private static async Task<double> SnapshotMarker(MediaPlayer player, string name)
    {
        var path = Path.Combine(root, name);
        Check(player.TakeSnapshot(0, path, 160, 90), "native video snapshot requested: " + name);
        await WaitUntil(() => File.Exists(path) && new FileInfo(path).Length > 100, TimeSpan.FromSeconds(5), "Video decoder did not produce " + name);
        using var stream = File.OpenRead(path);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgr32, null, 0);
        var pixels = new byte[160 * 90 * 4];
        bitmap.CopyPixels(pixels, 160 * 4, 0);
        long totalX = 0, count = 0;
        for (var y = 0; y < 90; y++)
        for (var x = 0; x < 160; x++)
        {
            var offset = (y * 160 + x) * 4;
            if (pixels[offset] > 210 && pixels[offset + 1] > 210 && pixels[offset + 2] > 210) { totalX += x; count++; }
        }
        Check(count > 300, "decoded moving video marker is present: " + name);
        return (double)totalX / count;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(failure);
    }

    private sealed class HlsFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly byte[][] segments;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly CancellationTokenSource stopping = new();
        private int playlistRequests;
        private int segmentRequests;
        public string Origin { get; }
        public int PlaylistRequests => Volatile.Read(ref playlistRequests);
        public int SegmentRequests => Volatile.Read(ref segmentRequests);

        public HlsFixture(string directory)
        {
            segments = Directory.GetFiles(directory, "*.ts").Order().Select(File.ReadAllBytes).ToArray();
            if (segments.Length < 4 || segments.Any(data => data.Length == 0 || data[0] != 0x47))
                throw new InvalidOperationException("Fixture must serve real MPEG-TS video.");
            listener.Start();
            Origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            _ = AcceptLoop();
        }

        private async Task AcceptLoop()
        {
            while (!stopping.IsCancellationRequested)
            {
                try { _ = Respond(await listener.AcceptTcpClientAsync(stopping.Token)); }
                catch { break; }
            }
        }

        private async Task Respond(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var request = await reader.ReadLineAsync(stopping.Token);
                if (request is null) return;
                var path = request.Split(' ')[1].Split('?')[0];
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stopping.Token))) { }
                byte[] body;
                string mime;
                if (path == "/live.m3u8")
                {
                    Interlocked.Increment(ref playlistRequests);
                    var edge = 3 + (int)(clock.Elapsed.TotalSeconds / 2);
                    var first = Math.Max(0, edge - 7);
                    var text = new StringBuilder($"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{first}\n");
                    for (var i = first; i <= edge; i++)
                    {
                        if (i > 0 && i % segments.Length == 0) text.AppendLine("#EXT-X-DISCONTINUITY");
                        text.AppendLine("#EXTINF:2.0,").AppendLine($"segment-{i}.ts");
                    }
                    body = Encoding.UTF8.GetBytes(text.ToString()); mime = "application/vnd.apple.mpegurl";
                }
                else if (path.StartsWith("/segment-", StringComparison.Ordinal) && path.EndsWith(".ts", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref segmentRequests);
                    body = segments[int.Parse(path[9..^3]) % segments.Length]; mime = "video/mp2t";
                }
                else { body = []; mime = "text/plain"; }
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {mime}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, stopping.Token);
                await stream.WriteAsync(body, stopping.Token);
            }
            catch { }
        }

        public void Dispose() { stopping.Cancel(); listener.Stop(); stopping.Dispose(); }
    }
}
