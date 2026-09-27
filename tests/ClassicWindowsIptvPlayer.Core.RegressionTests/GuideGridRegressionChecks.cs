using System.Text;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;

internal static class GuideGridRegressionChecks
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
        var channel = new Channel { Id = "invented-live", Name = "Invented Live", EpgId = "epg-live", MediaKind = MediaKind.Live };
        const string xml = "<tv><channel id='epg-live'><display-name>Invented Live</display-name></channel>" +
            "<programme channel='epg-live' start='20261025020000 +0200' stop='20261025023000 +0200'><title>First 02:00</title><desc>Invented fall programme</desc></programme>" +
            "<programme channel='epg-live' start='20261025020000 +0100' stop='20261025023000 +0100'><title>Second 02:00</title></programme>" +
            "<programme channel='epg-live' start='20261101090000 +0100' stop='20261101100000 +0100'><title>Later date</title></programme></tv>";
        async Task<EpgGuide> Guide()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            return await EpgService.ParseAsync(stream, [channel], new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
        }
        await Check("grid window respects repeated DST hour and end boundary", async () =>
        {
            var guide = await Guide();
            var first = guide.GetProgrammes(channel, new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero));
            var second = guide.GetProgrammes(channel, new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero));
            Equal("First 02:00", first.Single().Title);
            Equal("Second 02:00", second.Single().Title);
        });
        await Check("later date survives parsing and account correction", async () =>
        {
            var guide = await Guide();
            Equal(3, guide.ProgrammeCount);
            Equal("Later date", guide.GetProgrammes(channel,
                new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), offsetMinutes: 30).Single().Title);
            Equal(new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero),
                guide.GetProgrammes(channel, new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), offsetMinutes: 30).Single().Start);
        });
        await Check("two-hour absolute navigation covers both fallback occurrences", async () =>
        {
            var guide = await Guide();
            var windowStart = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
            var visible = guide.GetProgrammes(channel, windowStart, windowStart.AddHours(2));
            Equal(2, visible.Count);
            Equal("First 02:00", visible[0].Title);
            Equal("Second 02:00", visible[1].Title);
            Equal(0, guide.GetProgrammes(channel, windowStart.AddHours(2), windowStart.AddHours(4)).Count);
        });
        await Check("programme search fields remain available in grid query", async () =>
        {
            var guide = await Guide();
            Equal("First 02:00", guide.SearchProgrammes(channel, "FALL").Single().Title);
            Equal("Later date", guide.SearchProgrammes(channel, "later").Single().Title);
            Equal(0, guide.SearchProgrammes(channel, "missing").Count);
        });
        await Check("virtual row source creates only requested channel rows", () =>
        {
            var created = 0;
            var rows = new LazyList<int>(100_001, index => { created++; return index; });
            Equal(0, created);
            Equal(100_000, rows[100_000]);
            Equal(1, created);
            Equal(0, rows[0]);
            Equal(2, created);
            return Task.CompletedTask;
        });
        await Check("broad search indexes 100,001 matches without creating result rows", () =>
        {
            var start = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
            var programmes = Enumerable.Range(0, 100_001).Select(index =>
                new EpgProgramme("epg-live", "Synthetic Match " + index, "", "", start.AddMinutes(index),
                    start.AddMinutes(index + 1))).ToArray();
            var guide = new EpgGuide(new Dictionary<string, IReadOnlyList<EpgProgramme>>
                { ["epg-live"] = programmes });
            var created = 0;
            var rows = GuideSearchRows.Create(guide, [channel], "match", _ => null, 0,
                (_, programme) => { created++; return programme.Title; });
            Equal(100_001, rows.Count);
            Equal(0, created);
            Equal("Synthetic Match 100000", rows[100_000]);
            Equal(1, created);
            Equal("Synthetic Match 0", rows[0]);
            Equal(2, created);
            return Task.CompletedTask;
        });
        Console.WriteLine($"Guide grid: {6 - failures}/6 checks passed; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}");
    }
}
