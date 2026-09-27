using System.Security.Cryptography;
using System.Text;

namespace ClassicWindowsIptvPlayer.Core;

public static class ItemIdentity
{
    public static string For(Channel channel) => Build(channel.MediaKind, channel.EpgId, channel.Url, channel.IdentityName ?? channel.Name, channel.IdentityGroup ?? channel.Group);
    public static string For(RecentItem item) => Build(item.MediaKind, "", item.Url, item.Name, item.Group);

    private static string Build(MediaKind kind, string epgId, string url, string name, string group)
    {
        string source;
        if (kind == MediaKind.Series && url.StartsWith("series:", StringComparison.OrdinalIgnoreCase))
            source = url.ToLowerInvariant();
        else if (kind == MediaKind.Live && !string.IsNullOrWhiteSpace(epgId))
            source = "epg:" + epgId.Trim().ToLowerInvariant();
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Xtream URLs include credentials in path segments; the stream ID is the stable part.
            source = parts.Length >= 4 && (parts[0].Equals("live", StringComparison.OrdinalIgnoreCase) ||
                parts[0].Equals("movie", StringComparison.OrdinalIgnoreCase) ||
                parts[0].Equals("series", StringComparison.OrdinalIgnoreCase))
                ? uri.Authority.ToLowerInvariant() + "/" + parts[0].ToLowerInvariant() + "/" + parts[^1].ToLowerInvariant()
                : uri.Authority.ToLowerInvariant() + uri.AbsolutePath.ToLowerInvariant() + StableQuery(uri.Query);
        }
        else source = url.Length > 0 ? url : group + "/" + name;
        return "item:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + source)));
    }

    private static string StableQuery(string query)
    {
        var fields = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => !IsCredentialKey(Uri.UnescapeDataString(pair[0])))
            .Select(pair => pair[0].ToLowerInvariant() + "=" + (pair.Length > 1 ? pair[1] : ""))
            .OrderBy(part => part, StringComparer.Ordinal);
        return "?" + string.Join("&", fields);
    }

    private static bool IsCredentialKey(string key)
    {
        var value = key.ToLowerInvariant();
        return value.Contains("token") || value.Contains("pass") || value.Contains("user") ||
            value.Contains("auth") || value.Contains("key") || value.Contains("sig") || value.Contains("session");
    }
}
