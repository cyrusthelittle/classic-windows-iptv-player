using ClassicWindowsIptvPlayer.Windows;

internal static class VideoHostOwnershipRegressionChecks
{
    public static int Run()
    {
        var video = new IntPtr(11);
        var main = new IntPtr(21);
        var secondary = new IntPtr(31);
        var checks = new (string Name, bool Pass)[]
        {
            ("main owner accepts matching player and root", VideoHostOwnershipPolicy.IsExpectedOwner(video, video, main, main)),
            ("a different root window is rejected", !VideoHostOwnershipPolicy.IsExpectedOwner(video, video, main, secondary)),
            ("main window accepts its attached video surface", VideoHostOwnershipPolicy.IsExpectedOwner(video, video, main, main)),
            ("detached native output is rejected", !VideoHostOwnershipPolicy.IsExpectedOwner(video, new IntPtr(99), secondary, secondary)),
            ("missing or destroyed HWND is rejected", !VideoHostOwnershipPolicy.IsExpectedOwner(IntPtr.Zero, IntPtr.Zero, main, main) && !VideoHostOwnershipPolicy.IsExpectedOwner(video, video, main, IntPtr.Zero)),
        };
        foreach (var (name, pass) in checks) Console.WriteLine((pass ? "PASS " : "FAIL ") + name);
        Console.WriteLine($"Video ownership policy: {checks.Count(x => x.Pass)}/{checks.Length} passed.");
        return checks.All(x => x.Pass) ? 0 : 1;
    }
}
