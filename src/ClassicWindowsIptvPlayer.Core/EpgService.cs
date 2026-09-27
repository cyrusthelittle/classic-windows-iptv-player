using System.Globalization;
using System.IO.Compression;
using System.Net;
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
public sealed record EpgChannelInfo(string Id, string Name);
public sealed record EpgGuideSnapshot(DateTimeOffset FetchedAt, Dictionary<string, List<EpgProgramme>> Programmes,
    Dictionary<string, string> Aliases, List<EpgChannelInfo> Channels);

public sealed class EpgGuide
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<EpgProgramme>> _programmes;
    private readonly IReadOnlyDictionary<string, string> _channelAliases;
    private readonly IReadOnlyList<EpgChannelInfo> _channels;

    public EpgGuide(IReadOnlyDictionary<string, IReadOnlyList<EpgProgramme>> programmes)
        : this(programmes, new Dictionary<string, string>(), [])
    {
    }

    internal EpgGuide(
        IReadOnlyDictionary<string, IReadOnlyList<EpgProgramme>> programmes,
        IReadOnlyDictionary<string, string> channelAliases, IReadOnlyList<EpgChannelInfo> channels)
    {
        _programmes = programmes;
        _channelAliases = channelAliases;
        _channels = channels;
        ProgrammeCount = programmes.Values.Sum(items => items.Count);
    }

    public int ProgrammeCount { get; }
    public IReadOnlyList<EpgChannelInfo> Channels => _channels;
    public EpgGuideSnapshot Snapshot(DateTimeOffset fetchedAt) => new(fetchedAt,
        _programmes.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(_channelAliases, StringComparer.OrdinalIgnoreCase), _channels.ToList());
    public static EpgGuide FromSnapshot(EpgGuideSnapshot snapshot) => new(
        snapshot.Programmes.ToDictionary(p => p.Key, p => (IReadOnlyList<EpgProgramme>)p.Value, StringComparer.OrdinalIgnoreCase),
        snapshot.Aliases, snapshot.Channels);

    public bool HasMatch(Channel channel, string? mappedId = null)
    {
        bool Known(string value)
        {
            var key = Normalize(value);
            return _programmes.ContainsKey(key) || _channelAliases.ContainsKey(key) ||
                _channels.Any(item => Normalize(item.Id) == key);
        }
        if (!string.IsNullOrWhiteSpace(mappedId)) return Known(mappedId);
        var sourceName = channel.IdentityName ?? channel.Name;
        return Known(string.IsNullOrWhiteSpace(channel.EpgId) ? sourceName : channel.EpgId) ||
            (!string.IsNullOrWhiteSpace(channel.EpgId) && Known(sourceName));
    }

    public EpgNowNext GetNowNext(Channel channel, DateTimeOffset? at = null, string? mappedId = null, int offsetMinutes = 0)
    {
        var sourceName = channel.IdentityName ?? channel.Name;
        var key = Normalize(string.IsNullOrWhiteSpace(mappedId) ? (string.IsNullOrWhiteSpace(channel.EpgId) ? sourceName : channel.EpgId) : mappedId);
        if (!TryGetProgrammes(key, out var items) && string.IsNullOrWhiteSpace(mappedId) && !string.IsNullOrWhiteSpace(channel.EpgId))
        {
            TryGetProgrammes(Normalize(sourceName), out items);
        }

        if (items is null) return new EpgNowNext(null, null);
        var now = at ?? DateTimeOffset.Now;
        var shift = TimeSpan.FromMinutes(offsetMinutes);
        var current = items.FirstOrDefault(item => item.Start + shift <= now && item.Stop + shift > now);
        var next = items.FirstOrDefault(item => item.Start + shift >= (current?.Stop + shift ?? now));
        if (shift != TimeSpan.Zero)
        {
            current = current is null ? null : current with { Start = current.Start + shift, Stop = current.Stop + shift };
            next = next is null ? null : next with { Start = next.Start + shift, Stop = next.Stop + shift };
        }
        return new EpgNowNext(current, next);
    }

    public IReadOnlyList<EpgProgramme> GetProgrammes(Channel channel, DateTimeOffset from, DateTimeOffset until,
        string? mappedId = null, int offsetMinutes = 0)
    {
        if (until <= from) return [];
        var sourceName = channel.IdentityName ?? channel.Name;
        var key = Normalize(string.IsNullOrWhiteSpace(mappedId) ?
            (string.IsNullOrWhiteSpace(channel.EpgId) ? sourceName : channel.EpgId) : mappedId);
        if (!TryGetProgrammes(key, out var items) && string.IsNullOrWhiteSpace(mappedId) && !string.IsNullOrWhiteSpace(channel.EpgId))
            TryGetProgrammes(Normalize(sourceName), out items);
        if (items is null) return [];
        var shift = TimeSpan.FromMinutes(offsetMinutes);
        var result = new List<EpgProgramme>();
        foreach (var item in items)
        {
            var start = item.Start + shift;
            if (start >= until) break;
            var stop = item.Stop + shift;
            if (stop > from) result.Add(shift == TimeSpan.Zero ? item : item with { Start = start, Stop = stop });
        }
        return result;
    }

    public IReadOnlyList<EpgProgramme> SearchProgrammes(Channel channel, string query,
        string? mappedId = null, int offsetMinutes = 0)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        return GetProgrammes(channel, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, mappedId, offsetMinutes)
            .Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public (DateTimeOffset? First, DateTimeOffset? Last) DateRange()
    {
        DateTimeOffset? first = null, last = null;
        foreach (var items in _programmes.Values)
        {
            if (items.Count == 0) continue;
            if (first is null || items[0].Start < first) first = items[0].Start;
            foreach (var item in items)
                if (last is null || item.Stop > last) last = item.Stop;
        }
        return (first, last);
    }

    private bool TryGetProgrammes(string key, out IReadOnlyList<EpgProgramme>? items)
    {
        if (_programmes.TryGetValue(key, out items)) return true;
        return _channelAliases.TryGetValue(key, out var channelId) && _programmes.TryGetValue(channelId, out items);
    }

    internal static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

