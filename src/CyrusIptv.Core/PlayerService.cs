using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CyrusIptv.Core;

public sealed record PlaybackCandidate(string Url, string Label);

public static class PlayerService
{
    private static readonly Regex NumericStreamIdRegex = new("^[0-9]+$", RegexOptions.Compiled);

    public static IReadOnlyList<PlaybackCandidate> BuildPlaybackCandidates(Channel channel, AccountSettings account)
    {
        var originalUrl = (channel.Url ?? string.Empty).Trim();
        var candidates = new List<PlaybackCandidate>();

        if (string.IsNullOrWhiteSpace(originalUrl)) return candidates;

        // Use only the exact stream URL supplied by the playlist. Some providers
        // reject inferred Xtream/HLS/TS variants, and retrying them can hang LibVLC.
        AddUnique(candidates, originalUrl, "Playlist URL");

        return candidates;
    }

    public static string BuildPlayableUrl(Channel channel, AccountSettings account)
    {
        return BuildPlaybackCandidates(channel, account).FirstOrDefault()?.Url ?? channel.Url;
    }

    public static string BuildAllCandidateText(Channel channel, AccountSettings account)
    {
        var candidates = BuildPlaybackCandidates(channel, account);
        if (candidates.Count == 0) return channel.Url ?? string.Empty;
        return string.Join(Environment.NewLine + Environment.NewLine, candidates.Select((c, i) => $"{i + 1}. {c.Label}{Environment.NewLine}{c.Url}"));
    }

    private static bool TryExtractXtreamParts(
        string originalUrl,
        AccountSettings account,
        out Uri baseUri,
        out string username,
        out string password,
        out string streamId,
        out string prefix,
        out string extension)
    {
        baseUri = null!;
        username = (account.Username ?? string.Empty).Trim();
        password = (account.Password ?? string.Empty).Trim();
        streamId = string.Empty;
        prefix = string.Empty;
        extension = string.Empty;

        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var originalUri) || !IsHttpLike(originalUri))
        {
            return false;
        }

        baseUri = new Uri(originalUri.GetLeftPart(UriPartial.Authority));

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        var segments = originalUri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (segments.Length < 3) return false;

        // Copy out parameters to local variables before using them in lambdas.
        // C# does not allow ref/out/in parameters inside anonymous methods.
        var accountUsername = username;
        var userIndex = Array.FindIndex(segments, value => string.Equals(value, accountUsername, StringComparison.Ordinal));
        if (userIndex < 0 || userIndex + 2 >= segments.Length) return false;
        if (!string.Equals(segments[userIndex + 1], password, StringComparison.Ordinal)) return false;

        if (userIndex > 0)
        {
            var possiblePrefix = segments[userIndex - 1].ToLowerInvariant();
            if (possiblePrefix is "live" or "movie" or "series") prefix = possiblePrefix;
        }

        var streamSegment = segments[userIndex + 2];
        extension = GetKnownExtension(streamSegment);
        streamId = RemoveKnownExtension(streamSegment);
        return NumericStreamIdRegex.IsMatch(streamId);
    }

    private static void AddXtreamCandidate(
        List<PlaybackCandidate> candidates,
        Uri baseUri,
        string prefix,
        string username,
        string password,
        string streamId,
        string extension,
        string label)
    {
        if (string.IsNullOrWhiteSpace(extension) && label.Contains("original extension", StringComparison.OrdinalIgnoreCase)) return;

        var pathParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefix)) pathParts.Add(Uri.EscapeDataString(prefix));
        pathParts.Add(Uri.EscapeDataString(username));
        pathParts.Add(Uri.EscapeDataString(password));
        pathParts.Add(Uri.EscapeDataString(streamId) + extension);

        var builder = new UriBuilder(baseUri)
        {
            Path = "/" + string.Join("/", pathParts),
            Query = string.Empty,
            Fragment = string.Empty
        };

        AddUnique(candidates, builder.Uri.ToString(), label);
    }

    private static void AddUnique(List<PlaybackCandidate> candidates, string url, string label)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (candidates.Any(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase))) return;
        candidates.Add(new PlaybackCandidate(url, label));
    }

    private static string ReplaceExtension(Uri uri, string extension)
    {
        var path = uri.AbsolutePath;
        var dotIndex = path.LastIndexOf('.');
        if (dotIndex >= 0)
        {
            path = path[..dotIndex] + extension;
        }
        else
        {
            path += extension;
        }

        var builder = new UriBuilder(uri) { Path = path };
        return builder.Uri.ToString();
    }

    private static string AppendExtension(Uri uri, string extension)
    {
        var path = uri.AbsolutePath;
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) path += extension;
        var builder = new UriBuilder(uri) { Path = path };
        return builder.Uri.ToString();
    }

    private static string RemoveExtension(Uri uri)
    {
        var path = uri.AbsolutePath;
        var dotIndex = path.LastIndexOf('.');
        if (dotIndex >= 0) path = path[..dotIndex];
        var builder = new UriBuilder(uri) { Path = path };
        return builder.Uri.ToString();
    }

    private static string GetKnownExtension(string value)
    {
        foreach (var extension in new[] { ".ts", ".m3u8", ".mp4", ".mkv", ".avi", ".mov" })
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension;
            }
        }

        return string.Empty;
    }

    private static string RemoveKnownExtension(string value)
    {
        var extension = GetKnownExtension(value);
        return string.IsNullOrWhiteSpace(extension) ? value : value[..^extension.Length];
    }

    private static bool IsHttpLike(Uri uri)
    {
        return uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
    }
}
