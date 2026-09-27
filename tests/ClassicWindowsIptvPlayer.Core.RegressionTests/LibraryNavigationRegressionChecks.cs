using ClassicWindowsIptvPlayer.Windows;
using ClassicWindowsIptvPlayer.Core;

internal static class LibraryNavigationRegressionChecks
{
    public static int Run()
    {
        var history = new LibraryNavigationHistory();
        var live = new LibraryPosition("News", null, "B", 1, 0, "live-b", 37.5);
        var movies = new LibraryPosition("Drama", null, null, 0, 1, "movie-a", 12);
        var firstMovieVisit = history.Switch(1, 2, live);
        var restoredLive = history.Switch(2, 1, movies);
        var restoredMovies = history.Switch(1, 2, live);
        var results = new[] { (Key: "movie-c", Name: "C"), (Key: "movie-a", Name: "A") };
        var films = new[] { new Channel { Name = "Older", Year = 1990 }, new Channel { Name = "Unknown" }, new Channel { Name = "Newer", Year = 2024 } };
        var checks = new (string Name, bool Pass)[]
        {
            ("missing synthetic catalog offers provider retry", LibraryNavigationPolicy.EmptyMessage(0, "", 0).Contains("Retry the provider")),
            ("scoped search gives clear/reset recovery", LibraryNavigationPolicy.EmptyMessage(3, "invented query", 0).Contains("Clear search or reset")),
            ("empty favorites explains how to add one", LibraryNavigationPolicy.EmptyMessage(3, "", 1).Contains("add it to favorites")),
            ("empty recent explains how to populate it", LibraryNavigationPolicy.EmptyMessage(3, "", 2).Contains("Play an item")),
            ("reset stays disabled only for default scope", !LibraryNavigationPolicy.CanReset(0, 0, false, "") && LibraryNavigationPolicy.CanReset(1, 0, false, "") && LibraryNavigationPolicy.CanReset(0, 2, false, "") && LibraryNavigationPolicy.CanReset(0, 0, true, "") && LibraryNavigationPolicy.CanReset(0, 0, false, "query")),
            ("first media switch begins at category root", firstMovieVisit is null),
            ("returning to live restores folder, letter, selected key and scroll", restoredLive == live),
            ("returning to movies restores its separate filters and position", restoredMovies == movies),
            ("selected identity resolves after result reorder and missing identity stays unselected", LibraryNavigationHistory.SelectedIndex(results, "movie-a", x => x.Key) == 1 && LibraryNavigationHistory.SelectedIndex(results, "missing", x => x.Key) == -1),
            ("same category click does not overwrite its saved position", history.Switch(2, 2, live) is null && history.Switch(2, 1, movies) == live)
            ,("year sort puts known newest films first and unknown last", VodDiscovery.Sort(films, VodSort.Year).Select(c => c.Name).SequenceEqual(new[] { "Newer", "Older", "Unknown" }))
            ,("details and playback refresh retain same catalog scroll", CatalogPositionRetention.ShouldRestoreScroll("Movies|Drama", "Movies|Drama", false) && !CatalogPositionRetention.ShouldRestoreScroll("Movies|Drama", "Series|Drama", false) && !CatalogPositionRetention.ShouldRestoreScroll("Movies|Drama", "Movies|Drama", true))
        };
        foreach (var (name, pass) in checks) Console.WriteLine($"{(pass ? "PASS" : "FAIL")} library navigation: {name}");
        Console.WriteLine($"Library navigation checks: {checks.Count(c => c.Pass)}/{checks.Length} passed");
        return checks.All(c => c.Pass) ? 0 : 1;
    }
}
