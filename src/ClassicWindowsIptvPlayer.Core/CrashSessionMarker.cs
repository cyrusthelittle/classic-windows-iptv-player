namespace ClassicWindowsIptvPlayer.Core;

/// <summary>Tracks whether the previous app process exited normally without storing diagnostic content.</summary>
public sealed class CrashSessionMarker
{
    private readonly string _logsRoot;
    private readonly string _markerPath;
    private string? _currentSession;

    public CrashSessionMarker(string logsRoot)
    {
        _logsRoot = Path.GetFullPath(logsRoot ?? throw new ArgumentNullException(nameof(logsRoot)));
        _markerPath = Path.Combine(_logsRoot, "active-session.txt");
    }

    /// <summary>Returns the previous active session if it ended without MarkNormalExit.</summary>
    public string? BeginSession(string sessionDirectory)
    {
        var session = Path.GetFullPath(sessionDirectory ?? throw new ArgumentNullException(nameof(sessionDirectory)));
        var rootPrefix = _logsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!session.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Session directory must be inside the logs directory.", nameof(sessionDirectory));

        string? previous = null;
        try
        {
            if (File.Exists(_markerPath))
            {
                var marked = Path.GetFullPath(File.ReadAllText(_markerPath).Trim());
                if (marked.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(marked, session, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(marked))
                    previous = marked;
            }
        }
        catch { /* A damaged marker is ignored; existing session logs are left in place. */ }

        Directory.CreateDirectory(_logsRoot);
        var temporary = _markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, session);
            File.Move(temporary, _markerPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }

        _currentSession = session;
        return previous;
    }

    public void MarkNormalExit()
    {
        if (_currentSession is null) return;
        try
        {
            if (File.Exists(_markerPath) &&
                string.Equals(Path.GetFullPath(File.ReadAllText(_markerPath).Trim()), _currentSession, StringComparison.OrdinalIgnoreCase))
                File.Delete(_markerPath);
        }
        catch { /* If normal shutdown cannot be recorded, startup will conservatively offer a report. */ }
        finally { _currentSession = null; }
    }
}
