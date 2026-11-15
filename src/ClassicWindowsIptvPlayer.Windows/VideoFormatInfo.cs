using System;
using System.Globalization;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>The decoded video format of a stream, read from LibVLC's track list.</summary>
internal sealed record VideoFormatInfo(string Codec, int Width, int Height, int Profile, double FramesPerSecond)
{
    /// <summary>More pixels than 1920x1080: 1440p and 4K/UHD.</summary>
    public bool IsAboveFullHd => Width > 1920 || Height > 1080;

    /// <summary>SD and 720p. 1080-line pictures, including 1440x1080, are not below Full HD.</summary>
    public bool IsBelowFullHd => Height > 0 && Height < 1000;

    /// <summary>HEVC Main 10 (profile 2) needs 10-bit HEVC decoding hardware.</summary>
    public bool IsLikely10BitHevc => Codec == "HEVC" && Profile == 2;

    public static VideoFormatInfo? Create(uint fourCc, uint width, uint height, int profile, uint frameRateNum, uint frameRateDen)
    {
        if (width == 0 || height == 0 || width > 16384 || height > 16384) return null;
        var fps = frameRateDen == 0 ? 0 : Math.Round((double)frameRateNum / frameRateDen, 2);
        return new VideoFormatInfo(FourCcToText(fourCc), (int)width, (int)height, profile, fps);
    }

    public static string FourCcToText(uint fourCc)
    {
        Span<char> chars = stackalloc char[4];
        var length = 0;
        for (var i = 0; i < 4; i++)
        {
            var b = (byte)(fourCc >> (8 * i));
            if (b == 0) continue;
            chars[length++] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }
        var text = new string(chars[..length]).Trim().ToUpperInvariant();
        return text.Length == 0 ? "UNKNOWN" : text;
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"codec={Codec}; width={Width}; height={Height}; profile={Profile}; fps={FramesPerSecond:0.##}");
}
