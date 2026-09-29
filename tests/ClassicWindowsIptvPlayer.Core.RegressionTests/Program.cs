using System.Globalization;
using System.Text;
using ClassicWindowsIptvPlayer.Core;

var epgResult = await EpgRegressionChecks.RunAsync();
var tunerResult = await TunerRetryRegressionChecks.RunAsync();
var diagnosticsResult = await DiagnosticRedactionRegressionChecks.RunAsync();
var playbackBrowseResult = PlaybackBrowseRegressionChecks.Run();
var storageResult = StorageRegressionChecks.Run();
var accountEditingResult = AccountEditingRegressionChecks.Run();
var providerResult = await ProviderRegressionChecks.RunAsync();
var playbackMechanicsResult = PlaybackMechanicsRegressionChecks.Run();
var libraryNavigationResult = LibraryNavigationRegressionChecks.Run();
var largeLibraryResult = LargeLibraryRegressionChecks.Run();
var guideFoundationResult = await GuideFoundationRegressionChecks.RunAsync();
var guideGridResult = await GuideGridRegressionChecks.RunAsync();
var viewingHistoryResult = ViewingHistoryRegressionChecks.Run();
var playbackPreferenceResult = PlaybackPreferenceRegressionChecks.Run();
var videoOwnershipResult = VideoHostOwnershipRegressionChecks.Run();
var organizationResult = OrganizationRegressionChecks.Run();
var catchupResult = await CatchupRegressionChecks.RunAsync();
var recordingResult = RecordingRegressionChecks.Run();
var sharedHlsResult = SharedHlsSourceRegressionChecks.Run();
var scheduledRecordingResult = ScheduledRecordingRegressionChecks.Run();
var feedbackOutboxResult = await FeedbackOutboxRegressionChecks.RunAsync();
var crashSessionMarkerResult = CrashSessionMarkerRegressionChecks.Run();
return epgResult == 0 && tunerResult == 0 && diagnosticsResult == 0 && playbackBrowseResult == 0 && storageResult == 0 && accountEditingResult == 0 && providerResult == 0 && playbackMechanicsResult == 0 && libraryNavigationResult == 0 && largeLibraryResult == 0 && guideFoundationResult == 0 && guideGridResult == 0 && viewingHistoryResult == 0 && playbackPreferenceResult == 0 && videoOwnershipResult == 0 && organizationResult == 0 && catchupResult == 0 && recordingResult == 0 && sharedHlsResult == 0 && scheduledRecordingResult == 0 && feedbackOutboxResult == 0 && crashSessionMarkerResult == 0 ? 0 : 1;

internal static class EpgRegressionChecks
{
    private static readonly DateTimeOffset At = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel News = new() { Name = "Sample News", EpgId = "sample" };
    private static readonly List<(string Name, Func<Task> Check)> Checks = [];

