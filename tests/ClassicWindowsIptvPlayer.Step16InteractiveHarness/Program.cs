using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Windows.Controls;
using System.Reflection;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;

namespace ClassicWindowsIptvPlayer.Step16InteractiveHarness;

// Runs the production MainWindow in an isolated process-local data directory.
// This is intentionally a visible WPF session: the operator can inspect the
// rendered window and use keyboard/mouse while the harness records checkpoints.
internal static partial class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var automated = args.Contains("--automated", StringComparer.OrdinalIgnoreCase);
        var child = args.Contains("--child", StringComparer.OrdinalIgnoreCase);
        var root = child ? AppContext.BaseDirectory : Path.Combine(Path.GetTempPath(), "cyrus-step16-" + Guid.NewGuid().ToString("N"));
        if (!child)
        {
            Directory.CreateDirectory(root);
            foreach (var source in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, source);
                if (relative.StartsWith("cache\\", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("logs\\", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("accounts.", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
            }
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(root, Path.GetFileName(Environment.ProcessPath!)), "--child" + (automated ? " --automated" : "") + (args.Contains("--native-dialogs") ? " --native-dialogs" : "")) { WorkingDirectory = root });
            if (automated && !process!.WaitForExit(60000))
            {
                process.Kill(entireProcessTree: true);
                File.WriteAllText(Path.Combine(root, "HARNESS_TIMEOUT.txt"), "Automated WPF harness exceeded 60 seconds.");
                Environment.ExitCode = 1;
                Console.WriteLine("Harness timeout: " + root);
                return;
            }
            if (!automated) process!.WaitForExit();
            Console.WriteLine("Harness data: " + root);
            Environment.ExitCode = process!.ExitCode;
            return;
        }
        var store = new ConfigStore();
        var account = new AccountSettings { M3uUrl = "http://127.0.0.1:1/synthetic.m3u" };
        var state = store.Load();
        var saved = state.EnsureSelectedAccount();
        saved.Name = "Synthetic account";
        saved.Settings = account.Clone();
        saved.LastPlaylistUpdatedUtc = DateTime.UtcNow;
        state.SelectedAccountId = saved.Id;
        state.Account = account.Clone();
        state.CheckForUpdatesOnStartup = false;
        store.Save(state);
        store.SaveChannelCache(saved.Id, new[]
        {
            new Channel { Name = "Zebra News", Group = "News", Url = "http://127.0.0.1:1/news.ts", MediaKind = MediaKind.Live },
            new Channel { Name = "Alpha News", Group = "News", Url = "http://127.0.0.1:1/alpha.ts", MediaKind = MediaKind.Live },
            new Channel { Name = "Synthetic Sports", Group = "Sports", Url = "http://127.0.0.1:1/sports.ts", MediaKind = MediaKind.Live },
            new Channel { Name = "Synthetic Kids", Group = "Kids", Url = "http://127.0.0.1:1/kids.ts", MediaKind = MediaKind.Live }
        });

        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var startup = typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(object), typeof(StartupEventArgs) }, null)!;
        app.Startup -= (StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler), app);
        MainWindow window;
        try
        {
            window = new MainWindow(new LoginResult { Account = account, AccountId = saved.Id, UpdatePlaylist = false });
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(Path.Combine(root, "HARNESS_ERROR.txt"), ex.ToString());
            Console.Error.WriteLine(ex);
            return;
        }
        app.MainWindow = window;
        window.Closed += (_, _) => app.Shutdown();
        window.ContentRendered += (_, _) =>
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                if (window.FindName("ChannelList") is not ListBox list || list.Items.Count < 3 ||
                    window.FindName("AccountLoadingOverlay") is not System.Windows.Controls.Grid overlay ||
                    overlay.Visibility != Visibility.Collapsed) return;
                timer.Stop();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    var image = new RenderTargetBitmap((int)Math.Max(1, window.ActualWidth), (int)Math.Max(1, window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    image.Render(window);
                    using var output = File.Create(Path.Combine(root, "main-window.png"));
                    new PngBitmapEncoder { Frames = { BitmapFrame.Create(image) } }.Save(output);
                    var labels = string.Join(" | ", list.Items.Cast<object>().Take(3).Select(item => item?.ToString() ?? "<null>"));
                    File.WriteAllText(Path.Combine(root, "HARNESS_READY.txt"), "Production MainWindow rendered.\nOverlay=" + overlay.Visibility + "\nItems=" + list.Items.Count + "\nLabels=" + labels + "\nRoot=" + root + "\n");
                    RunGroupMenuProbe(window, list, root);
                    if (automated) { RunExtendedProbe(window, list, root); window.Close(); }
                }));
            };
            timer.Start();
        };
        window.Show();
        app.Run();
        Console.WriteLine("Harness data: " + root);
    }

    private static void RunGroupMenuProbe(MainWindow window, ListBox list, string root)
    {
        try
        {
            list.SelectedIndex = 0;
            list.UpdateLayout();
            list.ScrollIntoView(list.SelectedItem);
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromIndex(0) is not ListBoxItem item)
                throw new InvalidOperationException("No realized ListBoxItem for first channel.");
            var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { item, false }, null)!;
            args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            item.RaiseEvent(args);
            var menu = list.ContextMenu ?? throw new InvalidOperationException("Channel context menu was not created.");
            var before = string.Join(",", list.Items.Cast<object>().Select(GetFolder));
            var headers = string.Join(" | ", menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()));
            var move = menu.Items.OfType<MenuItem>().FirstOrDefault(m => string.Equals(m.Header?.ToString(), "Move group down", StringComparison.Ordinal));
            if (move is null) throw new InvalidOperationException("Move group down missing. Menu=" + headers);
            move.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, move));
            var wait = new System.Windows.Threading.DispatcherFrame();
            var stop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            stop.Tick += (_, _) => { stop.Stop(); wait.Continue = false; };
            stop.Start();
            System.Windows.Threading.Dispatcher.PushFrame(wait);
            list.UpdateLayout();
            var after = string.Join(",", list.Items.Cast<object>().Select(GetFolder));
            if (before == after) throw new InvalidOperationException("Move group down did not change group order.");
            File.AppendAllText(Path.Combine(root, "HARNESS_READY.txt"), "GroupMenu=PASS; Menu=" + headers + "; Before=" + before + "; After=" + after + "\n");
            menu.IsOpen = false;
            list.SelectedItem = list.Items.Cast<object>().FirstOrDefault(x => GetFolder(x) == "News");
            typeof(MainWindow).GetMethod("ActivateSelectedListEntry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            WaitUi(window);
            list.UpdateLayout();
            var itemBefore = string.Join(",", list.Items.Cast<object>().Select(GetChannelName));
            if (list.ItemContainerGenerator.ContainerFromIndex(0) is not ListBoxItem first) throw new InvalidOperationException("No News item container.");
            var itemArgs = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { first, false }, null)!;
            itemArgs.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            first.RaiseEvent(itemArgs);
            var itemMenu = list.ContextMenu!;
            var moveItem = itemMenu.Items.OfType<MenuItem>().First(m => string.Equals(m.Header?.ToString(), "Move item down", StringComparison.Ordinal));
            moveItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, moveItem));
            WaitUi(window);
            var itemAfter = string.Join(",", list.Items.Cast<object>().Select(GetChannelName));
            if (itemBefore != "Zebra News,Alpha News" || itemAfter != "Alpha News,Zebra News")
                Environment.ExitCode = 1;
            File.AppendAllText(Path.Combine(root, "HARNESS_READY.txt"), "ItemOrder=" + (itemBefore == "Zebra News,Alpha News" && itemAfter == "Alpha News,Zebra News" ? "PASS" : "FAIL") + "; Before=" + itemBefore + "; After=" + itemAfter + "\n");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.AppendAllText(Path.Combine(root, "HARNESS_READY.txt"), "GroupMenu=FAIL; " + ex.Message + "\n");
        }
    }

    private static string GetFolder(object item) => item.GetType().GetProperty("FolderName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item)?.ToString() ?? "?";
    private static string GetChannelName(object item) => (item.GetType().GetProperty("Channel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item) as Channel)?.Name ?? "?";
    private static void WaitUi(MainWindow window) { var f = new System.Windows.Threading.DispatcherFrame(); var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) }; t.Tick += (_, _) => { t.Stop(); f.Continue = false; }; t.Start(); System.Windows.Threading.Dispatcher.PushFrame(f); }
}
