using System.Text.Json;
using System.Net;
using System.Text;
using ClassicWindowsIptvPlayer.Core;

internal static class BackgroundRefreshRegressionChecks
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cyrus-background-refresh-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Check(!JsonSerializer.Deserialize<SavedAccount>("{}")!.BackgroundPlaylistRefresh, "legacy preference should default off");
            var state = new AppState
            {
                BuiltInAccountsVersion = 1, SelectedAccountId = "a",
                Accounts = [Account("a"), Account("b")]
            };
            var store = new ConfigStore(directory);
            store.Save(state);
            var canceledDraft = new AccountEditingSession(store.Load());
            canceledDraft.Selected.BackgroundPlaylistRefresh = true;
            Check(!store.Load().EnsureSelectedAccount().BackgroundPlaylistRefresh, "draft changed saved preference");
            var draft = new AccountEditingSession(store.Load());
            draft.Selected.BackgroundPlaylistRefresh = true;
            draft.EditSelected("Fixture A", draft.Selected.Settings);
            draft.ApplyTo(state);
            store.Save(state);
            var reopened = new ConfigStore(directory).Load();
            Check(reopened.Accounts[0].BackgroundPlaylistRefresh && !reopened.Accounts[1].BackgroundPlaylistRefresh, "account preference persistence failed");
            Console.WriteLine("PASS background refresh: legacy default, canceled drafts and per-account protected preference restart");

            var original = new List<Channel> { Channel("old") };
            var replacement = new List<Channel> { Channel("new") };
            store.SaveChannelCache("a", original);
            var originalBytes = File.ReadAllBytes(store.GetChannelCachePath("a"));
            using (var staged = store.StageChannelCache("a", replacement, CancellationToken.None))
                Check(store.LoadChannelCache("a")[0].Id == "old", "staging replaced verified cache early");
            Check(originalBytes.SequenceEqual(File.ReadAllBytes(store.GetChannelCachePath("a"))), "discarded stage changed verified cache");
            Check(!Directory.EnumerateFiles(directory, "*.refresh.tmp").Any(), "discard left staging files");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Expect<OperationCanceledException>(() => store.StageChannelCache("a", replacement, canceled.Token));
            }
            Check(originalBytes.SequenceEqual(File.ReadAllBytes(store.GetChannelCachePath("a"))), "canceled staging changed cache");
            using (var staged = store.StageChannelCache("a", replacement, CancellationToken.None)) staged.Commit();
            Check(store.LoadChannelCache("a")[0].Id == "new", "commit did not publish replacement");
            Check(originalBytes.SequenceEqual(File.ReadAllBytes(store.GetChannelCachePath("a") + ".bak")), "commit lost previous verified backup");
            Console.WriteLine("PASS background refresh: deferred atomic cache commit, canceled/discarded stages and protected backup");

            var verified = File.ReadAllBytes(store.GetChannelCachePath("a"));
            Expect<InvalidDataException>(() => store.StageChannelCache("a", [], CancellationToken.None));
            Expect<InvalidDataException>(() => store.StageChannelCache("a", [new Channel { Id = "x", Name = "x", Url = "relative/invalid" }], CancellationToken.None));
            Expect<InvalidDataException>(() => store.StageChannelCache("a", [Channel("x"), Channel("x")], CancellationToken.None));
            Expect<InvalidDataException>(() => store.StageChannelCache("a", [new Channel { Id = "s", Name = "Invalid series", Url = "series:1" }], CancellationToken.None));
            Expect<InvalidDataException>(() => store.StageChannelCache("a", [new Channel { Name = "Missing identity", Url = "https://fixture.invalid/live" }], CancellationToken.None));
            Expect<InvalidDataException>(() => store.StageChannelCache("a", [new Channel { Id = "x", Url = "https://fixture.invalid/live" }], CancellationToken.None));
            PlaylistRefreshValidation.Validate([new Channel { Id = "s", Name = "Series", MediaKind = MediaKind.Series, SeriesId = "1", Url = "series:1" }]);
            PlaylistRefreshValidation.Validate([new Channel { Id = "s", Name = "Legacy URI-backed series", MediaKind = MediaKind.Series, Url = "series:1" }]);
            Expect<InvalidDataException>(() => PlaylistRefreshValidation.Validate([new Channel { Id = "s", Name = "Empty series URI", MediaKind = MediaKind.Series, SeriesId = "1", Url = "series:" }]));
            try
            {
                PlaylistRefreshValidation.Validate([Channel("valid"), new Channel { Id = "invalid", Name = "Invented private name", Url = "relative/invented-secret" }]);
                throw new Exception("Invalid diagnostic fixture succeeded");
            }
            catch (InvalidDataException exception)
            {
                Check(exception.Message == "Playlist item 2 is invalid: invalid stream address.", "diagnostic lost safe item index/reason");
                Check(!exception.Message.Contains("Invented", StringComparison.OrdinalIgnoreCase) && !exception.Message.Contains("secret", StringComparison.OrdinalIgnoreCase), "validation diagnostic exposed row data");
            }
            foreach (var url in new[] { "udp://@239.0.0.1:1234", "rtp://239.0.0.2:1234", "mms://fixture.invalid/live", "file:///fixture/test.ts" })
                PlaylistRefreshValidation.Validate([new Channel { Id = "supported", Name = "Supported native URI", Url = url }]);
            Check(verified.SequenceEqual(File.ReadAllBytes(store.GetChannelCachePath("a"))), "invalid replacement changed verified cache");
            Check(!Directory.EnumerateFiles(directory, "*.refresh.tmp").Any(), "failed staging leaked files");
            Console.WriteLine("PASS background refresh: invalid/empty replacements rejected; series placeholders accepted");
            ParserCacheRoundTripAsync(store).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL background refresh: " + exception); return 1; }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task ParserCacheRoundTripAsync(ConfigStore store)
    {
        const string playlist = """
            #EXTM3U
            #EXTINF:-1 tvg-id="shared.guide" group-title="News",News HD
            https://fixture.invalid/live/1.ts
            #EXTINF:-1 tvg-id="shared.guide" group-title="News",News SD
            https://fixture.invalid/live/2.ts
            #EXTINF:-1 tvg-id="shared.guide" group-title="News",News HD
            https://fixture.invalid/live/1.ts
            #EXTINF:-1 tvg-name="Fallback name",
            rtsp://fixture.invalid/live/4
            """;
        using var client = new HttpClient(new PlaylistFixture(playlist, seriesCatalog: true));
        var settings = new AccountSettings { ServerUrl = "https://fixture.invalid", Username = "invented", Password = "invented" };
        var service = new PlaylistService(client);
        var loaded = await service.LoadPlaylistResultAsync(settings, CancellationToken.None);
        Check(!loaded.IsPartial && loaded.Channels.Count == 4, "parsed streams or API-backed series missing");
        Check(loaded.Channels[0].EpgId == loaded.Channels[1].EpgId && loaded.Channels[0].Id != loaded.Channels[1].Id,
            "shared guide identity changed persisted stream identity");
        Check(loaded.Channels[2].Name == "Fallback name", "M3U fallback name changed");
        var series = loaded.Channels.Single(c => c.MediaKind == MediaKind.Series);
        Check(string.IsNullOrEmpty(series.SeriesId) && SeriesPlaceholder.TryGetSeriesId(series, out var seriesId) && seriesId == "42", "fixture must reproduce production URI-backed series identity");
        using (var staged = store.StageChannelCache("parser", loaded.Channels, CancellationToken.None)) staged.Commit();
        var reopened = new ConfigStore(Path.GetDirectoryName(store.StatePath)!).LoadChannelCache("parser");
        Check(SeriesPlaceholder.TryGetSeriesId(reopened.Single(c => c.MediaKind == MediaKind.Series), out var reopenedSeriesId) && reopenedSeriesId == "42", "cache restart lost series navigation identity");
        Check(reopened.Select(c => c.Id).SequenceEqual(loaded.Channels.Select(c => c.Id)), "parser/cache round trip rewrote identities or provider order");
        var again = await service.LoadPlaylistResultAsync(settings, CancellationToken.None);
        Check(again.Channels.Select(c => c.Id).SequenceEqual(reopened.Select(c => c.Id)), "refresh changed favorite/history identities");
        var goodBytes = File.ReadAllBytes(store.GetChannelCachePath("parser"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await service.LoadPlaylistResultAsync(settings, canceled.Token); throw new Exception("Canceled parser succeeded"); }
        catch (OperationCanceledException) { }
        Expect<OperationCanceledException>(() => store.StageChannelCache("parser", again.Channels, canceled.Token));
        foreach (var invalid in new[] { "#EXTM3U\n", "#EXTM3U\n#EXTINF:-1,Malformed\nhttp://[invalid-host/live\n" })
        {
            using var invalidClient = new HttpClient(new PlaylistFixture(invalid));
            try
            {
                var invalidResult = await new PlaylistService(invalidClient).LoadPlaylistResultAsync(new AccountSettings { M3uUrl = "https://fixture.invalid/catalog.m3u" }, CancellationToken.None);
                using var staged = store.StageChannelCache("parser", invalidResult.Channels, CancellationToken.None);
                throw new Exception("Unplayable catalog staging succeeded");
            }
            catch (InvalidOperationException) { }
            catch (InvalidDataException) { }
        }
        Check(goodBytes.SequenceEqual(File.ReadAllBytes(store.GetChannelCachePath("parser"))), "failed/canceled parser changed protected cache");
        Console.WriteLine("PASS background refresh: fixture M3U/API series parse/stage/restart retains URI-backed series, repeated guide IDs, provider order and stable identities; empty/canceled catalogs preserve cache");
    }

    private sealed class PlaylistFixture(string playlist, bool seriesCatalog = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = request.RequestUri!.Query;
            var isSeriesRequest = seriesCatalog && (query.Contains("action=get_series&", StringComparison.Ordinal) || query.EndsWith("action=get_series", StringComparison.Ordinal));
            var body = request.RequestUri.AbsolutePath.EndsWith("/player_api.php", StringComparison.Ordinal) ?
                isSeriesRequest ? "[{\"series_id\":42,\"name\":\"Fixture series\",\"category_id\":1}]" : "[]" : playlist;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/x-mpegurl") });
        }
    }

    private static SavedAccount Account(string id) => new()
    {
        Id = id, Name = "Fixture " + id, Settings = new() { M3uUrl = "https://fixture.invalid/" + id + ".m3u" }
    };
    private static Channel Channel(string id) => new() { Id = id, Name = id, Url = "http://fixture.invalid/live/" + id + ".ts" };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
