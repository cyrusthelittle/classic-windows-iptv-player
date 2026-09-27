namespace ClassicWindowsIptvPlayer.Windows;

// One instance belongs to one tune request, across all of its native attempts.
internal sealed class VodRecoveryPosition
{
    public long LastConfirmedMs { get; private set; }

    public void Observe(long timeMs, long lengthMs)
    {
        if (lengthMs <= 0 || timeMs < 0 || timeMs > lengthMs) return;
        // The latest position matters: a deliberate backward seek must win too.
        LastConfirmedMs = timeMs;
    }

    public long? RecoveryTargetMs => LastConfirmedMs > 0 ? Math.Max(0, LastConfirmedMs - 750) : null;
}

internal static class PlaybackSeekMath
{
    public static long TimeForSlider(long lengthMs, double sliderValue) =>
        lengthMs <= 0 ? 0 : (long)(lengthMs * Math.Clamp(sliderValue, 0, 1000) / 1000d);
}

internal static class PlaybackCompletionPolicy
{
    public static bool IsNormalVodCompletion(bool isLive, bool endReached) => !isLive && endReached;
}
