using ClassicWindowsIptvPlayer.Core;
using LibVLCSharp.Shared;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    private sealed record AudioTrackOption(MediaPlayer Player, int Id, string Name);

    private void AudioTrackMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem menu || !ReferenceEquals(e.OriginalSource, menu)) return;
        menu.Items.Clear();
        var player = _mediaPlayer;
        if (player is not null)
        {
            try
            {
                var activeId = player.AudioTrack;
                foreach (var track in player.AudioTrackDescription.Where(track => track.Id >= 0))
                {
                    var name = string.IsNullOrWhiteSpace(track.Name)
                        ? $"Audio track {menu.Items.Count + 1}" : track.Name;
                    var item = new WpfMenuItem
                    {
                        Header = new TextBlock { Text = name },
                        IsCheckable = true,
                        IsChecked = track.Id == activeId,
                        Tag = new AudioTrackOption(player, track.Id, name)
                    };
                    item.Click += AudioTrackMenuItem_Click;
                    menu.Items.Add(item);
                }
            }
            catch (Exception exception)
            {
                menu.Items.Clear();
                AppLogger.Warn("Audio tracks unavailable: " + AppLogger.SanitizeText(exception.Message));
            }
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new WpfMenuItem { Header = "No audio tracks available", IsEnabled = false });
    }

    private void AudioTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: AudioTrackOption option } item) return;
        e.Handled = true;
        // A channel change can replace the player while its menu is still open.
        if (!ReferenceEquals(option.Player, _mediaPlayer))
        {
            item.IsChecked = false;
            return;
        }
        try
        {
            if (!option.Player.SetAudioTrack(option.Id))
            {
                item.IsChecked = option.Player.AudioTrack == option.Id;
                StatusText.Text = "Audio track could not be selected.";
                return;
            }
            // Keep the language preference timer from replacing a manual choice.
            _preferredAudioApplied = true;
            if (ItemsControl.ItemsControlFromItemContainer(item) is WpfMenuItem parent)
                foreach (var sibling in parent.Items.OfType<WpfMenuItem>())
                    sibling.IsChecked = sibling.Tag is AudioTrackOption track && track.Id == option.Id;
            StatusText.Text = "Audio track selected: " + option.Name;
        }
        catch (Exception exception)
        {
            item.IsChecked = false;
            StatusText.Text = "Audio track selection failed: " + AppLogger.SanitizeText(exception.Message);
        }
    }
}
