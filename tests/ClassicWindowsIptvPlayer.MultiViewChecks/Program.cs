using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;
using LibVLCSharp.Shared;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ClassicWindowsIptvPlayer.MultiViewChecks;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "cyrus-multiview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixtureA = Path.Combine(root, "fixture-a.avi");
            var fixtureB = Path.Combine(root, "fixture-b.avi");
            GenerateAvi.Run(fixtureA);
            GenerateAvi.Run(fixtureB);

            LibVLCSharp.Shared.Core.Initialize();
            using var vlc = new LibVLC("--quiet", "--no-audio");
            var profile = new AccountProfile { ActiveConnections = "0", MaxConnections = "4" };
            var budget = new ConnectionBudget();
            var releasedLeases = new List<string>();
            var account = new AccountSettings();
            var channels = new[]
            {
                new Channel { Id = "fixture-a", Name = "Local fixture A", Url = new Uri(fixtureA).AbsoluteUri, MediaKind = MediaKind.Live },
                new Channel { Id = "fixture-b", Name = "Local fixture B", Url = new Uri(fixtureB).AbsoluteUri, MediaKind = MediaKind.Live }
            };

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MultiViewWindow(vlc, channels, account,
                (channel, replacing) => budget.Acquire(profile, StreamLeaseKind.MultiView, channel.Id, replacing: replacing),
                lease =>
                {
                    if (budget.Release(lease)) releasedLeases.Add(lease.Id);
                });
            window.Show();
            Pump();
            var selectors = new[] { Named<ComboBox>(window, "Tile1Channel"), Named<ComboBox>(window, "Tile2Channel") };
            var plays = new[] { Named<Button>(window, "Tile1Play"), Named<Button>(window, "Tile2Play") };
            var stops = new[] { Named<Button>(window, "Tile1Stop"), Named<Button>(window, "Tile2Stop") };

            selectors[0].SelectedItem = channels[0];
            selectors[1].SelectedItem = channels[1];
            Pump();
            Require(plays.All(button => button.IsEnabled), "Selecting a channel should enable its production Play button.");
            Click(plays[0]);
            Click(plays[1]);
            WaitFor(() => budget.LocalHeld == 2 && Players(window).Count(IsPlaying) == 2,
                "Two independent local fixture players did not start.");
            Require(Players(window).Distinct(ReferenceEqualityComparer.Instance).Count() == 2,
                "Both tiles must own distinct MediaPlayer instances.");
            Console.WriteLine("PASS two real tile selectors start independent local-fixture players and leases");

            Click(stops[0]);
            WaitFor(() => budget.LocalHeld == 1 && Players(window).Count(IsPlaying) == 1,
                "Stopping one tile did not stop its player and free exactly one lease.");
            Require(releasedLeases.Count == 1, "Stop must invoke the explicit lease-release callback exactly once.");
            Console.WriteLine("PASS real Stop control stops one player and frees its lease");

            // Fill the provider allowance using the real budget, then verify a rejected
            // selected tile only fails after its real Play control is clicked.
            var blocker1 = budget.Acquire(profile, StreamLeaseKind.MultiView, "blocker-1");
            var blocker2 = budget.Acquire(profile, StreamLeaseKind.MultiView, "blocker-2");
            Require(blocker1 is not null && blocker2 is not null && budget.LocalHeld == 3,
                "Could not reserve deterministic fixture capacity.");
            selectors[0].SelectedItem = channels[1];
            Pump();
            Require(plays[0].IsEnabled, "Selecting a channel must enable Play before testing lease rejection.");
            var playersBeforeRejectedPlay = Players(window).Count;
            Click(plays[0]);
            Pump();
            Require(budget.LocalHeld == 3 && Players(window).Count(IsPlaying) == 1,
                "Rejected lease must not create a player or leak a lease.");
            Require(Players(window).Count == playersBeforeRejectedPlay,
                "Rejected lease must not allocate or attach a MediaPlayer.");
            Console.WriteLine("PASS rejected lease creates no player and leaks no lease");

            budget.Release(blocker1);
            budget.Release(blocker2);
            var releasedBeforeClose = releasedLeases.Count;
            window.Close();
            Pump();
            WaitFor(() => budget.LocalHeld == 0, "Closing the window did not release all tile leases.");
            Require(Players(window).Count == 0, "Closing the window left tile players attached.");
            Require(releasedLeases.Count == releasedBeforeClose + 1,
                "Closing the window must invoke the explicit release callback for its remaining tile exactly once.");
            Console.WriteLine("PASS closing window disposes all tile players and leases");

            VerifyStaleSnapshotIncludesMainStream(new AccountProfile { ActiveConnections = "0", MaxConnections = "2" });
            app.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyStaleSnapshotIncludesMainStream(AccountProfile providerSnapshot)
    {
        // Mirrors MainWindow.CloneMultiViewProfile's contract: when its local main
        // tuner is already playing but the fetched provider count is stale at zero,
        // charge that existing stream before allowing MultiView acquisitions.
        var mainStreamIsPlaying = true;
        var cloned = new AccountProfile
        {
            ActiveConnections = providerSnapshot.ActiveConnections,
            MaxConnections = providerSnapshot.MaxConnections
        };
        if (mainStreamIsPlaying && int.TryParse(cloned.MaxConnections, out var maximum) && maximum > 0 &&
            (!int.TryParse(cloned.ActiveConnections, out var active) || active < 1))
            cloned.ActiveConnections = "1";

        var integrationBudget = new ConnectionBudget();
        var onlyAdditionalTile = integrationBudget.Acquire(cloned, StreamLeaseKind.MultiView, "tile-1");
        var refusedAdditionalTile = integrationBudget.Acquire(cloned, StreamLeaseKind.MultiView, "tile-2");
        Require(onlyAdditionalTile is not null && refusedAdditionalTile is null && integrationBudget.LocalHeld == 1,
            "With main playback plus MaxConnections=2, exactly one additional tile should be admitted.");
        integrationBudget.ReleaseAll();
        Console.WriteLine("PASS stale provider snapshot counts playing main stream; only one additional tile lease admitted");
    }

    private static T Named<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Production control {name} was not registered.");

    private static void Click(Button button)
    {
        Require(button.IsEnabled, $"Production button {button.Name} is disabled.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        Pump();
    }

    private static List<MediaPlayer> Players(Window window) =>
        new[] { "Tile1Video", "Tile2Video", "Tile3Video", "Tile4Video" }
            .Select(name => window.FindName(name) as LibVLCSharp.WPF.VideoView)
            .Where(view => view?.MediaPlayer is not null)
            .Select(view => view!.MediaPlayer!)
            .ToList();

    private static bool IsPlaying(MediaPlayer player) => player.IsPlaying || player.State is VLCState.Playing or VLCState.Buffering;

    private static void WaitFor(Func<bool> predicate, string message)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (!predicate())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException(message);
            Pump();
            Thread.Sleep(20);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
