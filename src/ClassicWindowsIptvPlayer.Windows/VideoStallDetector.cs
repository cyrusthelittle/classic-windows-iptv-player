namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>
/// Recognizes the "frozen picture while audio keeps playing" failure. Streams
/// with a broken PCR/timestamp timeline can lose every video frame inside
/// LibVLC's clock (late or unconvertible timestamps) while audio stays healthy,
/// so the player never reports an error. Fed with LibVLC's per-media counters;
/// the caller resets it whenever playback is paused or the media is replaced.
/// </summary>
internal sealed class VideoStallDetector
{
    // LibVLC may still be prerolling the first pictures right after Playing.
    public const long GraceMs = 3000;
    public const long WindowMs = 6000;
    // A healthy video stream shows dozens of pictures per window; fewer than
    // this while audio advances means the picture is frozen.
    private const int MinDisplayedPerWindow = 2;

    private (long AtMs, int Displayed, int Audio)? _windowStart;

    public void Reset() => _windowStart = null;

    /// <summary>Returns true once audio advanced for a full window while no video was shown.</summary>
    public bool Observe(long playingForMs, bool hasVideo, int displayedPictures, int decodedAudio)
    {
        if (playingForMs < GraceMs || !hasVideo)
        {
            _windowStart = null;
            return false;
        }

        if (_windowStart is not { } start || displayedPictures < start.Displayed || decodedAudio < start.Audio || playingForMs < start.AtMs)
        {
            // First sample, or counters restarted because the media changed.
            _windowStart = (playingForMs, displayedPictures, decodedAudio);
            return false;
        }

        if (playingForMs - start.AtMs < WindowMs) return false;

        var stalled = decodedAudio > start.Audio && displayedPictures - start.Displayed < MinDisplayedPerWindow;
        _windowStart = (playingForMs, displayedPictures, decodedAudio);
        return stalled;
    }
}
