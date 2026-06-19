using System;
using System.Collections.Generic;
using System.Linq;

namespace CyrusIptv.Core;

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
        query = (query ?? string.Empty).Trim().ToLowerInvariant();

        IEnumerable<(Channel Channel, string SearchText)> source = _items;
        if (kind is not null)
        {
            var mediaKind = kind.Value;
            source = source.Where(item => item.Channel.MediaKind == mediaKind);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            source = source.Where(item => parts.All(part => item.SearchText.Contains(part, StringComparison.OrdinalIgnoreCase)));
        }

        return source.Take(Math.Max(50, limit)).Select(item => item.Channel).ToList();
    }
}
