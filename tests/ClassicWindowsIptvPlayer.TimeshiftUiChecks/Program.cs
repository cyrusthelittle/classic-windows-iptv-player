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
        using var origin = new HlsFixture(File.ReadAllBytes(Path.Combine(root, "segment.avi")));
        var store = new ConfigStore();
        var state = store.Load();
        var saved = state.EnsureSelectedAccount();
        var account = new AccountSettings { ServerUrl = origin.Origin, Username = "local-fixture", Password = "local-fixture" };
        saved.Settings = account.Clone(); saved.Name = "Local timeshift fixture";
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
            TimeSpan.FromSeconds(12), "local HLS fixture did not build a useful timeshift window");

        var slider = (Slider)window.FindName("SeekSlider");
        var timeText = (TextBlock)window.FindName("TimeText");
        var tuner = (ChannelTuner)Field("_tuner")!;
        tuner.TryInspectLiveTimeshift(out var available, out _);
        var totalSeconds = available!.Duration.TotalSeconds;
        var mainPlayer = (MediaPlayer)Field("_mediaPlayer")!;
        Check(true, $"live player seek metrics before drag: Time={mainPlayer.Time}ms, Length={mainPlayer.Length}ms, State={mainPlayer.State}");

        await DragSlider(slider, 500);
        await WaitUntil(() => Field("_liveBehind") is TimeSpan behind && behind.TotalSeconds > 2 && behind.TotalSeconds < totalSeconds,
            TimeSpan.FromSeconds(15), "non-edge drag did not commit a buffered seek; status=" + Status);
        var behindLive = (TimeSpan)Field("_liveBehind")!;
        Check(timeText.Visibility == Visibility.Visible, "TimeText is visible after non-edge drag/release");
        Check(timeText.Text.StartsWith("Behind live ", StringComparison.Ordinal) && timeText.Text.Contains(" / ", StringComparison.Ordinal),
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
        Check(timeText.Visibility == Visibility.Visible && timeText.Text.StartsWith("Live / ", StringComparison.Ordinal),
            "TimeText shows Live at the live edge after drag/release: " + timeText.Text);
        Check(Status.StartsWith("Live:", StringComparison.Ordinal), "live-edge slider release reports Live status");
        Check(origin.PlaylistRequests > 0 && origin.SegmentRequests > 0, "production tune read only the local synthetic HLS origin");
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
        private readonly byte[] segment;
        private readonly CancellationTokenSource stopping = new();
        private int playlistRequests;
        private int segmentRequests;
        public string Origin { get; }
        public int PlaylistRequests => Volatile.Read(ref playlistRequests);
        public int SegmentRequests => Volatile.Read(ref segmentRequests);

        public HlsFixture(byte[] data)
        {
            segment = data;
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
                    var edge = 7 + PlaylistRequests;
                    var first = edge - 7;
                    var text = new StringBuilder($"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{first}\n");
                    for (var i = first; i <= edge; i++) text.AppendLine("#EXTINF:2.0,").AppendLine($"segment-{i}.ts");
                    body = Encoding.UTF8.GetBytes(text.ToString()); mime = "application/vnd.apple.mpegurl";
                }
                else if (path.StartsWith("/segment-", StringComparison.Ordinal) && path.EndsWith(".ts", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref segmentRequests); body = segment; mime = "video/mp2t";
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
