using System.Security.Cryptography;
using System.Text;
namespace ClassicWindowsIptvPlayer.Core;

/// <summary>Held for the app lifetime, or exclusively throughout helper apply/rollback.</summary>
public sealed class InstallationLease : IDisposable
{
    private readonly Mutex _gate;
    public InstallationLease(string installRoot)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
        _gate = new Mutex(false, "Local\\CyrusIptvInstall-" + identity);
        bool owned;
        try { owned = _gate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) { _gate.Dispose(); throw new IOException("Another app or updater is using this installation. Close it and try again."); }
    }
    public void Dispose() { _gate.ReleaseMutex(); _gate.Dispose(); }
}
