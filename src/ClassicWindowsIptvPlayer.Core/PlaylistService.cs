using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassicWindowsIptvPlayer.Core;

public sealed class PlaylistService
{
    public sealed record LoadResult(IReadOnlyList<Channel> Channels, bool IsPartial, string Message);
    private static readonly Regex AttributeRegex = new("(?<key>[A-Za-z0-9_-]+)=\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.Compiled);
    private static readonly Regex WordSplitRegex = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeriesEpisodeRegex = new(
        "(^|[^a-z0-9])(?:s\\d{1,2}\\s*e\\d{1,3}|\\d{1,2}x\\d{1,3}|season\\s*\\d{1,2}|episode\\s*\\d{1,3}|ep\\s*\\d{1,3})([^a-z0-9]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex NumericStreamIdRegex = new("^[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;

    public PlaylistService(HttpClient? httpClient = null)
    {
        // Provider catalogs are JSON and compress by roughly an order of magnitude.
        // Without this the client never offers an encoding in Accept-Encoding, so a
        // large panel pushes every byte uncompressed and the refresh is dominated
        // by transfer time rather than by parsing.
        _httpClient = httpClient ?? new HttpClient(CreateDefaultHandler())
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Classic-Windows-IPTV-Player/0.9.0");
    }

    internal static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    };

    public async Task<IReadOnlyList<Channel>> LoadPlaylistAsync(AccountSettings account, CancellationToken cancellationToken) =>
        (await LoadPlaylistResultAsync(account, cancellationToken)).Channels;

    public async Task<LoadResult> LoadPlaylistResultAsync(AccountSettings account, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        cancellationToken = deadline.Token;
        progress?.Report("Downloading playlist...");
        var playlistUrl = BuildPlaylistUrl(account);
        var xtreamAccount = TryResolveXtreamAccount(account);
        AppLogger.Info("Loading playlist from " + AppLogger.SanitizeUrl(playlistUrl));

        Exception m3uFailure;
        try
        {
            using var response = await _httpClient.GetAsync(playlistUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            AppLogger.Info("Playlist HTTP response. status=" + (int)response.StatusCode + " " + response.ReasonPhrase);
            response.EnsureSuccessStatusCode();

            progress?.Report("Checking provider media types...");
            var total = Stopwatch.StartNew();
            var catalogTimer = Stopwatch.StartNew();
            var bootstrap = xtreamAccount is null
                ? new XtreamBootstrap()
                : await FetchXtreamBootstrapAsync(xtreamAccount, cancellationToken);
            catalogTimer.Stop();
            AppLogger.Info($"Xtream media kind map loaded. count={bootstrap.Kinds.Count} elapsedMs={catalogTimer.ElapsedMilliseconds}");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            progress?.Report("Reading playlist items...");
            var parseTimer = Stopwatch.StartNew();
            var channels = await ParseM3uFromStreamAsync(stream, bootstrap.Kinds, cancellationToken, bootstrap.Archives);
            parseTimer.Stop();
            AppLogger.Info($"Playlist parsed. channels={channels.Count} elapsedMs={parseTimer.ElapsedMilliseconds}");
            if (channels.Count > 0)
            {
                if (xtreamAccount is null) return new LoadResult(channels, false, "Playlist loaded.");
                progress?.Report("Loading series catalog...");
                var seriesTimer = Stopwatch.StartNew();
                var (withSeries, partial) = await ReplaceSeriesWithApiPlaceholdersAsync(channels, xtreamAccount, bootstrap.Series, cancellationToken);
                total.Stop();
                AppLogger.Info($"Playlist load finished. totalMs={total.ElapsedMilliseconds}");
                return new LoadResult(withSeries, partial, partial ? "Series catalog unavailable; saved library retained when available." : "Playlist loaded.");
            }

            m3uFailure = new InvalidOperationException("Playlist was downloaded but no playable channels were found.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            m3uFailure = ex;
        }

        // Some large/reseller Xtream panels block or break the get.php M3U export
        // (e.g. returning a non-standard status code with an empty body) while the
        // player_api.php JSON actions keep working. Fall back to building the channel
        // list straight from the API instead of failing outright.
        cancellationToken.ThrowIfCancellationRequested();
        AppLogger.Warn("M3U playlist unavailable, falling back to the Xtream API directly. " + m3uFailure.Message);

        if (xtreamAccount is not null)
        {
            progress?.Report("Trying provider API...");
            var apiResult = await BuildChannelsFromXtreamApiAsync(xtreamAccount, cancellationToken);
            if (apiResult.Channels.Count > 0)
            {
                AppLogger.Info("Xtream API fallback returned. channels=" + apiResult.Channels.Count);
                return apiResult;
            }
        }

        throw new InvalidOperationException(
            "Playlist was downloaded but no playable channels were found. The server may have returned a non-M3U response, expired account message, or an empty playlist.",
            m3uFailure);
    }

    // The M3U/get.php export has no concept of "a series" -- providers flatten every
    // episode into its own row (e.g. "Show Name S01E01"), so browsing by series only
    // works if those rows are swapped for the same API-backed placeholders the
    // Xtream-API fallback uses (see AddXtreamSeriesPlaceholdersAsync). This runs
    // whenever the account has Xtream credentials, regardless of whether get.php
    // itself succeeded, so series browsing behaves the same on every account.
    private async Task<(List<Channel> Channels, bool Partial)> ReplaceSeriesWithApiPlaceholdersAsync(
        List<Channel> channels,
        AccountSettings account,
        SeriesFetchResult? prebuilt,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(account.M3uUrl) ||
            string.IsNullOrWhiteSpace(account.Username) ||
            string.IsNullOrWhiteSpace(account.Password))
        {
            return (channels, false);
        }

        try
        {
            var apiUrl = BuildPlayerApiUrl(account);
            var seriesCategories = await FetchXtreamCategoriesAsync(apiUrl, "get_series_categories", cancellationToken);

            // The media-kind pass has usually already downloaded get_series for this
            // same load, so reuse that response instead of asking the provider for an
            // identical catalog a second time. Episodes are still resolved one series
            // at a time by FetchSeriesEpisodesAsync.
            var series = prebuilt ?? await FetchXtreamSeriesAsync(apiUrl, cancellationToken);

            var withoutSeries = channels.Where(c => c.MediaKind != MediaKind.Series).ToList();
            var seenIds = new HashSet<string>(withoutSeries.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
            var before = withoutSeries.Count;

            AddSeriesPlaceholders(series, seriesCategories, withoutSeries, seenIds);
            if (channels.Any(channel => channel.MediaKind == MediaKind.Series) &&
                !withoutSeries.Any(channel => channel.MediaKind == MediaKind.Series))
            {
                AppLogger.Warn("Series endpoint returned no placeholders; retaining M3U series rows.");
                return (channels, true);
            }
            AppLogger.Info("Replaced M3U series rows with API-backed series placeholders. placeholders=" + (withoutSeries.Count - before));
            return (withoutSeries, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Warn("Could not replace M3U series rows with API placeholders; keeping them as individual episodes. " + ex.Message);
            return (channels, true);
        }
    }

    private async Task<LoadResult> BuildChannelsFromXtreamApiAsync(AccountSettings account, CancellationToken cancellationToken)
    {
        var apiUrl = BuildPlayerApiUrl(account);
        var streamBaseUrl = BuildStreamBaseUrl(account);

        // Three independent category lookups, previously three serial round trips.
        var categories = await Task.WhenAll(
            FetchXtreamCategoriesAsync(apiUrl, "get_live_categories", cancellationToken),
            FetchXtreamCategoriesAsync(apiUrl, "get_vod_categories", cancellationToken),
            FetchXtreamCategoriesAsync(apiUrl, "get_series_categories", cancellationToken)).ConfigureAwait(false);
        var liveCategories = categories[0];
        var vodCategories = categories[1];
        var seriesCategories = categories[2];

        var channels = new List<Channel>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var partial = false;
        try { await AddXtreamChannelsAsync(apiUrl, streamBaseUrl, account, "get_live_streams", "live", MediaKind.Live, liveCategories, channels, seenIds, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { partial = true; AppLogger.Warn("Live catalog unavailable. " + ex.Message); }
        try { await AddXtreamChannelsAsync(apiUrl, streamBaseUrl, account, "get_vod_streams", "movie", MediaKind.Movie, vodCategories, channels, seenIds, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { partial = true; AppLogger.Warn("Movie catalog unavailable. " + ex.Message); }
        try
        {
            var series = await FetchXtreamSeriesAsync(apiUrl, cancellationToken);
            AddSeriesPlaceholders(series, seriesCategories, channels, seenIds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { partial = true; AppLogger.Warn("Series catalog unavailable. " + ex.Message); }

        AppLogger.Info("Xtream API fallback built " + channels.Count +
            " channels (live + VOD + series). Series episodes are fetched on demand when a series is opened, since listing them all up front would require one API call per series.");
        return new LoadResult(channels, partial, partial ? "Provider API returned an incomplete catalog." : "Provider API loaded.");
    }

    // Series-kind channels here are placeholders (Url = "series:{seriesId}"): listing
    // every episode for every series up front would mean one API call per series,
    // which for a large catalog is impractically slow and risks the provider
    // throttling the account. FetchSeriesEpisodesAsync resolves one series at a time,
    // called only when the user opens it.
    //
    // This only merges an already-downloaded catalog. The download lives in
    // FetchXtreamSeriesAsync so a single get_series response can serve both the
    // media-kind map and the placeholders.
    private static int AddSeriesPlaceholders(
        SeriesFetchResult series,
        IReadOnlyDictionary<string, string> categoryNames,
        List<Channel> channels,
        HashSet<string> seenIds)
    {
        var before = channels.Count;
        foreach (var pending in series.Placeholders)
        {
            if (!seenIds.Add(pending.Channel.Id)) continue;

            pending.Channel.Group = !string.IsNullOrWhiteSpace(pending.CategoryId) &&
                                    categoryNames.TryGetValue(pending.CategoryId, out var categoryName) &&
                                    !string.IsNullOrWhiteSpace(categoryName)
                ? categoryName.Trim()
                : "Uncategorized";
            channels.Add(pending.Channel);
        }

        var added = channels.Count - before;
        AppLogger.Info($"Xtream series placeholders added. added={added}");
        return added;
    }

    // Called on demand (when the user opens a specific series) rather than eagerly
    // for every series, since each call only covers one series's episodes.
    public async Task<IReadOnlyList<Channel>> FetchSeriesEpisodesAsync(AccountSettings account, string seriesId, CancellationToken cancellationToken)
    {
        var xtreamAccount = TryResolveXtreamAccount(account)
            ?? throw new InvalidOperationException("Xtream credentials are required to load series episodes.");
        var apiUrl = BuildPlayerApiUrl(xtreamAccount);
        var streamBaseUrl = BuildStreamBaseUrl(xtreamAccount);
        var url = AddOrReplaceQuery(apiUrl, new Dictionary<string, string>
        {
            ["action"] = "get_series_info",
            ["series_id"] = seriesId
        });

        AppLogger.Info("Fetching series episodes. seriesId=" + seriesId);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        AppLogger.Info($"Series info response. seriesId={seriesId}; status={(int)response.StatusCode} {response.ReasonPhrase}");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var episodes = new List<Channel>();
        if (!document.RootElement.TryGetProperty("episodes", out var episodesElement) || episodesElement.ValueKind != JsonValueKind.Object)
        {
            return episodes;
        }

        var username = Uri.EscapeDataString(xtreamAccount.Username.Trim());
        var password = Uri.EscapeDataString(xtreamAccount.Password.Trim());

        foreach (var season in episodesElement.EnumerateObject())
        {
            if (season.Value.ValueKind != JsonValueKind.Array) continue;

            foreach (var episode in season.Value.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetJsonText(episode, "id", out var episodeId) || string.IsNullOrWhiteSpace(episodeId)) continue;

                var title = TryGetJsonText(episode, "title", out var episodeTitle) && !string.IsNullOrWhiteSpace(episodeTitle)
                    ? episodeTitle
                    : "Episode";
                var extension = TryGetJsonText(episode, "container_extension", out var ext) && !string.IsNullOrWhiteSpace(ext) ? ext : "mp4";

                var episodeUrl = $"{streamBaseUrl}/series/{username}/{password}/{episodeId}.{extension}";
                var id = CreateStableId(MediaKind.Series + "|" + title + "|" + episodeUrl);
                _ = int.TryParse(season.Name, out var seasonNumber);
                var episodeNumber = TryGetJsonText(episode, "episode_num", out var numberText) && int.TryParse(numberText, out var parsedNumber)
                    ? parsedNumber : 0;

                var channel = new Channel
                {
                    Id = id,
                    Name = title.Trim(),
                    Group = "Season " + season.Name,
                    Logo = string.Empty,
                    EpgId = string.Empty,
                    Url = episodeUrl,
                    RawInfo = string.Empty,
                    MediaKind = MediaKind.Series,
                    SeriesId = seriesId,
                    SeasonNumber = seasonNumber,
                    EpisodeNumber = episodeNumber
                };
                VodDiscovery.ReadProviderMetadata(channel, episode,
                    episode.TryGetProperty("info", out var episodeInfo) ? episodeInfo : null);
                episodes.Add(channel);
            }
        }

        AppLogger.Info("Series episodes parsed. seriesId=" + seriesId + "; episodes=" + episodes.Count);
        return episodes;
    }

    private async Task<Dictionary<string, string>> FetchXtreamCategoriesAsync(string apiUrl, string action, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var url = AddOrReplaceQuery(apiUrl, new Dictionary<string, string> { ["action"] = action });
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return result;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!TryGetJsonText(item, "category_id", out var id) || string.IsNullOrWhiteSpace(id)) continue;
                if (TryGetJsonText(item, "category_name", out var name) && !string.IsNullOrWhiteSpace(name))
                {
                    result[id] = name;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Warn($"Fetching {action} failed. Falling back to uncategorized. {ex.Message}");
        }

        return result;
    }

    private async Task AddXtreamChannelsAsync(
        string apiUrl,
        string streamBaseUrl,
        AccountSettings account,
        string action,
        string urlSegment,
        MediaKind mediaKind,
        IReadOnlyDictionary<string, string> categoryNames,
        List<Channel> channels,
        HashSet<string> seenIds,
        CancellationToken cancellationToken)
    {
        var url = AddOrReplaceQuery(apiUrl, new Dictionary<string, string> { ["action"] = action });
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        AppLogger.Info($"Xtream fallback response. action={action}; status={(int)response.StatusCode} {response.ReasonPhrase}");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException(action + " catalog was not a list.");

        var username = Uri.EscapeDataString(account.Username.Trim());
        var password = Uri.EscapeDataString(account.Password.Trim());
        var before = channels.Count;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetJsonText(item, "stream_id", out var streamId) || string.IsNullOrWhiteSpace(streamId)) continue;

            var name = TryGetJsonText(item, "name", out var streamName) && !string.IsNullOrWhiteSpace(streamName) ? streamName : "Unnamed Channel";
            var group = TryGetJsonText(item, "category_id", out var categoryId) && categoryNames.TryGetValue(categoryId, out var categoryName)
                ? categoryName
                : "Uncategorized";
            var logo = TryGetJsonText(item, "stream_icon", out var icon) ? icon : string.Empty;
            var epgId = TryGetJsonText(item, "epg_channel_id", out var epg) ? epg : string.Empty;
            var extension = mediaKind == MediaKind.Movie && TryGetJsonText(item, "container_extension", out var ext) && !string.IsNullOrWhiteSpace(ext)
                ? ext
                : "ts";

            var streamUrl = $"{streamBaseUrl}/{urlSegment}/{username}/{password}/{streamId}.{extension}";
            var id = CreateStableId(mediaKind + "|" + name + "|" + streamUrl);
            if (!seenIds.Add(id)) continue;

            var channel = new Channel
            {
                Id = id,
                Name = name.Trim(),
                Group = group.Trim(),
                Logo = logo.Trim(),
                EpgId = epgId.Trim(),
                Url = streamUrl,
                RawInfo = string.Empty,
                MediaKind = mediaKind
            };
            if (mediaKind != MediaKind.Live) VodDiscovery.ReadProviderMetadata(channel, item);
            else Catchup.ReadProviderMetadata(channel, item);
            channels.Add(channel);
        }

        AppLogger.Info($"Xtream fallback parsed. action={action}; added={channels.Count - before}");
    }

    private static string BuildStreamBaseUrl(AccountSettings account)
    {
        var serverUrl = (account.ServerUrl ?? string.Empty).Trim();
        if (!serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            serverUrl = "http://" + serverUrl;
        }

        var uri = new Uri(serverUrl, UriKind.Absolute);
        return new UriBuilder(uri.Scheme, uri.Host, uri.Port) { Path = string.Empty }.Uri.ToString().TrimEnd('/');
    }


    public async Task<string> FetchAccountInfoSummaryAsync(AccountSettings account, CancellationToken cancellationToken)
    {
        var apiUrl = BuildPlayerApiUrl(account);
        using var response = await _httpClient.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var userInfo = root.TryGetProperty("user_info", out var ui) ? ui : default;
        var serverInfo = root.TryGetProperty("server_info", out var si) ? si : default;

        string Get(JsonElement element, string name)
        {
            if (element.ValueKind == JsonValueKind.Undefined || !element.TryGetProperty(name, out var value)) return string.Empty;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.ToString()
            };
        }

        static string FormatUnixTime(string value)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0) return "Unknown";
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "Unknown";
            }
        }

        var username = Get(userInfo, "username");
        var status = Get(userInfo, "status");
        var expiry = FormatUnixTime(Get(userInfo, "exp_date"));
        var created = FormatUnixTime(Get(userInfo, "created_at"));
        var isTrial = Get(userInfo, "is_trial") == "1" ? "Yes" : Get(userInfo, "is_trial") == "0" ? "No" : Get(userInfo, "is_trial");
        var active = Get(userInfo, "active_cons");
        var max = Get(userInfo, "max_connections");
        var serverTime = Get(serverInfo, "time_now");
        var timezone = Get(serverInfo, "timezone");
        var serverProtocol = Get(serverInfo, "server_protocol");
        var serverUrl = Get(serverInfo, "url");
        var port = Get(serverInfo, "port");

        var sb = new StringBuilder();
        sb.AppendLine("Provider account information (current when checked)");
        sb.AppendLine();
        sb.AppendLine("Username: " + (string.IsNullOrWhiteSpace(username) ? "Hidden / unavailable" : username));
        sb.AppendLine("Status: " + (string.IsNullOrWhiteSpace(status) ? "Unknown" : status));
        sb.AppendLine("Expiry (local time): " + expiry);
        sb.AppendLine("Created at: " + created);
        sb.AppendLine("Trial: " + (string.IsNullOrWhiteSpace(isTrial) ? "Unknown" : isTrial));
        sb.AppendLine("Connections in use / account limit: " + (string.IsNullOrWhiteSpace(active) ? "Unknown" : active) + " / " + (string.IsNullOrWhiteSpace(max) ? "Unknown" : max));
        sb.AppendLine();
        sb.AppendLine("Server Information");
        sb.AppendLine("Server time: " + (string.IsNullOrWhiteSpace(serverTime) ? "Unknown" : serverTime));
        sb.AppendLine("Timezone: " + (string.IsNullOrWhiteSpace(timezone) ? "Unknown" : timezone));
        sb.AppendLine("Server: " + (string.IsNullOrWhiteSpace(serverUrl) ? "Unavailable" : serverUrl) + (string.IsNullOrWhiteSpace(port) ? string.Empty : ":" + port));
        if (!string.IsNullOrWhiteSpace(serverProtocol)) sb.AppendLine("Protocol: " + serverProtocol);
        return sb.ToString().Trim();
    }

    // During one load the provider is asked for get_series twice: once to learn
    // which ids are series so M3U rows can be classified, and again to build the
    // series placeholders. On a large panel that second copy is one of the slowest
    // parts of a refresh, so it is downloaded once and both results are kept.
    private sealed class SeriesFetchResult
    {
        public List<string> Ids { get; } = new();
        public List<PendingSeries> Placeholders { get; } = new();
    }

    // Named PendingSeries rather than SeriesPlaceholder because the Core project
    // already has a SeriesPlaceholder type used to build the series URL.
    private sealed class PendingSeries
    {
        public Channel Channel { get; set; } = new();
        public string CategoryId { get; set; } = string.Empty;
    }

    private sealed class XtreamBootstrap
    {
        public Dictionary<string, MediaKind> Kinds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Channel> Archives { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SeriesFetchResult? Series { get; set; }
    }

    private async Task<XtreamBootstrap> FetchXtreamBootstrapAsync(AccountSettings account, CancellationToken cancellationToken)
    {
        var bootstrap = new XtreamBootstrap();
        if (!string.IsNullOrWhiteSpace(account.M3uUrl))
        {
            return bootstrap;
        }

        try
        {
            var apiUrl = BuildPlayerApiUrl(account);
            AppLogger.Info("Fetching Xtream media kind map from " + AppLogger.SanitizeUrl(apiUrl));
            var timer = Stopwatch.StartNew();

            // These three catalogs are independent of each other. Fetching them one
            // after another meant paying three full round trips back to back, which
            // is most of the wall-clock time of a large refresh. Kinds are still
            // merged in the original order, so the resulting map is identical.
            var liveTask = FetchXtreamLiveAsync(apiUrl, bootstrap.Archives, cancellationToken);
            var vodTask = FetchXtreamIdsAsync(apiUrl, "get_vod_streams", "stream_id", cancellationToken);
            var seriesTask = FetchXtreamSeriesAsync(apiUrl, cancellationToken);

            // WhenAll rather than three separate awaits: if one catalog faults, the
            // other two are still observed, so no task exception is left unobserved.
            await Task.WhenAll(liveTask, vodTask, seriesTask).ConfigureAwait(false);
            var liveIds = liveTask.Result;
            var vodIds = vodTask.Result;
            var series = seriesTask.Result;
            timer.Stop();

            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MergeMediaKinds(bootstrap.Kinds, conflicts, liveIds, MediaKind.Live);
            MergeMediaKinds(bootstrap.Kinds, conflicts, vodIds, MediaKind.Movie);
            MergeMediaKinds(bootstrap.Kinds, conflicts, series.Ids, MediaKind.Series);
            bootstrap.Series = series;

            AppLogger.Info($"Xtream catalogs fetched. elapsedMs={timer.ElapsedMilliseconds} kinds={bootstrap.Kinds.Count} " +
                          $"series={series.Ids.Count} placeholders={series.Placeholders.Count}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Some providers expose M3U but block one or more player_api actions.
            // Keep playlist loading resilient and fall back to local classification.
            AppLogger.Warn("Xtream media kind map failed. Falling back to local classification. " + ex.Message);
            return new XtreamBootstrap();
        }

        return bootstrap;
    }

    // First id seen wins; an id claimed by two catalogs is dropped entirely. This
    // reproduces the previous single-dictionary behaviour exactly, including the
    // order the catalogs are merged in.
    private static void MergeMediaKinds(
        Dictionary<string, MediaKind> result,
        HashSet<string> conflicts,
        List<string> ids,
        MediaKind mediaKind)
    {
        foreach (var id in ids)
        {
            if (conflicts.Contains(id)) continue;

            if (result.TryGetValue(id, out var existingKind) && existingKind != mediaKind)
            {
                result.Remove(id);
                conflicts.Add(id);
                continue;
            }

            result[id] = mediaKind;
        }
    }

    private async Task<List<string>> FetchXtreamIdsAsync(
        string apiUrl,
        string action,
        string idPropertyName,
        CancellationToken cancellationToken)
    {
        var url = AddOrReplaceQuery(apiUrl, new Dictionary<string, string>
        {
            ["action"] = action
        });

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        AppLogger.Info($"Xtream action response. action={action}; status={(int)response.StatusCode} {response.ReasonPhrase}");
        if (!response.IsSuccessStatusCode) return new List<string>();

        var (buffer, length) = await ReadCatalogAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var ids = ReadCatalogIds(buffer.AsSpan(0, length), idPropertyName, action, strict: false, cancellationToken);
        AppLogger.Info($"Xtream action parsed. action={action}; ids={ids.Count}");
        return ids;
    }

    private async Task<List<string>> FetchXtreamLiveAsync(string apiUrl, Dictionary<string, Channel> archives, CancellationToken cancellationToken)
    {
        var url = AddOrReplaceQuery(apiUrl, new Dictionary<string, string> { ["action"] = "get_live_streams" });
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        var (buffer, length) = await ReadCatalogAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return ReadLiveCatalog(buffer, length, archives, cancellationToken);
    }

    private static List<string> ReadLiveCatalog(byte[] buffer, int length, Dictionary<string, Channel> archives, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        try
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, length), ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return ids;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                var id = string.Empty; var enabled = string.Empty; var duration = string.Empty;
                var seenId = false; var seenEnabled = false; var seenDuration = false;
                // Read the three scalar fields in the existing reader. This keeps
                // live metadata free of per-row JsonDocument allocations and
                // avoids a second HTTP catalog request or second JSON pass.
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    var field = !seenId && reader.ValueTextEquals("stream_id") ? 1 :
                        !seenEnabled && reader.ValueTextEquals("tv_archive") ? 2 :
                        !seenDuration && reader.ValueTextEquals("tv_archive_duration") ? 3 : 0;
                    if (field == 0) { reader.Skip(); continue; }
                    if (field == 1) seenId = true; else if (field == 2) seenEnabled = true; else seenDuration = true;
                    if (!reader.Read()) break;
                    var value = reader.TokenType switch
                    {
                        JsonTokenType.String => reader.GetString() ?? string.Empty,
                        JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
                        _ => string.Empty
                    };
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                    if (field == 1) id = value; else if (field == 2) enabled = value; else duration = value;
                }
                if (string.IsNullOrWhiteSpace(id)) continue;
                ids.Add(id);
                if (enabled == "1" && Catchup.NumericId(id) && int.TryParse(duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is > 0 and <= 3650)
                    archives.TryAdd(id, new Channel { CatchupMode = CatchupMode.Xtream, ArchiveDays = days, ArchiveStreamId = id });
            }
        }
        catch (JsonException ex) { AppLogger.Warn("Xtream live catalog ended early. " + ex.Message); }
        return ids;
    }

    public async Task<AccountProfile> FetchCatchupProfileAsync(AccountSettings account, CancellationToken cancellationToken)
    {
        if (TryResolveXtreamAccount(account) is null) throw new InvalidOperationException("Xtream credentials are required for archives.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _httpClient.GetAsync(BuildPlayerApiUrl(account), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The provider account response is invalid.");
        string Text(string section, string key)
        {
            if (!document.RootElement.TryGetProperty(section, out var element) || element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return "";
            return value.ToString();
        }
        return new AccountProfile { Timezone = Text("server_info", "timezone"), ActiveConnections = Text("user_info", "active_cons"),
            MaxConnections = Text("user_info", "max_connections"), Status = Text("user_info", "status") };
    }

    internal static AccountSettings? ResolveCatchupAccount(AccountSettings account) => TryResolveXtreamAccount(account);

    private async Task<SeriesFetchResult> FetchXtreamSeriesAsync(string apiUrl, CancellationToken cancellationToken)
    {
        var result = new SeriesFetchResult();
        var url = AddOrReplaceQuery(apiUrl, new Dictionary<string, string> { ["action"] = "get_series" });

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        AppLogger.Info($"Xtream fallback response. action=get_series; status={(int)response.StatusCode} {response.ReasonPhrase}");
        response.EnsureSuccessStatusCode();

        var (buffer, length) = await ReadCatalogAsync(response.Content, cancellationToken).ConfigureAwait(false);
        ReadCatalogSeries(buffer, length, "get_series", cancellationToken, result);

        AppLogger.Info($"Xtream fallback parsed. action=get_series; series={result.Ids.Count}");
        return result;
    }

    // Default reader options on purpose: the previous JsonDocument.ParseAsync call also
    // used defaults, so a catalog the old code rejected is still rejected here rather
    // than silently becoming a different load.
    private static readonly JsonReaderOptions ReaderOptions = default;

    // Pulls the whole catalog into one contiguous block, then parses it with a
    // forward-only reader. This is the third option tried for this path and the one
    // that is actually cheap:
    //
    //   JsonDocument.ParseAsync          fastest CPU, but holds the raw bytes plus a
    //                                    metadata tree, roughly 5x the response.
    //   DeserializeAsyncEnumerable       about 1x memory, but builds a throwaway
    //                                    document per element, which is the most
    //                                    CPU of the three.
    //   buffered Utf8JsonReader          about 1x memory and the cheapest CPU, since
    //                                    nothing is allocated per element at all.
    private static async Task<(byte[] Buffer, int Length)> ReadCatalogAsync(HttpContent content, CancellationToken cancellationToken)
    {
        // Sized from Content-Length when the provider sends it, so the common case is a
        // single right-sized allocation instead of the repeated doubling a growing
        // stream does. The upper bound is a guard against a bogus length header.
        var declared = content.Headers.ContentLength is { } value && value > 0 ? value : 0L;
        var capacity = (int)Math.Clamp(declared, 64L * 1024, 256L * 1024 * 1024);

        var buffer = new MemoryStream(capacity);
        await using (var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        buffer.TryGetBuffer(out var segment);
        return (segment.Array!, segment.Count);
    }

    // Reads one string or number field out of every object in a JSON array. The
    // property is matched case-sensitively and the first occurrence wins, which is
    // what JsonElement.TryGetProperty did before.
    private static List<string> ReadCatalogIds(
        ReadOnlySpan<byte> utf8,
        string propertyName,
        string action,
        bool strict,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();

        try
        {
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                if (strict) throw new InvalidOperationException($"Xtream {action} catalog was not a list.");
                AppLogger.Warn($"Xtream {action} response was not a JSON array.");
                return ids;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                var value = string.Empty;
                var seen = false;

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;

                    if (seen || !reader.ValueTextEquals(propertyName))
                    {
                        reader.Skip();
                        continue;
                    }

                    seen = true;
                    if (!reader.Read()) break;

                    value = reader.TokenType switch
                    {
                        JsonTokenType.String => reader.GetString() ?? string.Empty,
                        // Providers are inconsistent and sometimes send ids unquoted.
                        JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
                        _ => string.Empty
                    };
                }

                if (!string.IsNullOrWhiteSpace(value)) ids.Add(value);
            }

            return ids;
        }
        catch (JsonException ex)
        {
            if (strict) throw new InvalidOperationException($"Xtream {action} catalog was not a list.", ex);
            AppLogger.Warn($"Xtream {action} response ended early. {ex.Message}");
            return ids;
        }
    }

    // Series rows need more than one field plus provider metadata, which the metadata
    // reader still takes as a JsonElement. Each row is therefore parsed on its own
    // from the buffered bytes and disposed immediately, so only one small row document
    // is ever alive instead of the entire catalog.
    private static void ReadCatalogSeries(
        byte[] buffer,
        int length,
        string action,
        CancellationToken cancellationToken,
        SeriesFetchResult result)
    {
        try
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, length), ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                throw new InvalidOperationException($"Xtream {action} catalog was not a list.");
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                // Skip() consumes the whole row including any nested objects, so a
                // series entry carrying an "info" block does not truncate the slice.
                // Reading to the first EndObject instead would cut the row in half.
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                var end = (int)reader.BytesConsumed;

                using var document = JsonDocument.Parse(buffer.AsMemory(start, end - start));
                AddSeriesRow(document.RootElement, result);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Xtream {action} catalog was not a list.", ex);
        }
    }

