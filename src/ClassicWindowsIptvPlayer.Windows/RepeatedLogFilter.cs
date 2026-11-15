using System;
using System.Collections.Generic;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>
/// Drops identical log lines that repeat within a short window (LibVLC can print the
/// same Direct3D error dozens of times per second) and reports how many were dropped.
/// Thread-safe.
/// </summary>
internal sealed class RepeatedLogFilter
{
    private const int MaxTrackedMessages = 256;
    private readonly long _windowMs;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long LoggedAtMs, int Suppressed)> _recent = new(StringComparer.Ordinal);

    public RepeatedLogFilter(long windowMs = 5000) => _windowMs = windowMs;

    /// <summary>
    /// True when the message should be written now. <paramref name="suppressedBefore"/> is the
    /// number of identical messages dropped since this message was last written.
    /// </summary>
    public bool ShouldLog(string message, long nowMs, out int suppressedBefore)
    {
        lock (_lock)
        {
            if (_recent.TryGetValue(message, out var entry) && nowMs - entry.LoggedAtMs < _windowMs)
            {
                _recent[message] = (entry.LoggedAtMs, entry.Suppressed + 1);
                suppressedBefore = 0;
                return false;
            }
            suppressedBefore = entry.Suppressed;
            if (_recent.Count >= MaxTrackedMessages && !_recent.ContainsKey(message)) _recent.Clear();
            _recent[message] = (nowMs, 0);
            return true;
        }
    }
}
