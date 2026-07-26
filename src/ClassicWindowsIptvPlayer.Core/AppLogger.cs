using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ClassicWindowsIptvPlayer.Core;

public static class AppLogger
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassicWindowsIptvPlayer",
        "logs");

    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");

    public static string CurrentLogPath => LogPath;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static string DescribeChannel(Channel? channel)
    {
        if (channel is null) return "none";
        return $"id={channel.Id}; type={channel.MediaKind}; name={channel.Name}; group={channel.Group}; url={SanitizeUrl(channel.Url)}";
    }

    public static string SanitizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return RedactLooseSecrets(value);

            var query = ParseQuery(uri.Query);
            foreach (var key in query.Keys.ToList())
            {
                if (IsSecretKey(key)) query[key] = "***";
            }

            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Join("&", query.Select(pair =>
                    Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
                Path = RedactXtreamPath(uri.AbsolutePath)
            };

            return builder.Uri.ToString();
        }
        catch
        {
            return RedactLooseSecrets(value);
        }
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();

                var sb = new StringBuilder();
                sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
                sb.Append(" [");
                sb.Append(level);
                sb.Append("] ");
                sb.AppendLine(message);
                if (exception is not null) sb.AppendLine(exception.ToString());

                File.AppendAllText(LogPath, sb.ToString());
            }
        }
        catch
        {
            // Logging must never break playback or startup.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath)) return;
        var info = new FileInfo(LogPath);
        if (info.Length < MaxLogBytes) return;

        var backupPath = Path.Combine(LogDirectory, "app.previous.log");
        if (File.Exists(backupPath)) File.Delete(backupPath);
        File.Move(LogPath, backupPath);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        query = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(query)) return result;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0]);
            var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private static string RedactXtreamPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var credentialIndex = FindCredentialSegmentIndex(segments);
        if (credentialIndex >= 0 && credentialIndex + 1 < segments.Count)
        {
            segments[credentialIndex] = "***";
            segments[credentialIndex + 1] = "***";
            return "/" + string.Join("/", segments.Select(Uri.EscapeDataString));
        }

        return path;
    }

    private static int FindCredentialSegmentIndex(IReadOnlyList<string> segments)
    {
        if (segments.Count >= 4 && IsKnownXtreamPrefix(segments[0]) && LooksLikeStreamId(segments[^1])) return 1;
        if (segments.Count >= 3 && LooksLikeStreamId(segments[^1])) return 0;
        return -1;
    }

    private static bool IsKnownXtreamPrefix(string value)
    {
        return value.Equals("live", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("movie", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("series", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStreamId(string value)
    {
        var clean = value;
        var dotIndex = clean.LastIndexOf('.');
        if (dotIndex > 0) clean = clean[..dotIndex];
        return clean.Length > 0 && clean.All(char.IsDigit);
    }

    private static bool IsSecretKey(string key)
    {
        return key.Contains("pass", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("user", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("key", StringComparison.OrdinalIgnoreCase);
    }

    private static string RedactLooseSecrets(string value)
    {
        return value
            .Replace("password=", "password=***", StringComparison.OrdinalIgnoreCase)
            .Replace("username=", "username=***", StringComparison.OrdinalIgnoreCase)
            .Replace("token=", "token=***", StringComparison.OrdinalIgnoreCase);
    }
}
