using ClassicWindowsIptvPlayer.Core;

internal static class CrashSessionMarkerRegressionChecks
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cyrus-session-marker-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        var normal = Path.Combine(logs, "session-normal");
        var interrupted = Path.Combine(logs, "session-interrupted");
        var next = Path.Combine(logs, "session-next");
        Directory.CreateDirectory(normal);
        Directory.CreateDirectory(interrupted);
        Directory.CreateDirectory(next);
        try
        {
            var first = new CrashSessionMarker(logs);
            if (first.BeginSession(normal) is not null) return Fail("first launch has no previous abnormal session");
            first.MarkNormalExit();

            var second = new CrashSessionMarker(logs);
            if (second.BeginSession(interrupted) is not null) return Fail("normal exit clears the marker");

            // Simulate process termination by intentionally leaving the second marker active.
            var third = new CrashSessionMarker(logs);
            if (!string.Equals(third.BeginSession(next), interrupted, StringComparison.OrdinalIgnoreCase))
                return Fail("abnormal exit leaves a marker for the next launch");
            third.MarkNormalExit();
            if (!Directory.Exists(interrupted)) return Fail("session logs are retained for report review");

            try
            {
                third.BeginSession(Path.Combine(root, "outside"));
                return Fail("marker rejects a session outside the logs directory");
            }
            catch (ArgumentException) { }

            Console.WriteLine("Crash session marker checks: normal exit clears state; interrupted exit is detected.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL crash session marker: " + ex.GetType().Name);
            return 1;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static int Fail(string check)
    {
        Console.Error.WriteLine("FAIL crash session marker: " + check);
        return 1;
    }
}