    public static async Task<int> RunAsync()
    {
        Add("compact adjacent title, description and category", async () =>
        {
            var now = Current(await Parse(Programme("<title>Title</title><desc>Description</desc><category>News</category>")));
            Equal("Title", now.Title);
            Equal("Description", now.Description);
            Equal("News", now.Category);
        });

        Add("formatted XML preserves all text fields", async () =>
        {
            var now = Current(await Parse(Programme("\n  <title> Title </title>\n  <desc> Description &amp; details </desc>\n  <category> News </category>\n")));
            Equal("Title", now.Title);
            Equal("Description & details", now.Description);
            Equal("News", now.Category);
        });

        Add("repeated fields keep first nonempty value", async () =>
        {
            var now = Current(await Parse(Programme("<title/><title> </title><title lang='en'>First</title><title lang='de'>Second</title><desc/><desc>Details</desc><desc>Other</desc><category/><category>News</category><category>Other</category>")));
            Equal("First", now.Title);
            Equal("Details", now.Description);
            Equal("News", now.Category);
        });

        Add("unknown and empty elements do not consume following fields", async () =>
        {
            var now = Current(await Parse(Programme("<credits><title>Wrong nested title</title><desc>Wrong nested description</desc></credits><icon/><title>Right</title><!-- comment --><unknown/><desc><![CDATA[Details <safe>]]></desc><category>News</category>")));
            Equal("Right", now.Title);
            Equal("Details <safe>", now.Description);
            Equal("News", now.Category);
        });

        Add("empty programme fields use title fallback", async () =>
        {
            var now = Current(await Parse(Programme("<title/><desc/><category/>")));
            Equal("Untitled programme", now.Title);
            Equal("", now.Description);
            Equal("", now.Category);
        });

        Add("second display-name aliases match regardless of whitespace and case", async () =>
        {
            var channel = new Channel { Name = " sample news ", EpgId = "missing-playlist-id" };
            var guide = await Parse("<channel id='provider'><display-name>Unmatched</display-name><display-name>Sample News</display-name></channel>" +
                Programme("<title>Alias programme</title>", channelId: "provider"), [channel]);
            Equal("Alias programme", guide.GetNowNext(channel, At).Now?.Title);
            Equal(1, guide.ProgrammeCount);
        });

        Add("all matching aliases and exact ID share one programme", async () =>
        {
            Channel[] channels = [new() { Name = "English" }, new() { Name = "Deutsch" }, new() { Name = "Exact", EpgId = "provider" }];
            var guide = await Parse("<channel id='provider'><display-name/><display-name>English</display-name><icon/><display-name>Deutsch</display-name></channel>" +
                Programme("<title>Shared programme</title>", channelId: "provider"), channels);
            foreach (var channel in channels) Equal("Shared programme", guide.GetNowNext(channel, At).Now?.Title);
            Equal(1, guide.ProgrammeCount);
        });

        Add("unmatched channels and nested display names stay excluded", async () =>
        {
            var channel = new Channel { Name = "Sample News" };
            var guide = await Parse("<channel id='provider'><unknown><display-name>Sample News</display-name></unknown></channel>" +
                Programme("<title>Unmatched</title>", channelId: "provider"), [channel]);
            Equal(0, guide.ProgrammeCount);
        });

        foreach (var offset in new[] { TimeSpan.Zero, TimeSpan.FromHours(2), TimeSpan.FromMinutes(-330), TimeSpan.FromMinutes(825), TimeSpan.FromHours(14), TimeSpan.FromHours(-14) })
        {
            var capturedOffset = offset;
            Add($"valid offset {offset} preserves instants and offset", async () =>
            {
                var now = Current(await Parse(Programme("<title>Zoned</title>",
                    Date(At.AddMinutes(-30), capturedOffset), Date(At.AddMinutes(30), capturedOffset))));
                Equal(At.AddMinutes(-30), now.Start);
                Equal(At.AddMinutes(30), now.Stop);
                Equal(capturedOffset, now.Start.Offset);
            });
        }

        foreach (var (zone, hours) in new[] { ("UTC", 0), ("GMT", 0), ("BST", 1), ("bst", 1) })
        {
            Add($"named timezone {zone} uses its documented offset", async () =>
            {
                var offset = TimeSpan.FromHours(hours);
                var start = Date(At.AddMinutes(-30), offset)[..14] + " " + zone;
                var stop = Date(At.AddMinutes(30), offset)[..14] + " " + zone;
                var now = Current(await Parse(Programme("<title>Named zone</title>", start, stop)));
                Equal(At.AddMinutes(-30), now.Start);
                Equal(At.AddMinutes(30), now.Stop);
                Equal(offset, now.Start.Offset);
            });
        }

        Add("missing timezone remains UTC and minute precision works", async () =>
        {
            var now = Current(await Parse(Programme("<title>UTC</title>", "202609201130", "202609201230")));
            Equal(At.AddMinutes(-30), now.Start);
            Equal(TimeSpan.Zero, now.Start.Offset);
        });

        Add("timestamp whitespace and negative zero offset are accepted", async () =>
        {
            var now = Current(await Parse(Programme("<title>Whitespace</title>", "  20260920113000   -0000  ", "20260920123000 +0000")));
            Equal(At.AddMinutes(-30), now.Start);
        });

        foreach (var zone in new[] { "+9900", "+1500", "-1500", "+1401", "-1401", "+0060", "+1261", "prefix+0100", "+0100suffix", "+01:00", "0100", "+0a00", "UNKNOWN", "+0100 extra" })
        {
            var capturedZone = zone;
            Add($"invalid timezone '{zone}' skips record and preserves next", () => BadRecordThenGood("20260920113000 " + capturedZone, "20260920123000 +0000"));
        }

        foreach (var value in new[] { "", "not-a-date", "20260230000000 +0000", "20260920256000 +0000", "00010101000000 +0100", "99991231235959 -0100" })
        {
            var capturedValue = value;
            Add($"invalid timestamp '{value}' skips record and preserves next", () => BadRecordThenGood(capturedValue, "20260920123000 +0000"));
        }

        Add("invalid stop timestamp skips record and preserves next", () => BadRecordThenGood("20260920113000 +0000", "99991231235959 -0100"));
        Add("inverted programme window is skipped", () => BadRecordThenGood("20260920123000 +0000", "20260920113000 +0000"));
        Add("zero duration programme is skipped", () => BadRecordThenGood("20260920120000 +0000", "20260920120000 +0000"));

        Add("now-next sorts programmes and respects exact stop boundary", async () =>
        {
            var guide = await Parse(
                Programme("<title>Next</title>", Date(At, TimeSpan.Zero), Date(At.AddHours(1), TimeSpan.Zero)) +
                Programme("<title>Previous</title>", Date(At.AddHours(-1), TimeSpan.Zero), Date(At, TimeSpan.Zero)) +
                Programme("<title>Later</title>", Date(At.AddHours(1), TimeSpan.Zero), Date(At.AddHours(2), TimeSpan.Zero)));
            var nowNext = guide.GetNowNext(News, At);
            Equal("Next", nowNext.Now?.Title);
            Equal("Later", nowNext.Next?.Title);
        });

        Add("guide retains all valid dates for date navigation", async () =>
        {
            var guide = await Parse(
                Programme("<title>Too old</title>", Date(At.AddHours(-8), TimeSpan.Zero), Date(At.AddHours(-7), TimeSpan.Zero)) +
                Programme("<title>Too new</title>", Date(At.AddDays(3), TimeSpan.Zero), Date(At.AddDays(3).AddHours(1), TimeSpan.Zero)) +
                Programme("<title>Other channel</title>", channelId: "unmatched") +
                Programme("<title>Retained</title>"));
            Equal(3, guide.ProgrammeCount);
            Equal("Retained", Current(guide).Title);
        });

        Add("already cancelled parse does not read the stream", async () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using var stream = new TrackingStream(Encoding.UTF8.GetBytes("<tv/>"));
            await Cancelled(() => EpgService.ParseAsync(stream, [News], At, cts.Token));
            Equal(0, stream.ReadCount);
        });

