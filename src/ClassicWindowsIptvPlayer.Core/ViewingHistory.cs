using System.Text.RegularExpressions;

namespace ClassicWindowsIptvPlayer.Core;

public static class ViewingHistory
{
    private static readonly Regex EpisodePattern = new(@"(?:\bS(?<s>\d{1,3})\s*E(?<e>\d{1,4})\b|\b(?<s>\d{1,3})x(?<e>\d{1,4})\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void RecordPlay(AccountLibraryState library, Channel channel, DateTime utcNow)
    {
        var key = ItemIdentity.For(channel);
        library.Recent ??= [];
        library.Recent.RemoveAll(item => string.Equals(item.ItemKey, key, StringComparison.OrdinalIgnoreCase));
        library.Recent.Insert(0, new RecentItem
        {
            ChannelId = channel.Id, ItemKey = key, Name = channel.Name, Group = channel.Group,
            Url = channel.Url, MediaKind = channel.MediaKind, PlayedAtUtc = utcNow,
            SeriesId = channel.SeriesId, SeasonNumber = channel.SeasonNumber, EpisodeNumber = channel.EpisodeNumber
        });
        if (library.Recent.Count > 100) library.Recent.RemoveRange(100, library.Recent.Count - 100);
        if (channel.MediaKind != MediaKind.Live)
        {
            var progress = GetOrCreate(library, key);
            progress.Dismissed = false;
            progress.UpdatedAtUtc = utcNow;
        }
    }

    public static bool RecordPosition(AccountLibraryState library, Channel channel, long positionMs, long durationMs, DateTime utcNow)
    {
        if (channel.MediaKind == MediaKind.Live || positionMs < 0 || durationMs <= 0 || positionMs > durationMs + 60_000) return false;
        var progress = GetOrCreate(library, ItemIdentity.For(channel));
        var watched = IsComplete(positionMs, durationMs);
        if (progress.Watched && !watched) return false;
        var position = watched ? 0 : Math.Min(positionMs, durationMs);
        if (progress.PositionMs == position && progress.DurationMs == durationMs && progress.Watched == watched) return false;
        progress.PositionMs = position;
        progress.DurationMs = durationMs;
        progress.Watched = watched;
        progress.UpdatedAtUtc = utcNow;
        return true;
    }

    public static bool IsComplete(long positionMs, long durationMs) =>
        durationMs > 0 && positionMs >= durationMs - Math.Min(120_000, durationMs / 20);

    public static void Finish(AccountLibraryState library, Channel channel, DateTime utcNow)
    {
        if (channel.MediaKind == MediaKind.Live) return;
        var progress = GetOrCreate(library, ItemIdentity.For(channel));
        progress.PositionMs = 0;
        progress.Watched = true;
        progress.UpdatedAtUtc = utcNow;
    }

    public static void StartOver(AccountLibraryState library, Channel channel, DateTime utcNow)
    {
        var progress = GetOrCreate(library, ItemIdentity.For(channel));
        progress.PositionMs = 0;
        progress.Watched = false;
        progress.Dismissed = false;
        progress.UpdatedAtUtc = utcNow;
    }

    public static void Dismiss(AccountLibraryState library, Channel channel) =>
        GetOrCreate(library, ItemIdentity.For(channel)).Dismissed = true;

    public static IReadOnlyList<RecentItem> Recent(AccountLibraryState library) =>
        (library.Recent ?? []).OrderByDescending(item => item.PlayedAtUtc).ToList();

    public static IReadOnlyList<RecentItem> Continue(AccountLibraryState library) =>
        Recent(library).Where(item => item.MediaKind != MediaKind.Live &&
            library.ViewingProgress is not null && library.ViewingProgress.TryGetValue(item.ItemKey, out var progress) &&
            progress.PositionMs > 0 && !progress.Watched && !progress.Dismissed).ToList();

    public static Channel Resolve(RecentItem item, IReadOnlyDictionary<string, Channel> catalog) =>
        catalog.TryGetValue(item.ItemKey, out var current) ? current : new Channel
        {
            Id = item.ChannelId, Name = item.Name, Group = item.Group, Url = item.Url,
            MediaKind = item.MediaKind, SeriesId = item.SeriesId,
            SeasonNumber = item.SeasonNumber, EpisodeNumber = item.EpisodeNumber
        };

    public static Channel? NextEpisode(AccountLibraryState library, IReadOnlyList<Channel> episodes, Channel current)
    {
        var ordered = episodes.Where(episode => episode.MediaKind == MediaKind.Series)
            .OrderBy(episode => EpisodeOrder(episode).Season)
            .ThenBy(episode => EpisodeOrder(episode).Episode)
            .ThenBy(episode => episode.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var index = ordered.FindIndex(episode => ItemIdentity.For(episode) == ItemIdentity.For(current));
        if (index < 0) return null;
        return ordered.Skip(index + 1).FirstOrDefault(episode =>
            library.ViewingProgress is null || !library.ViewingProgress.TryGetValue(ItemIdentity.For(episode), out var progress) || !progress.Watched);
    }

    public static IReadOnlyList<Channel> SeriesSiblings(IReadOnlyList<Channel> catalog, Channel current)
    {
        if (current.MediaKind != MediaKind.Series) return [];
        if (!string.IsNullOrWhiteSpace(current.SeriesId))
            return catalog.Where(channel => channel.MediaKind == MediaKind.Series &&
                string.Equals(channel.SeriesId, current.SeriesId, StringComparison.OrdinalIgnoreCase)).ToList();
        var match = EpisodePattern.Match(current.Name);
        if (!match.Success) return [];
        var prefix = current.Name[..match.Index].Trim(' ', '-', ':', '.');
        if (prefix.Length == 0) return [];
        return catalog.Where(channel => channel.MediaKind == MediaKind.Series &&
            string.Equals(channel.Group, current.Group, StringComparison.OrdinalIgnoreCase) &&
            EpisodePattern.Match(channel.Name) is { Success: true } episodeMatch &&
            string.Equals(channel.Name[..episodeMatch.Index].Trim(' ', '-', ':', '.'), prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static void Merge(AccountLibraryState target, AccountLibraryState source)
    {
        target.ViewingProgress ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, progress) in source.ViewingProgress ?? new Dictionary<string, ViewingProgress>())
            if (!target.ViewingProgress.TryGetValue(key, out var saved) || progress.UpdatedAtUtc >= saved.UpdatedAtUtc)
                target.ViewingProgress[key] = progress;
        target.Recent = (target.Recent ?? []).Concat(source.Recent ?? [])
            .GroupBy(item => item.ItemKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.PlayedAtUtc).First())
            .OrderByDescending(item => item.PlayedAtUtc).Take(100).ToList();
    }

    public static (int Season, int Episode) EpisodeOrder(Channel channel)
    {
        if (channel.SeasonNumber > 0 || channel.EpisodeNumber > 0) return (channel.SeasonNumber, channel.EpisodeNumber);
        var match = EpisodePattern.Match(channel.Name);
        if (match.Success && int.TryParse(match.Groups["s"].Value, out var season) && int.TryParse(match.Groups["e"].Value, out var episode))
            return (season, episode);
        return (int.MaxValue, int.MaxValue);
    }

    private static ViewingProgress GetOrCreate(AccountLibraryState library, string key)
    {
        library.ViewingProgress ??= new(StringComparer.OrdinalIgnoreCase);
        if (!library.ViewingProgress.TryGetValue(key, out var progress))
            library.ViewingProgress[key] = progress = new ViewingProgress { ItemKey = key };
        return progress;
    }
}
