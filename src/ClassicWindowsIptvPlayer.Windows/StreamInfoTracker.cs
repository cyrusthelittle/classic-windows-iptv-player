using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassicWindowsIptvPlayer.Windows;

public sealed class StreamInfoTracker
{
    private long? _lastReadBytes;
    private DateTime? _lastReadBytesUtc;
    private double? _lastMeasuredMbps;

    public StreamInfoSnapshot Build(MediaPlayer? mediaPlayer, Media? currentMedia, string channelName, string sourceLabel, string sourceMode, int bufferMs)
    {
        try
        {
            var stateText = mediaPlayer?.State.ToString() ?? "Not ready";
            var resolutionText = TryGetVideoResolutionText(mediaPlayer);
            var stats = TryReadPlayerStats(mediaPlayer, currentMedia);
            var trackBitrate = TryReadTrackBitrate(mediaPlayer, currentMedia);
            var measured = UpdateMeasuredBandwidth(stats.ReadBytes);
            var bandwidthText = FormatBandwidth(measured, stats.InputBitrate ?? stats.DemuxBitrate ?? trackBitrate);
            var fpsText = FormatFps(mediaPlayer?.Fps);
            var trackDetails = TryReadTrackDetails(mediaPlayer, currentMedia);
            var playing = mediaPlayer?.IsPlaying == true;
            var videoCodecText = trackDetails.VideoCodec ?? (playing ? "detecting" : "not playing");
            var audioCodecText = trackDetails.AudioCodec ?? (playing ? "detecting" : "not playing");
            var audioFormatText = FormatAudioFormat(trackDetails.AudioChannels, trackDetails.AudioRate) ?? (playing ? "detecting" : "not playing");
            var bufferText = $"{bufferMs:N0} ms";
            var shortText = $"{stateText} • {resolutionText} • {bandwidthText} • {sourceLabel}";
            var fullText =
                $"State: {stateText}   |   Resolution: {resolutionText}   |   Bandwidth: {bandwidthText}   |   FPS: {fpsText}\n" +
                $"Video codec: {videoCodecText}   |   Audio codec: {audioCodecText}   |   Audio: {audioFormatText}\n" +
                $"Buffer: {bufferText}   |   Source: {sourceMode} / {sourceLabel}   |   Channel: {(string.IsNullOrWhiteSpace(channelName) ? "none" : channelName)}";
            return new StreamInfoSnapshot(stateText, resolutionText, bandwidthText, fpsText, videoCodecText, audioCodecText, audioFormatText, bufferText, sourceLabel, fullText, shortText);
        }
        catch
        {
            return new StreamInfoSnapshot("Unknown", "detecting", "detecting", "detecting", "detecting", "detecting", "detecting", $"{bufferMs:N0} ms", sourceLabel, "Stream information is not available yet.", "Stream info unavailable");
        }
    }

    public void ResetBandwidth()
    {
        _lastReadBytes = null;
        _lastReadBytesUtc = null;
        _lastMeasuredMbps = null;
    }

    private static string TryGetVideoResolutionText(MediaPlayer? mediaPlayer)
    {
        if (mediaPlayer is null) return "not ready";

        try
        {
            foreach (var methodName in new[] { "Size", "GetVideoSize" })
            {
                var methods = mediaPlayer.GetType().GetMethods().Where(m => m.Name == methodName && m.GetParameters().Length == 3);
                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();
                    var args = new object?[] { Convert.ChangeType(0, parameters[0].ParameterType), null, null };
                    args[1] = parameters[1].ParameterType == typeof(int).MakeByRefType() ? 0 : 0u;
                    args[2] = parameters[2].ParameterType == typeof(int).MakeByRefType() ? 0 : 0u;

                    var result = method.Invoke(mediaPlayer, args);
                    var ok = result is bool boolResult ? boolResult : true;
                    var width = ConvertToLong(args[1]);
                    var height = ConvertToLong(args[2]);
                    if (ok && width > 0 && height > 0) return $"{width}x{height}";
                }
            }

            var videoTrack = ReadMember(mediaPlayer, "VideoTrack");
            var trackWidth = ConvertToLong(ReadMember(videoTrack, "Width"));
            var trackHeight = ConvertToLong(ReadMember(videoTrack, "Height"));
            if (trackWidth > 0 && trackHeight > 0) return $"{trackWidth}x{trackHeight}";
        }
        catch
        {
            // LibVLCSharp has small API differences between builds.
        }

