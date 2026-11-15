using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;

internal static class UiChecks
{
    public static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var account = new AccountSettings { M3uUrl = "http://127.0.0.1:1/fixture.m3u" };
        var state = new AppState { Account = account.Clone(), SelectedAccountId = "fixture-ui", CheckForUpdatesOnStartup = false, RemoteControlEnabled = false,
            EpgEnabled = false, Accounts = [new() { Id = "fixture-ui", Name = "Invented UI", Settings = account.Clone() }] };
        var store = new ConfigStore(); store.Save(state);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var window = new MainWindow(new LoginResult { Account = account, AccountId = "fixture-ui", UpdatePlaylist = false });
        var checkbox = (MenuItem)window.FindName("AutomaticUpdateDownloadMenuItem");
        FixtureTools.Check(checkbox.IsCheckable && !checkbox.IsChecked, "production Settings auto-download checkbox starts off");
        var toggle = typeof(MainWindow).GetMethod("ToggleAutomaticUpdateDownload_Click", flags)!;
        checkbox.IsChecked = true; toggle.Invoke(window, [checkbox, new RoutedEventArgs()]);
        FixtureTools.Check(store.Load().DownloadUpdatesAutomatically, "auto-download checkbox saves enabled preference");
        checkbox.IsChecked = false; toggle.Invoke(window, [checkbox, new RoutedEventArgs()]);
        FixtureTools.Check(!store.Load().DownloadUpdatesAutomatically, "auto-download checkbox saves disabled preference");
        var gate = typeof(MainWindow).GetMethod("CanRestartForUpdate", flags)!;
        bool Allowed() => (bool)gate.Invoke(window, null)!;
        FixtureTools.Check(Allowed(), "restart predicate permits idle app");
        var playback = typeof(MainWindow).GetField("_playbackState", flags)!.GetValue(window)!;
        var tune = new Channel { Id = "fixture-playing", Url = "http://127.0.0.1:1/fixture" };
        playback.GetType().GetMethod("Start")!.Invoke(playback, [tune, new PlaybackCandidate[] { new(tune.Url, "Invented") }, 0, new object()]);
        FixtureTools.Check(!Allowed(), "restart predicate blocks selected playback session including paused or buffering state");
        playback.GetType().GetMethod("Stop")!.Invoke(playback, null);
        FixtureTools.Check(Allowed(), "restart predicate permits stopped playback with retained title");
        playback.GetType().GetMethod("Reset")!.Invoke(playback, null);
        foreach (var name in new[] { "_recordingTransitionTask", "_recordingStopTask" })
        {
            var field = typeof(MainWindow).GetField(name, flags)!;
            field.SetValue(window, new TaskCompletionSource().Task);
            FixtureTools.Check(!Allowed(), "restart predicate blocks pending " + name);
            field.SetValue(window, Task.CompletedTask);
            FixtureTools.Check(Allowed(), "restart predicate permits completed " + name);
            field.SetValue(window, null);
        }
        var shutdown = typeof(MainWindow).GetField("_isShuttingDown", flags)!;
        shutdown.SetValue(window, true); FixtureTools.Check(!Allowed(), "restart predicate blocks shutting down"); shutdown.SetValue(window, false);
        var page = new Uri("https://github.com/cyrusthelittle/classic-windows-iptv-player/releases/tag/v2.0.0");
        var release = new GitHubReleaseInfo(new Version(2, 0, 0), "v2.0.0", "Invented", page, "Fixture notes");
        var manifest = new UpdateManifest(AuthenticatedUpdate.ManifestFormat, AuthenticatedUpdate.Repository, "v2.0.0", "2.0.0", "win-x64", AuthenticatedUpdate.PackageName("v2.0.0", UpdateEdition.Full), 1, new string('0', 64), AppState.CurrentSchemaVersion, []);
        var ready = new UpdateWindow(new Version(1, 0, 0), release, stagedUpdate: new StagedUpdate(FixtureTools.Root, manifest));
        FixtureTools.Check(((Button)ready.FindName("UpdateButton")).Content.ToString() == "Restart to update" && ((Button)ready.FindName("LaterButton")).Content.ToString() == "Later", "staged offer presents explicit restart and later choices");
        FixtureTools.Check(ready.PromptResult == UpdatePromptResult.RemindLater, "staged offer does not choose restart itself");
        ready.IsModelessPrompt = true;
        ready.Show();
        Capture(ready, "staged-light");
        typeof(App).Assembly.GetType("ClassicWindowsIptvPlayer.Windows.ThemeManager")!.GetMethod("Apply")!.Invoke(null, [true]);
        ready.Width = 480; ready.Height = 360;
        Capture(ready, "staged-dark-minimum");
        var readyClosed = false; ready.Closed += (_, _) => readyClosed = true;
        typeof(UpdateWindow).GetMethod("Update_Click", flags)!.Invoke(ready, [ready, new RoutedEventArgs()]);
        FixtureTools.Check(readyClosed && ready.PromptResult == UpdatePromptResult.UpdateNow, "modeless staged restart closes without modal DialogResult");
        foreach (var (handler, result) in new[] { ("Later_Click", UpdatePromptResult.RemindLater), ("Never_Click", UpdatePromptResult.NeverRemind) })
        {
            var modeless = new UpdateWindow(new Version(1, 0, 0), release) { IsModelessPrompt = true };
            var closed = false; modeless.Closed += (_, _) => closed = true;
            typeof(UpdateWindow).GetMethod(handler, flags)!.Invoke(modeless, [modeless, new RoutedEventArgs()]);
            FixtureTools.Check(closed && modeless.PromptResult == result, "modeless " + handler + " closes without modal DialogResult");
        }
        // An invalid tag is rejected before HttpClient is called. Exercise real catch/finally and Retry UI safely.
        var invalid = new UpdateWindow(new Version(1, 0, 0), release with { DisplayVersion = "invalid-fixture-tag" });
        invalid.IsModelessPrompt = true;
        invalid.Show();
        var download = typeof(UpdateWindow).GetMethod("DownloadAsync", flags)!;
        foreach (var attempt in new[] { 1, 2 })
        {
            ((Task)download.Invoke(invalid, null)!).GetAwaiter().GetResult();
            FixtureTools.Check(((Button)invalid.FindName("UpdateButton")).IsEnabled && ((Button)invalid.FindName("UpdateButton")).Content.ToString() == "Retry update" &&
                ((Button)invalid.FindName("CancelDownloadButton")).Visibility == Visibility.Collapsed && invalid.StagedUpdate is null, "failed pre-network validation restores retry state attempt " + attempt);
        }
        Capture(invalid, "failed-retry-dark");
        invalid.Close();
        window.Close(); app.Shutdown();
    }
    static void Capture(UpdateWindow window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(FixtureTools.Root, name + ".png")); png.Save(output);
        foreach (var controlName in new[] { "UpdateButton", "LaterButton" })
        {
            var button = (Button)window.FindName(controlName);
            var position = button.TransformToAncestor(window).Transform(new Point());
            FixtureTools.Check(button.IsVisible && button.ActualWidth > 0 && position.X >= 0 && position.Y >= 0 &&
                position.X + button.ActualWidth <= window.ActualWidth && position.Y + button.ActualHeight <= window.ActualHeight,
                name + " retains visible " + controlName);
        }
    }
}
