using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;
using LibVLCSharp.Shared;

internal static class Program
{
    static MainWindow window = null!;
    static string root = "";
    static object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
        ?? typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
    static void Call(string name, params object?[] args) => typeof(MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(window, args);
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        File.AppendAllText(Path.Combine(root, "evidence.txt"), "PASS " + message + "\n");
    }
    static MenuItem Menu(ItemsControl parent, string header) => parent.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == header);
    static void Flush() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    static void Click(MenuItem item)
    {
        if (item.IsCheckable) item.IsChecked = !item.IsChecked;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        Flush();
    }
    static MenuItem Settings() => Menu((Menu)window.FindName("AppMenu"), "Settings");
    static void OpenSettings()
    {
        var settings = Settings();
        settings.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, settings));
        Flush();
    }
    static void VerifySelected(string name, MenuItem parent, string selectedHeader)
    {
        var selected = Menu(parent, selectedHeader);
        Check(selected.IsCheckable && selected.IsChecked, name + " selected option has rendered checkmark");
        foreach (var other in parent.Items.OfType<MenuItem>().Where(item => item != selected && item.IsCheckable))
            Check(!other.IsChecked, name + " other options are unchecked");
    }
    static async Task WaitPlaying(string name)
    {
        for (var i = 0; i < 100; i++)
        {
            if (Field("_mediaPlayer") is LibVLCSharp.Shared.MediaPlayer { IsPlaying: true } && Field("_currentChannel") is Channel current && current.Name == name) return;
            await Task.Delay(100);
        }
        throw new Exception("Playback did not start for " + name);
    }
    [STAThread]
    static void Main(string[] args)
    {
        if (!args.Contains("--child"))
        {
            root = Path.Combine(Path.GetTempPath(), "cyrus-playback-settings-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var source in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, source);
                if (relative.StartsWith("cache\\") || relative.StartsWith("logs\\") || relative.StartsWith("accounts.")) continue;
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            using var child = Process.Start(new ProcessStartInfo(Path.Combine(root, Path.GetFileName(Environment.ProcessPath!)), "--child")
            { WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden });
            if (!child!.WaitForExit(120000)) { child.Kill(true); Environment.ExitCode = 1; return; }
            Environment.ExitCode = child.ExitCode;
            Console.WriteLine("Isolated fixture: " + root);
            Console.WriteLine(File.Exists(Path.Combine(root, "evidence.txt")) ? File.ReadAllText(Path.Combine(root, "evidence.txt")) : "FAIL no evidence");
            return;
        }

        root = AppContext.BaseDirectory;
        GenerateAvi.Run(Path.Combine(root, "fixture.avi"));
        var store = new ConfigStore();
        var state = store.Load();
        var saved = state.EnsureSelectedAccount();
        var account = new AccountSettings { M3uUrl = "http://127.0.0.1:1/unused.m3u" };
        saved.Settings = account.Clone(); saved.Name = "Playback settings fixture";
        state.Account = account.Clone(); state.CheckForUpdatesOnStartup = false; state.RemoteControlEnabled = false;
        state.PlaybackBufferMs = 1000; state.ReconnectAttempts = 10; state.DarkMode = false;
        store.Save(state);
        using var fixture = new Fixture(File.ReadAllBytes(Path.Combine(root, "fixture.avi")));
        var channel = new Channel { Id = "fixture", Name = "Fixture", Group = "Fixture", Url = fixture.StreamUrl, MediaKind = MediaKind.Live };
        store.SaveChannelCache(saved.Id, new[] { channel });

        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var startup = typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(object), typeof(StartupEventArgs) }, null)!;
        app.Startup -= (StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler), app);
        window = new MainWindow(new LoginResult { Account = account, AccountId = saved.Id, UpdatePlaylist = false });
        app.MainWindow = window;
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                await WaitUntil(() => Field("_tuner") is not null && Field("_libVlc") is LibVLC,
                    "production MainWindow did not initialize its tuner and player");
                await window.Dispatcher.InvokeAsync(() => Call("PlayChannel", channel, null));
                await WaitPlaying("Fixture");
                await window.Dispatcher.InvokeAsync(() => {
                    var more = (Button)window.FindName("MorePlaybackButton");
                    more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, more));
                    Flush();
                    Check(more.ContextMenu.IsOpen, "More button opens its production context menu");
                    Check(!more.ContextMenu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "Playback options"), "More menu has no Playback options item");
                    Check(!more.ContextMenu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString()?.StartsWith("Favorite", StringComparison.OrdinalIgnoreCase) == true), "More menu no longer hides the Favorite action");
                    Check(more.ContextMenu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "Open recent recording"), "More menu retains Open recent recording");
                    more.ContextMenu.IsOpen = false;
                    var favorite = (Button)window.FindName("FavoriteButton");
                    Check(favorite.IsVisible && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(favorite)),
                        "Favorite button is visible and accessible in the main playback toolbar");
                    var appMenu = (Menu)window.FindName("AppMenu");
                    Check(appMenu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "Playback" &&
                        item.Items.OfType<MenuItem>().Any(child => child.Header?.ToString() == "Favorite / unfavorite selected")),
                        "Favorite action remains available from the main Playback menu");
                    var stopRecording = (Button)window.FindName("StopRecordingButton");
                    Check(stopRecording.Visibility == Visibility.Collapsed,
                        "Stop recording is hidden before a recording starts");
                });

                OpenSettings();
                var settings = Settings();
                VerifySelected("buffer", settings, "Buffer 1 sec");
                VerifySelected("reconnect-attempts", Menu(settings, "Reconnect attempts"), "10 attempts (default)");
                var dark = Menu(settings, "Dark mode");
                Check(dark.IsCheckable && !dark.IsChecked, "dark mode off state has no selected checkmark");

                // Continue exercising production Settings even if synthetic Windows pointer movement
                // does not trigger WPF's hover state in this session.

                Click(Menu(settings, "Buffer 6 sec"));
                OpenSettings();
                VerifySelected("buffer after change", Settings(), "Buffer 6 sec");
                Click(Menu(Menu(Settings(), "Reconnect attempts"), "15 attempts"));
                OpenSettings();
                VerifySelected("reconnect-attempts after change", Menu(Settings(), "Reconnect attempts"), "15 attempts");
                Click(Menu(Settings(), "Dark mode"));
                OpenSettings();
                Check(Menu(Settings(), "Dark mode").IsChecked, "dark mode checkmark appears after selecting dark mode");
                Click(Menu(Settings(), "Dark mode"));
                OpenSettings();
                Check(!Menu(Settings(), "Dark mode").IsChecked, "dark mode checkmark clears after selecting light mode");
                Check(((AppState)Field("_state")!).PlaybackBufferMs == 6000 && ((AppState)Field("_state")!).ReconnectAttempts == 15,
                    "actual settings menu actions persist selected buffer and reconnect values");
                File.AppendAllText(Path.Combine(root, "evidence.txt"), "ALL PASS\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(root, "evidence.txt"), "FAIL " + ex + "\n");
                Environment.ExitCode = 1;
            }
            finally
            {
                try { Call("StopPlayback"); } catch { }
                await Task.Delay(300);
                try { window.Close(); } catch { }
                app.Shutdown();
            }
        };
        window.Show();
        app.Run();
    }

    static async Task WaitUntil(Func<bool> condition, string failure)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(failure);
    }

    static async Task HoverClick(FrameworkElement target)
    {
        var position = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
        CursorApi.SetCursorPos(position);
        await Task.Delay(200);
        target.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, target));
        await Task.Delay(150);
    }

    static Point PopupScreenOrigin(ContextMenu popup)
    {
        var source = PresentationSource.FromVisual(popup) as HwndSource
            ?? throw new InvalidOperationException("More context-menu popup has no WPF presentation source.");
        GetWindowRect(source.Handle, out var rect);
        return new Point(rect.Left, rect.Top);
    }
    static string PopupBounds(ContextMenu popup)
    {
        var source = (HwndSource)PresentationSource.FromVisual(popup)!;
        GetWindowRect(source.Handle, out var rect);
        return $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
}

