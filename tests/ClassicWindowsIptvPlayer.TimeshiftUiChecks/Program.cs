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
        await WaitUntil(() => Field("_tuner") is ChannelTuner, TimeSpan.FromSeconds(20), "production MainWindow tuner was not initialized; status=" + Status);
        Call("PlayChannel", channel, null);
        await WaitUntil(() => Field("_mediaPlayer") is MediaPlayer { IsPlaying: true }, TimeSpan.FromSeconds(25), "production MainWindow did not start local live HLS playback; status=" + Status);
        await WaitUntil(() => Field("_tuner") is ChannelTuner tuner && tuner.TryInspectLiveTimeshift(out var snapshot, out _) && snapshot is { Duration.TotalSeconds: > 8 },
            TimeSpan.FromSeconds(25), "local HLS fixture did not build a useful timeshift window");

        var slider = (Slider)window.FindName("SeekSlider");
        var timeText = (TextBlock)window.FindName("TimeText");
        var tuner = (ChannelTuner)Field("_tuner")!;
        tuner.TryInspectLiveTimeshift(out var available, out _);
        var totalSeconds = available!.Duration.TotalSeconds;
        var mainPlayer = (MediaPlayer)Field("_mediaPlayer")!;
        var goLiveButton = (Button)window.FindName("GoLiveButton");
        Check(goLiveButton.Visibility == Visibility.Collapsed, "Go Live button is hidden during normal playback");
        Check(true, $"live player seek metrics before drag: Time={mainPlayer.Time}ms, Length={mainPlayer.Length}ms, State={mainPlayer.State}");

        await DragSlider(slider, 500);
        await WaitUntil(() => Field("_liveBehind") is TimeSpan behind && behind.TotalSeconds > 2 && behind.TotalSeconds < totalSeconds,
            TimeSpan.FromSeconds(15), "non-edge drag did not commit a buffered seek; status=" + Status);
        var behindLive = (TimeSpan)Field("_liveBehind")!;
        Check(timeText.Visibility == Visibility.Visible, "TimeText is visible after non-edge drag/release");
        Check(!timeText.Text.EndsWith("LIVE", StringComparison.Ordinal) && timeText.Text.Contains(" / ", StringComparison.Ordinal),
            "TimeText shows the selected behind-live position and available duration: " + timeText.Text);
        Check(behindLive.TotalSeconds > 2 && behindLive.TotalSeconds < totalSeconds - 2,
            $"production slider selected an interior buffered point ({behindLive.TotalSeconds:0.0}s of {totalSeconds:0.0}s)");
        if (mainPlayer.Length > 0)
            Check(mainPlayer.Time < mainPlayer.Length * 0.9, "non-edge live timeshift drag did not seek main MediaPlayer.Time to the media end");
        else
            Check(behindLive > TimeSpan.Zero, "unknown-length live HLS uses the buffered timeshift position instead of MediaPlayer.Time");

        tuner.TryInspectLiveTimeshift(out available, out _);
        totalSeconds = available!.Duration.TotalSeconds;
        await DragSlider(slider, 1000);
        await WaitUntil(() => Field("_liveBehind") is TimeSpan edge && edge <= TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(15), "live-edge drag did not return to the current live edge; status=" + Status);
        Check(timeText.Visibility == Visibility.Visible && timeText.Text.EndsWith("LIVE", StringComparison.Ordinal),
            "TimeText shows Live at the live edge after drag/release: " + timeText.Text);
        Check(Status.StartsWith("Live:", StringComparison.Ordinal), "live-edge slider release reports Live status");
        Check(origin.PlaylistRequests > 0 && origin.SegmentRequests > 0, "production tune read only the local synthetic HLS origin");
        var durationMenu = (MenuItem)window.FindName("TimeshiftSecondsMenu");
        foreach (var seconds in new[] { 30, 60, 90, 120 })
        {
            var item = durationMenu.Items.OfType<MenuItem>().Single(option => option.Tag?.ToString() == seconds.ToString());
            Call("TimeshiftSeconds_Click", item, new RoutedEventArgs());
            Check(new ConfigStore().Load().TimeshiftSeconds == seconds && item.IsChecked &&
                durationMenu.Items.OfType<MenuItem>().Count(option => option.IsChecked) == 1,
                $"{seconds}-second rewind option is saved and exclusively selected");
        }
        var reset = durationMenu.Items.OfType<MenuItem>().First();
        Call("TimeshiftSeconds_Click", reset, new RoutedEventArgs());
        await Task.Delay(1500);
        Call("PauseCurrentPlayback");
        Check(goLiveButton.Visibility == Visibility.Visible && goLiveButton.IsVisible, "Go Live button is rendered when live playback is paused");
        var pausedBehindStart = (TimeSpan)Call("GetCurrentLiveBehind")!;
        var pausedSliderStart = slider.Value;
        await Task.Delay(2500);
        var pausedBehindLater = (TimeSpan)Call("GetCurrentLiveBehind")!;
        Call("UpdatePlaybackPosition");
        Check(pausedBehindLater - pausedBehindStart >= TimeSpan.FromSeconds(1.8), "paused live position counts elapsed pause seconds");
        Check(timeText.Text.StartsWith("Paused 00:02", StringComparison.Ordinal), "playback time visibly reports seconds spent paused: " + timeText.Text);
        Check(slider.Value < pausedSliderStart, "seek bar advances through the growing pause buffer");
        await DragSlider(slider, 500);
        await WaitUntil(() => ((MediaPlayer)Field("_mediaPlayer")!).State == VLCState.Paused &&
            Field("_pausedPlayback") is not null && (TimeSpan)Call("GetCurrentLiveBehind")! > TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(12), "paused seek bar did not move playback to the selected buffered time");
        Check(timeText.Text.StartsWith("Paused 00:", StringComparison.Ordinal) &&
            (TimeSpan)Call("GetLivePauseElapsed")! >= TimeSpan.FromSeconds(2), "seeking while paused retains the pause clock: " + timeText.Text);
        var savedPause = typeof(ChannelTuner).GetField("_pausedLivePosition", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tuner);
        Check(savedPause is not null, "live pause captures a recorded timestamp in the single file");
        var mediaBeforeResume = mainPlayer.Media!.Mrl;
        var pausedTime = mainPlayer.Time;
        var pausedMarker = await SnapshotMarker(mainPlayer, "paused-frame.png");
        await Task.Delay(6000);
        var stillPausedMarker = await SnapshotMarker(mainPlayer, "still-paused-frame.png");
        Check(Math.Abs(pausedMarker - stillPausedMarker) < 0.1, "decoded video frame remains fixed while recording continues");
        Call("ResumePausedPlayback");
        await WaitUntil(() => Field("_pausedPlayback") is null && mainPlayer.IsPlaying,
            TimeSpan.FromSeconds(15), "buffered pause did not resume; status=" + Status);
        Check(mainPlayer.Media!.Mrl.EndsWith(".ts", StringComparison.Ordinal) && mainPlayer.Media.Mrl == mediaBeforeResume,
            "resume preserves the paused decoder and continuous file reader");
        Check(Math.Abs(mainPlayer.Time - pausedTime) < 1000, "real video resumes within one second of the paused playback timestamp");
        var resumedMarker = await SnapshotMarker(mainPlayer, "resumed-frame.png");
        var markerDelta = Math.Abs(resumedMarker - pausedMarker);
        Check(Math.Min(markerDelta, Math.Abs(100 - markerDelta)) < 12,
            "resumed decoded frame continues beside the paused marker rather than jumping to live");
        Check(Status.Contains("paused position", StringComparison.Ordinal), "resume reports the retained pause position");
        Check(goLiveButton.Visibility == Visibility.Collapsed, "Go Live button hides after resuming playback");
        Call("PauseCurrentPlayback");
        Check(goLiveButton.Visibility == Visibility.Visible, "Go Live button returns on the next pause");
        Check((TimeSpan)Call("GetLivePauseElapsed")! < TimeSpan.FromSeconds(1), "each new pause starts its own elapsed-seconds counter");
        Call("GoLive_Click", goLiveButton, new RoutedEventArgs());
        await WaitUntil(() => mainPlayer.IsPlaying && goLiveButton.Visibility == Visibility.Collapsed,
            TimeSpan.FromSeconds(15), "Go Live did not jump to the live edge and resume playback");
        Check(Status.StartsWith("Live:", StringComparison.Ordinal), "Go Live resumes at the current live edge");
        var toggle = (MenuItem)window.FindName("TimeshiftMenu");
        toggle.IsChecked = false;
        Call("TimeshiftToggle_Click", toggle, new RoutedEventArgs());
        await WaitUntil(() => slider.Visibility == Visibility.Collapsed,
            TimeSpan.FromSeconds(15), "disabled timeshift should hide the bar until paused");
        Check(!new ConfigStore().Load().TimeshiftEnabled, "timeshift off is saved");
        Check(slider.Visibility == Visibility.Collapsed, "live seek bar is hidden with timeshift off");
        await WaitUntil(() => Field("_mediaPlayer") is MediaPlayer replacement && replacement != mainPlayer && replacement.IsPlaying,
            TimeSpan.FromSeconds(15), "retune with timeshift off did not start");
        Call("PauseCurrentPlayback");
        await Task.Delay(2000);
        Check(slider.Visibility == Visibility.Visible && slider.IsEnabled,
            "pausing exposes the recorded timeline even when rewind controls were disabled");
        Call("ResumePausedPlayback");
        await WaitUntil(() => Field("_pausedPlayback") is null && ((MediaPlayer)Field("_mediaPlayer")!).IsPlaying,
            TimeSpan.FromSeconds(15), "pause/resume with timeshift UI off did not retain playback");
        Check(Status.Contains("paused position", StringComparison.Ordinal), "pause protection works with timeshift controls disabled");
        toggle.IsChecked = true;
        Call("TimeshiftToggle_Click", toggle, new RoutedEventArgs());
        await WaitUntil(() => tuner.TryInspectLiveTimeshift(out var resumed, out _) && resumed!.Duration > TimeSpan.Zero,
            TimeSpan.FromSeconds(20), "enabling timeshift should start a fresh buffer");
        Call("UpdatePlaybackPosition");
        Check(slider.Visibility == Visibility.Visible && new ConfigStore().Load().TimeshiftEnabled, "timeshift on is saved and live seek bar returns");
        Call("EnterFullScreen"); Call("UpdatePlaybackPosition");
        Check(slider.Visibility == Visibility.Visible && timeText.Visibility == Visibility.Visible, "full screen retains seek bar and elapsed / buffer duration");
        Call("ExitFullScreen");
    }

    private static async Task DragSlider(Slider slider, double value)
    {
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = slider };
        Call("SeekSlider_PreviewMouseDown", slider, down);
        slider.Value = value;
        var preview = (TextBlock)window.FindName("TimeText");
        Check(preview.Visibility == Visibility.Visible && preview.Text.Contains(" / ", StringComparison.Ordinal),
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
