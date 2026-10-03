using System.Security.Cryptography;
using System.Text;
namespace ClassicWindowsIptvPlayer.Core;

/// <summary>Prevents separate application processes from changing one data root concurrently.</summary>
public sealed class AppDataLease : IDisposable
{
    private readonly Mutex _mutex;
    private bool _owned;
    private AppDataLease(string root)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())));
        _mutex = new Mutex(false, "Local\\CyrusIptvRuntime-" + identity);
        try { _owned = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _owned = true; }
        if (!_owned) { _mutex.Dispose(); throw new IOException("Another app is using this saved-data folder. Close it before opening this version."); }
    }
    public static AppDataLease Acquire(string root) => new(root);
    public void Dispose()
    {
        if (!_owned) return;
        _owned = false;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
