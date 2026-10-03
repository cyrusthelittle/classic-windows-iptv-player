namespace ClassicWindowsIptvPlayer.Core;

public static class PlaylistRefreshValidation
{
    public static void Validate(IReadOnlyList<Channel> channels, CancellationToken cancellationToken = default)
    {
        if (channels.Count == 0) throw new InvalidDataException("The playlist contains no playable items.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < channels.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = channels[index];
            // API catalog placeholders and saved caches carry the provider series
            // identity in their URL; SeriesId is populated for resolved episodes.
            var reason = string.IsNullOrWhiteSpace(channel.Id) ? "missing identity" :
                string.IsNullOrWhiteSpace(channel.Name) ? "missing name" :
                !Uri.TryCreate(channel.Url, UriKind.Absolute, out var uri) ? "invalid stream address" :
                uri.Scheme == "series" && !SeriesPlaceholder.TryGetSeriesId(channel, out _) ? "invalid series placeholder" :
                !identities.Add(channel.MediaKind + "|" + channel.Id) ? "duplicate identity" : null;
            if (reason is not null)
                throw new InvalidDataException($"Playlist item {index + 1} is invalid: {reason}.");
        }
    }
}
