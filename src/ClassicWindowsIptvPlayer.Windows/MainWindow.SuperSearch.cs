using ClassicWindowsIptvPlayer.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    private sealed record SuperSearchSection(MediaKind Kind, List<FolderResult> Folders, List<Channel> Channels);
    private sealed record SuperSearchRow(MediaKind Kind, FolderResult? Folder, Channel? Channel, bool IsHeading);
    private List<SuperSearchRow>? _searchRows;

    private static FilterResult BuildSuperSearchResult(IReadOnlyList<Channel> channels, MediaSearchIndex? searchIndex,
        string search, MediaKind? preferredKind, IReadOnlyDictionary<string, GroupOrganization> groupOrganization,
        CancellationToken cancellationToken)
    {
        var query = SplitSearchQuery(search);
        var matches = searchIndex is null
            ? channels.Where(channel => MatchesSearch(channel, query))
            : searchIndex.SearchAll(search, null, cancellationToken);
        var byKind = new Dictionary<MediaKind, List<Channel>>();
        foreach (var channel in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byKind.TryGetValue(channel.MediaKind, out var items))
                byKind[channel.MediaKind] = items = [];
            items.Add(channel);
        }

        var order = new[] { MediaKind.Live, MediaKind.Movie, MediaKind.Series };
        var sections = new List<SuperSearchSection>();
        foreach (var mediaKind in order.OrderBy(value => value == preferredKind ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byKind.TryGetValue(mediaKind, out var items) || items.Count == 0) continue;
            var folders = items.GroupBy(channel => NormalizeGroupName(channel.Group), StringComparer.OrdinalIgnoreCase)
                .Select(group => new FolderResult(group.Key, group.Count()))
                .OrderBy(folder => groupOrganization.FirstOrDefault(group => string.Equals(
                    string.IsNullOrWhiteSpace(group.Value.Name) ? group.Key : group.Value.Name, folder.Name,
                    StringComparison.OrdinalIgnoreCase)).Value?.Order is > 0 and var position ? position : int.MaxValue)
                .ThenBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var folderPositions = folders.Select((folder, index) => (folder.Name, index))
                .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            var groupedItems = items.OrderBy(channel => folderPositions[NormalizeGroupName(channel.Group)]).ToList();
            sections.Add(new SuperSearchSection(mediaKind, folders, groupedItems));
        }

        var visible = sections.SelectMany(section => section.Channels).ToList();
        return new FilterResult
        {
            SearchSections = sections,
            FilteredChannels = visible,
            VisibleChannels = visible,
            CountText = $"{visible.Count:N0} results across {sections.Count:N0} sections"
        };
    }
}
