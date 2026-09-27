using ClassicWindowsIptvPlayer.Core;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    private Window? _miniPlayerWindow;
    private Grid? _miniVideoPanel;

    private void MiniPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (_miniPlayerWindow is not null)
        {
            _miniPlayerWindow.Activate();
            return;
        }
        if (_mediaPlayer is null || _isShuttingDown) return;
        if (_isFullScreen) ExitFullScreen();

        var mini = new Window
        {
            Title = "Mini player - " + (_currentChannel?.Name ?? "Now playing"),
            Owner = this,
            Width = 400,
            Height = 285,
            MinWidth = 280,
            MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Topmost = true,
            Background = System.Windows.Media.Brushes.Black
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var videoPanel = new Grid { Background = System.Windows.Media.Brushes.Black };
        layout.Children.Add(videoPanel);
        var controls = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(4)
        };
        AddMiniButton(controls, "Play / pause", () => TogglePlayPause());
        AddMiniButton(controls, "Stop", StopPlayback);
        AddMiniButton(controls, "Full screen", ToggleFullScreen);
        AddMiniButton(controls, "Return to player", () => mini.Close());
        Grid.SetRow(controls, 1);
        layout.Children.Add(controls);
        mini.Content = layout;
        mini.KeyDown += (_, args) =>
        {
            if (args.Key == Key.F11)
            {
                ToggleFullScreen();
                args.Handled = true;
            }
            else if (args.Key == Key.Escape) { if (mini.WindowState == WindowState.Maximized) mini.WindowState = WindowState.Normal; else mini.Close(); args.Handled = true; }
            else if (args.Key == Key.Space) { TogglePlayPause(); args.Handled = true; }
            else if (args.Key == Key.S) { StopPlayback(); args.Handled = true; }
        };
        mini.Closing += (_, _) => ReturnFromMiniPlayer();
        _miniPlayerWindow = mini;
        _miniVideoPanel = videoPanel;
        mini.Show();
        MainVideoSurfacePanel.Children.Remove(VideoViewHost);
        videoPanel.Children.Add(VideoViewHost);
        VideoViewHost.Visibility = Visibility.Visible;
        videoPanel.UpdateLayout();
        IdleBackgroundText.Text = "Playing in mini player";
        IdleBackground.Visibility = Visibility.Visible;
        if (!VerifyVideoSurfaceOwner(mini))
        {
            AppLogger.Warn("Mini player did not retain the embedded video HWND; stopping playback.");
            StatusText.Text = "Mini player could not attach video. Playback stopped.";
            StopPlayback();
        }
    }

    private static void AddMiniButton(System.Windows.Controls.Panel panel, string label, Action action)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = label,
            Margin = new Thickness(3),
            Padding = new Thickness(7, 4, 7, 4),
            ToolTip = label
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private void ReturnFromMiniPlayer()
    {
        if (_miniVideoPanel is null) return;
        _miniVideoPanel.Children.Remove(VideoViewHost);
        MainVideoSurfacePanel.Children.Add(VideoViewHost);
        MainVideoSurfacePanel.UpdateLayout();
        _miniVideoPanel = null;
        _miniPlayerWindow = null;
        IdleBackgroundText.Text = "Select a channel to start watching";
        if (_mediaPlayer is null) ShowIdleBackground();
        else HideIdleBackground();
        if (!_isShuttingDown && _mediaPlayer is not null && !VerifyVideoSurfaceOwner(this))
        {
            AppLogger.Warn("Returning from mini player did not retain the embedded video HWND; stopping playback.");
            StatusText.Text = "Video could not return to the main player. Playback stopped.";
            StopPlayback();
        }
    }

    private void CloseMiniPlayer() => _miniPlayerWindow?.Close();

    private bool VerifyVideoSurfaceOwner(Window expected)
    {
        if (_mediaPlayer is null || !_videoView.IsHandleCreated) return false;
        var videoHwnd = _videoView.Handle;
        var expectedRoot = new WindowInteropHelper(expected).Handle;
        return VideoHostOwnershipPolicy.IsExpectedOwner(videoHwnd, _mediaPlayer.Hwnd,
            GetAncestor(videoHwnd, 2), expectedRoot);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