        return mediaPlayer.IsPlaying ? "detecting" : "not playing";
    }

    private static (long? ReadBytes, double? InputBitrate, double? DemuxBitrate) TryReadPlayerStats(MediaPlayer? mediaPlayer, Media? currentMedia)
    {
        if (mediaPlayer is null) return (null, null, null);

        try
        {
            var media = ReadMember(mediaPlayer, "Media") ?? currentMedia;
            var stats = ReadMember(media, "Stats") ?? ReadMember(media, "Statistics") ?? ReadMember(mediaPlayer, "Stats") ?? ReadMember(mediaPlayer, "Statistics");
            if (stats is null) return (null, null, null);

            var readBytes = ConvertToNullableLong(
                ReadMember(stats, "ReadBytes") ??
                ReadMember(stats, "DemuxReadBytes") ??
                ReadMember(stats, "InputReadBytes"));
            var inputBitrate = ConvertToNullableDouble(
                ReadMember(stats, "InputBitrate") ??
                ReadMember(stats, "InputBitrateFloat") ??
                ReadMember(stats, "InputBitrateKbps"));
            var demuxBitrate = ConvertToNullableDouble(
                ReadMember(stats, "DemuxBitrate") ??
                ReadMember(stats, "DemuxBitrateFloat") ??
                ReadMember(stats, "DemuxBitrateKbps"));
            return (readBytes, inputBitrate, demuxBitrate);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static double? TryReadTrackBitrate(MediaPlayer? mediaPlayer, Media? currentMedia)
    {
        try
        {
            var media = ReadMember(mediaPlayer, "Media") ?? currentMedia;
            var tracks = ReadMember(media, "Tracks") as System.Collections.IEnumerable;
            if (tracks is null) return null;

            double total = 0;
            foreach (var track in tracks)
            {
                var bitrate = ConvertToNullableDouble(ReadMember(track, "Bitrate"));
                if (bitrate is > 0) total += bitrate.Value;
            }

            return total > 0 ? total : null;
        }
        catch
        {
            return null;
        }
    }

    private static (string? VideoCodec, string? AudioCodec, long? AudioChannels, long? AudioRate) TryReadTrackDetails(MediaPlayer? mediaPlayer, Media? currentMedia)
    {
        try
        {
            var media = ReadMember(mediaPlayer, "Media") ?? currentMedia;
            var tracks = ReadMember(media, "Tracks") as System.Collections.IEnumerable;
            if (tracks is null) return (null, null, null, null);

            string? videoCodec = null;
            string? audioCodec = null;
            long? audioChannels = null;
            long? audioRate = null;

            foreach (var track in tracks)
            {
                var trackType = ReadMember(track, "TrackType")?.ToString();
                if (videoCodec is null && string.Equals(trackType, "Video", StringComparison.Ordinal))
                {
                    videoCodec = DescribeCodec(track);
                }
                else if (audioCodec is null && string.Equals(trackType, "Audio", StringComparison.Ordinal))
                {
                    audioCodec = DescribeCodec(track);
                    var audioData = ReadMember(ReadMember(track, "Data"), "Audio");
                    audioChannels = ConvertToNullableLong(ReadMember(audioData, "Channels"));
                    audioRate = ConvertToNullableLong(ReadMember(audioData, "Rate"));
                }
            }

            return (videoCodec, audioCodec, audioChannels, audioRate);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    // LibVLC's track "description"/"language" fields are rarely filled in for
    // video/audio (mainly used for subtitle track labels), so the codec name
    // comes from decoding the FourCC identifier instead (e.g. 'h264', 'mp4a').
    private static string? DescribeCodec(object? track)
    {
        var fourCc = ConvertToNullableLong(ReadMember(track, "Codec"));
        return FormatFourCc(fourCc);
    }

    private static string? FormatFourCc(long? value)
    {
        if (!value.HasValue || value.Value <= 0) return null;

        var bytes = BitConverter.GetBytes((uint)value.Value);
        var chars = bytes.Where(b => b != 0).Select(b => b is >= 0x20 and < 0x7F ? (char)b : '?').ToArray();
        var text = new string(chars).Trim();
        return text.Length == 0 ? null : text.ToUpperInvariant();
    }

    private static string? FormatAudioFormat(long? channels, long? rate)
    {
        if (!channels.HasValue && !rate.HasValue) return null;

        var parts = new List<string>();
        if (channels is > 0) parts.Add(channels == 1 ? "mono" : channels == 2 ? "stereo" : $"{channels}ch");
        if (rate is > 0) parts.Add($"{rate.Value / 1000.0:0.#} kHz");
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    private double? UpdateMeasuredBandwidth(long? readBytes)
    {
        if (!readBytes.HasValue) return _lastMeasuredMbps;

        var now = DateTime.UtcNow;
        if (_lastReadBytes.HasValue && _lastReadBytesUtc.HasValue)
        {
            var seconds = (now - _lastReadBytesUtc.Value).TotalSeconds;
            var delta = readBytes.Value - _lastReadBytes.Value;
            if (seconds > 0.25 && delta >= 0)
            {
                _lastMeasuredMbps = (delta * 8d) / seconds / 1_000_000d;
            }
        }

        _lastReadBytes = readBytes.Value;
        _lastReadBytesUtc = now;
        return _lastMeasuredMbps;
    }

    private static string FormatBandwidth(double? measuredMbps, double? fallbackBitrate)
    {
        if (measuredMbps.HasValue) return $"{measuredMbps.Value:0.00} Mbps";

        var fallbackMbps = NormalizeBitrateToMbps(fallbackBitrate);
        return fallbackMbps.HasValue ? $"estimated {fallbackMbps.Value:0.00} Mbps" : "detecting";
    }

    private static string FormatFps(float? fps)
    {
        if (!fps.HasValue || fps.Value <= 0 || float.IsNaN(fps.Value) || float.IsInfinity(fps.Value)) return "detecting";
        return $"{fps.Value:0.##}";
    }

    private static double? NormalizeBitrateToMbps(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value <= 0) return null;

        var raw = value.Value;
        return raw switch
        {
            // LibVLC media stats report input/demux bitrate in kilobytes per second.
            < 10_000 => raw * 8d / 1_000d,
            // MediaTrack.Bitrate is exposed in bits per second.
            _ => raw / 1_000_000d
        };
    }

    private static object? ReadMember(object? source, string name)
    {
        if (source is null) return null;
        var type = source.GetType();
        var prop = type.GetProperty(name);
        if (prop is not null) return prop.GetValue(source);
        var field = type.GetField(name);
        return field?.GetValue(source);
    }

    private static long ConvertToLong(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt64(value); }
        catch { return 0; }
    }

    private static long? ConvertToNullableLong(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToInt64(value); }
        catch { return null; }
    }

    private static double? ConvertToNullableDouble(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToDouble(value); }
        catch { return null; }
    }
}

public sealed record StreamInfoSnapshot(
    string State,
    string Resolution,
    string Bandwidth,
    string Fps,
    string VideoCodec,
    string AudioCodec,
    string AudioFormat,
    string Buffer,
    string Source,
    string FullText,
    string ShortText);
