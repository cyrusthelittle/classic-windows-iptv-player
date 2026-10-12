using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace ClassicWindowsIptvPlayer.Windows;

public sealed class VlcRuntimeUnavailableException : Exception
{
    public VlcRuntimeUnavailableException() : base("A working 64-bit VLC 3 installation or bundled VLC library could not be found.") { }
}

public static class VlcRuntime
{
    public const string ProbeArgument = "--vlc-runtime-probe";
    private static readonly object Gate = new();
    private static string? _initializedDirectory;
    private static Exception? _initializationFailure;

    public static void Initialize(string? chosenDirectory = null)
    {
        lock (Gate)
        {
            if (_initializedDirectory is not null) return;
            if (_initializationFailure is not null)
                throw new InvalidOperationException("VLC changed or could not be loaded after validation. Close and restart the player after repairing VLC.", _initializationFailure);
            var selected = VlcRuntimeLocator.Select(InstalledDirectories(), AppContext.BaseDirectory, ProbeInChildProcess, chosenDirectory)
                ?? throw new VlcRuntimeUnavailableException();
            try
            {
                Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", Path.Combine(selected, "plugins"));
                LibVLCSharp.Shared.Core.Initialize(selected);
                _initializedDirectory = selected;
            }
            catch (Exception error)
            {
                // Native DLLs may already be mapped. Do not mix a second runtime into this process.
                _initializationFailure = error;
                throw new InvalidOperationException("VLC could not be loaded after validation. Close and restart the player after repairing VLC.", error);
            }
        }
    }

    private static IEnumerable<string> InstalledDirectories()
    {
        var paths = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
                if (key?.GetValue("InstallDir") is string path && !string.IsNullOrWhiteSpace(path)) paths.Add(path);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (!string.IsNullOrEmpty(root)) paths.Add(Path.Combine(root, "VideoLAN", "VLC"));
        return paths;
    }

    private static bool ProbeInChildProcess(string directory)
    {
        try
        {
            var assembly = typeof(App).Assembly.Location;
            var appHost = Path.ChangeExtension(assembly, ".exe");
            var start = new ProcessStartInfo
            {
                FileName = File.Exists(appHost) ? appHost : "dotnet",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            if (!File.Exists(appHost))
            {
                // A project-reference test harness may copy the player DLL without its apphost.
                // Run the player's entry point using the harness's dependency/runtime manifests.
                start.ArgumentList.Add("exec");
                var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                var manifestAssembly = File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")) ? assembly : entry;
                if (!string.IsNullOrEmpty(manifestAssembly))
                {
                    var runtimeConfig = Path.ChangeExtension(manifestAssembly, ".runtimeconfig.json");
                    var dependencies = Path.ChangeExtension(manifestAssembly, ".deps.json");
                    if (File.Exists(runtimeConfig)) { start.ArgumentList.Add("--runtimeconfig"); start.ArgumentList.Add(runtimeConfig); }
                    if (File.Exists(dependencies)) { start.ArgumentList.Add("--depsfile"); start.ArgumentList.Add(dependencies); }
                }
                start.ArgumentList.Add(assembly);
            }
            start.ArgumentList.Add(ProbeArgument);
            start.ArgumentList.Add(directory);
            // A VLC check must never acknowledge an updater startup or acquire account-data leases.
            start.Environment.Remove(ClassicWindowsIptvPlayer.Core.UpdateInstaller.HealthVariable);
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or NotSupportedException)
        { return false; }
    }

    // Called only by the dedicated CLI branch, before App creates logs, settings or leases.
    internal static int RunProbe(string[] arguments)
    {
        SetErrorMode(0x0001 | 0x0002 | 0x8000); // Suppress native loader/crash dialogs in the hidden child.
        if (arguments.Length != 2 || arguments[0] != ProbeArgument || !VlcRuntimeLocator.IsCandidate(arguments[1])) return 1;
        try
        {
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", Path.Combine(arguments[1], "plugins"));
            LibVLCSharp.Shared.Core.Initialize(arguments[1]);
            using var native = new LibVLC("--ignore-config", "--no-plugins-cache", "--no-video-title-show");
            if (!native.Version.StartsWith("3.", StringComparison.Ordinal) ||
                !native.AudioFilters.Any() || !native.VideoFilters.Any()) return 1;
            using var player = new MediaPlayer(native);
            return 0;
        }
        catch { return 1; }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
