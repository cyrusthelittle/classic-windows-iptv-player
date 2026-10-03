using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;

// A real WPF dispatcher and production MainWindow library paths, without showing a
// window or initializing native playback. No normal accounts/settings are read.
internal static class Program
{
    static string root = "";
    static MainWindow window = null!;
    static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    static object? Field(string name) => typeof(MainWindow).GetField(name, Flags)?.GetValue(window);
    static object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethods(Flags)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(window, args);
    static void Evidence(string message) => File.AppendAllText(Path.Combine(root, "evidence.txt"), message + "\n");
    static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        Evidence("PASS " + message);
    }
    static AppState State => (AppState)Field("_state")!;
    static List<Channel> Channels => (List<Channel>)Field("_channels")!;
    static ListBox List => (ListBox)window.FindName("ChannelList");

    [STAThread]
    static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("CYRUS_IPTV_DATA_ROOT", AppContext.BaseDirectory);
        if (!args.Contains("--child"))
        {
            root = Path.Combine(Path.GetTempPath(), "cyrus-library-loading-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var source in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, source);
                if (relative.StartsWith("cache\\", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("logs\\", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("accounts.", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("feedback-endpoint.txt", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("feedback-endpoint.bootstrap", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("evidence.txt", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            using var child = Process.Start(new ProcessStartInfo(Path.Combine(root, Path.GetFileName(Environment.ProcessPath!)), "--child")
                { WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
            if (!child!.WaitForExit(180000)) { child.Kill(true); Environment.ExitCode = 1; }
            else Environment.ExitCode = child.ExitCode;
            if (Environment.ExitCode == 0)
            {
                using var restart = Process.Start(new ProcessStartInfo(Path.Combine(root, Path.GetFileName(Environment.ProcessPath!)), "--child --restart")
                    { WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
                if (!restart!.WaitForExit(60000)) { restart.Kill(true); Environment.ExitCode = 1; }
                else Environment.ExitCode = restart.ExitCode;
            }
            Console.WriteLine("Isolated fixture: " + root);
            Console.WriteLine(File.Exists(Path.Combine(root, "evidence.txt")) ? File.ReadAllText(Path.Combine(root, "evidence.txt")) : "FAIL no evidence");
            return;
        }

        root = AppContext.BaseDirectory;
        var store = new ConfigStore();
        var account = new AccountSettings { M3uUrl = "http://127.0.0.1:1/fixture-offline.m3u" };
        var state = new AppState { CheckForUpdatesOnStartup = false, RemoteControlEnabled = false, EpgEnabled = false,
            FeedbackEndpoint = "", SelectedAccountId = "fixture-a", Account = account.Clone(), Accounts =
            [ new() { Id = "fixture-a", Name = "Invented A", Settings = account.Clone() },
              new() { Id = "fixture-b", Name = "Invented B", Settings = account.Clone() },
              new() { Id = "fixture-large", Name = "Invented large", Settings = account.Clone() } ] };
        if (!args.Contains("--restart"))
        {
        store.Save(state);
        foreach (var (id, count) in new[] { ("fixture-a", 50000), ("fixture-b", 50000), ("fixture-large", 150000) })
        {
            var fixture = Enumerable.Range(0, count).Select(i => new Channel { Id = id + i,
                Name = id + " Channel " + i.ToString("D6"), Group = "Group " + i % 30,
                Url = "http://127.0.0.1:1/" + id + "/" + i, MediaKind = MediaKind.Live }).ToList();
            store.SaveChannelCache(id, fixture);
        }
        state.SelectedLibrary.FavoriteIds.Add("fixture-favorite-marker");
        store.Save(state);
        }
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var startup = typeof(App).GetMethod("OnStartup", Flags, null, [typeof(object), typeof(StartupEventArgs)], null)!;
        app.Startup -= (StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler), app);
        window = new MainWindow(new LoginResult { Account = account, AccountId = "fixture-a", UpdatePlaylist = false });
        typeof(MainWindow).GetField("_browseMode", Flags)!.SetValue(window, 2);
        app.MainWindow = window;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                Evidence("Fixture uses production MainWindow LoadChannelsAsync and actual bound WPF ListBox; no visible window, native playback, provider or account-dialog interaction.");
                if (args.Contains("--restart"))
                {
                    await Measure("full process restart 50k disk reload offline", "fixture-a", 50000);
                    Check(State.SelectedLibrary.FavoriteIds.Contains("fixture-favorite-marker"), "full process restart preserves account favorites");
                    Evidence("RESTART ALL PASS");
                    return;
                }
                await Measure("cold 50k offline", "fixture-a", 50000);
                List.SelectedIndex = 123;
                var firstA = Field("_searchIndex");
                Switch("fixture-b"); await Measure("cold B 50k offline", "fixture-b", 50000);
                Switch("fixture-a"); await Measure("warm A 50k offline", "fixture-a", 50000);
                Check(List.SelectedIndex == 123, "production account browse-position helpers restore A selection after B");
                if (Field("_preparedLibraries") is not null)
                    Check(ReferenceEquals(firstA, Field("_searchIndex")), "A→B→A reuses exact prepared search index");
                Check(State.SelectedLibrary.FavoriteIds.Contains("fixture-favorite-marker"), "account A favorites survive switching");
                var sourceA = ((List<Channel>)Field("_sourceChannels")!)[0];
                State.SelectedLibrary.ChannelOrganization[ItemIdentity.For(sourceA)] = new() { Name = "Renamed fixture A" };
                Switch("fixture-b"); await Load();
                var oldB = Field("_searchIndex");
                Switch("fixture-a"); await Load();
                Verify("fixture-a", 50000);
                Check(!ReferenceEquals(firstA, Field("_searchIndex")) && Channels.Any(c => c.Name == "Renamed fixture A"),
                    "organization edits invalidate prepared A index and publish renamed channel");
                var replacementB = store.LoadChannelCache("fixture-b");
                replacementB[0].Name = "Replaced fixture B";
                store.SaveChannelCache("fixture-b", replacementB);
                Switch("fixture-b"); await Load();
                Verify("fixture-b", 50000);
                Check(!ReferenceEquals(oldB, Field("_searchIndex")) && Channels.Any(c => c.Name == "Replaced fixture B"),
                    "protected disk catalog replacement invalidates prepared B index");
                Switch("fixture-large"); await Measure("cold 150k offline", "fixture-large", 150000);
                // Begin a request, switch before its awaited work can publish, then
                // await both. This uses actual request cancellation/publication guards.
                var stalePublication = false;
                var observer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(1) };
                observer.Tick += (_, _) =>
                {
                    foreach (var field in new[] { "_sourceChannels", "_channels", "_visibleChannels" })
                        if (Field(field) is List<Channel> { Count: > 0 } list &&
                            !list[0].Id.StartsWith(State.SelectedAccountId, StringComparison.Ordinal)) stalePublication = true;
                };
                observer.Start();
                for (var i = 0; i < 8; i++)
                {
                    Switch(i % 2 == 0 ? "fixture-a" : "fixture-large");
                    var stale = Load();
                    Switch("fixture-b"); var current = Load();
                    await Task.WhenAll(stale, current);
                    Verify("fixture-b", 50000);
                }
                observer.Stop();
                Check(!stalePublication, "dispatcher sampling saw no obsolete source/organized/visible catalog published during overlapping loads");
                Check(true, "8 concurrent obsolete-request switches publish only selected account B");
                var cache = Field("_preparedLibraries");
                if (cache is not null)
                    Evidence("Prepared cache retained bytes=" + cache.GetType().GetProperty("RetainedBytes")?.GetValue(cache) +
                             "; entries=" + cache.GetType().GetProperty("Count")?.GetValue(cache));
                window.Close();
                window = new MainWindow(new LoginResult { Account = account, AccountId = "fixture-a", UpdatePlaylist = false });
                typeof(MainWindow).GetField("_browseMode", Flags)!.SetValue(window, 2);
                app.MainWindow = window;
                await Measure("fresh MainWindow 50k disk reload offline", "fixture-a", 50000);
                Check(new ConfigStore().LoadChannelCache("fixture-a").Count == 50000, "protected disk library survives closing/reopening MainWindow");
                Evidence("ALL PASS");
            }
            catch (Exception ex) { Evidence("FAIL " + ex); Environment.ExitCode = 1; }
            finally { try { window.Close(); } catch { } app.Shutdown(); }
        }, DispatcherPriority.Normal);
        app.Run();
    }

    static void Switch(string id)
    {
        Call("RememberAccountBrowsePosition");
        Call("ResetAccountView");
        State.SelectedAccountId = id;
        State.Account = State.EnsureSelectedAccount().Settings.Clone();
        Call("ApplyDefaultStartupFilters");
        typeof(MainWindow).GetField("_browseMode", Flags)!.SetValue(window, 2);
        Call("RestoreAccountBrowsePosition");
    }
    static Task Load() => (Task)Call("LoadChannelsAsync", false, true, true)!;
    static void Verify(string id, int count)
    {
        Check(Channels.Count == count && Channels.All(c => c.Id.StartsWith(id, StringComparison.Ordinal)), id + " source catalog identity and count");
        Check(List.Items.Count == count, id + " actual WPF ListBox has " + count + " bound live items");
        Check(((FrameworkElement)window.FindName("AccountLoadingOverlay")).Visibility == Visibility.Collapsed,
            id + " loading overlay releases cached library");
        Check(Field("_libraryLoadCts") is null, id + " completed library token is cleared before disposal");
    }
    static async Task Measure(string label, string id, int count)
    {
        var memoryBefore = GC.GetTotalAllocatedBytes();
        var clock = Stopwatch.StartNew();
        await Load();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        clock.Stop();
        Verify(id, count);
        Evidence($"MEASURE {label}: usable bound list={clock.Elapsed.TotalMilliseconds:F1}ms; allocated={(GC.GetTotalAllocatedBytes() - memoryBefore) / 1048576.0:F1}MiB; process private={Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:F1}MiB");
    }
}
