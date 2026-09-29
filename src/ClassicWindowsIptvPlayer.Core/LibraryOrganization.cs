namespace ClassicWindowsIptvPlayer.Core;

public static class LibraryOrganization
{
    public static List<Channel> Apply(IReadOnlyList<Channel> source, AccountLibraryState library)
    {
        var channels = library.ChannelOrganization ?? new(StringComparer.OrdinalIgnoreCase);
        var groups = library.GroupOrganization ?? new(StringComparer.OrdinalIgnoreCase);
        var result = new List<(Channel Channel, int GroupOrder, int ItemOrder, int Index)>(source.Count);
        var groupFirstIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            groupFirstIndex.TryAdd(item.Group, i);
            var key = ItemIdentity.For(item);
            channels.TryGetValue(key, out var overrideItem);
            groups.TryGetValue(item.Group, out var overrideGroup);
            if (overrideItem?.Hidden == true || overrideGroup?.Hidden == true) continue;
            // The provider cache remains pristine. Display copies keep their original
            // identity so favorites, guide mapping and history survive a rename.
            var display = new Channel
            {
                Id = item.Id, IdentityName = item.IdentityName ?? item.Name, IdentityGroup = item.IdentityGroup ?? item.Group,
                Name = string.IsNullOrWhiteSpace(overrideItem?.Name) ? item.Name : overrideItem.Name,
                Group = string.IsNullOrWhiteSpace(overrideGroup?.Name) ? item.Group : overrideGroup.Name,
                Logo = item.Logo, EpgId = item.EpgId, Url = item.Url, RawInfo = item.RawInfo,
                MediaKind = item.MediaKind, SeriesId = item.SeriesId, SeasonNumber = item.SeasonNumber,
                EpisodeNumber = item.EpisodeNumber, Description = item.Description, Genre = item.Genre,
                Year = item.Year, DurationMinutes = item.DurationMinutes, AddedAt = item.AddedAt,
                CatchupMode = item.CatchupMode, ArchiveDays = item.ArchiveDays, ArchiveStreamId = item.ArchiveStreamId
            };
            result.Add((display, overrideGroup?.Order ?? 0, overrideItem?.Order ?? 0, i));
        }
        return result.OrderBy(x => x.GroupOrder == 0 ? int.MaxValue : x.GroupOrder)
            .ThenBy(x => x.GroupOrder == 0 ? groupFirstIndex[x.Channel.IdentityGroup!] : 0)
            .ThenBy(x => x.Channel.IdentityGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ItemOrder == 0 ? int.MaxValue : x.ItemOrder)
            .ThenBy(x => x.Index).Select(x => x.Channel).ToList();
    }

    public static void MoveFavorite(List<string> ids, string id, int direction)
    {
        var index = ids.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        var target = index + direction;
        if (index < 0 || target < 0 || target >= ids.Count) return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
    }
}
