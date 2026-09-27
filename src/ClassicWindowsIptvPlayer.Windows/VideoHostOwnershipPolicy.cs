using System;

namespace ClassicWindowsIptvPlayer.Windows;

internal static class VideoHostOwnershipPolicy
{
    internal static bool IsExpectedOwner(IntPtr videoHwnd, IntPtr playerHwnd, IntPtr actualRoot, IntPtr expectedRoot) =>
        videoHwnd != IntPtr.Zero && expectedRoot != IntPtr.Zero &&
        playerHwnd == videoHwnd && actualRoot == expectedRoot;
}