        Add("cancellation during streamed input is propagated", async () =>
        {
            using var cts = new CancellationTokenSource();
            using var stream = new TrackingStream(Encoding.UTF8.GetBytes("<tv>" + Programme("<title>Cancelled</title>") + "</tv>"), cts);
            await Cancelled(() => EpgService.ParseAsync(stream, [News], At, cts.Token));
            Equal(true, stream.ReadCount > 0);
        });

        Add("chunked stream preserves adjacent fields", async () =>
        {
            using var stream = new TrackingStream(Encoding.UTF8.GetBytes("<tv>" + Programme("<title>Streamed</title><desc>Details</desc><category>News</category>") + "</tv>"), maxChunk: 7);
            var guide = await EpgService.ParseAsync(stream, [News], At, CancellationToken.None);
            Equal("Details", Current(guide).Description);
            Equal("News", Current(guide).Category);
            Equal(true, stream.ReadCount > 10);
        });

        var failures = 0;
        foreach (var (name, check) in Checks)
        {
            try
            {
                await check();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }
        Console.WriteLine($"{Checks.Count - failures}/{Checks.Count} checks passed; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Add(string name, Func<Task> check) => Checks.Add((name, check));

    private static async Task<EpgGuide> Parse(string content, IReadOnlyList<Channel>? channels = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<tv>" + content + "</tv>"));
        return await EpgService.ParseAsync(stream, channels ?? [News], At, CancellationToken.None);
    }

    private static string Programme(string content, string? start = null, string? stop = null, string channelId = "sample") =>
        $"<programme channel='{channelId}' start='{start ?? Date(At.AddMinutes(-30), TimeSpan.Zero)}' stop='{stop ?? Date(At.AddMinutes(30), TimeSpan.Zero)}'>{content}</programme>";

    private static string Date(DateTimeOffset instant, TimeSpan offset) =>
        instant.ToOffset(offset).ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture).Replace(":", "");

    private static EpgProgramme Current(EpgGuide guide) =>
        guide.GetNowNext(News, At).Now ?? throw new InvalidOperationException("Expected a current programme.");

    private static async Task BadRecordThenGood(string start, string stop)
    {
        var guide = await Parse(Programme("<title>Bad</title>", start, stop) + Programme("<title>Good</title><desc>Still present</desc>"));
        Equal(1, guide.ProgrammeCount);
        Equal("Good", Current(guide).Title);
        Equal("Still present", Current(guide).Description);
    }

    private static async Task Cancelled(Func<Task<EpgGuide>> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected OperationCanceledException.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

internal sealed class TrackingStream(byte[] data, CancellationTokenSource? cancelOnRead = null, int maxChunk = 32) : MemoryStream(data)
{
    public int ReadCount { get; private set; }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ReadCount++;
        var result = base.Read(buffer, offset, Math.Min(count, maxChunk));
        cancelOnRead?.Cancel();
        return Task.FromResult(result);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        var result = base.Read(buffer.Span[..Math.Min(buffer.Length, maxChunk)]);
        cancelOnRead?.Cancel();
        return ValueTask.FromResult(result);
    }
}
