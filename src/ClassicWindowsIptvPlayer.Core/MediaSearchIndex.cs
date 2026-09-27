using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ClassicWindowsIptvPlayer.Core;

public sealed class MediaSearchIndex
{
    private readonly List<(Channel Channel, string SearchText)> _items = [];

    public MediaSearchIndex(IEnumerable<Channel> channels)
    {
        foreach (var channel in channels)
        {
            var text = string.Join(' ', channel.Name, channel.Group, channel.MediaKind.ToString()).ToLowerInvariant();
            _items.Add((channel, text));
        }
    }

    public IReadOnlyList<Channel> Search(string query, MediaKind? kind, int limit = 500)
    {
        return SearchAll(query, kind, CancellationToken.None).Take(Math.Max(50, limit)).ToList();
    }

    // Enumerate the full match set for library filtering. The bounded Search API
    // remains available for small previews, but must not drive the main list.
    public IEnumerable<Channel> SearchAll(string query, MediaKind? kind, CancellationToken cancellationToken)
    {
        var parts = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kind is not null && item.Channel.MediaKind != kind.Value) continue;
            if (parts.All(part => item.SearchText.Contains(part, StringComparison.OrdinalIgnoreCase)))
                yield return item.Channel;
        }
    }
}
