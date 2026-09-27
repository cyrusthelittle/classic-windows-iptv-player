using ClassicWindowsIptvPlayer.Core;

internal static class ViewingHistoryRegressionChecks
{
    public static int Run()
    {
        var checks = new (string Name, Action Check)[]
        {
            ("viewing position survives close and reopen", CloseAndReopen),
            ("viewing positions stay account scoped", AccountIsolation),
            ("completion and Start Over govern Continue", CompletionAndStartOver),
            ("episodes sort by season and skip watched entries", EpisodeProgression),
            ("recent episodes resolve after reopen and storage recovery", RecentRecovery)
        };
        var failures = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        return failures;
    }

    private static void CloseAndReopen()
    {
        WithStore((store, state) =>
        {
            var movie = Movie();
            ViewingHistory.RecordPlay(state.SelectedLibrary, movie, At(1));
            Check(ViewingHistory.RecordPosition(state.SelectedLibrary, movie, 612_000, 3_600_000, At(2)), "position not accepted");
            store.Save(state);
            var reopened = store.Load();
            var progress = reopened.SelectedLibrary.ViewingProgress[ItemIdentity.For(movie)];
            Check(progress.PositionMs == 612_000 && progress.DurationMs == 3_600_000 && !progress.Watched, "saved resume point changed");
            Check(ViewingHistory.Continue(reopened.SelectedLibrary).Single().ItemKey == ItemIdentity.For(movie), "Continue missing movie");
            ViewingHistory.Finish(reopened.SelectedLibrary, movie, At(3));
            store.Save(reopened);
            var finished = store.Load();
            Check(finished.SelectedLibrary.ViewingProgress[ItemIdentity.For(movie)].Watched &&
                  ViewingHistory.Continue(finished.SelectedLibrary).Count == 0, "watched state did not survive reopen");
        });
    }

    private static void AccountIsolation()
    {
        var state = new AppState { SelectedAccountId = "account-a" };
        var movie = Movie();
        ViewingHistory.RecordPlay(state.SelectedLibrary, movie, At(1));
        ViewingHistory.RecordPosition(state.SelectedLibrary, movie, 420_000, 3_600_000, At(2));
        state.SelectedAccountId = "account-b";
        Check(ViewingHistory.Continue(state.SelectedLibrary).Count == 0, "other account inherited progress");
        ViewingHistory.RecordPlay(state.SelectedLibrary, movie, At(3));
        ViewingHistory.RecordPosition(state.SelectedLibrary, movie, 90_000, 3_600_000, At(4));
        Check(state.SelectedLibrary.ViewingProgress[ItemIdentity.For(movie)].PositionMs == 90_000, "second account position wrong");
        state.SelectedAccountId = "account-a";
        Check(state.SelectedLibrary.ViewingProgress[ItemIdentity.For(movie)].PositionMs == 420_000, "first account position changed");
        var editorSnapshot = new AccountLibraryState();
        ViewingHistory.RecordPlay(editorSnapshot, movie, At(1));
        ViewingHistory.RecordPosition(editorSnapshot, movie, 120_000, 3_600_000, At(1));
        ViewingHistory.Merge(editorSnapshot, state.SelectedLibrary);
        Check(editorSnapshot.ViewingProgress[ItemIdentity.For(movie)].PositionMs == 420_000 &&
              ViewingHistory.Recent(editorSnapshot).Count == 1, "account editor merge lost newer playback progress");
    }

