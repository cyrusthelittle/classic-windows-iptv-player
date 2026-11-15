using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>The saved picture-quality choices, validated by <see cref="PictureEnhancementOptions.Normalize"/>.</summary>
internal sealed record PictureEnhancementSettings(
    string DeinterlaceMode, string UpscaleMode, bool RtxHdr,
    int DeblockQuality, int DenoiseLevel, bool Deband, int SharpenLevel, bool OnlyBelowFullHd)
{
    public bool AnyCpuFilter => DeblockQuality > 0 || DenoiseLevel > 0 || Deband || SharpenLevel > 0;
}

/// <summary>Turns picture-quality settings into LibVLC start-up arguments. Pure; covered by regression checks.</summary>
internal static class PictureEnhancementOptions
{
    public static readonly string[] DeinterlaceModes = { "default", "off", "yadif", "yadif2x", "bob" };
    public static readonly string[] UpscaleModes = { "linear", "processor", "super" };
    public static readonly int[] DeblockQualities = { 0, 2, 4, 6 };
    /// <summary>LibVLC module names of the CPU filters, in the order they run.</summary>
    public static readonly string[] CpuFilterModules = { "postproc", "hqdn3d", "gradfun", "sharpen" };

    public static PictureEnhancementSettings Normalize(string? deinterlaceMode, string? upscaleMode, bool rtxHdr,
        int deblockQuality, int denoiseLevel, bool deband, int sharpenLevel, bool onlyBelowFullHd) => new(
        deinterlaceMode is not null && DeinterlaceModes.Contains(deinterlaceMode) ? deinterlaceMode : "default",
        upscaleMode is not null && UpscaleModes.Contains(upscaleMode) ? upscaleMode : "linear",
        rtxHdr,
        DeblockQualities.Contains(deblockQuality) ? deblockQuality : 0,
        denoiseLevel is >= 0 and <= 3 ? denoiseLevel : 0,
        deband,
        sharpenLevel is >= 0 and <= 3 ? sharpenLevel : 0,
        onlyBelowFullHd);

    /// <summary>Deinterlacing and Direct3D 11 output options. Cheap; applied to every channel.</summary>
    public static List<string> BuildEngineArgs(PictureEnhancementSettings settings)
    {
        var args = new List<string>();
        switch (settings.DeinterlaceMode)
        {
            case "off":
                args.Add("--deinterlace=0");
                break;
            case "yadif":
            case "yadif2x":
            case "bob":
                args.Add("--deinterlace=-1"); // only pictures flagged as interlaced
                args.Add("--deinterlace-mode=" + settings.DeinterlaceMode);
                break;
        }
        if (settings.UpscaleMode is "processor" or "super") args.Add("--d3d11-upscale-mode=" + settings.UpscaleMode);
        if (settings.RtxHdr) args.Add("--d3d11-hdr-mode=generate");
        return args;
    }

    /// <summary>CPU filter options, limited to the filters this LibVLC has. Empty when none apply.</summary>
    public static List<string> BuildCpuFilterArgs(PictureEnhancementSettings settings, IReadOnlySet<string> availableFilters)
    {
        var chain = new List<string>();
        var args = new List<string>();
        if (settings.DeblockQuality > 0 && availableFilters.Contains("postproc"))
        {
            chain.Add("postproc");
            args.Add("--postproc-q=" + settings.DeblockQuality.ToString(CultureInfo.InvariantCulture));
        }
        if (settings.DenoiseLevel > 0 && availableFilters.Contains("hqdn3d"))
        {
            // Level 2 is LibVLC's default strength; level 1 is half of it and level 3 is 1.5 times.
            var scale = settings.DenoiseLevel switch { 1 => 0.5, 2 => 1.0, _ => 1.5 };
            chain.Add("hqdn3d");
            args.Add("--hqdn3d-luma-spat=" + Format(4.0 * scale));
            args.Add("--hqdn3d-chroma-spat=" + Format(3.0 * scale));
            args.Add("--hqdn3d-luma-temp=" + Format(6.0 * scale));
            args.Add("--hqdn3d-chroma-temp=" + Format(4.5 * scale));
        }
        if (settings.Deband && availableFilters.Contains("gradfun"))
        {
            chain.Add("gradfun");
            args.Add("--gradfun-strength=1.2");
            args.Add("--gradfun-radius=16");
        }
        if (settings.SharpenLevel > 0 && availableFilters.Contains("sharpen"))
        {
            chain.Add("sharpen");
            args.Add("--sharpen-sigma=" + Format(settings.SharpenLevel switch { 1 => 0.3, 2 => 0.6, _ => 1.0 }));
        }
        if (chain.Count == 0) return new List<string>();
        args.Insert(0, "--video-filter=" + string.Join(":", chain));
        return args;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
