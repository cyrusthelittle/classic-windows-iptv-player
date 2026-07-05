using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CyrusIptv.Core;

public sealed class PlaylistService
{
    private static readonly Regex AttributeRegex = new("(?<key>[A-Za-z0-9_-]+)=\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.Compiled);
    private static readonly Regex WordSplitRegex = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeriesEpisodeRegex = new(
        "(^|[^a-z0-9])(?:s\\d{1,2}\\s*e\\d{1,3}|\\d{1,2}x\\d{1,3}|season\\s*\\d{1,2}|episode\\s*\\d{1,3}|ep\\s*\\d{1,3})([^a-z0-9]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex NumericStreamIdRegex = new("^[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;

    public PlaylistService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Cyrus-IPTV-Native/0.9.0");
    }

    public async Task<IReadOnlyList<Channel>> LoadPlaylistAsync(AccountSettings account, CancellationToken cancellationToken)
    {
        var playlistUrl = BuildPlaylistUrl(account);
        AppLogger.Info("Loading playlist from " + AppLogger.SanitizeUrl(playlistUrl));
        var mediaKindByStreamId = await FetchXtreamMediaKindMapAsync(account, cancellationToken);
        AppLogger.Info("Xtream media kind map loaded. count=" + mediaKindByStreamId.Count);
        using var response = await _httpClient.GetAsync(playlistUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        AppLogger.Info("Playlist HTTP response. status=" + (int)response.StatusCode + " " + response.ReasonPhrase);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var channels = await ParseM3uFromStreamAsync(stream, mediaKindByStreamId, cancellationToken);
        AppLogger.Info("Playlist parsed. channels=" + channels.Count);
        if (channels.Count == 0)
        {
            AppLogger.Warn("Playlist parsed but no playable channels were found.");
            throw new InvalidOperationException("Playlist was downloaded but no playable channels were found. The server may have returned a non-M3U response, expired account message, or an empty playlist.");
        }

        return channels;
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
        sb.AppendLine("Account Information");
        sb.AppendLine();
        sb.AppendLine("Username: " + (string.IsNullOrWhiteSpace(username) ? "Hidden / unavailable" : username));
        sb.AppendLine("Status: " + (string.IsNullOrWhiteSpace(status) ? "Unknown" : status));
        sb.AppendLine("Expiry date: " + expiry);
        sb.AppendLine("Created at: " + created);
        sb.AppendLine("Trial: " + (string.IsNullOrWhiteSpace(isTrial) ? "Unknown" : isTrial));
        sb.AppendLine("Connections: " + (string.IsNullOrWhiteSpace(active) ? "?" : active) + " / " + (string.IsNullOrWhiteSpace(max) ? "?" : max));
        sb.AppendLine();
        sb.AppendLine("Server Information");
        sb.AppendLine("Server time: " + (string.IsNullOrWhiteSpace(serverTime) ? "Unknown" : serverTime));
        sb.AppendLine("Timezone: " + (string.IsNullOrWhiteSpace(timezone) ? "Unknown" : timezone));
        sb.AppendLine("Server: " + (string.IsNullOrWhiteSpace(serverUrl) ? "Unavailable" : serverUrl) + (string.IsNullOrWhiteSpace(port) ? string.Empty : ":" + port));
        if (!string.IsNullOrWhiteSpace(serverProtocol)) sb.AppendLine("Protocol: " + serverProtocol);
        return sb.ToString().Trim();
    }

    private async Task<IReadOnlyDictionary<string, MediaKind>> FetchXtreamMediaKindMapAsync(AccountSettings account, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(account.M3uUrl))
        {
            return new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var apiUrl = BuildPlayerApiUrl(account);
            AppLogger.Info("Fetching Xtream media kind map from " + AppLogger.SanitizeUrl(apiUrl));
            var result = new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await AddXtreamStreamIdsAsync(apiUrl, "get_live_streams", "stream_id", MediaKind.Live, result, conflicts, cancellationToken);
            await AddXtreamStreamIdsAsync(apiUrl, "get_vod_streams", "stream_id", MediaKind.Movie, result, conflicts, cancellationToken);
            await AddXtreamStreamIdsAsync(apiUrl, "get_series", "series_id", MediaKind.Series, result, conflicts, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            // Some providers expose M3U but block one or more player_api actions.
            // Keep playlist loading resilient and fall back to local classification.
            AppLogger.Warn("Xtream media kind map failed. Falling back to local classification. " + ex.Message);
            return new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task AddXtreamStreamIdsAsync(
        string apiUrl,
        string action,
        string idPropertyName,
        MediaKind mediaKind,
        Dictionary<string, MediaKind> result,
        HashSet<string> conflicts,
        CancellationToken cancellationToken)
    {
        var url = AddOrReplaceQuery(apiUrl, new Dictionary<string, string>
        {
            ["action"] = action
        });

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        AppLogger.Info($"Xtream action response. action={action}; status={(int)response.StatusCode} {response.ReasonPhrase}");
        if (!response.IsSuccessStatusCode) return;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return;

        var before = result.Count;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetJsonText(item, idPropertyName, out var id) || string.IsNullOrWhiteSpace(id)) continue;
            if (conflicts.Contains(id)) continue;

            if (result.TryGetValue(id, out var existingKind) && existingKind != mediaKind)
            {
                result.Remove(id);
                conflicts.Add(id);
                continue;
            }

            result[id] = mediaKind;
        }

        AppLogger.Info($"Xtream action parsed. action={action}; added={result.Count - before}; conflicts={conflicts.Count}");
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
        var serverUrl = (account.ServerUrl ?? string.Empty).Trim();
        var username = (account.Username ?? string.Empty).Trim();
        var password = (account.Password ?? string.Empty).Trim();

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

    private static async Task<List<Channel>> ParseM3uFromStreamAsync(
        Stream stream,
        IReadOnlyDictionary<string, MediaKind> mediaKindByStreamId,
        CancellationToken cancellationToken)
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
                var group = ExtractAttribute(currentInfo, "group-title");
                var logo = ExtractAttribute(currentInfo, "tvg-logo");
                var tvgName = ExtractAttribute(currentInfo, "tvg-name");

                if (string.IsNullOrWhiteSpace(name)) name = tvgName;
                if (string.IsNullOrWhiteSpace(name)) name = "Unnamed Channel";
                if (string.IsNullOrWhiteSpace(group)) group = "Uncategorized";

                var mediaKind = ClassifyMediaKind(line, group, name);
                if (TryExtractStreamId(line, out var streamId) && mediaKindByStreamId.TryGetValue(streamId, out var apiMediaKind))
                {
                    mediaKind = apiMediaKind;
                }

                var id = CreateStableId(mediaKind + "|" + name + "|" + line);
                if (seenIds.Add(id))
                {
                    result.Add(new Channel
                    {
                        Id = id,
                        Name = name.Trim(),
                        Group = group.Trim(),
                        Logo = logo.Trim(),
                        Url = line,
                        RawInfo = string.Empty,
                        MediaKind = mediaKind
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

    private static MediaKind ClassifyMediaKind(string url, string group, string name)
    {
        var text = ((group ?? string.Empty) + " " + (name ?? string.Empty)).ToLowerInvariant();

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => RemoveKnownExtension(Uri.UnescapeDataString(value)).ToLowerInvariant())
                .ToArray();

            if (segments.Any(segment => segment == "live"))
            {
                return MediaKind.Live;
            }

            if (segments.Any(IsMoviePathSegment))
            {
                return MediaKind.Movie;
            }

            if (segments.Any(IsSeriesPathSegment))
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
                return LooksLikeSeriesText(text) ? MediaKind.Series : MediaKind.Movie;
            }

            if (LooksLikeRawLiveXtreamUrl(segments))
            {
                return MediaKind.Live;
            }
        }

        if (LooksLikeSeriesText(text))
        {
            return MediaKind.Series;
        }

        if (LooksLikeMovieText(text))
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

    private static bool LooksLikeRawLiveXtreamUrl(IReadOnlyList<string> segments)
    {
        if (segments.Count < 3) return false;

        var streamId = segments[^1];
        if (!NumericStreamIdRegex.IsMatch(streamId)) return false;

        // Raw Xtream live URLs are commonly /username/password/channelId.
        // Movie and series entries usually carry an explicit prefix or media extension.
        return !segments.Any(IsMoviePathSegment) && !segments.Any(IsSeriesPathSegment);
    }

    private static bool TryExtractStreamId(string url, out string streamId)
    {
        streamId = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => RemoveKnownExtension(Uri.UnescapeDataString(value)).ToLowerInvariant())
            .ToArray();

        if (segments.Length == 0) return false;

        var candidate = segments[^1];
        if (!NumericStreamIdRegex.IsMatch(candidate)) return false;

        streamId = candidate;
        return true;
    }

    private static bool LooksLikeMovieText(string value)
    {
        var words = GetWords(value);
        return words.Contains("vod") ||
               words.Contains("movie") ||
               words.Contains("movies") ||
               words.Contains("film") ||
               words.Contains("films") ||
               words.Contains("cinema");
    }

    private static bool LooksLikeSeriesText(string value)
    {
        if (SeriesEpisodeRegex.IsMatch(value)) return true;

        var words = GetWords(value);
        return words.Contains("series") ||
               words.Contains("season") ||
               words.Contains("episode") ||
               words.Contains("episodes") ||
               (words.Contains("tv") && (words.Contains("show") || words.Contains("shows")));
    }

    private static HashSet<string> GetWords(string value)
    {
        return WordSplitRegex
            .Split(value.ToLowerInvariant())
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    private static string GetKnownExtension(string value)
    {
        foreach (var extension in new[] { ".ts", ".m3u8", ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".wmv" })
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
        var commaIndex = extInf.LastIndexOf(',');
        return commaIndex >= 0 && commaIndex + 1 < extInf.Length ? extInf[(commaIndex + 1)..] : string.Empty;
    }

    private static string ExtractAttribute(string input, string key)
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

    private static string CreateStableId(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLower(CultureInfo.InvariantCulture)[..16];
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
