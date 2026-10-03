using System.Text;
using System.Text.Json;
using ClassicWindowsIptvPlayer.Windows;

internal static class UpdateReleaseRegressionChecks
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var release = Parse("v2.3.4", "notes", "2026-10-03T10:13:17Z");
            Check(release?.Version == new Version(2, 3, 4, 0) && release.ReleaseNotes == "notes" &&
                release.PublishedAt == new DateTimeOffset(2026, 10, 3, 10, 13, 17, TimeSpan.Zero), "offered release owns its notes/date");
            Check(Parse("v1.0.0", "old", null) is null && Parse("v0.9.0", "old", null) is null, "same/older release ignored");
            using (var malformed = JsonDocument.Parse("{\"tag_name\":\"v2.0\",\"html_url\":\"https://github.com/cyrusthelittle/classic-windows-iptv-player/releases/tag/v2.0\",\"body\":{},\"name\":[],\"published_at\":15}"))
            {
                var fallback = GitHubUpdateService.ParseAvailableUpdate(malformed.RootElement, new Version(1, 0));
                Check(fallback?.ReleaseNotes is null && fallback?.PublishedAt is null && fallback?.Name == "Version v2.0", "malformed optional metadata fallback");
            }
            Check(Parse("v2.0", null, "not a date")?.ReleaseNotes is null && Parse("v2.0", " \n ", null)?.ReleaseNotes is null, "missing/blank notes and invalid date fallback");
            var longNotes = Parse("v2.0", new string('x', 15999) + "\uD83D\uDE00" + new string('y', 50000), null)!.ReleaseNotes!;
            Check(longNotes.Length < 16100 && longNotes.Contains("Truncated") && !longNotes.Contains("\uD83D\uDE00"), "bounded notes preserve surrogate boundary");
            const string literal = "<script>alert('x')</script>\n![image](https://example.test/image)\n[link](javascript:run())";
            Check(Parse("v2.0", literal, null)?.ReleaseNotes == literal, "HTML/Markdown preserved as literal text");
            foreach (var bad in new[] { "https://github.com/other/repo/releases/tag/v2.0", "https://github.com/cyrusthelittle/classic-windows-iptv-player/releases/tag/v3.0", "https://user@github.com/cyrusthelittle/classic-windows-iptv-player/releases/tag/v2.0", "http://github.com/cyrusthelittle/classic-windows-iptv-player/releases/tag/v2.0" })
            {
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { tag_name = "v2.0", html_url = bad }));
                Throws<InvalidOperationException>(() => GitHubUpdateService.ParseAvailableUpdate(document.RootElement, new Version(1, 0)));
            }
            foreach (var invalid in new[] { "null", "[]", "{\"tag_name\":1}", "{}" })
            {
                using var document = JsonDocument.Parse(invalid);
                Throws<InvalidOperationException>(() => GitHubUpdateService.ParseAvailableUpdate(document.RootElement, new Version(1, 0)));
            }
            await using (var oversized = new MemoryStream(new byte[GitHubUpdateService.MaximumResponseBytes + 1]))
                await ThrowsAsync<InvalidOperationException>(() => GitHubUpdateService.ReadAvailableUpdateAsync(oversized, new Version(1, 0)));
            await using (var invalidJson = new MemoryStream(Encoding.UTF8.GetBytes("broken")))
                await ThrowsAsync<JsonException>(() => GitHubUpdateService.ReadAvailableUpdateAsync(invalidJson, new Version(1, 0)));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await using (var data = new MemoryStream(Encoding.UTF8.GetBytes("{}")))
                await ThrowsAsync<OperationCanceledException>(() => GitHubUpdateService.ReadAvailableUpdateAsync(data, new Version(1, 0), canceled.Token));
            Console.WriteLine("PASS update release metadata: version identity, exact release URL, optional/malformed/literal/long notes, bounded response and cancellation");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static GitHubReleaseInfo? Parse(string tag, string? body, string? published)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { tag_name = tag, html_url = "https://github.com/cyrusthelittle/classic-windows-iptv-player/releases/tag/" + tag, body, published_at = published }));
        return GitHubUpdateService.ParseAvailableUpdate(document.RootElement, new Version(1, 0));
    }
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
