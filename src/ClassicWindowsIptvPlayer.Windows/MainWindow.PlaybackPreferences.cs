using ClassicWindowsIptvPlayer.Core;
using LibVLCSharp.Shared;
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
    private bool _settingsMenuStateHooked;

    private void UpdateRemoteControlMenuState() => RemoteControlMenuItem.IsChecked = _state.RemoteControlEnabled && _remoteControlService.IsRunning;

    private void SettingsMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateRemoteControlMenuState();
        if (sender is MenuItem settings)
        {
            if (!_settingsMenuStateHooked)
            {
                settings.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(SettingsMenuItem_Click), true);
                _settingsMenuStateHooked = true;
            }
            UpdateSettingsCheckmarks(settings);
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem settings) UpdateSettingsCheckmarks(settings);
        else if (AppMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header?.ToString() == "Settings") is { } root)
            UpdateSettingsCheckmarks(root);
    }

    private void UpdateSettingsCheckmarks(MenuItem settings)
    {
        var buffer = _state.PlaybackBufferMs <= 0 ? 1000 : Math.Clamp(_state.PlaybackBufferMs, 500, 30000);
        foreach (var (header, selected) in new[]
        {
            ("Buffer 1 sec", buffer <= 1000),
            ("Buffer 3 sec", buffer > 1000 && buffer <= 3000),
            ("Buffer 6 sec", buffer > 3000 && buffer <= 6000),
            ("Buffer 10 sec", buffer > 6000)
        })
        {
            if (settings.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header?.ToString() == header) is { } item)
            {
                item.IsCheckable = true;
                item.IsChecked = selected;
            }
        }

        var reconnect = settings.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header?.ToString() == "Reconnect attempts");
        if (reconnect is not null)
        {
            var attempts = _state.ReconnectAttempts <= 0 ? 10 : Math.Clamp(_state.ReconnectAttempts, 1, 30);
            foreach (var (header, value) in new[]
            {
                ("5 attempts", 5), ("10 attempts (default)", 10),
                ("15 attempts", 15), ("20 attempts", 20)
            })
            {
                if (reconnect.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header?.ToString() == header) is { } item)
                {
                    item.IsCheckable = true;
                    item.IsChecked = attempts == value;
                }
            }
        }
        DarkModeMenuItem.IsChecked = _state.DarkMode;
    }

    private void RemoteControlMenuItem_Click(object sender, RoutedEventArgs e)
    {
        UpdateRemoteControlMenuState();
        ToggleRemoteControl_Click(sender, e);
        UpdateRemoteControlMenuState();
    }

    private void MorePlaybackMenu_Opened(object sender, RoutedEventArgs e)
    {
        var recent = _recordingIndex.Recent(_state.SelectedAccountId).FirstOrDefault(entry =>
            !entry.IsActive && entry.ByteSize > 0);
        var recentPath = recent is null ? null : !string.IsNullOrWhiteSpace(recent.FilePath)
            ? recent.FilePath
            : !string.IsNullOrWhiteSpace(_recordingFolder) ? System.IO.Path.Combine(_recordingFolder, recent.FileName) : null;
        var openRecent = MorePlaybackButton.ContextMenu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Open recent recording", StringComparison.Ordinal));
        if (openRecent is not null)
        {
            openRecent.Tag = recentPath;
            openRecent.IsEnabled = recentPath is not null && System.IO.File.Exists(recentPath);
            if (recentPath is not null) OpenRecordingButton.Tag = recentPath;
        }
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
}
