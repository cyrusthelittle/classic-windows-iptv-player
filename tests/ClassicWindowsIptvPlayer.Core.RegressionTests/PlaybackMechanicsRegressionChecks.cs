using ClassicWindowsIptvPlayer.Windows;

internal static class PlaybackMechanicsRegressionChecks
{
    public static int Run()
    {
        var checks = new (string Name, Action Check)[]
        {
            ("mouse and keyboard use the same clamped seek time", () =>
            {
                Equal(60_000L, PlaybackSeekMath.TimeForSlider(120_000, 500));
                Equal(0L, PlaybackSeekMath.TimeForSlider(120_000, -1));
                Equal(120_000L, PlaybackSeekMath.TimeForSlider(120_000, 1001));
            }),
            ("VOD error resumes near last confirmed position", () =>
            {
                var position = new VodRecoveryPosition();
                position.Observe(46_000, 120_000);
                Equal(45_250L, position.RecoveryTargetMs);
            }),
            ("backward seek replaces the previous confirmed point", () =>
            {
                var position = new VodRecoveryPosition();
                position.Observe(90_000, 120_000);
                position.Observe(20_000, 120_000);
                Equal(19_250L, position.RecoveryTargetMs);
            }),
            ("invalid or unknown positions cannot replace a confirmed point", () =>
            {
                var position = new VodRecoveryPosition();
                position.Observe(40_000, 120_000);
                position.Observe(-1, 120_000);
                position.Observe(0, -1);
                position.Observe(200_000, 120_000);
                Equal(39_250L, position.RecoveryTargetMs);
            }),
            ("a new VOD request has no inherited resume position", () =>
            {
                var previous = new VodRecoveryPosition();
                previous.Observe(40_000, 120_000);
                Equal(null, new VodRecoveryPosition().RecoveryTargetMs);
            }),
            ("normal VOD completion ends without recovery", () =>
            {
                Equal(true, PlaybackCompletionPolicy.IsNormalVodCompletion(false, true));
                Equal(false, PlaybackCompletionPolicy.IsNormalVodCompletion(false, false));
                Equal(false, PlaybackCompletionPolicy.IsNormalVodCompletion(true, true));
            })
        };
        var failures = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} playback mechanics checks passed; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
