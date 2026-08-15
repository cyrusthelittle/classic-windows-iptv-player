using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassicWindowsIptvPlayer.Windows;

public sealed record GitHubReleaseInfo(
    Version Version,
    string DisplayVersion,
    string Name,
    Uri PageUri);

public sealed class GitHubUpdateService
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/cyrusthelittle/classic-windows-iptv-player/releases/latest";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public Version CurrentVersion => NormalizeVersion(
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0));

    public async Task<GitHubReleaseInfo?> GetAvailableUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await HttpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tag = GetRequiredString(root, "tag_name");
        var pageUrl = GetRequiredString(root, "html_url");
        if (!TryParseVersionTag(tag, out var releaseVersion))
        {
            throw new InvalidOperationException($"GitHub returned an unsupported release tag: {tag}");
        }

        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) ||
            pageUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(pageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GitHub returned an invalid release page URL.");
        }

        if (releaseVersion <= CurrentVersion) return null;

        var name = root.TryGetProperty("name", out var nameProperty)
            ? nameProperty.GetString()
            : null;

        return new GitHubReleaseInfo(
            releaseVersion,
            tag.Trim(),
            string.IsNullOrWhiteSpace(name) ? $"Version {tag.Trim()}" : name.Trim(),
            pageUri);
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

        version = NormalizeVersion(parsed);
        return true;
    }

    private static Version NormalizeVersion(Version version) => new(
        version.Major,
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException($"GitHub's release response did not contain {propertyName}.");
        }

        return property.GetString()!;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "Classic-Windows-IPTV-Player",
            "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
