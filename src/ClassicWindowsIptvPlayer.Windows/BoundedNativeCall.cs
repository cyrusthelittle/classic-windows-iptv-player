using System;
using System.Threading;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>
/// Runs a native call that can block forever (LibVLC Stop on a stuck video output)
/// on its own background thread and waits only a bounded time for it.
/// </summary>
internal static class BoundedNativeCall
{
    /// <summary>
    /// Returns false when <paramref name="call"/> is still running after <paramref name="timeout"/>.
    /// The call keeps running on a background thread; it never blocks app exit.
    /// </summary>
    public static bool Run(Action call, TimeSpan timeout, Action<Exception>? onError = null)
    {
        var thread = new Thread(() =>
        {
            try { call(); }
            catch (Exception exception)
            {
                try { onError?.Invoke(exception); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "Bounded native call"
        };
        thread.Start();
        return thread.Join(timeout);
    }
}