public sealed class EpgService
{
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

    public async Task<EpgGuide?> LoadAsync(AccountSettings account, IReadOnlyList<Channel> channels, CancellationToken cancellationToken,
        IReadOnlyCollection<string>? mappedIds = null)
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
            var guide = await ParseAsync(input, channels, DateTimeOffset.UtcNow, cancellationToken, mappedIds);
            AppLogger.Info("EPG parsed. programmes=" + guide.ProgrammeCount);
            return guide;
        }
    }

    public static Task<EpgGuide> ParseAsync(Stream stream, IReadOnlyList<Channel> channels, CancellationToken cancellationToken)
        => ParseAsync(stream, channels, DateTimeOffset.UtcNow, cancellationToken);

    internal static async Task<EpgGuide> ParseAsync(
        Stream stream,
        IReadOnlyList<Channel> channels,
        DateTimeOffset at,
        CancellationToken cancellationToken, IReadOnlyCollection<string>? mappedIds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceName = channel.IdentityName ?? channel.Name;
            if (!string.IsNullOrWhiteSpace(sourceName)) accepted.Add(EpgGuide.Normalize(sourceName));
            if (!string.IsNullOrWhiteSpace(channel.EpgId)) accepted.Add(EpgGuide.Normalize(channel.EpgId));
        }
        if (mappedIds is not null) foreach (var id in mappedIds) accepted.Add(EpgGuide.Normalize(id));

        var acceptedChannelIds = new HashSet<string>(accepted, StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guideChannels = new List<EpgChannelInfo>();
        var result = new Dictionary<string, List<EpgProgramme>>(StringComparer.OrdinalIgnoreCase);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(stream, settings);

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1) continue;

            if (reader.Name == "channel")
            {
                var id = reader.GetAttribute("id") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var sourceChannelId = EpgGuide.Normalize(id);
                string displayName = id;
                await ReadTextFieldsAsync(reader, static name => name == "display-name", (_, value) =>
                {
                    if (displayName == id && !string.IsNullOrWhiteSpace(value)) displayName = value.Trim();
                    var name = EpgGuide.Normalize(value);
                    if (accepted.Contains(name))
                    {
                        aliases.TryAdd(name, sourceChannelId);
                        acceptedChannelIds.Add(sourceChannelId);
                    }
                }, cancellationToken);
                guideChannels.Add(new EpgChannelInfo(id, displayName));
                continue;
            }

            if (reader.Name != "programme") continue;
            var channelId = reader.GetAttribute("channel") ?? string.Empty;
            var key = EpgGuide.Normalize(channelId);
            if (!acceptedChannelIds.Contains(key))
            {
                continue;
            }

            if (!TryParseXmlTvDate(reader.GetAttribute("start"), out var start) ||
                !TryParseXmlTvDate(reader.GetAttribute("stop"), out var stop) ||
                stop <= start)
            {
                continue;
            }

            string title = string.Empty, description = string.Empty, category = string.Empty;
            await ReadTextFieldsAsync(reader, static name => name is "title" or "desc" or "category", (name, value) =>
            {
                // XMLTV may repeat these fields for languages or categories. Keep
                // the first nonempty value until the UI supports choosing one.
                if (name == "title" && string.IsNullOrWhiteSpace(title)) title = value;
                else if (name == "desc" && string.IsNullOrWhiteSpace(description)) description = value;
                else if (name == "category" && string.IsNullOrWhiteSpace(category)) category = value;
            }, cancellationToken);

            if (string.IsNullOrWhiteSpace(title)) title = "Untitled programme";
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(new EpgProgramme(channelId, title.Trim(), description.Trim(), category.Trim(), start, stop));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new EpgGuide(
            result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<EpgProgramme>)pair.Value.OrderBy(item => item.Start).ToList(), StringComparer.OrdinalIgnoreCase),
            aliases, guideChannels);
    }

    private static async Task ReadTextFieldsAsync(
        XmlReader parent,
        Func<string, bool> isField,
        Action<string, string> onField,
        CancellationToken cancellationToken)
    {
        using var subtree = parent.ReadSubtree();
        while (!subtree.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtree.NodeType == XmlNodeType.Element && subtree.Depth == 1 && isField(subtree.Name))
            {
                var name = subtree.Name;
                var value = await subtree.ReadElementContentAsStringAsync();
                onField(name, value);
                // ReadElementContentAsStringAsync already advances to the next
                // sibling. Process that position before asking the reader to move.
                continue;
            }

            await subtree.ReadAsync();
        }
    }

    private static bool TryParseXmlTvDate(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) return false;
        if (!DateTime.TryParseExact(parts[0], new[] { "yyyyMMddHHmmss", "yyyyMMddHHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
        var offset = TimeSpan.Zero;
        if (parts.Length == 2)
        {
            var zone = parts[1].ToUpperInvariant();
            if (zone == "BST") offset = TimeSpan.FromHours(1);
            else if (zone is not ("UTC" or "GMT"))
            {
                if (zone.Length != 5 || zone[0] is not ('+' or '-') ||
                    zone.Skip(1).Any(character => character is < '0' or > '9')) return false;

                var hours = (zone[1] - '0') * 10 + zone[2] - '0';
                var minutes = (zone[3] - '0') * 10 + zone[4] - '0';
                if (hours > 14 || minutes > 59 || (hours == 14 && minutes != 0)) return false;
                offset = new TimeSpan(hours, minutes, 0);
                if (zone[0] == '-') offset = -offset;
            }
        }

        var utcTicks = date.Ticks - offset.Ticks;
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks) return false;
        result = new DateTimeOffset(date, offset);
        return true;
    }
}
