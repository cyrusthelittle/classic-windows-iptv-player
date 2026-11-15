using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace ClassicWindowsIptvPlayer.Windows;
public sealed record GitHubReleaseInfo(Version Version, string DisplayVersion, string Name, Uri PageUri,
    string? ReleaseNotes = null, DateTimeOffset? PublishedAt = null);
public sealed class GitHubUpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/cyrusthelittle/classic-windows-iptv-player/releases/latest";
    internal const int MaximumResponseBytes = 1024 * 1024;
    internal const int MaximumNotesCharacters = 16_000;
    private static readonly HttpClient HttpClient = CreateHttpClient();
    public Version CurrentVersion => NormalizeVersion(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0));
    /// <summary>The edition stamped into this build (Full or Lite); updates download the same edition.</summary>
    public static string Edition { get; } = typeof(GitHubUpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateEdition")?.Value is Core.UpdateEdition.Lite ? Core.UpdateEdition.Lite : Core.UpdateEdition.Full;
    public async Task<GitHubReleaseInfo?> GetAvailableUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        cancellationToken = timeout.Token;
        using var response = await HttpClient.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadAvailableUpdateAsync(stream, CurrentVersion, cancellationToken);
    }
    internal static async Task<GitHubReleaseInfo?> ReadAvailableUpdateAsync(Stream stream, Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(block, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes) throw new InvalidOperationException("GitHub's release response exceeded the size limit.");
            buffer.Write(block, 0, read);
        }
        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        return ParseAvailableUpdate(document.RootElement, currentVersion);
    }
    internal static GitHubReleaseInfo? ParseAvailableUpdate(JsonElement root, Version currentVersion)
    {
        var tag = GetRequiredString(root, "tag_name").Trim();
        var pageUrl = GetRequiredString(root, "html_url");
        if (!TryParseVersionTag(tag, out var releaseVersion)) throw new InvalidOperationException("GitHub returned an unsupported release tag.");
        const string releasePath = "/cyrusthelittle/classic-windows-iptv-player/releases/tag/";
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) || pageUri.Scheme != Uri.UriSchemeHttps ||
            !pageUri.IsDefaultPort || pageUri.UserInfo.Length != 0 || !string.Equals(pageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !pageUri.AbsolutePath.StartsWith(releasePath, StringComparison.Ordinal) ||
            Uri.UnescapeDataString(pageUri.AbsolutePath[releasePath.Length..]) != tag || pageUri.Query.Length != 0 || pageUri.Fragment.Length != 0)
            throw new InvalidOperationException("GitHub returned an invalid release page URL.");
        if (releaseVersion <= NormalizeVersion(currentVersion)) return null;
        var name = BoundText(GetOptionalString(root, "name"), 256);
        var notes = BoundText(GetOptionalString(root, "body"), MaximumNotesCharacters);
        DateTimeOffset? publishedAt = null;
        if (DateTimeOffset.TryParse(GetOptionalString(root, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate)) publishedAt = parsedDate;
        return new GitHubReleaseInfo(releaseVersion, tag, name ?? $"Version {tag}", pageUri, notes, publishedAt);
    }
    // API text is displayed literally; no Markdown/HTML or remote media are rendered.
    private static string? BoundText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length <= maximum) return text;
        var length = maximum;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "\n\n[Truncated. Open this release on GitHub to read the complete notes.]";
    }
    internal static bool TryParseVersionTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var suffixIndex = value.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0) value = value[..suffixIndex];
        if (!Version.TryParse(value, out var parsed)) return false;
        version = NormalizeVersion(parsed); return true;
    }
    private static Version NormalizeVersion(Version version) => new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private static string? GetOptionalString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static string GetRequiredString(JsonElement root, string name)
    {
        var value = GetOptionalString(root, name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"GitHub's release response did not contain {name}.");
        return value;
    }
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Classic-Windows-IPTV-Player", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28"); return client;
    }
}
