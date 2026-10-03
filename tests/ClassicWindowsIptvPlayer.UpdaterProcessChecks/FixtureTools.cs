using System.Diagnostics;
using System.IO;

internal static class FixtureTools
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "cyrus-updater-process-" + Guid.NewGuid().ToString("N"));
    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        File.AppendAllText(Path.Combine(Root, "evidence.txt"), "PASS " + message + Environment.NewLine);
    }
    public static async Task<(int ExitCode, string Output)> Run(string executable, IEnumerable<string> arguments, string dataRoot, string workingDirectory, int timeoutSeconds = 90, bool captureOutput = true)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput
        };
        start.Environment["CYRUS_IPTV_DATA_ROOT"] = Path.GetFullPath(dataRoot);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Fixture child did not start.");
        var stdout = captureOutput ? child.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
        var stderr = captureOutput ? child.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { child.Kill(true); throw new TimeoutException("Fixture child exceeded its deadline."); }
        return (child.ExitCode, await stdout + await stderr);
    }
}
