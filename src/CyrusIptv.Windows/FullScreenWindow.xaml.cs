using LibVLCSharp.Shared;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CyrusIptv.Windows;

public partial class FullScreenWindow : Window
{
    private readonly MediaPlayer _mediaPlayer;
    private readonly Action _playPause;
    private readonly Action _stop;
    private readonly Action<int> _relative;
    private readonly Action<int> _setVolume;
    private readonly Action _toggleMute;
    private readonly Action _closed;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _positionTimer;
    private bool _isSeeking;
    private bool _closedCallbackSent;
    private LowLevelMouseProc? _mouseHookProc;
    private IntPtr _mouseHook;
    private DateTime _lastLeftClickUtc = DateTime.MinValue;

    public FullScreenWindow(
        MediaPlayer mediaPlayer,
        string title,
        int volume,
        bool muted,
        Action playPause,
        Action stop,
        Action<int> relative,
        Action<int> setVolume,
        Action toggleMute,
        Action closed)
    {
        InitializeComponent();
        _mediaPlayer = mediaPlayer;
        _playPause = playPause;
        _stop = stop;
        _relative = relative;
        _setVolume = setVolume;
        _toggleMute = toggleMute;
        _closed = closed;
        TitleText.Text = title;
        VolumeSlider.Value = Math.Max(0, Math.Min(150, volume));
        InitializeButtonIcons(muted);

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideTimer.Tick += (_, _) => { if (_mediaPlayer.IsPlaying) ControlsOverlay.Visibility = Visibility.Collapsed; };
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _positionTimer.Tick += (_, _) => UpdatePosition();

        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            InstallMouseHook();
            _positionTimer.Start();
            ShowControlsTemporarily();
            Activate();
            Focus();
        }), DispatcherPriority.ApplicationIdle);
        Closed += (_, _) =>
        {
            UninstallMouseHook();
            _positionTimer.Stop();
            _hideTimer.Stop();
            FullVideoView.MediaPlayer = null;
            RaiseClosedOnce();
        };
    }

    public void AttachPlayer()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FullVideoView.MediaPlayer = _mediaPlayer;
            ShowControlsTemporarily();
        }), DispatcherPriority.Loaded);
    }

    public void UpdateInfo(StreamInfoSnapshot snapshot, string title, int volume, bool muted)
    {
        TitleText.Text = title;
        MetaText.Text = snapshot.ShortText + "   -   Esc/F11 exits   -   Left/Right previous/next";
        PlayPauseButton.Content = IconFactory.Create(_mediaPlayer.IsPlaying ? IconFactory.Pause : IconFactory.Play);
        MuteButton.Content = IconFactory.Create(muted ? IconFactory.Mute : IconFactory.Volume);
        if (!VolumeSlider.IsMouseCaptureWithin && Math.Abs(VolumeSlider.Value - volume) > 0.5)
        {
            VolumeSlider.Value = Math.Max(0, Math.Min(150, volume));
        }
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        if (_isSeeking) return;
        var length = _mediaPlayer.Length;
        var time = _mediaPlayer.Time;
        if (length > 0)
        {
            FullSeekSlider.IsEnabled = true;
            FullSeekSlider.Value = Math.Max(0, Math.Min(1000, (double)time / length * 1000d));
            TimeText.Text = FormatTime(time) + " / " + FormatTime(length);
        }
        else
        {
            FullSeekSlider.IsEnabled = false;
            FullSeekSlider.Value = 0;
            TimeText.Text = _mediaPlayer.IsPlaying ? "Live" : "00:00 / 00:00";
        }
    }

    private void InitializeButtonIcons(bool muted)
    {
        FullPreviousButton.Content = IconFactory.Create(IconFactory.Previous);
        PlayPauseButton.Content = IconFactory.Create(IconFactory.Play);
        FullStopButton.Content = IconFactory.Create(IconFactory.Stop);
        FullNextButton.Content = IconFactory.Create(IconFactory.Next);
        MuteButton.Content = IconFactory.Create(muted ? IconFactory.Mute : IconFactory.Volume);
        FullExitButton.Content = IconFactory.Create(IconFactory.Exit);
    }

    private static string FormatTime(long ms)
    {
        if (ms <= 0) return "00:00";
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private void FullSeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;
    private void FullSeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        if (_mediaPlayer.Length > 0) _mediaPlayer.Time = (long)(_mediaPlayer.Length * (FullSeekSlider.Value / 1000d));
    }

    private void FullSeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking && _mediaPlayer.Length > 0)
        {
            TimeText.Text = FormatTime((long)(_mediaPlayer.Length * (FullSeekSlider.Value / 1000d))) + " / " + FormatTime(_mediaPlayer.Length);
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) { _playPause(); ShowControlsTemporarily(); }
    private void Stop_Click(object sender, RoutedEventArgs e) { _stop(); ShowControlsTemporarily(); }
    private void Previous_Click(object sender, RoutedEventArgs e) { _relative(-1); ShowControlsTemporarily(); }
    private void Next_Click(object sender, RoutedEventArgs e) { _relative(1); ShowControlsTemporarily(); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Mute_Click(object sender, RoutedEventArgs e) { _toggleMute(); ShowControlsTemporarily(); }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _setVolume((int)e.NewValue);
        ShowControlsTemporarily();
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) => ShowControlsTemporarily();
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2) Close();
        else ShowControlsTemporarily();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
            case Key.F11:
                Close();
                e.Handled = true;
                break;
            case Key.Space:
                _playPause();
                e.Handled = true;
                break;
            case Key.Left:
                _relative(-1);
                e.Handled = true;
                break;
            case Key.Right:
                _relative(1);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Add:
            case Key.OemPlus:
                _setVolume((int)Math.Min(150, VolumeSlider.Value + 5));
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Subtract:
            case Key.OemMinus:
                _setVolume((int)Math.Max(0, VolumeSlider.Value - 5));
                e.Handled = true;
                break;
            case Key.M:
                _toggleMute();
                e.Handled = true;
                break;
            case Key.S:
                _stop();
                e.Handled = true;
                break;
        }
        ShowControlsTemporarily();
    }


    private void ShowStreamInfo_Click(object sender, RoutedEventArgs e)
    {
        var message = TitleText.Text + "\n\n" + MetaText.Text + "\n" + TimeText.Text;
        MessageBox.Show(this, message, "Stream information", MessageBoxButton.OK, MessageBoxImage.Information);
        ShowControlsTemporarily();
    }

    private void ShowContextMenu()
    {
        if (RootGrid.ContextMenu is null) return;
        ShowControlsTemporarily();
        RootGrid.ContextMenu.PlacementTarget = RootGrid;
        RootGrid.ContextMenu.IsOpen = true;
    }

    private void ShowControlsTemporarily()
    {
        ControlsOverlay.Visibility = Visibility.Visible;
        _hideTimer.Stop();
        if (_mediaPlayer.IsPlaying) _hideTimer.Start();
    }

    private void RaiseClosedOnce()
    {
        if (_closedCallbackSent) return;
        _closedCallbackSent = true;
        _closed();
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseHookProc = LowLevelMouseCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module is null ? IntPtr.Zero : GetModuleHandle(module.ModuleName);
        _mouseHook = SetWindowsHookEx(14, _mouseHookProc, moduleHandle, 0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseHookProc = null;
    }

    private IntPtr LowLevelMouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        const int WmMouseMove = 0x0200;
        const int WmLButtonDown = 0x0201;
        const int WmLButtonDblClk = 0x0203;
        const int WmRButtonUp = 0x0205;
        if (nCode >= 0)
        {
            if (wParam == (IntPtr)WmMouseMove)
            {
                Dispatcher.BeginInvoke(new Action(ShowControlsTemporarily));
            }
            else if (wParam == (IntPtr)WmRButtonUp)
            {
                Dispatcher.BeginInvoke(new Action(ShowContextMenu));
                return (IntPtr)1;
            }
            else if (wParam == (IntPtr)WmLButtonDblClk || wParam == (IntPtr)WmLButtonDown)
            {
                var now = DateTime.UtcNow;
                if (wParam == (IntPtr)WmLButtonDblClk || now - _lastLeftClickUtc < TimeSpan.FromMilliseconds(430))
                {
                    Dispatcher.BeginInvoke(new Action(Close));
                    return (IntPtr)1;
                }
                _lastLeftClickUtc = now;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
