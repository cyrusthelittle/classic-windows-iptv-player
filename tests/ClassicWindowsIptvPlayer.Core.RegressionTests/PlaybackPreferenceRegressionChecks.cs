using ClassicWindowsIptvPlayer.Core;

internal static class PlaybackPreferenceRegressionChecks
{
    public static int Run()
    {
        (int Id, string Name)[] audio = [(-1, "Disable"), (3, "Deutsch"), (7, "English (eng)")];
        (int Id, string Name)[] subtitles = [(5, "English"), (8, "French [forced]")];
        var checks = new (string, bool)[]
        {
            ("preferred language selects advertised audio track", PlaybackPreferencePolicy.FindLanguageTrack("en", audio) == 7),
            ("localized audio language name matches preference", PlaybackPreferencePolicy.FindLanguageTrack("de", audio) == 3),
            ("unmatched language retains stream default", PlaybackPreferencePolicy.FindLanguageTrack("ja", audio) is null),
            ("blank preference retains stream default", PlaybackPreferencePolicy.FindLanguageTrack("", audio) is null),
            ("disabled tracks never match a preference", PlaybackPreferencePolicy.FindLanguageTrack("Disable", audio) is null),
            ("subtitle language matches name", PlaybackPreferencePolicy.FindLanguageTrack("fr", subtitles) == 8),
            ("audio choices require multiple actual tracks", !PlaybackPreferencePolicy.HasAudioChoices(1) && PlaybackPreferencePolicy.HasAudioChoices(2)),
            ("subtitle controls require subtitle stream", !PlaybackPreferencePolicy.HasSubtitles(0) && PlaybackPreferencePolicy.HasSubtitles(1)),
            ("video options require video stream", !PlaybackPreferencePolicy.HasVideo(0) && PlaybackPreferencePolicy.HasVideo(1)),
            ("VOD speed requires seekable movie or episode video", PlaybackPreferencePolicy.HasVodSpeed(MediaKind.Movie, true, 1) && PlaybackPreferencePolicy.HasVodSpeed(MediaKind.Series, true, 1) && !PlaybackPreferencePolicy.HasVodSpeed(MediaKind.Live, true, 1) && !PlaybackPreferencePolicy.HasVodSpeed(MediaKind.Movie, false, 1) && !PlaybackPreferencePolicy.HasVodSpeed(MediaKind.Movie, true, 0)),
            ("delay uses bounded milliseconds to native microseconds", PlaybackPreferencePolicy.DelayMicroseconds(-250) == -250000 && PlaybackPreferencePolicy.DelayMicroseconds(6000) == 5000000),
            ("saved language preferences survive state serialization", SavedPreferenceRoundTrip()),
        };
        foreach (var (name, passed) in checks) Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
        Console.WriteLine($"Playback preferences: {checks.Count(x => x.Item2)}/{checks.Length} passed.");
        return checks.All(x => x.Item2) ? 0 : 1;
    }

    private static bool SavedPreferenceRoundTrip()
    {
        var state = new AppState { PreferredAudioLanguage = "de", PreferredSubtitleLanguage = "en" };
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppState>(json);
        return restored?.PreferredAudioLanguage == "de" && restored.PreferredSubtitleLanguage == "en";
    }
}
