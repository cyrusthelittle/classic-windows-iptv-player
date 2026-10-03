using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;

internal static class Program
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static MainWindow window = null!;
    static string root = "";
    static void Evidence(string text) => File.AppendAllText(Path.Combine(root, "evidence.txt"), text + "\n");
    static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); Evidence("PASS " + text); }
    static object? Field(string name) => typeof(MainWindow).GetField(name, Flags)?.GetValue(window);
    static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethods(Flags)
        .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args);
    static object? Call(string name, params object?[] args) => Call(window, name, args);
    static AppState State => (AppState)Field("_state")!;
    static List<Channel> Channels => (List<Channel>)Field("_channels")!;
    static Task Load(bool refresh) => (Task)Call("LoadChannelsAsync", refresh, false, true)!;
    static void Preference(SavedAccount account, bool value) => typeof(SavedAccount).GetProperty("BackgroundPlaylistRefresh")!.SetValue(account, value);
    static bool Preference(SavedAccount account) => (bool)typeof(SavedAccount).GetProperty("BackgroundPlaylistRefresh")!.GetValue(account)!;
    static async Task Wait(Func<bool> condition, string failure)
    {
        for (var i = 0; i < 150; i++) { if (condition()) return; await Task.Delay(20); }
        throw new InvalidOperationException(failure);
    }
    static string Playlist(string suffix, bool movie = true) => "#EXTM3U\n#EXTINF:-1 group-title=\"Live\",Live " + suffix +
        "\nhttp://127.0.0.1:1/live/" + suffix + ".ts\n" + (movie ? "#EXTINF:-1 group-title=\"Movies\",Movie " + suffix + "\nhttp://127.0.0.1:1/movie/" + suffix + ".mp4\n" : "");
    static void Switch(string id)
    {
        Call("ResetAccountView"); State.SelectedAccountId = id;
        State.Account = State.EnsureSelectedAccount().Settings.Clone();
        typeof(MainWindow).GetField("_browseMode", Flags)!.SetValue(window, 2);
        typeof(MainWindow).GetField("_mediaKindMode", Flags)!.SetValue(window, 0);
    }
    static DateTime? Updated(string id) => new ConfigStore().Load().Accounts.Single(a => a.Id == id).LastPlaylistUpdatedUtc;
    static void Retained(string name, DateTime? successful)
    {
        Check(Channels.Any(c => c.Name == name), "last verified displayed catalog remains " + name);
        Check(new ConfigStore().LoadChannelCache("fixture-a").Any(c => c.Name == name), "last verified disk catalog remains " + name);
        Check(Updated("fixture-a") == successful, "failed/canceled/partial refresh preserves successful timestamp");
    }

    [STAThread]
    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("CYRUS_IPTV_DATA_ROOT", AppContext.BaseDirectory);
        if (!args.Contains("--child"))
        {
            root = Path.Combine(Path.GetTempPath(), "cyrus-background-refresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var source in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, source);
                if (relative.StartsWith("cache\\", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("logs\\", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("accounts.", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("feedback-endpoint", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
            }
            using var child = Process.Start(new ProcessStartInfo(Path.Combine(root, Path.GetFileName(Environment.ProcessPath!)), "--child")
                { WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
            if (!child!.WaitForExit(120000)) { child.Kill(true); Environment.ExitCode = 1; }
            else Environment.ExitCode = child.ExitCode;
            Console.WriteLine("Isolated fixture: " + root);
            Console.WriteLine(File.Exists(Path.Combine(root, "evidence.txt")) ? File.ReadAllText(Path.Combine(root, "evidence.txt")) : "FAIL no evidence");
            return;
        }
        root = AppContext.BaseDirectory;
        using var provider = new SlowProviderFixture();
        var account = new AccountSettings { M3uUrl = provider.Url };
        var store = new ConfigStore();
        var initialTime = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var state = new AppState { SelectedAccountId = "fixture-a", Account = account.Clone(), EpgEnabled = false,
            RemoteControlEnabled = false, CheckForUpdatesOnStartup = false, FeedbackEndpoint = "", Accounts =
            [ new() { Id = "fixture-a", Name = "Invented A", Settings = account.Clone(), LastPlaylistUpdatedUtc = initialTime },
              new() { Id = "fixture-b", Name = "Invented B", Settings = account.Clone(), LastPlaylistUpdatedUtc = initialTime },
              new() { Id = "fixture-empty", Name = "Invented uncached", Settings = account.Clone() } ] };
        store.Save(state);
        foreach (var id in new[] { "fixture-a", "fixture-b" })
            store.SaveChannelCache(id, new[] { new Channel { Id = id + "live", Name = "Saved " + id, Group = "Live", Url = "http://127.0.0.1:1/live/old.ts" },
                new Channel { Id = id + "movie", Name = "Saved movie", Group = "Movies", Url = "http://127.0.0.1:1/movie/old.mp4", MediaKind = MediaKind.Movie } });
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var startup = typeof(App).GetMethod("OnStartup", Flags, null, [typeof(object), typeof(StartupEventArgs)], null)!;
        app.Startup -= (StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler), app);
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                Check(store.Load().Accounts.All(a => !Preference(a)), "existing accounts default remembered background refresh off");
                var editor = new LoginWindow();
                var checkbox = (CheckBox)editor.FindName("BackgroundPlaylistRefreshCheck");
                Check((checkbox.Content is TextBlock label ? label.Text : checkbox.Content?.ToString()) == "Update playlist in the background every time I open this account", "account editor checkbox has requested label");
                checkbox.IsChecked = true; Call(editor, "SaveAccount_Click", editor, new RoutedEventArgs()); editor.Close();
                Check(Preference(store.Load().Accounts.Single(a => a.Id == "fixture-a")) && !Preference(store.Load().Accounts.Single(a => a.Id == "fixture-b")), "editor Save persists preference only for account A");
                editor = new LoginWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
                var cancelEditor = editor;
                editor.Loaded += (_, _) =>
                {
                    ((CheckBox)cancelEditor.FindName("BackgroundPlaylistRefreshCheck")).IsChecked = false;
                    Call(cancelEditor, "CaptureFields");
                    Call(cancelEditor, "Cancel_Click", cancelEditor, new RoutedEventArgs());
                };
                editor.ShowDialog();
                Check(Preference(store.Load().Accounts.Single(a => a.Id == "fixture-a")), "actual account editor Cancel discards changed preference");

                window = new MainWindow(new LoginResult { Account = account, AccountId = "fixture-a", UpdatePlaylist = false });
                app.MainWindow = window; Switch("fixture-a");
                var menu = (MenuItem)window.FindName("BackgroundPlaylistRefreshMenuItem");
                Call("SettingsMenu_Opened", menu, new RoutedEventArgs());
                Check(menu.IsCheckable && menu.IsChecked, "Settings reflects selected account background preference");
                menu.IsChecked = false; menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
                Check(!Preference(store.Load().Accounts.Single(a => a.Id == "fixture-a")), "Settings checkbox persists preference off");
                menu.IsChecked = true; menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
                Check(Preference(store.Load().Accounts.Single(a => a.Id == "fixture-a")), "Settings checkbox persists preference on");

                var slow = provider.Enqueue(Playlist("new"));
                var clock = Stopwatch.StartNew(); var load = Load(true);
                await slow.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Wait(() => Channels.Count == 2, "cached library was not published while provider held");
                Check(((ListBox)window.FindName("ChannelList")).Items.Count == 2, "actual bound WPF list remains usable while slow provider blocked");
                Check(((FrameworkElement)window.FindName("AccountLoadingOverlay")).Visibility == Visibility.Collapsed, "background refresh has no blocking loading overlay");
                Evidence($"MEASURE saved library usable with provider blocked: {clock.Elapsed.TotalMilliseconds:F1}ms");
                var requests = provider.Requests; var duplicate = Load(true);
                Check(ReferenceEquals(load, duplicate), "same-account repeated refresh returns the same task");
                ((ListBox)window.FindName("ChannelList")).SelectedIndex = 0;
                await Task.Delay(150);
                Check(provider.Requests == requests, "same-account repeated refresh trigger makes no duplicate provider request");
                Check(((Button)window.FindName("CancelLibraryRefreshButton")).IsEnabled,
                    "busy background refresh exposes enabled Cancel action");
                slow.Release.TrySetResult(); await Task.WhenAll(load, duplicate).WaitAsync(TimeSpan.FromSeconds(8));
                Check(Channels.Any(c => c.Name == "Live new") && store.LoadChannelCache("fixture-a").Any(c => c.Name == "Live new"), "successful background replacement publishes and saves verified catalog");
                var successful = Updated("fixture-a");
                Check(successful > initialTime, "success advances persisted update timestamp");
                Check(((TextBlock)window.FindName("LibraryRefreshStatusText")).Text.Contains("Last successful update:", StringComparison.Ordinal),
                    "background status presents last successful update");
                var requestsAfterSuccess = provider.Requests;
                editor = new LoginWindow();
                ((TextBox)editor.FindName("AccountNameBox")).Text = "Invented A edited while open";
                Call(editor, "SaveAccount_Click", editor, new RoutedEventArgs()); editor.Close();
                Call("ReloadAfterAccountEditor");
                await Task.Delay(100);
                Check(provider.Requests == requestsAfterSuccess, "saving current account edits does not start another refresh");

                var failed = provider.Enqueue("Fixture failure", 503); load = Load(true);
                await failed.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5)); failed.Release.TrySetResult(); await load.WaitAsync(TimeSpan.FromSeconds(8));
                Retained("Live new", successful);
                Check(((Button)window.FindName("RetryLibraryRefreshButton")).IsEnabled, "failure exposes enabled Retry action");
                var omitted = provider.Enqueue(Playlist("partial", movie: false)); load = Load(true);
                await omitted.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5)); omitted.Release.TrySetResult(); await load.WaitAsync(TimeSpan.FromSeconds(8));
                Retained("Live new", successful);
                Check(!Channels.Any(c => c.Name == "Live partial"), "provider missing saved Movie kind does not replace verified library");

                var canceled = provider.Enqueue(Playlist("canceled")); load = Load(true);
                await canceled.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var cancel = (Button)window.FindName("CancelLibraryRefreshButton"); cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cancel));
                await load.WaitAsync(TimeSpan.FromSeconds(8)); canceled.Release.TrySetResult(); Retained("Live new", successful);
                var retry = provider.Enqueue(Playlist("retry"));
                ((Button)window.FindName("RetryLibraryRefreshButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await retry.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5)); retry.Release.TrySetResult();
                await Wait(() => Channels.Any(c => c.Name == "Live retry") && Field("_libraryLoadCts") is null, "Retry did not complete a verified refresh");
                Check(Updated("fixture-a") >= successful, "Retry succeeds and records successful update");

                var obsolete = provider.Enqueue(Playlist("obsolete")); load = Load(true);
                await obsolete.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Switch("fixture-b"); await Load(false); obsolete.Release.TrySetResult(); await load.WaitAsync(TimeSpan.FromSeconds(8));
                Check(Channels.All(c => c.Name.StartsWith("Saved", StringComparison.Ordinal)), "switch B cancels obsolete A publication");
                Check(!store.LoadChannelCache("fixture-a").Any(c => c.Name == "Live obsolete"), "obsolete refresh never overwrites A verified disk cache");
                Switch("fixture-a"); await Load(false);
                Check(Channels.Any(c => c.Name == "Live retry"), "A→B→A returns to latest verified A cache");
                Check(Field("_libraryLoadCts") is null, "completed refresh cancellation token cleared before disposal");
                var noCache = provider.Enqueue(Playlist("initial"));
                Switch("fixture-empty"); load = Load(false);
                await noCache.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(((FrameworkElement)window.FindName("AccountLoadingOverlay")).Visibility == Visibility.Visible,
                    "uncached account still uses mandatory initial-load overlay with preference off");
                noCache.Release.TrySetResult(); await load.WaitAsync(TimeSpan.FromSeconds(8));
                Check(Channels.Any(c => c.Name == "Live initial") && store.LoadChannelCache("fixture-empty").Count == 2,
                    "uncached account performs and saves required initial provider load");
                Evidence("ALL PASS");
            }
            catch (Exception ex) { Evidence("FAIL " + ex); Environment.ExitCode = 1; }
            finally { try { window?.Close(); } catch { } app.Shutdown(); }
        }, DispatcherPriority.Normal);
        app.Run();
    }
}
