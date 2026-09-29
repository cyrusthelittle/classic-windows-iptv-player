using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
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

        await Check("provider catalogs are requested once and in parallel", async () =>
        {
            var recorder = new RequestRecorder();
            using var client = new HttpClient(new Fixture(async (request, token) =>
            {
                recorder.Record(request);
                recorder.Enter();
                try
                {
                    // Hold the request open briefly so overlapping calls are observable.
                    await Task.Delay(60, token);
                    return Reply(CatalogBody(request));
                }
                finally { recorder.Exit(); }
            }));

            var result = await new PlaylistService(client).LoadPlaylistResultAsync(Xtream(), CancellationToken.None);

            // The series catalog used to be fetched twice per load: once to classify
            // M3U rows and again to build placeholders. That is the slowest part of a
            // large refresh, so it must now happen exactly once.
            Equal(1, recorder.CountOfAction("get_series"));
            Equal(1, recorder.CountOfAction("get_live_streams"));
            Equal(1, recorder.CountOfAction("get_vod_streams"));
            // Categories are a separate, cheap call and is not part of this budget.
            Equal(1, recorder.CountOfAction("get_series_categories"));

            // The three catalogs are independent and must overlap rather than run
            // one after another.
            True(recorder.MaxConcurrent >= 3, $"expected 3 overlapping catalog requests, saw {recorder.MaxConcurrent}");

            // And the placeholder reuse must still produce real series channels.
            Equal(1, result.Channels.Count(c => c.MediaKind == MediaKind.Series));
            Equal("Drama", result.Channels.Single(c => c.MediaKind == MediaKind.Series).Group);
        });

        await Check("default playlist client negotiates compression", () =>
        {
            var handler = PlaylistService.CreateDefaultHandler() as HttpClientHandler;
            True(handler is not null, "default handler should be an HttpClientHandler");
            // Provider catalogs are large JSON documents; without this the refresh
            // spends all its time on the wire.
            Equal(DecompressionMethods.All, handler!.AutomaticDecompression);
            return Task.CompletedTask;
        });

        await Check("gzip-encoded provider catalogs are decoded end to end", async () =>
        {
            using var server = new GzipServer(CatalogBody);
            var settings = new AccountSettings { ServerUrl = server.BaseUrl, Username = "invented", Password = "invented" };

            // No injected client here on purpose: this is the only way to exercise the
            // production HttpClientHandler, so it actually proves AutomaticDecompression
            // is applied rather than merely configured.
            var result = await new PlaylistService().LoadPlaylistResultAsync(settings, CancellationToken.None);

            True(server.SawRequest, "server received no requests");
            True(server.OfferedGzip, $"client did not offer gzip: {string.Join(", ", server.AcceptEncodings)}");
            True(result.Channels.Any(c => c.Name == "Some Channel"), "gzip catalog produced no channels");
            Equal(1, result.Channels.Count(c => c.MediaKind == MediaKind.Series));
        });

        await Check("attribute extraction matches per-key lookup on tricky lines", () =>
        {
            // Guards the single-pass rewrite against the four-pass version it replaced.
            foreach (var line in new[]
            {
                "#EXTINF:-1 tvg-id=\"a\" group-title=\"A, B\" tvg-logo=\"http://x/l.png\" tvg-name=\"Alpha\",Alpha HD",
                "#EXTINF:-1 group-title=\"only-group\",No Attributes",
                "#EXTINF:-1 tvg-id=\"\" tvg-id=\"second\" group-title=\"dup\",Dup",
                "#EXTINF:-1 GROUP-TITLE=\"Upper\" TVG-ID=\"Upper2\",Upper Case",
                "#EXTINF:-1 tvg-logo=\"\",Empty Logo",

                // The hand-rolled scanner has to agree with the regex on greedy keys
                // and on where a match is allowed to restart.
                "#EXTINF:-1 ttvg-id=\"a\",Greedy Prefix Key",           // key run is "ttvg-id", not a match
                "#EXTINF:-1 tvg-idx=\"a\",Greedy Suffix Key",           // key run is "tvg-idx", not a match
                "#EXTINF:-1 pre-tvg-id=\"a\",Dashed Prefix Key",        // '-' is a key char, so no match
                "#EXTINF:-1 tvg-id=abc,Unquoted",                       // no opening quote, no match
                "#EXTINF:-1 tvg-id=\"a,Unterminated",                      // no closing quote, no match
                "#EXTINF:-1 kk==\"v\",Double Equals",                    // '=' is not a key char
                "#EXTINF:-1 -_=\"v\",Key Is Punctuation Only",
                "#EXTINF:-1 tvg-id=\"a\"b\"c\",Value Stops At Quote",
                "#EXTINF:-1 tvg-id=\"1\"abc=\"2\",Back To Back Keys",
                "#EXTINF:-1 group-title=\"a tvg-id=\"b\"\",Quoted Key Inside Value",
                "#EXTINF:-1 tvg-name=\"N\" tvg-logo=\"L\" group-title=\"G\" tvg-id=\"I\",All Four"
            })
            {
                var all = PlaylistService.TestOnly.ExtractAttributes(line);
                Equal(PlaylistService.TestOnly.ExtractAttribute(line, "group-title"), all.GroupTitle);
                Equal(PlaylistService.TestOnly.ExtractAttribute(line, "tvg-logo"), all.Logo);
                Equal(PlaylistService.TestOnly.ExtractAttribute(line, "tvg-id"), all.EpgId);
                Equal(PlaylistService.TestOnly.ExtractAttribute(line, "tvg-name"), all.TvgName);
            }

            return Task.CompletedTask;
        });

        await Check("stable channel ids are unchanged by the hex rewrite", () =>
        {
            foreach (var input in new[]
            {
                string.Empty,
                "Live|Alpha HD|http://panel.example.com/live/user/pass/12345.ts",
                "Movie|Ünïcödé — 中文 テスト|relative/1.mkv",
                "Series|Season 1|emoji \U0001F3E0 series",
                new string('x', 5000)
            })
            {
                Equal(PlaylistService.TestOnly.CreateStableIdOld(input), PlaylistService.TestOnly.CreateStableId(input));
            }

            return Task.CompletedTask;
        });

        await Check("url path analysis matches the previous per-row pipeline", () =>
        {
            // The row loop now parses the URI once and shares one path walk between
            // classification and stream-id lookup. Both must still agree with the
            // Split/Unescape/ToLower/regex version they replaced.
            var urls = new[]
            {
                "http://panel.example.com/live/user/pass/12345.ts",
                "http://panel.example.com/movie/user/pass/999.mkv",
                "http://panel.example.com/series/user/pass/4242",
                "http://panel.example.com/MOVIE/user/pass/7.mp4",
                "http://panel.example.com/Vod/user/pass/7.mp4",
                "http://panel.example.com/SHOWS/user/pass/7.mp4",
                "http://panel.example.com/lives/user/pass/7.mp4",      // 'lives' is not 'live'
                "http://panel.example.com/showtime/user/pass/7",         // not a segment match
                "http://panel.example.com/vod/user/pass/7.mp4",         // video ext after 'vod'
                "http://panel.example.com/series/user/pass/7.mp4",       // series wins over ext
                "http://panel.example.com/live/user/pass/12345",         // raw live, no extension
                "http://panel.example.com/a/b/12345",                    // exactly three segments
                "http://panel.example.com/a/12345",                      // two segments
                "http://panel.example.com/12345",                        // one segment
                "http://panel.example.com/",                             // no segments
                "http://panel.example.com",
                "http://panel.example.com/%6Dovie/1.mp4",               // escaped 'movie'
                "http://panel.example.com/movie%2Ffolder/1.mp4",
                "http://panel.example.com/live/user/pass/12345.ts?token=abc",
                "http://panel.example.com/live/user/pass/12345.TS",     // upper-case extension
                "http://panel.example.com/live/user/pass/12345.ts.m3u8",
                "http://panel.example.com/live/user/pass/abc",           // not numeric
                "http://panel.example.com/live/user/pass/12a45",         // not all digits
                "http://panel.example.com/live/user/pass/-12345",        // not all digits
                "http://panel.example.com/live/user/pass/12345.ts/",
                "not a url",
                "",
                "relative/12345.ts"
            };

            foreach (var url in urls)
            {
                foreach (var group in new[] { string.Empty, "Sports", "Movies", "Series" })
                {
                    foreach (var name in new[] { string.Empty, "Alpha HD", "Season 2 Episode 4" })
                    {
                        Equal(
                            PlaylistService.TestOnly.ClassifyMediaKindOld(url, group, name),
                            PlaylistService.TestOnly.ClassifyMediaKind(url, group, name));
                    }
                }

                Equal(
                    PlaylistService.TestOnly.TryExtractStreamIdOld(url, out var expectedId),
                    PlaylistService.TestOnly.TryExtractStreamId(url, out var actualId));
                Equal(expectedId, actualId);
            }

            return Task.CompletedTask;
        });

        await Check("keyword classification matches the previous word-set behaviour", () =>
        {
            foreach (var text in new[]
            {
                "Sports", "Movies", "VOD", "Film", "Cinema", "Series", "Season 2", "Episode 4",
                "Episodes", "TV Show", "TV Shows", "Show", "Shows", "tv", "TV", "television",
                "Movie Films Cinema", "series tv", "S01E02", "10x20", "season 3", "ep 7",
                "", "   ", "MOVIE", "v.o.d", "tvshow", "shows tv"
            })
            {
                var keywords = PlaylistService.TestOnly.ScanKeywords(text);
                Equal(PlaylistService.TestOnly.LooksLikeMovieTextOld(text), PlaylistService.TestOnly.LooksLikeMovieText(keywords));
                Equal(PlaylistService.TestOnly.LooksLikeSeriesTextOld(text), PlaylistService.TestOnly.LooksLikeSeriesText(text, keywords));
            }

            return Task.CompletedTask;
        });

        await Check("streaming catalog reader agrees with the document reader", () =>
        {
            // The buffered Utf8JsonReader replaced JsonDocument.ParseAsync, so every
            // shape the old code tolerated has to produce identical results.
            foreach (var json in new[]
            {
                "[]",
                "[{\"stream_id\":\"1\"},{\"stream_id\":\"2\"}]",
                "[{\"stream_id\":12},{\"stream_id\":34.5}]",
                "[{\"stream_id\":\"1\",\"stream_id\":\"2\"}]",
                "[{\"stream_id\":\"\"},{\"stream_id\":\"  \"}]",
                "[{\"other\":\"1\"},{\"stream_id\":\"3\"}]",
                "[{\"stream_id\":null},{\"stream_id\":\"4\"}]",
                "[{\"stream_id\":true},{\"stream_id\":\"5\"}]",
                "[{\"stream_id\":[\"x\"]},{\"stream_id\":\"6\"}]",
                "[{\"stream_id\":{\"a\":1}},{\"stream_id\":\"7\"}]",
                "[{\"info\":{\"stream_id\":\"8\"}},{\"stream_id\":\"9\"}]",
                "[{\"stream_id\":\"\\u00e9\\u00e8\"}]",
                "[{\"Stream_id\":\"10\"},{\"stream_id\":\"11\"}]",
                // A trailing comma is rejected by both readers, matching the old
                // JsonDocument.ParseAsync behaviour rather than becoming more lenient.
                "[\n {\"stream_id\" : \"13\"} \n]\n",                "{}",
                "null"
            })
            {
                Equal("match", PlaylistService.TestOnly.ReadIds(json, "stream_id", strict: false));
            }

            // A catalog large enough to overflow any initial read buffer must not be
            // mistaken for a broken one; this is what a non-expandable buffer bug looked
            // like from the outside: silently empty catalogs.
            var bulk = string.Join(",", Enumerable.Range(0, 50_000).Select(i => $"{{\"stream_id\":\"{i}\",\"name\":\"row {i}\"}}"));
            var large = "[" + bulk + "]";
            True(PlaylistService.TestOnly.ReadIds(large, "stream_id", strict: false) == "match", "large catalog ids diverged");
            Equal(50_000, PlaylistService.TestOnly.ReadCount(large, "stream_id"));

            return Task.CompletedTask;
        });

        await Check("streaming series reader agrees with the document reader", () =>
        {
            foreach (var json in new[]
            {
                "[]",
                "[{\"series_id\":\"9\",\"name\":\"A Show\",\"category_id\":\"5\",\"cover\":\"http://x/c.png\",\"plot\":\"P\",\"release_date\":\"2019-05-05\",\"genre\":\"Comedy\"}]",
                "[{\"series_id\":77,\"name\":\"Numbered\"}]",
                "[{\"name\":\"No Id\"},{\"series_id\":\"8\",\"name\":\"Kept\"}]",
                "[{\"series_id\":\"7\",\"series_id\":\"6\",\"name\":\"First Wins\"}]",
                "[{\"series_id\":\"5\",\"name\":\"\"}]",
                "[{\"series_id\":\"4\",\"name\":\"  Padded  \",\"cover\":\"  http://x/c.png  \"}]",
                "[{\"series_id\":\"3\",\"name\":\"Nested\",\"info\":{\"plot\":\"Inner\"}}]",
                "[{\"series_id\":\"2\",\"name\":\"Escaped \\u00e9\"}]",
                "[\n{\"series_id\":\"1\",\"name\":\"Spaced\"}\n]"
            })
            {
                Equal("match", PlaylistService.TestOnly.ReadSeries(json));
            }

            return Task.CompletedTask;
        });

        await Check("broken catalogs still report partial instead of throwing", () =>
        {
            // Non-strict callers get an empty result; the strict series caller gets the
            // exception it used to get, so the load can be reported as partial.
            Equal("match", PlaylistService.TestOnly.ReadIds("{\"not\":\"an array\"}", "stream_id", strict: false));
            var threw = false;
            try { PlaylistService.TestOnly.ReadSeries("{\"not\":\"an array\"}"); }
            catch (InvalidOperationException) { threw = true; }
            True(threw, "strict series read should reject a non-array body");

            threw = false;
            try { PlaylistService.TestOnly.ReadIds("not json at all", "stream_id", strict: true); }
            catch (InvalidOperationException) { threw = true; }
            True(threw, "strict id read should reject a malformed body");

            return Task.CompletedTask;
        });

        return failures;
    }

    // A provider that answers every action the loader can ask for, and gzips the
    // body so the decompression path is exercised for real.
    private static string CatalogBody(HttpRequestMessage request)
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains("get.php") && !url.Contains("action=")) return "#EXTM3U\n#EXTINF:-1 group-title=\"News\",Some Channel\nhttp://fixture/live/1.ts\n";

        if (url.Contains("action=get_live_streams")) return "[{\"stream_id\":\"1\",\"name\":\"Live\"}]";
        if (url.Contains("action=get_vod_streams")) return "[{\"stream_id\":\"2\",\"name\":\"Film\"}]";
        if (url.Contains("action=get_series_categories")) return "[{\"category_id\":\"5\",\"category_name\":\"Drama\"}]";
        if (url.Contains("action=get_series")) return "[{\"series_id\":\"9\",\"name\":\"A Show\",\"category_id\":\"5\"}]";
        return "[]";
    }

    private sealed class RequestRecorder
    {
        private readonly List<string> _urls = new();
        private readonly Lock _gate = new();
        private int _inFlight;
        private int _peak;

        public int MaxConcurrent { get { lock (_gate) return _peak; } }

        public void Enter()
        {
            lock (_gate) { _inFlight++; if (_inFlight > _peak) _peak = _inFlight; }
        }

        public void Exit() { lock (_gate) _inFlight--; }

        // Parses the action out of the query string rather than substring matching,
        // because "action=get_series" is a prefix of "action=get_series_categories".
        public int CountOfAction(string action)
        {
            lock (_gate)
            {
                return _urls.Count(url =>
                    QueryValue(url, "action") is { } found && string.Equals(found, action, StringComparison.Ordinal));
            }
        }

        private static string? QueryValue(string url, string key)
        {
            var query = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : string.Empty;
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator < 0) continue;
                if (string.Equals(pair[..separator], key, StringComparison.Ordinal)) return pair[(separator + 1)..];
            }

            return null;
        }

        public void Record(HttpRequestMessage request)
        {
            lock (_gate) _urls.Add(request.RequestUri?.ToString() ?? string.Empty);
        }
    }

    // A minimal loopback HTTP server that gzips every response. Real sockets rather
    // than a fake handler, because decompression is a property of the production
    // handler stack and cannot be simulated by a stub.
    private sealed class GzipServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<HttpRequestMessage, string> _body;
        private readonly CancellationTokenSource _stop = new();
        private readonly Lock _gate = new();
        private readonly List<string> _acceptEncodings = new();
        private int _requestCount;

        public GzipServer(Func<HttpRequestMessage, string> body)
        {
            _body = body;
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }
        public bool SawRequest { get { lock (_gate) return _requestCount > 0; } }

        public IReadOnlyList<string> AcceptEncodings { get { lock (_gate) return _acceptEncodings.ToArray(); } }

        public bool OfferedGzip
        {
            get
            {
                lock (_gate)
                {
                    return _acceptEncodings.Any(value => value.Contains("gzip", StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestLine)) return;

                while (true)
                {
                    var header = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(header)) break;
                    if (header.StartsWith("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_gate) { _acceptEncodings.Add(header); _requestCount++; }
                    }
                }

                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;
                var payload = _body(new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl + parts[1])));
                var raw = Encoding.UTF8.GetBytes(payload);

                var buffer = new MemoryStream();
                using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(raw, 0, raw.Length);
                var body = buffer.ToArray();

                var contentType = payload.StartsWith("#EXTM3U", StringComparison.Ordinal) ? "audio/x-mpegurl" : "application/json";
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    $"Content-Type: {contentType}\r\n" +
                    "Content-Encoding: gzip\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n");

                await stream.WriteAsync(head);
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
        }
    }

    private static void True(bool condition, string message) { if (!condition) throw new Exception(message); }

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