    private static void AddSeriesRow(JsonElement item, SeriesFetchResult result)
    {
        if (!TryGetJsonText(item, "series_id", out var seriesId) || string.IsNullOrWhiteSpace(seriesId)) return;
        result.Ids.Add(seriesId);

        var name = TryGetJsonText(item, "name", out var seriesName) && !string.IsNullOrWhiteSpace(seriesName) ? seriesName : "Unnamed Series";
        var cover = TryGetJsonText(item, "cover", out var coverUrl) ? coverUrl : string.Empty;
        var categoryId = TryGetJsonText(item, "category_id", out var parsedCategoryId) ? parsedCategoryId : string.Empty;

        var placeholderUrl = SeriesPlaceholder.BuildUrl(seriesId);
        var channel = new Channel
        {
            Id = CreateStableId(MediaKind.Series + "|" + name + "|" + placeholderUrl),
            Name = name.Trim(),
            Group = string.Empty,
            Logo = cover.Trim(),
            EpgId = string.Empty,
            Url = placeholderUrl,
            RawInfo = string.Empty,
            MediaKind = MediaKind.Series
        };
        VodDiscovery.ReadProviderMetadata(channel, item);
        result.Placeholders.Add(new PendingSeries { Channel = channel, CategoryId = categoryId });
    }