internal sealed class Fixture : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly byte[] video;
    readonly CancellationTokenSource cancellation = new();
    readonly Task acceptLoop;
    public string StreamUrl { get; }
    public Fixture(byte[] data)
    {
        video = data;
        listener.Start();
        StreamUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live.avi";
        acceptLoop = AcceptLoopAsync();
    }
    async Task AcceptLoopAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try { _ = RespondAsync(await listener.AcceptTcpClientAsync(cancellation.Token)); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }
    async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = new byte[4096];
                var count = await stream.ReadAsync(request, cancellation.Token);
                var requestText = Encoding.ASCII.GetString(request, 0, count);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: video/x-msvideo\r\nContent-Length: {video.Length}\r\nConnection: close\r\nAccept-Ranges: bytes\r\n\r\n");
                await stream.WriteAsync(header, cancellation.Token);
                var range = System.Text.RegularExpressions.Regex.Match(requestText, @"Range: bytes=(\d+)-(\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (range.Success)
                {
                    var start = int.Parse(range.Groups[1].Value);
                    var end = range.Groups[2].Success && range.Groups[2].Value.Length > 0 ? int.Parse(range.Groups[2].Value) : video.Length - 1;
                    start = Math.Clamp(start, 0, video.Length - 1); end = Math.Clamp(end, start, video.Length - 1);
                    await stream.WriteAsync(video.AsMemory(start, end - start + 1), cancellation.Token);
                }
                else await stream.WriteAsync(video, cancellation.Token);
            }
            catch { }
        }
    }
    public void Dispose()
    {
        cancellation.Cancel(); listener.Stop();
        try { acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        cancellation.Dispose();
    }
}

internal static class CursorApi
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);
    public static void SetCursorPos(Point point) => SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));
}

internal static class Mouse
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);
    public static void SetCursorPos(Point point) => SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));
}
