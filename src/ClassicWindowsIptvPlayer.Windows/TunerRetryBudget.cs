using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>Tracks the retry budget for one tune cycle, independently of native playback.</summary>
internal sealed class TunerRetryBudget(int maxAttempts)
{
    private static readonly TimeSpan HealthyPlayThreshold = TimeSpan.FromSeconds(30);
    private static readonly int[] RetryDelaysMs = [250, 300, 400, 500];
    private int _attempt;

    internal bool CanAttempt => _attempt < maxAttempts;

    internal int BeginAttempt()
    {
        if (!CanAttempt) throw new InvalidOperationException("The retry budget is exhausted.");
        return ++_attempt;
    }

    /// <summary>Returns null when the budget is exhausted; a healthy stream starts a new budget.</summary>
    internal int? NextDelayMs(TimeSpan? playedFor)
    {
        if (playedFor >= HealthyPlayThreshold) _attempt = 0;
        if (!CanAttempt) return null;

        // The first delay belongs to the failed attempt. A healthy stream resets
        // the count to zero, but the first delay must still use index zero.
        var index = Math.Clamp(_attempt - 1, 0, RetryDelaysMs.Length - 1);
        return RetryDelaysMs[index];
    }

    /// <summary>Stop and superseding Play cancel the token; the generation check rejects stale work.</summary>
    internal static async Task<bool> WaitForRetryAsync(int delayMs, CancellationToken cancellationToken, Func<bool> isCurrent)
    {
        if (cancellationToken.IsCancellationRequested || !isCurrent()) return false;
        try
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        return !cancellationToken.IsCancellationRequested && isCurrent();
    }
}