    private static bool TryGetJsonText(JsonElement item, string propertyName, out string value)
    {
        value = string.Empty;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(propertyName, out var property)) return false;

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.ToString(),
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    public string BuildPlayerApiUrl(AccountSettings account)
    {
        var xtreamAccount = TryResolveXtreamAccount(account) ?? account;
        var serverUrl = (xtreamAccount.ServerUrl ?? string.Empty).Trim();
        var username = (xtreamAccount.Username ?? string.Empty).Trim();
        var password = (xtreamAccount.Password ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(serverUrl)) throw new InvalidOperationException("Server URL is required.");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Username and password are required to check account information.");

        if (!serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            serverUrl = "http://" + serverUrl;
        }

        if (serverUrl.Contains("player_api.php", StringComparison.OrdinalIgnoreCase))
        {
            return AddOrReplaceQuery(serverUrl, new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password
            });
        }

        if (serverUrl.Contains("get.php", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(serverUrl);
            builder.Path = builder.Path.Replace("get.php", "player_api.php", StringComparison.OrdinalIgnoreCase);
            builder.Query = string.Empty;
            return AddOrReplaceQuery(builder.Uri.ToString(), new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password
            });
        }

        var baseUri = new Uri(serverUrl.TrimEnd('/') + "/");
        var apiUri = new Uri(baseUri, "player_api.php").ToString();
        return AddOrReplaceQuery(apiUri, new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password
        });
    }

    public string BuildPlaylistUrl(AccountSettings account)
    {
        var m3uUrl = (account.M3uUrl ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(m3uUrl))
        {
            if (!m3uUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !m3uUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                m3uUrl = "http://" + m3uUrl;
            }

            if (!Uri.TryCreate(m3uUrl, UriKind.Absolute, out var directPlaylistUri) ||
                (directPlaylistUri.Scheme != Uri.UriSchemeHttp && directPlaylistUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("Enter a valid HTTP or HTTPS M3U playlist link.");
            }

            return directPlaylistUri.ToString();
        }

        var serverUrl = (account.ServerUrl ?? string.Empty).Trim();
        var username = (account.Username ?? string.Empty).Trim();
        var password = (account.Password ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(serverUrl)) throw new InvalidOperationException("Server URL is required.");

        if (!serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            serverUrl = "http://" + serverUrl;
        }

        if (serverUrl.Contains("get.php", StringComparison.OrdinalIgnoreCase))
        {
            var values = new Dictionary<string, string>
            {
                ["type"] = "m3u_plus",
                ["output"] = "ts"
            };
            if (!string.IsNullOrWhiteSpace(username)) values["username"] = username;
            if (!string.IsNullOrWhiteSpace(password)) values["password"] = password;
            return AddOrReplaceQuery(serverUrl, values);
        }

        if (serverUrl.Contains("player_api.php", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(serverUrl);
            var path = builder.Path.Replace("player_api.php", "get.php", StringComparison.OrdinalIgnoreCase);
            builder.Path = path;
            builder.Query = string.Empty;
            var values = new Dictionary<string, string>
            {
                ["type"] = "m3u_plus",
                ["output"] = "ts"
            };
            if (!string.IsNullOrWhiteSpace(username)) values["username"] = username;
            if (!string.IsNullOrWhiteSpace(password)) values["password"] = password;
            return AddOrReplaceQuery(builder.Uri.ToString(), values);
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            // Direct M3U URLs do not always need username/password.
            if (serverUrl.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
                serverUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                serverUrl.Contains("/playlist", StringComparison.OrdinalIgnoreCase))
            {
                return serverUrl;
            }

            throw new InvalidOperationException("Username and password are required for Xtream-style server URLs.");
        }

        var baseUri = new Uri(serverUrl.TrimEnd('/') + "/");
        var getUri = new Uri(baseUri, "get.php").ToString();
        return AddOrReplaceQuery(getUri, new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["type"] = "m3u_plus",
            ["output"] = "ts"
        });
    }

    private static AccountSettings? TryResolveXtreamAccount(AccountSettings account)
    {
        var serverUrl = (account.ServerUrl ?? string.Empty).Trim();
        var username = (account.Username ?? string.Empty).Trim();
        var password = (account.Password ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(serverUrl) &&
            !string.IsNullOrWhiteSpace(username) &&
            !string.IsNullOrWhiteSpace(password))
        {
            var resolved = account.Clone();
            resolved.M3uUrl = string.Empty;
            return resolved;
        }

        var m3uUrl = (account.M3uUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(m3uUrl)) return null;
        if (!m3uUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !m3uUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            m3uUrl = "http://" + m3uUrl;
        }

        if (!Uri.TryCreate(m3uUrl, UriKind.Absolute, out var uri) ||
            !uri.AbsolutePath.EndsWith("/get.php", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("username", out username) || string.IsNullOrWhiteSpace(username) ||
            !query.TryGetValue("password", out password) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var serverBuilder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        return new AccountSettings
        {
            ServerUrl = serverBuilder.Uri.ToString(),
            Username = username,
            Password = password,
            EpgUrl = account.EpgUrl,
            PreferredPlayerPath = account.PreferredPlayerPath
        };
    }

    private static async Task<List<Channel>> ParseM3uFromStreamAsync(
        Stream stream,
        IReadOnlyDictionary<string, MediaKind> mediaKindByStreamId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, Channel>? archives = null)
    {
        var result = new List<Channel>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? currentInfo = null;
        var sawExtInf = false;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawLine = await reader.ReadLineAsync(cancellationToken);
            if (rawLine is null) break;

            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                currentInfo = line;
                sawExtInf = true;
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal)) continue;

            if (currentInfo is not null && IsLikelyStreamUrl(line))
            {
                var name = ExtractName(currentInfo);
                var attributes = ExtractAttributes(currentInfo);
                var group = attributes.GroupTitle;
                var logo = attributes.Logo;
                var epgId = attributes.EpgId;
                var tvgName = attributes.TvgName;

                if (string.IsNullOrWhiteSpace(name)) name = tvgName;
                if (string.IsNullOrWhiteSpace(name)) name = "Unnamed Channel";
                if (string.IsNullOrWhiteSpace(group)) group = "Uncategorized";

                // One URI parse and one path walk, shared by classification and by the
                // provider stream-id lookup, which used to each redo that work.
                var hasUri = Uri.TryCreate(line, UriKind.Absolute, out var rowUri);
                var facts = hasUri && rowUri is not null ? AnalyzePath(rowUri) : default;

                var mediaKind = ClassifyMediaKind(facts, group, name);
                if (facts.SegmentCount > 0 && facts.LastIsNumeric &&
                    mediaKindByStreamId.TryGetValue(facts.LastSegmentText, out var apiMediaKind))
                {
                    mediaKind = apiMediaKind;
                }

                var id = CreateStableId(mediaKind + "|" + name + "|" + line);
                Channel? archive = null;
                if (mediaKind == MediaKind.Live && facts.LastIsNumeric) archives?.TryGetValue(facts.LastSegmentText, out archive);
                if (seenIds.Add(id))
                {
                    result.Add(new Channel
                    {
                        Id = id,
                        Name = name.Trim(),
                        Group = group.Trim(),
                        Logo = logo.Trim(),
                        EpgId = epgId.Trim(),
                        Url = line,
                        RawInfo = string.Empty,
                        MediaKind = mediaKind,
                        CatchupMode = archive?.CatchupMode ?? CatchupMode.None,
                        ArchiveDays = archive?.ArchiveDays ?? 0,
                        ArchiveStreamId = archive?.ArchiveStreamId ?? string.Empty
                    });
                }
            }

            currentInfo = null;
        }

        if (!sawExtInf)
        {
            AppLogger.Warn("M3U parse failed because no #EXTINF lines were found.");
            throw new InvalidOperationException("The server response is not an M3U playlist. No #EXTINF lines were found.");
        }

        // Keep provider order. Sorting and grouping hundreds of thousands of rows can create large RAM spikes.
        return result;
    }

    // Everything the row loop needs to know about a URL path, gathered in one pass.
    //
    // ClassifyMediaKind and TryExtractStreamId each used to build their own
    // Split/Where/Select pipeline that unescaped and lowercased every segment, and
    // both parsed the URI again. On a 300k row playlist that was the single largest
    // cost in loading a playlist, and it ran twice per row.
    private readonly struct PathFacts
    {
        private readonly string? _path;
        private readonly string? _lastOverride;
        private readonly int _lastStart;
        private readonly int _lastLength;

        public PathFacts(string? path, int lastStart, int lastLength, string? lastOverride, int segmentCount,
            bool hasLive, bool hasMovie, bool hasSeries, bool lastIsNumeric)
        {
            _path = path;
            _lastStart = lastStart;
            _lastLength = lastLength;
            _lastOverride = lastOverride;
            SegmentCount = segmentCount;
            HasLive = hasLive;
            HasMovie = hasMovie;
            HasSeries = hasSeries;
            LastIsNumeric = lastIsNumeric;

            // Same value the old code read off the whole path with GetKnownExtension.
            Extension = path is null ? string.Empty : GetKnownExtension(path);
        }

        public int SegmentCount { get; }
        public bool HasLive { get; }
        public bool HasMovie { get; }
        public bool HasSeries { get; }
        public bool LastIsNumeric { get; }
        public string Extension { get; }

        // Only built when the caller actually looks the id up, so the common
        // "not a provider stream url" row allocates nothing here.
        public string LastSegmentText =>
            !LastIsNumeric ? string.Empty
            : _lastOverride ?? (_path is null ? string.Empty : _path.Substring(_lastStart, _lastLength));

        public bool LooksLikeRawLive => SegmentCount >= 3 && LastIsNumeric && !HasMovie && !HasSeries;
    }

    private static PathFacts AnalyzePath(Uri uri)
    {
        var path = uri.AbsolutePath;
        var count = 0;
        var hasLive = false;
        var hasMovie = false;
        var hasSeries = false;
        var lastIsNumeric = false;
        var lastStart = 0;
        var lastLength = 0;
        string? lastOverride = null;

        var index = 0;
        while (index < path.Length)
        {
            var next = path.IndexOf('/', index);
            var end = next < 0 ? path.Length : next;
            if (end <= index)
            {
                index = end + 1;
                continue;
            }

            count++;
            var start = index;
            var length = end - start;
            index = end + 1;

            var segment = path.AsSpan(start, length);
            ReadOnlySpan<char> normalized;

            if (segment.IndexOf('%') >= 0)
            {
                // Percent escapes are rare; unescape properly rather than guess.
                var unescaped = RemoveKnownExtension(Uri.UnescapeDataString(segment.ToString())).ToLowerInvariant();
                hasLive |= unescaped == "live";
                hasMovie |= IsMoviePathSegment(unescaped);
                hasSeries |= IsSeriesPathSegment(unescaped);
                lastIsNumeric = IsAllAsciiDigits(unescaped);
                lastStart = start;
                lastLength = length;
                lastOverride = lastIsNumeric ? unescaped : null;
            }
            else
            {
                normalized = TrimKnownExtension(segment);
                hasLive |= normalized.Equals("live", StringComparison.OrdinalIgnoreCase);
                hasMovie |= IsMovieSegment(normalized);
                hasSeries |= IsSeriesSegment(normalized);
                lastIsNumeric = IsAllAsciiDigits(normalized);
                lastStart = start;
                // The trimmed span, not the raw segment: "12345.ts" has to report
                // "12345" so it can be looked up as a provider stream id.
                lastLength = normalized.Length;
                lastOverride = null;
            }
        }

        return new PathFacts(path, lastStart, lastLength, lastOverride, count, hasLive, hasMovie, hasSeries, lastIsNumeric);
    }

    private static bool IsMovieSegment(ReadOnlySpan<char> segment) =>
        segment.Equals("movie", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("movies", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("vod", StringComparison.OrdinalIgnoreCase);

    private static bool IsSeriesSegment(ReadOnlySpan<char> segment) =>
        segment.Equals("series", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("show", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("shows", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllAsciiDigits(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return false;
        foreach (var character in value)
        {
            if (character < '0' || character > '9') return false;
        }

        return true;
    }

    private static ReadOnlySpan<char> TrimKnownExtension(ReadOnlySpan<char> value)
    {
        foreach (var extension in KnownExtensions)
        {
            if (value.Length > extension.Length && value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return value[..^extension.Length];
            }
        }

        return value;
    }

    private static MediaKind ClassifyMediaKind(string url, string group, string name) =>
        ClassifyMediaKind(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? AnalyzePath(uri) : default, group, name);

    private static MediaKind ClassifyMediaKind(PathFacts facts, string group, string name)
    {
        // No ToLowerInvariant here any more: the keyword scan and SeriesEpisodeRegex
        // are both case-insensitive, so the per-row lowercase copy was pure waste.
        var text = (group ?? string.Empty) + " " + (name ?? string.Empty);
        var keywords = ScanKeywords(text);

        if (facts.SegmentCount > 0)
        {
            if (facts.HasLive)
            {
                return MediaKind.Live;
            }

            if (facts.HasMovie)
            {
                return MediaKind.Movie;
            }

            if (facts.HasSeries)
            {
                return MediaKind.Series;
            }

            var extension = facts.Extension;
            if (IsLiveStreamExtension(extension))
            {
                return MediaKind.Live;
            }

            if (IsVideoFileExtension(extension))
            {
                return LooksLikeSeriesText(text, keywords) ? MediaKind.Series : MediaKind.Movie;
            }

            if (facts.LooksLikeRawLive)
            {
                return MediaKind.Live;
            }
        }

        if (LooksLikeSeriesText(text, keywords))
        {
            return MediaKind.Series;
        }

        if (LooksLikeMovieText(keywords))
        {
            return MediaKind.Movie;
        }

        return MediaKind.Live;
    }

    private static bool IsMoviePathSegment(string segment)
    {
        return segment is "movie" or "movies" or "vod";
    }

    private static bool IsSeriesPathSegment(string segment)
    {
        return segment is "series" or "show" or "shows";
    }

    // Keyword bits returned by ScanKeywords. Scanning once into a bitmask replaces
    // building a HashSet<string> per channel row, which on a large playlist was the
    // main source of allocation churn while parsing.
    private const int KeywordVod = 1 << 0;
    private const int KeywordMovie = 1 << 1;
    private const int KeywordMovies = 1 << 2;
    private const int KeywordFilm = 1 << 3;
    private const int KeywordFilms = 1 << 4;
    private const int KeywordCinema = 1 << 5;
    private const int KeywordSeries = 1 << 6;
    private const int KeywordSeason = 1 << 7;
    private const int KeywordEpisode = 1 << 8;
    private const int KeywordEpisodes = 1 << 9;
    private const int KeywordTv = 1 << 10;
    private const int KeywordShow = 1 << 11;
    private const int KeywordShows = 1 << 12;

    private const int MovieKeywords = KeywordVod | KeywordMovie | KeywordMovies | KeywordFilm | KeywordFilms | KeywordCinema;
    private const int SeriesKeywords = KeywordSeries | KeywordSeason | KeywordEpisode | KeywordEpisodes;

    private static readonly string[] KnownKeywords =
    [
        "vod", "movie", "movies", "film", "films", "cinema",
        "series", "season", "episode", "episodes", "tv", "show", "shows"
    ];

    // Equivalent to splitting on non-alphanumerics and testing membership, but
    // without allocating a lowercase copy, a string[] and a HashSet per row.
    // The old GetWords split the *lowercased* text; matching case-insensitively
    // over the original gives the same words.
    private static int ScanKeywords(string value)
    {
        var found = 0;
        var index = 0;

        while (index < value.Length)
        {
            while (index < value.Length && !IsAsciiLetterOrDigit(value[index])) index++;
            var start = index;
            while (index < value.Length && IsAsciiLetterOrDigit(value[index])) index++;
            if (index == start) break;

            var length = index - start;
            foreach (var keyword in KnownKeywords)
            {
                if (keyword.Length != length) continue;
                if (!MatchesAt(value, start, keyword)) continue;
                found |= KeywordBit(keyword);
                break;
            }
        }

        return found;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') || (value >= '0' && value <= '9');

    private static bool MatchesAt(string value, int start, string keyword)
    {
        for (var i = 0; i < keyword.Length; i++)
        {
            var character = value[start + i];
            if (character >= 'A' && character <= 'Z') character = (char)(character + 32);
            if (character != keyword[i]) return false;
        }

        return true;
    }

    private static int KeywordBit(string keyword) => keyword switch
    {
        "vod" => KeywordVod,
        "movie" => KeywordMovie,
        "movies" => KeywordMovies,
        "film" => KeywordFilm,
        "films" => KeywordFilms,
        "cinema" => KeywordCinema,
        "series" => KeywordSeries,
        "season" => KeywordSeason,
        "episode" => KeywordEpisode,
        "episodes" => KeywordEpisodes,
        "tv" => KeywordTv,
        "show" => KeywordShow,
        "shows" => KeywordShows,
        _ => 0
    };

    // Equivalence harnesses for the parsing hot path. The rewritten helpers are
    // only trusted because these keep the previous implementations reachable, so
    // the suite can prove the fast paths still agree with the originals.
    internal static class TestOnly
    {
        internal static ExtInfAttributes ExtractAttributes(string input) => PlaylistService.ExtractAttributes(input);

        // The four-pass version that ExtractAttributes replaced.
        internal static string ExtractAttribute(string input, string key)
        {
            foreach (Match match in AttributeRegex.Matches(input))
            {
                if (string.Equals(match.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Groups["value"].Value;
                }
            }

            return string.Empty;
        }

        internal static int ScanKeywords(string value) => PlaylistService.ScanKeywords(value);
        internal static bool LooksLikeMovieText(int keywords) => PlaylistService.LooksLikeMovieText(keywords);
        internal static bool LooksLikeSeriesText(string value, int keywords) => PlaylistService.LooksLikeSeriesText(value, keywords);

        internal static MediaKind ClassifyMediaKind(string url, string group, string name) =>
            PlaylistService.ClassifyMediaKind(url, group, name);

        internal static bool TryExtractStreamId(string url, out string streamId) =>
            TryExtractStreamIdFromPathFacts(url, out streamId);

        // Replays the row loop's stream-id decision through the same production
        // AnalyzePath walk, so the equivalence test covers the code that ships
        // rather than a parallel copy of it.
        private static bool TryExtractStreamIdFromPathFacts(string url, out string streamId)
        {
            streamId = string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri is null) return false;

            var facts = PlaylistService.AnalyzePath(uri);
            if (facts.SegmentCount == 0 || !facts.LastIsNumeric) return false;

            streamId = facts.LastSegmentText;
            return true;
        }

        internal static string CreateStableId(string input) => PlaylistService.CreateStableId(input);

        // Channel ids are persisted and matched against saved favourites, so the
        // allocation-saving rewrite has to agree with the original byte for byte.
        internal static string CreateStableIdOld(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLower(CultureInfo.InvariantCulture)[..16];
        }

        // The pre-fusion path logic, kept as the reference the single-pass version is
        // checked against: a Split/Where/Select pipeline that unescaped and lowercased
        // every segment, with a separate URI parse per call.
        internal static MediaKind ClassifyMediaKindOld(string url, string group, string name)
        {
            var text = (group ?? string.Empty).ToLowerInvariant() + " " + (name ?? string.Empty).ToLowerInvariant();

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var segments = OldSegments(uri);

                if (segments.Any(segment => segment == "live"))
                {
                    return MediaKind.Live;
                }

                if (segments.Any(segment => segment == "movie" || segment == "movies" || segment == "vod"))
                {
                    return MediaKind.Movie;
                }

                if (segments.Any(segment => segment == "series" || segment == "show" || segment == "shows"))
                {
                    return MediaKind.Series;
                }

                var extension = GetKnownExtension(uri.AbsolutePath);
                if (IsLiveStreamExtension(extension))
                {
                    return MediaKind.Live;
                }

                if (IsVideoFileExtension(extension))
                {
                    return LooksLikeSeriesTextOld(text) ? MediaKind.Series : MediaKind.Movie;
                }

                if (LooksLikeRawLiveXtreamUrlOld(segments))
                {
                    return MediaKind.Live;
                }
            }

            if (LooksLikeSeriesTextOld(text))
            {
                return MediaKind.Series;
            }

            if (LooksLikeMovieTextOld(text))
            {
                return MediaKind.Movie;
            }

            return MediaKind.Live;
        }

        internal static bool TryExtractStreamIdOld(string url, out string streamId)
        {
            streamId = string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

            var segments = OldSegments(uri);
            if (segments.Length == 0) return false;

            var candidate = segments[segments.Length - 1];
            if (!NumericStreamIdRegex.IsMatch(candidate)) return false;

            streamId = candidate;
            return true;
        }

        private static string[] OldSegments(Uri uri) => uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => PlaylistService.RemoveKnownExtension(Uri.UnescapeDataString(value)).ToLowerInvariant())
            .ToArray();

        private static bool LooksLikeRawLiveXtreamUrlOld(IReadOnlyList<string> segments) =>
            segments.Count >= 3 &&
            NumericStreamIdRegex.IsMatch(segments[segments.Count - 1]) &&
            !segments.Any(segment => segment == "movie" || segment == "movies" || segment == "vod") &&
            !segments.Any(segment => segment == "series" || segment == "show" || segment == "shows");

        // Streaming id reader against the original JsonDocument version.
        internal static string ReadIds(string json, string propertyName, bool strict)
        {
            var streamed = PlaylistService.ReadCatalogIds(Encoding.UTF8.GetBytes(json), propertyName, "test", strict, CancellationToken.None);
            var document = ReadIdsWithDocument(json, propertyName);
            return Reference(streamed, document);
        }

        internal static int ReadCount(string json, string propertyName) =>
            PlaylistService.ReadCatalogIds(Encoding.UTF8.GetBytes(json), propertyName, "test", strict: false, CancellationToken.None).Count;

        private static List<string> ReadIdsWithDocument(string json, string propertyName)
        {
            var ids = new List<string>();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return ids;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (PlaylistService.TryGetJsonText(item, propertyName, out var id)) ids.Add(id);
            }

            return ids;
        }

        // Streaming series reader against the original JsonDocument version.
        internal static string ReadSeries(string json)
        {
            var streamed = new SeriesFetchResult();
            var bytes = Encoding.UTF8.GetBytes(json);
            PlaylistService.ReadCatalogSeries(bytes, bytes.Length, "get_series", CancellationToken.None, streamed);

            var document = new SeriesFetchResult();
            using (var parsed = JsonDocument.Parse(json))
            {
                if (parsed.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("not a list");
                foreach (var item in parsed.RootElement.EnumerateArray()) PlaylistService.AddSeriesRow(item, document);
            }

            return Reference(Summarize(streamed), Summarize(document));
        }

        private static string Summarize(SeriesFetchResult result)
        {
            var first = result.Placeholders.Count > 0 ? result.Placeholders[0].Channel : null;
            return string.Join("|", result.Ids.Count, result.Placeholders.Count, first?.Id, first?.Name, first?.Group,
                first?.Url, first?.Logo, first?.MediaKind, first?.Description, first?.Year, first?.Genre,
                result.Placeholders.Count > 0 ? result.Placeholders[0].CategoryId : string.Empty);
        }

        private static string Reference<T>(T streamed, T document) =>
            string.Equals(streamed?.ToString(), document?.ToString(), StringComparison.Ordinal)
                ? "match"
                : $"streamed={streamed} document={document}";

        internal static HashSet<string> GetWords(string value) =>
            WordSplitRegex
                .Split(value.ToLowerInvariant())
                .Where(word => !string.IsNullOrWhiteSpace(word))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The previous word-set implementations, kept verbatim for equivalence.
        internal static bool LooksLikeMovieTextOld(string value)
        {
            var words = GetWords(value);
            return words.Contains("vod") ||
                   words.Contains("movie") ||
                   words.Contains("movies") ||
                   words.Contains("film") ||
                   words.Contains("films") ||
                   words.Contains("cinema");
        }

        internal static bool LooksLikeSeriesTextOld(string value)
        {
            if (SeriesEpisodeRegex.IsMatch(value)) return true;

            var words = GetWords(value);
            return words.Contains("series") ||
                   words.Contains("season") ||
                   words.Contains("episode") ||
                   words.Contains("episodes") ||
                   (words.Contains("tv") && (words.Contains("show") || words.Contains("shows")));
        }
    }

    private static bool LooksLikeMovieText(int keywords) => (keywords & MovieKeywords) != 0;

    private static bool LooksLikeSeriesText(string value, int keywords)
    {
        if (SeriesEpisodeRegex.IsMatch(value)) return true;
        if ((keywords & SeriesKeywords) != 0) return true;

        // "tv" only counts when it is paired with a show noun, as before.
        return (keywords & KeywordTv) != 0 && (keywords & (KeywordShow | KeywordShows)) != 0;
    }

    private static bool IsLiveStreamExtension(string extension)
    {
        return extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVideoFileExtension(string extension)
    {
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] KnownExtensions = [".ts", ".m3u8", ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".wmv"];

    private static string GetKnownExtension(string value)
    {
        // Was allocating this eight-element array on every call, and it is called
        // two to three times per playlist row.
        foreach (var extension in KnownExtensions)
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension;
            }
        }

        return string.Empty;
    }

    private static string RemoveKnownExtension(string value)
    {
        var extension = GetKnownExtension(value);
        return string.IsNullOrWhiteSpace(extension) ? value : value[..^extension.Length];
    }

    private static bool IsLikelyStreamUrl(string line)
    {
        return line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractName(string extInf)
    {
        var quoted = false;
        var commaIndex = -1;
        for (var index = 0; index < extInf.Length; index++)
        {
            if (extInf[index] == '"') quoted = !quoted;
            else if (extInf[index] == ',' && !quoted) { commaIndex = index; break; }
        }
        return commaIndex >= 0 && commaIndex + 1 < extInf.Length ? extInf[(commaIndex + 1)..] : string.Empty;
    }

    internal readonly record struct ExtInfAttributes(string GroupTitle, string Logo, string EpgId, string TvgName);

    // One pass over the #EXTINF line, by hand rather than by regex.
    //
    // ExtractAttribute re-ran a named-group regex over the same string once per
    // attribute, so a 300k row playlist paid four regex passes per row plus a
    // MatchCollection, a Match and two Groups per attribute. This walks the line
    // once with no allocation beyond the four values.
    //
    // Semantics deliberately match the regex it replaces
    // ((?<key>[A-Za-z0-9_-]+)="(?<value>[^"]*)"), including that a failed attempt
    // retries one character later and that the first occurrence of a key wins.
    private static ExtInfAttributes ExtractAttributes(string input)
    {
        var groupTitle = string.Empty;
        var logo = string.Empty;
        var epgId = string.Empty;
        var tvgName = string.Empty;
        var haveGroupTitle = false;
        var haveLogo = false;
        var haveEpgId = false;
        var haveTvgName = false;

        var index = 0;
        while (index < input.Length)
        {
            var keyStart = index;
            while (index < input.Length && IsAttributeKeyChar(input[index])) index++;
            if (index == keyStart)
            {
                index = keyStart + 1;
                continue;
            }

            if (index + 1 >= input.Length || input[index] != '=' || input[index + 1] != '"')
            {
                // Not "key=\"" here, so no match starts at keyStart; resume as the
                // regex would, one character on.
                index = keyStart + 1;
                continue;
            }

            var valueStart = index + 2;
            var close = input.IndexOf('"', valueStart);
            if (close < 0)
            {
                index = valueStart;
                continue;
            }

            // The key is only needed to choose a slot, so it is compared in place
            // instead of being materialised.
            if (!haveGroupTitle && MatchesKey(input, keyStart, index, "group-title"))
            {
                groupTitle = input.Substring(valueStart, close - valueStart);
                haveGroupTitle = true;
            }
            else if (!haveLogo && MatchesKey(input, keyStart, index, "tvg-logo"))
            {
                logo = input.Substring(valueStart, close - valueStart);
                haveLogo = true;
            }
            else if (!haveEpgId && MatchesKey(input, keyStart, index, "tvg-id"))
            {
                epgId = input.Substring(valueStart, close - valueStart);
                haveEpgId = true;
            }
            else if (!haveTvgName && MatchesKey(input, keyStart, index, "tvg-name"))
            {
                tvgName = input.Substring(valueStart, close - valueStart);
                haveTvgName = true;
            }

            // Non-overlapping, like Regex.Matches: resume after the closing quote.
            index = close + 1;
        }

        return new ExtInfAttributes(groupTitle, logo, epgId, tvgName);
    }

    private static bool IsAttributeKeyChar(char value) =>
        (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
        (value >= '0' && value <= '9') || value == '_' || value == '-';

    private static bool MatchesKey(string input, int start, int end, string key)
    {
        if (end - start != key.Length) return false;
        for (var i = 0; i < key.Length; i++)
        {
            var character = input[start + i];
            if (character >= 'A' && character <= 'Z') character = (char)(character + 32);
            if (character != key[i]) return false;
        }

        return true;
    }

    private const string LowerHexDigits = "0123456789abcdef";

    // Same digest as SHA256 -> hex -> lower -> first 16 characters, but it used to
    // build four throwaway strings per channel (a 32 byte array, a 64 char hex
    // string, a lowercased copy of it and the final slice). On a 300k row playlist
    // that was the largest remaining allocation after the attribute regex went.
    private static string CreateStableId(string input)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), digest);

        Span<char> characters = stackalloc char[16];
        for (var index = 0; index < characters.Length; index++)
        {
            var shift = (index & 1) == 0 ? 4 : 0;
            characters[index] = LowerHexDigits[(digest[index / 2] >> shift) & 0xF];
        }

        return new string(characters);
    }

    private static string AddOrReplaceQuery(string url, IReadOnlyDictionary<string, string> values)
    {
        var builder = new UriBuilder(url);
        var query = ParseQuery(builder.Query);

        foreach (var pair in values)
        {
            query[pair.Key] = pair.Value;
        }

        builder.Query = string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return builder.Uri.ToString();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        query = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query)) return result;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0]);
            var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }
}
