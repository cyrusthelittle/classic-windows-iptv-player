using System.Text;
using ClassicWindowsIptvPlayer.Core;

internal static class GuideFoundationRegressionChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
        var at = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);
        var channel = new Channel { Id = "live-1", Name = "Invented channel", EpgId = "wrong" };
        const string xml = "<tv><channel id='guide-1'><display-name>Guide One</display-name></channel>" +
            "<programme channel='guide-1' start='20261025020000 +0200' stop='20261025020000 +0100'><title>Autumn crossover</title><desc>Synthetic detail</desc></programme>" +
            "<programme channel='guide-1' start='20261025020000 +0100' stop='20261025030000 +0100'><title>After fallback</title></programme></tv>";
        async Task<EpgGuide> Guide(CancellationToken token = default)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            return await EpgService.ParseAsync(stream, [channel], at, token, ["guide-1"]);
        }
        await Check("manual mapping and DST repeated hour use absolute instants", async () =>
        {
            var guide = await Guide();
            Equal(2, guide.ProgrammeCount);
            Equal(false, guide.HasMatch(channel));
            Equal(true, guide.HasMatch(channel, "guide-1"));
            Equal("Autumn crossover", guide.GetNowNext(channel, at.AddMinutes(-40), "guide-1").Now?.Title);
            Equal("After fallback", guide.GetNowNext(channel, at, "guide-1").Now?.Title);
            Equal("Synthetic detail", guide.GetNowNext(channel, at.AddMinutes(-40), "guide-1").Now?.Description);
            Equal("Guide One", guide.Channels.Single().Name);
        });
        await Check("time correction shifts both programme boundaries", async () =>
        {
            var guide = await Guide();
            Equal("Autumn crossover", guide.GetNowNext(channel, at.AddMinutes(-40), "guide-1", 30).Now?.Title);
            Equal("After fallback", guide.GetNowNext(channel, at.AddMinutes(30), "guide-1", 30).Now?.Title);
            Equal(at, guide.GetNowNext(channel, at.AddMinutes(30), "guide-1", 30).Now?.Start);
        });
        await Check("cached snapshot remains available after corrupt primary", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cyrus-guide-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new ConfigStore(root);
                var guide = await Guide();
                var first = guide.Snapshot(at);
                store.SaveGuideCache("invented-account", first);
                store.SaveGuideCache("invented-account", guide.Snapshot(at.AddMinutes(1)));
                File.WriteAllText(store.GetGuideCachePath("invented-account"), "broken");
                var restored = store.LoadGuideCache("invented-account") ?? throw new Exception("Missing cache");
                Equal(at, restored.FetchedAt);
                Equal("Autumn crossover", EpgGuide.FromSnapshot(restored).GetNowNext(channel, at.AddMinutes(-40), "guide-1").Now?.Title);
                Equal(true, store.RecoveryNotice is not null);
                Equal(false, File.ReadAllText(store.GetGuideCachePath("invented-account")).Contains("Autumn crossover"));
            }
            finally { Directory.Delete(root, recursive: true); }
        });
        await Check("guide parse honors cancellation with mappings", async () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try { await Guide(cts.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
        });
        Console.WriteLine($"Guide foundation: {4 - failures}/4 checks passed; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; got {actual}");
    }
}
