using System;
using System.Collections.Generic;

namespace ClassicWindowsIptvPlayer.Windows;

internal enum StallFallback
{
    None,
    /// <summary>4K/UHD: lighter decoding, plus software decoding if a GPU decoder was in use.</summary>
    FastDecode,
    /// <summary>Reopen without the CPU "adjust" filter (brightness, contrast, ...).</summary>
    WithoutAdjustments,
    /// <summary>Reopen without the CPU picture enhancements (Step 36).</summary>
    WithoutEnhancements,
    /// <summary>Reopen without the local live pause buffer.</summary>
    Direct,
    /// <summary>Nothing left to try for a 4K channel: tell the user once.</summary>
    WarnUltraHd
}

internal readonly record struct StallDecision(StallFallback Action, bool AdjustmentsSuspended, bool EnhancementsSuspended);

/// <summary>
/// Remembers, per stream URL for this app session, which fallbacks a frozen picture has
/// already used, and picks the next one. Not thread-safe: the tuner calls it under its lock.
/// </summary>
internal sealed class StallFallbackPolicy
{
    private readonly HashSet<string> _fastDecode = new(StringComparer.Ordinal);
    private readonly HashSet<string> _softwareDecode = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unadjusted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unenhanced = new(StringComparer.Ordinal);
    private readonly HashSet<string> _direct = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedUltraHd = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lowResolution = new(StringComparer.Ordinal);

    /// <summary>Forgets which channels dropped enhancements, so changed picture settings get a fresh try.</summary>
    public void ResetEnhancementFallbacks()
    {
        _unenhanced.Clear();
        _lowResolution.Clear();
    }

    public bool UsesFastDecode(string url) => _fastDecode.Contains(url);
    public bool UsesSoftwareDecode(string url) => _softwareDecode.Contains(url);
    public bool IsUnadjusted(string url) => _unadjusted.Contains(url);
    public bool IsUnenhanced(string url) => _unenhanced.Contains(url);
    public bool IsDirectOnly(string url) => _direct.Contains(url);

    public StallDecision Decide(string url, bool aboveFullHd, bool hardwareDecoderUsed,
        bool adjustmentsActive, bool enhancementsActive, bool remuxed)
    {
        // A 4K picture is usually more than a weak PC can decode: lighten decoding and
        // drop every CPU filter at the same time instead of one reopen per cause.
        if (aboveFullHd && _fastDecode.Add(url))
        {
            if (hardwareDecoderUsed) _softwareDecode.Add(url);
            var adjustments = adjustmentsActive && _unadjusted.Add(url);
            var enhancements = enhancementsActive && _unenhanced.Add(url);
            return new(StallFallback.FastDecode, adjustments, enhancements);
        }
        if (adjustmentsActive && _unadjusted.Add(url)) return new(StallFallback.WithoutAdjustments, true, false);
        if (enhancementsActive && _unenhanced.Add(url)) return new(StallFallback.WithoutEnhancements, false, true);
        if (remuxed && _direct.Add(url)) return new(StallFallback.Direct, false, false);
        if (aboveFullHd && _warnedUltraHd.Add(url)) return new(StallFallback.WarnUltraHd, false, false);
        return new(StallFallback.None, false, false);
    }

    /// <summary>Step 36/37: whether this attempt should use the LibVLC engine with CPU picture enhancements.</summary>
    public bool ShouldUseEnhancements(string url, bool enhancementsAvailable, bool onlyBelowFullHd) =>
        enhancementsAvailable && !_unenhanced.Contains(url) && (!onlyBelowFullHd || _lowResolution.Contains(url));

    /// <summary>Step 37: true once per URL when a channel below 1080p was opened without enhancements.</summary>
    public bool ShouldReopenWithEnhancements(string url, VideoFormatInfo format, bool enhancementsAvailable,
        bool onlyBelowFullHd, bool attemptUsedEnhancements, bool recording) =>
        enhancementsAvailable && onlyBelowFullHd && !attemptUsedEnhancements && !recording &&
        format.IsBelowFullHd && !_unenhanced.Contains(url) && _lowResolution.Add(url);
}
