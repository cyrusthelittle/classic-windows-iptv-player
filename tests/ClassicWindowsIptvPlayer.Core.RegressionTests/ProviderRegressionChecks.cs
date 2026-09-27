using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClassicWindowsIptvPlayer.Core;

internal static class ProviderRegressionChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS provider: " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL provider: " + name + " - " + ex.Message); }
        }

        await Check("comma-bearing M3U name and quoted comma attribute", async () =>
        {
            using var client = new HttpClient(new Fixture(_ => Reply("#EXTM3U\n#EXTINF:-1 group-title=\"News, Local\",News, Weather\nhttp://fixture/live/1.ts\n")));
            var result = await new PlaylistService(client).LoadPlaylistResultAsync(M3u(), CancellationToken.None);
            Equal("News, Weather", result.Channels.Single().Name);
            Equal("News, Local", result.Channels.Single().Group);
            Equal(false, result.IsPartial);
        });

        await Check("HTTP failure and empty playlist preserve failure signal", async () =>
        {
            foreach (var response in new[] { new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), Reply("#EXTM3U\n") })
            {
                using var client = new HttpClient(new Fixture(_ => response));
                try { await new PlaylistService(client).LoadPlaylistResultAsync(M3u(), CancellationToken.None); throw new Exception("Unexpected success"); }
                catch (InvalidOperationException) { }
            }
        });

        await Check("partial Xtream fallback reports incomplete series", async () =>
        {
            using var client = new HttpClient(new Fixture(request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains("get.php")) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (url.Contains("action=get_live_streams")) return Reply("[{\"stream_id\":\"1\",\"name\":\"Live\"}]");
                if (url.Contains("action=get_vod_streams")) return Reply("[]");
                if (url.Contains("action=get_series") && !url.Contains("categories")) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return Reply("[]");
            }));
            var result = await new PlaylistService(client).LoadPlaylistResultAsync(Xtream(), CancellationToken.None);
            Equal(true, result.IsPartial);
            Equal(1, result.Channels.Count);
        });

        await Check("optional series failure retains M3U episodes", async () =>
        {
            using var client = new HttpClient(new Fixture(request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains("get.php")) return Reply("#EXTM3U\n#EXTINF:-1 group-title=\"Series\",Show S01E01\nhttp://fixture/series/invented/invented/4.mp4\n");
                if (url.Contains("action=get_series") && !url.Contains("categories")) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return Reply("[]");
            }));
            var result = await new PlaylistService(client).LoadPlaylistResultAsync(Xtream(), CancellationToken.None);
            Equal(true, result.IsPartial);
            Equal("Show S01E01", result.Channels.Single().Name);
        });

        await Check("slow provider honors cancellation", async () =>
        {
            using var client = new HttpClient(new Fixture(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Reply("#EXTM3U\n");
            }));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try { await new PlaylistService(client).LoadPlaylistResultAsync(M3u(), cts.Token); throw new Exception("Unexpected success"); }
            catch (OperationCanceledException) { }
        });

        await Check("Xtream movie and series list metadata survives catalog loading", async () =>
        {
            using var client = new HttpClient(new Fixture(request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains("get.php")) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (url.Contains("action=get_vod_streams")) return Reply("[{\"stream_id\":\"7\",\"name\":\"Invented film\",\"stream_icon\":\"http://fixture/poster.jpg\",\"plot\":\"Invented plot\",\"genre\":\"Drama\",\"releaseDate\":\"2024-03-01\",\"duration\":\"01:42:00\",\"added\":\"1700000000\"}]");
                if (url.Contains("action=get_series") && !url.Contains("categories")) return Reply("[{\"series_id\":\"8\",\"name\":\"Invented series\",\"cover\":\"http://fixture/series.jpg\",\"plot\":\"Series plot\",\"genre\":\"Mystery\",\"releaseDate\":\"2023-01-01\"}]");
                return Reply("[]");
            }));
            var result = await new PlaylistService(client).LoadPlaylistResultAsync(Xtream(), CancellationToken.None);
            var movie = result.Channels.Single(c => c.MediaKind == MediaKind.Movie);
            var series = result.Channels.Single(c => c.MediaKind == MediaKind.Series);
            Equal("Invented plot", movie.Description);
            Equal("Drama", movie.Genre);
            Equal(2024, movie.Year);
            Equal(102, movie.DurationMinutes);
            Equal("http://fixture/poster.jpg", movie.Logo);
            Equal("Series plot", series.Description);
            Equal("Mystery", series.Genre);
            Equal(2023, series.Year);
        });

        await Check("missing or malformed metadata does not remove playable movies", async () =>
        {
            using var client = new HttpClient(new Fixture(request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains("get.php")) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (url.Contains("action=get_vod_streams")) return Reply("[{\"stream_id\":\"7\",\"name\":\"Playable\",\"releaseDate\":\"nonsense\",\"duration\":\"oops\"}]");
                return Reply("[]");
            }));
            var movie = (await new PlaylistService(client).LoadPlaylistResultAsync(Xtream(), CancellationToken.None)).Channels.Single();
            Equal((int?)null, movie.Year);
            Equal((int?)null, movie.DurationMinutes);
            Equal(true, movie.Url.Contains("/movie/"));
            Equal(true, VodDiscovery.Details(movie).Contains("unavailable"));
        });

        await Check("nested episode metadata and duration seconds normalize safely", () =>
        {
            using var document = JsonDocument.Parse("{\"info\":{\"plot\":\"Episode plot\",\"genre\":\"Comedy\",\"duration_secs\":\"3661\",\"release_date\":\"2022-08-09\"}}");
            var channel = new Channel { Name = "Episode", MediaKind = MediaKind.Series, Url = "http://fixture/series/invented/invented/9.mp4" };
            VodDiscovery.ReadProviderMetadata(channel, document.RootElement, document.RootElement.GetProperty("info"));
            Equal("Episode plot", channel.Description);
            Equal("Comedy", channel.Genre);
            Equal(62, channel.DurationMinutes);
            Equal(2022, channel.Year);
            return Task.CompletedTask;
        });

        return failures;
    }

    private static AccountSettings M3u() => new() { M3uUrl = "http://fixture/list.m3u" };
    private static AccountSettings Xtream() => new() { ServerUrl = "http://fixture", Username = "invented", Password = "invented" };
    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = (request, _) => Task.FromResult(respond(request));
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _respond(request, cancellationToken);
    }
}
