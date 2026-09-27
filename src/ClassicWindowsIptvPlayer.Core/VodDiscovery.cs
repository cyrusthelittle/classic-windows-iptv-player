using System.Globalization;
using System.Text.Json;

namespace ClassicWindowsIptvPlayer.Core;

public enum VodSort { ProviderOrder, Title, Newest, Year, Duration }

public static class VodDiscovery
{
    public static void ReadProviderMetadata(Channel channel, JsonElement item, JsonElement? nestedInfo = null)
    {
        // Xtream installations use different names and sometimes nest episode info.
        channel.Description = First(item, nestedInfo, "plot", "description", "overview");
        channel.Genre = First(item, nestedInfo, "genre", "category_name");
        channel.Year = ParseYear(First(item, nestedInfo, "releaseDate", "release_date", "releasedate", "year"));
        var duration = First(item, nestedInfo, "duration");
        channel.DurationMinutes = duration.Length > 0
            ? ParseDuration(duration, false)
            : ParseDuration(First(item, nestedInfo, "duration_secs"), true);
        channel.AddedAt = ParseAdded(First(item, nestedInfo, "added", "date_added"));
    }

    public static IReadOnlyList<Channel> Sort(IEnumerable<Channel> channels, VodSort sort) => sort switch
    {
        VodSort.Title => channels.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        VodSort.Newest => channels.OrderByDescending(c => c.AddedAt.HasValue).ThenByDescending(c => c.AddedAt)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        VodSort.Year => channels.OrderByDescending(c => c.Year.HasValue).ThenByDescending(c => c.Year)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        VodSort.Duration => channels.OrderByDescending(c => c.DurationMinutes.HasValue).ThenByDescending(c => c.DurationMinutes)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        _ => channels.ToList()
    };

    public static string Details(Channel channel)
    {
        var parts = new List<string>();
        if (channel.Year is { } year) parts.Add(year.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(channel.Genre)) parts.Add(channel.Genre);
        if (channel.DurationMinutes is { } minutes) parts.Add($"{minutes} min");
        var summary = parts.Count == 0 ? "Provider details unavailable" : string.Join("  ·  ", parts);
        return summary + (string.IsNullOrWhiteSpace(channel.Description) ? "" : "\n\n" + channel.Description);
    }

    private static string First(JsonElement item, JsonElement? info, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var source in new[] { item, info ?? default })
            {
                if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(key, out var value)) continue;
                var text = value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString().Trim() : "";
                if (text.Length > 0) return text;
            }
        }
        return "";
    }

    private static int? ParseYear(string value)
    {
        if (value.Length >= 4 && int.TryParse(value[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1888 and <= 2100) return year;
        return null;
    }

    private static int? ParseDuration(string value, bool seconds)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            var minutes = seconds ? (long)Math.Ceiling(number / 60d) : number;
            return minutes <= 1440 ? (int)minutes : null;
        }
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero && span <= TimeSpan.FromDays(1))
            return (int)Math.Ceiling(span.TotalMinutes);
        return null;
    }

    private static DateTimeOffset? ParseAdded(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { return null; }
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }
}
