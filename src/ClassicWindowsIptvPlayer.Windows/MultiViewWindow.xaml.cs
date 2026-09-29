using ClassicWindowsIptvPlayer.Core;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibVlcMedia = LibVLCSharp.Shared.Media;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MultiViewWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly AccountSettings _account;
    private readonly Func<Channel, StreamLease?, StreamLease?> _acquireLease;
    private readonly Action<StreamLease> _releaseLease;
    private readonly IReadOnlyList<Channel> _channels;
    private readonly List<Tile> _tiles = [];
    private bool _isClosing;

    public MultiViewWindow(LibVLC libVlc, IReadOnlyList<Channel> channels, AccountSettings account,
        Func<Channel, StreamLease?, StreamLease?> acquireLease, Action<StreamLease> releaseLease)
    {
        ArgumentNullException.ThrowIfNull(libVlc);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(acquireLease);
        ArgumentNullException.ThrowIfNull(releaseLease);
        InitializeComponent();
        _libVlc = libVlc;
        _account = account;
        _acquireLease = acquireLease;
        _releaseLease = releaseLease;
        _channels = channels.Where(c => c is not null && c.MediaKind == MediaKind.Live).ToArray();

        for (var index = 0; index < 4; index++) AddTile();
    }

    private void AddTile()
    {
        var video = new LibVLCSharp.WPF.VideoView { Background = WpfBrushes.Black, MinHeight = 120 };
        var selector = new WpfComboBox { MinWidth = 150, ItemsSource = GetLiveChannels(), DisplayMemberPath = nameof(Channel.Name), Name = $"Tile{_tiles.Count + 1}Channel" };
        var status = new TextBlock { Text = "Choose a channel", VerticalAlignment = VerticalAlignment.Center, Foreground = WpfBrushes.LightGray };
        var play = new WpfButton { Content = "Play", MinWidth = 64, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false, Name = $"Tile{_tiles.Count + 1}Play" };
        var stop = new WpfButton { Content = "Stop", MinWidth = 64, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
        stop.Name = $"Tile{_tiles.Count + 1}Stop";
        video.Name = $"Tile{_tiles.Count + 1}Video";
        RegisterName(selector.Name, selector);
        RegisterName(play.Name, play);
        RegisterName(stop.Name, stop);
        RegisterName(video.Name, video);
        var bar = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
        DockPanel.SetDock(stop, Dock.Right);
        DockPanel.SetDock(play, Dock.Right);
        DockPanel.SetDock(status, Dock.Right);
        bar.Children.Add(stop);
        bar.Children.Add(play);
        bar.Children.Add(status);
        bar.Children.Add(selector);

        var surface = new Grid { Background = WpfBrushes.Black };
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.Children.Add(video);
        Grid.SetRow(bar, 1);
        surface.Children.Add(bar);

        var frame = new Border
        {
            Margin = new Thickness(6), BorderBrush = new SolidColorBrush(WpfColor.FromRgb(55, 65, 81)),
            BorderThickness = new Thickness(1), Child = surface
        };
        TilesGrid.Children.Add(frame);

        var tile = new Tile(video, selector, status, play, stop);
        _tiles.Add(tile);
        selector.SelectionChanged += (_, _) => play.IsEnabled = selector.SelectedItem is Channel;
        play.Click += (_, _) => { if (selector.SelectedItem is Channel selected) StartTile(tile, selected); };
        stop.Click += (_, _) => StopTile(tile);
    }

    private IReadOnlyList<Channel> GetLiveChannels()
    {
        // The constructor's initial channels are also the selectable source list.
        // Each tile gets its own selector snapshot, assigned from the retained list below.
        return _channels;
    }

    private void StartTile(Tile tile, Channel channel)
    {
        if (_isClosing) return;
        if (_tiles.Any(other => !ReferenceEquals(other, tile) && other.Channel is { } active &&
            string.Equals(active.Id, channel.Id, StringComparison.OrdinalIgnoreCase)))
        {
            tile.Status.Text = "Already playing in another tile";
            return;
        }

        StreamLease? lease = null;
        LibVlcMedia? media = null;
        VlcPlayer? player = null;
        try
        {
            var candidate = PlayerService.BuildPlaybackCandidates(channel, _account).FirstOrDefault();
            if (candidate is null || !Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri))
            {
                tile.Status.Text = "No playable stream URL";
                return;
            }

            var priorLease = tile.Lease;
            lease = _acquireLease(channel, priorLease);
            if (lease is null)
            {
                tile.Status.Text = "Connection limit reached";
                return;
            }

            // Replace the slot atomically in the budget before retiring the old input,
            // so a stale provider snapshot doesn't prevent switching this same tile.
            StopTile(tile, releaseLease: false);

            media = new LibVlcMedia(_libVlc, uri.AbsoluteUri, FromType.FromLocation);
            player = new VlcPlayer(_libVlc);
            var activePlayer = player;
            tile.Video.MediaPlayer = player;
            if (!player.Play(media)) throw new InvalidOperationException("LibVLC could not start this stream.");

            tile.Channel = channel;
            tile.Lease = lease;
            tile.Media = media;
            tile.Player = player;
            lease = null;
            media = null;
            player = null;
            tile.Stop.IsEnabled = true;
            tile.Status.Text = channel.Name;
            tile.ReleaseLease = _releaseLease;
            activePlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => FailTile(tile, activePlayer));
            activePlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(() => FailTile(tile, activePlayer));
        }
        catch (Exception ex)
        {
            tile.Status.Text = "Could not open stream: " + ex.Message;
            SafeDispose(player);
            SafeDispose(media);
            ReleaseLease(lease);
            tile.Video.MediaPlayer = null;
            tile.Stop.IsEnabled = false;
        }
    }

    private void FailTile(Tile tile, VlcPlayer player)
    {
        if (_isClosing || !ReferenceEquals(tile.Player, player)) return;
        StopTile(tile);
        tile.Status.Text = "Stream ended or failed";
    }

    private void StopTile(Tile tile, bool releaseLease = true)
    {
        var player = tile.Player;
        var media = tile.Media;
        var lease = tile.Lease;
        var release = tile.ReleaseLease;
        tile.Player = null;
        tile.Media = null;
        tile.Lease = null;
        tile.ReleaseLease = null;
        tile.Channel = null;
        tile.Video.MediaPlayer = null;
        tile.Stop.IsEnabled = false;
        tile.Status.Text = "Stopped";
        try { player?.Stop(); } catch { }
        SafeDispose(player);
        SafeDispose(media);
        if (releaseLease) ReleaseLease(lease, release);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;
        foreach (var tile in _tiles) StopTile(tile);
    }

    private void ReleaseLease(StreamLease? lease, Action<StreamLease>? release = null)
    {
        if (lease is null) return;
        try { (release ?? _releaseLease)?.Invoke(lease); } catch { }
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        try { disposable?.Dispose(); } catch { }
    }

    private sealed class Tile(LibVLCSharp.WPF.VideoView video, WpfComboBox selector, TextBlock status, WpfButton play, WpfButton stop)
    {
        public LibVLCSharp.WPF.VideoView Video { get; } = video;
        public WpfComboBox Selector { get; } = selector;
        public TextBlock Status { get; } = status;
        public WpfButton Play { get; } = play;
        public WpfButton Stop { get; } = stop;
        public Channel? Channel { get; set; }
        public StreamLease? Lease { get; set; }
        public Action<StreamLease>? ReleaseLease { get; set; }
        public LibVlcMedia? Media { get; set; }
        public VlcPlayer? Player { get; set; }
    }
}
