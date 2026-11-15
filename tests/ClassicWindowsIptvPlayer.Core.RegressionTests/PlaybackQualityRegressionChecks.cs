using System.Diagnostics;
using ClassicWindowsIptvPlayer.Windows;

internal static class PlaybackQualityRegressionChecks
{
    public static int Run()
    {
        var checks = new List<(string Name, Action Run)>
        {
            // Step 32
            ("bounded native call returns true when the call finishes", () =>
            {
                var ran = false;
                True(BoundedNativeCall.Run(() => ran = true, TimeSpan.FromSeconds(5)));
                True(ran);
            }),
            ("bounded native call gives up on a call that never returns", () =>
            {
                var never = new ManualResetEventSlim(false);
                var watch = Stopwatch.StartNew();
                True(!BoundedNativeCall.Run(() => never.Wait(), TimeSpan.FromMilliseconds(300)));
                True(watch.ElapsedMilliseconds < 3000, "Run waited too long: " + watch.ElapsedMilliseconds + " ms");
                never.Set();
            }),
            ("bounded native call reports an exception and still counts as finished", () =>
            {
                Exception? seen = null;
                True(BoundedNativeCall.Run(() => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5), ex => seen = ex));
                True(seen is InvalidOperationException);
            }),
            // Step 33
            ("4K HEVC Main 10 is above Full HD and likely 10-bit", () =>
            {
                var format = VideoFormatInfo.Create(FourCc("hevc"), 3840, 2160, 2, 50, 1)!;
                Equal("HEVC", format.Codec);
                True(format.IsAboveFullHd);
                True(!format.IsBelowFullHd);
                True(format.IsLikely10BitHevc);
                Equal("codec=HEVC; width=3840; height=2160; profile=2; fps=50", format.Describe());
            }),
            ("1080p and 1440x1080 are neither above nor below Full HD", () =>
            {
                var full = VideoFormatInfo.Create(FourCc("h264"), 1920, 1080, 100, 25, 1)!;
                True(!full.IsAboveFullHd && !full.IsBelowFullHd);
                var anamorphic = VideoFormatInfo.Create(FourCc("h264"), 1440, 1080, 100, 25, 1)!;
                True(!anamorphic.IsAboveFullHd && !anamorphic.IsBelowFullHd);
            }),
            ("720p and SD are below Full HD", () =>
            {
                True(VideoFormatInfo.Create(FourCc("h264"), 1280, 720, 100, 50, 1)!.IsBelowFullHd);
                True(VideoFormatInfo.Create(FourCc("mpgv"), 720, 576, 4, 25, 1)!.IsBelowFullHd);
            }),
            ("unknown size gives no format and a zero frame rate denominator gives 0 fps", () =>
            {
                Equal<VideoFormatInfo?>(null, VideoFormatInfo.Create(FourCc("h264"), 0, 0, 0, 0, 0));
                Equal(0.0, VideoFormatInfo.Create(FourCc("h264"), 1920, 1080, 0, 25, 0)!.FramesPerSecond);
                Equal("UNKNOWN", VideoFormatInfo.FourCcToText(0));
            }),
            ("repeated log lines are dropped inside the window and counted", () =>
            {
                var filter = new RepeatedLogFilter(windowMs: 1000);
                True(filter.ShouldLog("a", 0, out var first));
                Equal(0, first);
                True(!filter.ShouldLog("a", 100, out _));
                True(!filter.ShouldLog("a", 200, out _));
                True(filter.ShouldLog("b", 300, out var other));
                Equal(0, other);
                True(!filter.ShouldLog("a", 900, out _));
                True(filter.ShouldLog("a", 1100, out var later));
                Equal(3, later);
            }),
            // Step 34
            ("frozen 4K with a GPU decoder: fast software decoding first, then warn once", () =>
            {
                var policy = new StallFallbackPolicy();
                var first = policy.Decide("u", aboveFullHd: true, hardwareDecoderUsed: true, adjustmentsActive: false, enhancementsActive: false, remuxed: false);
                Equal(StallFallback.FastDecode, first.Action);
                True(policy.UsesFastDecode("u") && policy.UsesSoftwareDecode("u"));
                Equal(StallFallback.WarnUltraHd, policy.Decide("u", true, true, false, false, false).Action);
                Equal(StallFallback.None, policy.Decide("u", true, true, false, false, false).Action);
            }),
            ("frozen 4K with software decoding keeps software decoding and drops CPU filters at once", () =>
            {
                var policy = new StallFallbackPolicy();
                var first = policy.Decide("u", aboveFullHd: true, hardwareDecoderUsed: false, adjustmentsActive: true, enhancementsActive: true, remuxed: true);
                Equal(StallFallback.FastDecode, first.Action);
                True(first.AdjustmentsSuspended && first.EnhancementsSuspended);
                True(!policy.UsesSoftwareDecode("u"));
                True(policy.IsUnadjusted("u") && policy.IsUnenhanced("u"));
                Equal(StallFallback.Direct, policy.Decide("u", true, false, true, true, true).Action);
                Equal(StallFallback.WarnUltraHd, policy.Decide("u", true, false, true, true, true).Action);
            }),
            ("frozen 1080p keeps the old order: adjustments, enhancements, direct, then nothing", () =>
            {
                var policy = new StallFallbackPolicy();
                Equal(StallFallback.WithoutAdjustments, policy.Decide("u", false, false, true, true, true).Action);
                Equal(StallFallback.WithoutEnhancements, policy.Decide("u", false, false, true, true, true).Action);
                Equal(StallFallback.Direct, policy.Decide("u", false, false, true, true, true).Action);
                Equal(StallFallback.None, policy.Decide("u", false, false, true, true, true).Action);
                True(policy.IsDirectOnly("u"));
            }),
            ("fallbacks are remembered per URL", () =>
            {
                var policy = new StallFallbackPolicy();
                policy.Decide("a", false, false, true, false, false);
                True(policy.IsUnadjusted("a") && !policy.IsUnadjusted("b"));
            }),
            ("enhancement engine rules for all channels and below-1080p only", () =>
            {
                var policy = new StallFallbackPolicy();
                var sd = VideoFormatInfo.Create(FourCc("h264"), 720, 576, 0, 25, 1)!;
                var hd = VideoFormatInfo.Create(FourCc("h264"), 1920, 1080, 0, 25, 1)!;
                True(policy.ShouldUseEnhancements("sd", enhancementsAvailable: true, onlyBelowFullHd: false));
                True(!policy.ShouldUseEnhancements("sd", enhancementsAvailable: false, onlyBelowFullHd: false));
                True(!policy.ShouldUseEnhancements("sd", true, onlyBelowFullHd: true));
                True(!policy.ShouldReopenWithEnhancements("hd", hd, true, true, attemptUsedEnhancements: false, recording: false));
                True(!policy.ShouldReopenWithEnhancements("sd", sd, true, true, attemptUsedEnhancements: false, recording: true));
                True(policy.ShouldReopenWithEnhancements("sd", sd, true, true, attemptUsedEnhancements: false, recording: false));
                True(!policy.ShouldReopenWithEnhancements("sd", sd, true, true, attemptUsedEnhancements: false, recording: false));
                True(policy.ShouldUseEnhancements("sd", true, onlyBelowFullHd: true));
                policy.Decide("sd", false, false, false, enhancementsActive: true, remuxed: false);
                True(!policy.ShouldUseEnhancements("sd", true, onlyBelowFullHd: true));
            }),
            // Step 35
            ("default picture settings add no LibVLC options", () =>
            {
                var settings = Picture();
                Equal(0, PictureEnhancementOptions.BuildEngineArgs(settings).Count);
                Equal(0, PictureEnhancementOptions.BuildCpuFilterArgs(settings, AllFilters()).Count);
                True(!settings.AnyCpuFilter);
            }),
            ("unknown saved values fall back to defaults", () =>
            {
                var settings = PictureEnhancementOptions.Normalize("weird", null, false, 3, 9, false, -1, true);
                Equal("default", settings.DeinterlaceMode);
                Equal("linear", settings.UpscaleMode);
                Equal(0, settings.DeblockQuality);
                Equal(0, settings.DenoiseLevel);
                Equal(0, settings.SharpenLevel);
            }),
            ("deinterlace, upscaling and RTX HDR options", () =>
            {
                Equal("--deinterlace=0", string.Join(" ", PictureEnhancementOptions.BuildEngineArgs(Picture(deinterlace: "off"))));
                Equal("--deinterlace=-1 --deinterlace-mode=yadif2x", string.Join(" ", PictureEnhancementOptions.BuildEngineArgs(Picture(deinterlace: "yadif2x"))));
                Equal("--d3d11-upscale-mode=super", string.Join(" ", PictureEnhancementOptions.BuildEngineArgs(Picture(upscale: "super"))));
                Equal("--d3d11-upscale-mode=processor --d3d11-hdr-mode=generate",
                    string.Join(" ", PictureEnhancementOptions.BuildEngineArgs(Picture(upscale: "processor", rtxHdr: true))));
            }),
            ("CPU filters run in a fixed order with invariant numbers", () =>
            {
                var previous = System.Globalization.CultureInfo.CurrentCulture;
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                try
                {
                    var args = PictureEnhancementOptions.BuildCpuFilterArgs(Picture(deblock: 4, denoise: 1, deband: true, sharpen: 2), AllFilters());
                    Equal("--video-filter=postproc:hqdn3d:gradfun:sharpen --postproc-q=4 --hqdn3d-luma-spat=2 --hqdn3d-chroma-spat=1.5 " +
                          "--hqdn3d-luma-temp=3 --hqdn3d-chroma-temp=2.25 --gradfun-strength=1.2 --gradfun-radius=16 --sharpen-sigma=0.6",
                        string.Join(" ", args));
                }
                finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
            }),
            ("missing CPU filters are skipped", () =>
            {
                var withoutPostproc = new HashSet<string>(new[] { "sharpen" }, StringComparer.OrdinalIgnoreCase);
                Equal("--video-filter=sharpen --sharpen-sigma=1",
                    string.Join(" ", PictureEnhancementOptions.BuildCpuFilterArgs(Picture(deblock: 6, sharpen: 3), withoutPostproc)));
                Equal(0, PictureEnhancementOptions.BuildCpuFilterArgs(Picture(deblock: 6), withoutPostproc).Count);
            }),
            // Step 36
            ("any CPU filter marks the settings as needing the enhancement engine", () =>
            {
                True(Picture(deblock: 2).AnyCpuFilter);
                True(Picture(denoise: 1).AnyCpuFilter);
                True(Picture(deband: true).AnyCpuFilter);
                True(Picture(sharpen: 1).AnyCpuFilter);
                True(!Picture(deinterlace: "yadif", upscale: "super", rtxHdr: true).AnyCpuFilter);
            }),
            // Step 37
            ("a channel reopened with enhancements that then freezes falls back for good", () =>
            {
                var policy = new StallFallbackPolicy();
                var sd = VideoFormatInfo.Create(FourCc("h264"), 1024, 576, 0, 25, 1)!;
                True(policy.ShouldReopenWithEnhancements("sd", sd, true, true, false, false));
                True(policy.ShouldUseEnhancements("sd", true, true));
                Equal(StallFallback.WithoutEnhancements, policy.Decide("sd", false, false, false, enhancementsActive: true, remuxed: false).Action);
                True(!policy.ShouldUseEnhancements("sd", true, true));
                True(!policy.ShouldReopenWithEnhancements("sd", sd, true, true, false, false));
            }),
        };

        var failures = 0;
        foreach (var (name, run) in checks)
        {
            try
            {
                run();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
            }
        }
        Console.WriteLine($"{checks.Count - failures}/{checks.Count} playback quality checks passed; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void True(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "Expected true.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected <{expected}>, got <{actual}>.");
    }

    private static uint FourCc(string text) =>
        (uint)text[0] | ((uint)text[1] << 8) | ((uint)text[2] << 16) | ((uint)text[3] << 24);

    private static PictureEnhancementSettings Picture(string deinterlace = "default", string upscale = "linear", bool rtxHdr = false,
        int deblock = 0, int denoise = 0, bool deband = false, int sharpen = 0, bool onlyBelowFullHd = true) =>
        PictureEnhancementOptions.Normalize(deinterlace, upscale, rtxHdr, deblock, denoise, deband, sharpen, onlyBelowFullHd);

    private static HashSet<string> AllFilters() =>
        new(PictureEnhancementOptions.CpuFilterModules, StringComparer.OrdinalIgnoreCase);
}
