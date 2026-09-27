using ClassicWindowsIptvPlayer.Core;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    private bool _preferredAudioApplied;
    private bool _preferredSubtitleApplied;

    private void MorePlaybackMenu_Opened(object sender, RoutedEventArgs e) =>
        MorePlaybackPreferencesMenu.Visibility = _mediaPlayer is not null ? Visibility.Visible : Visibility.Collapsed;

    private void PlaybackPreferencesMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu) return;
        menu.Items.Clear();
        var player = _mediaPlayer;
        if (player is null)
        {
            menu.Items.Add(new MenuItem { Header = "Available during playback", IsEnabled = false });
            return;
        }
        int videoCount;
        TrackDescription[] audioTracks, subtitleTracks;
        try
        {
            audioTracks = player.AudioTrackDescription.Where(t => t.Id >= 0).ToArray();
            subtitleTracks = player.SpuDescription.Where(t => t.Id >= 0).ToArray();
            videoCount = player.VideoTrackCount;
        }
        catch { return; }
        var audioCount = audioTracks.Length;
        var subtitleCount = subtitleTracks.Length;

        if (PlaybackPreferencePolicy.HasAudioChoices(audioCount))
        {
            var tracks = new MenuItem { Header = "Audio track" };
            foreach (var track in audioTracks)
            {
                var id = track.Id;
                var item = new MenuItem { Header = track.Name, IsCheckable = true, IsChecked = id == player.AudioTrack };
                item.Click += (_, _) => ApplyChange(player, () =>
                {
                    var selected = player.SetAudioTrack(id);
                    if (selected) _preferredAudioApplied = true;
                    return selected;
                }, "Audio track selected.");
                tracks.Items.Add(item);
            }
            if (tracks.Items.Count > 1) menu.Items.Add(tracks);
        }

        if (audioCount > 0) AddLanguage(menu, "Preferred audio language", _state.PreferredAudioLanguage, value =>
        {
            _state.PreferredAudioLanguage = value;
            _preferredAudioApplied = false;
            ApplyPreferredTracks(player);
        });
        if (PlaybackPreferencePolicy.HasSubtitles(subtitleCount))
        {
            AddLanguage(menu, "Preferred subtitle language", _state.PreferredSubtitleLanguage, value =>
            {
                _state.PreferredSubtitleLanguage = value;
                _preferredSubtitleApplied = false;
                ApplyPreferredTracks(player);
            });
            if (player.Spu >= 0) AddDelay(menu, player, "Subtitle delay", player.SpuDelay, value => player.SetSpuDelay(value));
        }
        if (audioCount > 0) AddDelay(menu, player, "Audio delay", player.AudioDelay, value => player.SetAudioDelay(value));
        if (PlaybackPreferencePolicy.HasVideo(videoCount))
        {
            var aspect = new MenuItem { Header = "Aspect ratio" };
            foreach (var (label, ratio) in new (string Label, string? Ratio)[] { ("Original", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9") })
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = string.Equals(player.AspectRatio, ratio, StringComparison.Ordinal) };
                item.Click += (_, _) => ApplyChange(player, () => { player.AspectRatio = ratio!; return true; }, "Aspect ratio: " + label);
                aspect.Items.Add(item);
            }
            menu.Items.Add(aspect);
            var deinterlace = new MenuItem { Header = "Deinterlace" };
            foreach (var (label, mode) in new (string Label, string? Mode)[] { ("Off", null), ("Blend", "blend"), ("Yadif", "yadif") })
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => ApplyChange(player, () => { player.SetDeinterlace(mode!); return true; }, "Deinterlace: " + label);
                deinterlace.Items.Add(item);
            }
            menu.Items.Add(deinterlace);
        }
        if (_currentChannel is { } channel && PlaybackPreferencePolicy.HasVodSpeed(channel.MediaKind, player.IsSeekable, videoCount))
        {
            var speed = new MenuItem { Header = "Playback speed" };
            foreach (var rate in new[] { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f })
            {
                var item = new MenuItem { Header = rate.ToString("0.##") + "×", IsCheckable = true, IsChecked = Math.Abs(player.Rate - rate) < 0.01f };
                item.Click += (_, _) => ApplyChange(player, () => player.SetRate(rate) == 0, "Speed requested: " + item.Header);
                speed.Items.Add(item);
            }
            menu.Items.Add(speed);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No options for this stream", IsEnabled = false });
    }

    private void AddLanguage(MenuItem parent, string label, string current, Action<string> set)
    {
        var menu = new MenuItem { Header = label };
        foreach (var (name, code) in new[] { ("Stream default (next tune)", ""), ("English", "en"), ("German", "de"), ("French", "fr"), ("Spanish", "es"), ("Italian", "it"), ("Portuguese", "pt"), ("Japanese", "ja") })
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = current == code };
            item.Click += (_, _) => { set(code); _store.Save(_state); };
            menu.Items.Add(item);
        }
        parent.Items.Add(menu);
    }

    private void AddDelay(MenuItem parent, MediaPlayer player, string label, long currentMicroseconds, Func<long, bool> set)
    {
        var menu = new MenuItem { Header = label };
        foreach (var milliseconds in new[] { -1000, -500, -250, 0, 250, 500, 1000 })
        {
            var item = new MenuItem { Header = (milliseconds > 0 ? "+" : "") + milliseconds + " ms", IsCheckable = true,
                IsChecked = currentMicroseconds == PlaybackPreferencePolicy.DelayMicroseconds(milliseconds) };
            item.Click += (_, _) => ApplyChange(player, () => set(PlaybackPreferencePolicy.DelayMicroseconds(milliseconds)), label + ": " + item.Header);
            menu.Items.Add(item);
        }
        parent.Items.Add(menu);
    }

    private void ApplyPreferredTracks(MediaPlayer player)
    {
        if (!ReferenceEquals(player, _mediaPlayer) || !player.IsPlaying) return;
        try
        {
            if (!_preferredAudioApplied && player.AudioTrackCount > 0)
            {
                var id = PlaybackPreferencePolicy.FindLanguageTrack(_state.PreferredAudioLanguage,
                    player.AudioTrackDescription.Select(t => (t.Id, t.Name)));
                if (id is not null && player.SetAudioTrack(id.Value) || string.IsNullOrWhiteSpace(_state.PreferredAudioLanguage)) _preferredAudioApplied = true;
            }
            if (!_preferredSubtitleApplied && player.SpuCount > 0)
            {
                var id = PlaybackPreferencePolicy.FindLanguageTrack(_state.PreferredSubtitleLanguage,
                    player.SpuDescription.Select(t => (t.Id, t.Name)));
                if (id is not null && player.SetSpu(id.Value) || string.IsNullOrWhiteSpace(_state.PreferredSubtitleLanguage)) _preferredSubtitleApplied = true;
            }
        }
        catch { /* Native descriptions may be unavailable until decoder startup. */ }
    }

    private void ApplyChange(MediaPlayer? player, Func<bool> action, string message)
    {
        if (player is null || !ReferenceEquals(player, _mediaPlayer)) return;
        try { StatusText.Text = action() ? message : "Playback option is unavailable for this stream."; }
        catch (Exception ex) { StatusText.Text = "Playback option failed: " + AppLogger.SanitizeText(ex.Message); }
    }
}
