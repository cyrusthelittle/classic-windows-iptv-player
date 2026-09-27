namespace ClassicWindowsIptvPlayer.Core;

public static class PlaybackPreferencePolicy
{
    public static int? FindLanguageTrack(string preference, IEnumerable<(int Id, string Name)> tracks)
    {
        var wanted = ExpandLanguage(Normalize(preference));
        if (wanted.Length == 0) return null;
        foreach (var (id, name) in tracks)
        {
            if (id < 0) continue;
            var candidate = ExpandLanguage(Normalize(name));
            if (candidate == wanted || candidate.StartsWith(wanted + "-", StringComparison.Ordinal) ||
                candidate.StartsWith(wanted + " ", StringComparison.Ordinal) ||
                candidate.Contains("(" + wanted + ")", StringComparison.Ordinal)) return id;
        }
        return null;
    }

    public static bool HasAudioChoices(int audioTracks) => audioTracks > 1;
    public static bool HasSubtitles(int subtitleTracks) => subtitleTracks > 0;
    public static bool HasVideo(int videoTracks) => videoTracks > 0;
    public static bool HasVodSpeed(MediaKind kind, bool seekable, int videoTracks) =>
        kind is MediaKind.Movie or MediaKind.Series && seekable && videoTracks > 0;
    public static long DelayMicroseconds(int milliseconds) => Math.Clamp(milliseconds, -5000, 5000) * 1000L;

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');

    private static string ExpandLanguage(string value) => value switch
    {
        "en" or "eng" => "english", "de" or "deu" or "ger" or "deutsch" => "german",
        "fr" or "fra" or "fre" => "french", "es" or "spa" => "spanish",
        "it" or "ita" => "italian", "pt" or "por" => "portuguese",
        "ja" or "jpn" => "japanese", _ => value
    };
}