    private static void CompletionAndStartOver()
    {
        var library = new AccountLibraryState();
        var movie = Movie();
        ViewingHistory.RecordPlay(library, movie, At(1));
        ViewingHistory.RecordPosition(library, movie, 500_000, 1_000_000, At(2));
        Check(ViewingHistory.Continue(library).Count == 1, "unfinished movie absent");
        ViewingHistory.Dismiss(library, movie);
        Check(ViewingHistory.Continue(library).Count == 0, "dismissed movie remained");
        ViewingHistory.RecordPlay(library, movie, At(3));
        Check(ViewingHistory.Continue(library).Count == 1, "replayed movie remained dismissed");
        ViewingHistory.RecordPosition(library, movie, 960_000, 1_000_000, At(4));
        Check(library.ViewingProgress[ItemIdentity.For(movie)].Watched && ViewingHistory.Continue(library).Count == 0, "near-end completion failed");
        ViewingHistory.StartOver(library, movie, At(5));
        Check(!library.ViewingProgress[ItemIdentity.For(movie)].Watched && library.ViewingProgress[ItemIdentity.For(movie)].PositionMs == 0, "Start Over did not clear progress");
        ViewingHistory.RecordPosition(library, movie, 100_000, 1_000_000, At(6));
        ViewingHistory.Finish(library, movie, At(7));
        Check(ViewingHistory.Continue(library).Count == 0, "finished movie remained in Continue");
    }

    private static void EpisodeProgression()
    {
        var library = new AccountLibraryState();
        var e1 = Episode("one", 1, 1);
        var e2 = Episode("two", 1, 2);
        var e3 = Episode("three", 2, 1);
        var list = new[] { e3, e2, e1 };
        Check(ViewingHistory.NextEpisode(library, list, e1) == e2, "next episode order incorrect");
        ViewingHistory.Finish(library, e2, At(2));
        Check(ViewingHistory.NextEpisode(library, list, e1) == e3, "watched episode not skipped");
        Check(ViewingHistory.NextEpisode(library, list, e3) is null, "last episode has successor");
        Check(ViewingHistory.EpisodeOrder(new Channel { Name = "Show S03E07" }) == (3, 7), "M3U episode label not parsed");
        var m3u1 = new Channel { Name = "Invented Show S01E02", Group = "Shows", MediaKind = MediaKind.Series };
        var m3u2 = new Channel { Name = "Invented Show S02E01", Group = "Shows", MediaKind = MediaKind.Series };
        var other = new Channel { Name = "Other Show S01E03", Group = "Shows", MediaKind = MediaKind.Series };
        Check(ViewingHistory.SeriesSiblings([m3u2, other, m3u1], m3u1).Count == 2,
            "M3U episode siblings mixed unrelated series");
    }

    private static void RecentRecovery()
    {
        WithStore((store, state) =>
        {
            var episode = Episode("one", 1, 1);
            ViewingHistory.RecordPlay(state.SelectedLibrary, Movie(), At(1));
            ViewingHistory.RecordPlay(state.SelectedLibrary, episode, At(3));
            ViewingHistory.RecordPlay(state.SelectedLibrary, Movie(), At(2));
            store.Save(state);
            store.Save(state); // last-good backup
            File.WriteAllText(store.StatePath, "corrupt synthetic data");
            var recovered = store.Load();
            var recent = ViewingHistory.Recent(recovered.SelectedLibrary);
            Check(recent.Count == 2 && recent[0].ItemKey == ItemIdentity.For(episode), "recent order or episode missing after recovery");
            var resolved = ViewingHistory.Resolve(recent[0], new Dictionary<string, Channel>());
            Check(resolved.SeriesId == "series-1" && resolved.EpisodeNumber == 1 && resolved.Url == episode.Url, "episode snapshot failed to resolve");
            Check(store.RecoveryNotice is not null, "backup recovery was silent");
        });
    }

    private static Channel Movie() => new() { Id = "movie-1", Name = "Synthetic movie", Url = "https://fixture.invalid/movie/user/pass/1.mp4", MediaKind = MediaKind.Movie };
    private static Channel Episode(string name, int season, int number) => new()
    {
        Id = "episode-" + name, Name = "Synthetic " + name, Group = "Season " + season,
        Url = $"https://fixture.invalid/series/user/pass/{season}-{number}.mp4", MediaKind = MediaKind.Series,
        SeriesId = "series-1", SeasonNumber = season, EpisodeNumber = number
    };
    private static DateTime At(int minute) => new(2026, 9, 21, 10, minute, 0, DateTimeKind.Utc);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void WithStore(Action<ConfigStore, AppState> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cyrus-viewing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(directory);
            var state = store.Load();
            check(store, state);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
