using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;

namespace ClassicWindowsIptvPlayer.Core;

public sealed record EpgProgramme(
    string ChannelId,
    string Title,
    string Description,
    string Category,
    DateTimeOffset Start,
    DateTimeOffset Stop);

public sealed record EpgNowNext(EpgProgramme? Now, EpgProgramme? Next);

public sealed class EpgGuide
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<EpgProgramme>> _programmes;

    public EpgGuide(IReadOnlyDictionary<string, IReadOnlyList<EpgProgramme>> programmes)
    {
        _programmes = programmes;
        ProgrammeCount = programmes.Values.Sum(items => items.Count);
    }

    public int ProgrammeCount { get; }

    public EpgNowNext GetNowNext(Channel channel, DateTimeOffset? at = null)
    {
        var key = Normalize(string.IsNullOrWhiteSpace(channel.EpgId) ? channel.Name : channel.EpgId);
        if (!_programmes.TryGetValue(key, out var items) && !string.IsNullOrWhiteSpace(channel.EpgId))
        {
            _programmes.TryGetValue(Normalize(channel.Name), out items);
        }

        if (items is null) return new EpgNowNext(null, null);
        var now = at ?? DateTimeOffset.Now;
        var current = items.FirstOrDefault(item => item.Start <= now && item.Stop > now);
        var next = items.FirstOrDefault(item => item.Start >= (current?.Stop ?? now));
        return new EpgNowNext(current, next);
    }

    internal static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

public sealed class EpgService
{
    private static readonly Regex OffsetRegex = new(@"(?<sign>[+-])(?<hours>\d{2})(?<minutes>\d{2})$", RegexOptions.Compiled);
    private readonly HttpClient _httpClient;

    public EpgService()
    {
        _httpClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Classic-Windows-IPTV-Player/0.9.0");
    }

    public string? BuildEpgUrl(AccountSettings account)
    {
        if (!string.IsNullOrWhiteSpace(account.EpgUrl))
        {
            var value = account.EpgUrl.Trim();
            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = "http://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var direct)) throw new InvalidOperationException("Enter a valid HTTP or HTTPS EPG URL.");
            return direct.ToString();
        }

        if (string.IsNullOrWhiteSpace(account.ServerUrl) || string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrWhiteSpace(account.Password)) return null;
        var server = account.ServerUrl.Trim();
        if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) server = "http://" + server;
        var builder = new UriBuilder(server);
        var path = builder.Path;
        var marker = path.LastIndexOf('/');
        builder.Path = (marker < 0 ? "/" : path[..(marker + 1)]) + "xmltv.php";
        builder.Query = "username=" + Uri.EscapeDataString(account.Username.Trim()) + "&password=" + Uri.EscapeDataString(account.Password.Trim());
        return builder.Uri.ToString();
    }

    public async Task<EpgGuide?> LoadAsync(AccountSettings account, IReadOnlyList<Channel> channels, CancellationToken cancellationToken)
    {
        var url = BuildEpgUrl(account);
        if (url is null) return null;
        AppLogger.Info("Loading EPG from " + AppLogger.SanitizeUrl(url));
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        Stream input = responseStream;
        if (url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || response.Content.Headers.ContentType?.MediaType == "application/gzip")
            input = new GZipStream(responseStream, CompressionMode.Decompress, leaveOpen: false);
        await using (input)
        {
            var guide = await ParseAsync(input, channels, cancellationToken);
            AppLogger.Info("EPG parsed. programmes=" + guide.ProgrammeCount);
            return guide;
        }
    }

    public static async Task<EpgGuide> ParseAsync(Stream stream, IReadOnlyList<Channel> channels, CancellationToken cancellationToken)
    {
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            accepted.Add(EpgGuide.Normalize(channel.Name));
            if (!string.IsNullOrWhiteSpace(channel.EpgId)) accepted.Add(EpgGuide.Normalize(channel.EpgId));
        }

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, List<EpgProgramme>>(StringComparer.OrdinalIgnoreCase);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(stream, settings);
        var earliest = DateTimeOffset.Now.AddHours(-6);
        var latest = DateTimeOffset.Now.AddDays(2);

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.Name == "channel")
            {
                var id = reader.GetAttribute("id") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id)) continue;
                using var subtree = reader.ReadSubtree();
                while (await subtree.ReadAsync())
                {
                    if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "display-name")
                    {
                        var name = await subtree.ReadElementContentAsStringAsync();
                        if (accepted.Contains(EpgGuide.Normalize(name))) aliases[id] = EpgGuide.Normalize(name);
                    }
                }
                continue;
            }

            if (reader.Name != "programme") continue;
            var channelId = reader.GetAttribute("channel") ?? string.Empty;
            var key = EpgGuide.Normalize(channelId);
            if (!accepted.Contains(key) && !aliases.TryGetValue(channelId, out key))
            {
                continue;
            }

            if (!TryParseXmlTvDate(reader.GetAttribute("start"), out var start) || !TryParseXmlTvDate(reader.GetAttribute("stop"), out var stop) || stop < earliest || start > latest)
            {
                continue;
            }

            string title = string.Empty, description = string.Empty, category = string.Empty;
            using (var subtree = reader.ReadSubtree())
            {
                while (await subtree.ReadAsync())
                {
                    if (subtree.NodeType != XmlNodeType.Element) continue;
                    if (subtree.Name == "title") title = await subtree.ReadElementContentAsStringAsync();
                    else if (subtree.Name == "desc") description = await subtree.ReadElementContentAsStringAsync();
                    else if (subtree.Name == "category") category = await subtree.ReadElementContentAsStringAsync();
                }
            }

            if (string.IsNullOrWhiteSpace(title)) title = "Untitled programme";
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(new EpgProgramme(channelId, title.Trim(), description.Trim(), category.Trim(), start, stop));
        }

        return new EpgGuide(result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<EpgProgramme>)pair.Value.OrderBy(item => item.Start).ToList(), StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryParseXmlTvDate(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!DateTime.TryParseExact(parts[0], new[] { "yyyyMMddHHmmss", "yyyyMMddHHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
        var offset = TimeSpan.Zero;
        if (parts.Length > 1)
        {
            var match = OffsetRegex.Match(parts[1]);
            if (match.Success)
            {
                offset = new TimeSpan(int.Parse(match.Groups["hours"].Value), int.Parse(match.Groups["minutes"].Value), 0);
                if (match.Groups["sign"].Value == "-") offset = -offset;
            }
        }
        result = new DateTimeOffset(date, offset);
        return true;
    }
}
