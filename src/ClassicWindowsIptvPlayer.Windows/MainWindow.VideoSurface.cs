using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
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
