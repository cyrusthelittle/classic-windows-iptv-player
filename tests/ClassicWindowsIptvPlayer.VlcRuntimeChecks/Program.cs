using ClassicWindowsIptvPlayer.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

var root = Path.Combine(Path.GetTempPath(), "cyrus-vlc-selection-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string Fixture(string name, ushort machine = 0x8664, bool plugins = true)
    {
        var directory = Path.GetFullPath(Path.Combine(root, name));
        Directory.CreateDirectory(directory);
        using var stream = new MemoryStream(new byte[128]);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x5a4d);
        stream.Position = 0x3c; writer.Write(64);
        stream.Position = 64; writer.Write(0x00004550); writer.Write(machine);
        stream.Position = 86; writer.Write((ushort)0x2000);
        File.WriteAllBytes(Path.Combine(directory, "libvlc.dll"), stream.ToArray());
        File.WriteAllBytes(Path.Combine(directory, "libvlccore.dll"), stream.ToArray());
        if (plugins)
        {
            Directory.CreateDirectory(Path.Combine(directory, "plugins", "codec"));
            File.WriteAllBytes(Path.Combine(directory, "plugins", "codec", "fixture.dll"), [0]);
        }
        return directory;
    }
    void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    var system = Fixture("installed");
    var app = Path.Combine(root, "app");
    var local = Fixture("app/libvlc/win-x64");
    var calls = new List<string>();
    Check(VlcRuntimeLocator.Select([system], app, p => { calls.Add(p); return true; }) == system && calls.SequenceEqual([system]),
        "working system installation wins without probing bundled libraries");
    calls.Clear();
    Check(VlcRuntimeLocator.Select([system], app, p => { calls.Add(p); return p == local; }) == local && calls.SequenceEqual([system, local]),
        "native system probe failure falls back to the local runtime");
    var x86 = Fixture("32bit", 0x014c);
    calls.Clear();
    Check(VlcRuntimeLocator.Select([x86], app, p => { calls.Add(p); return true; }) == local && !calls.Contains(x86),
        "32-bit libraries never reach native initialization");
    var empty = Fixture("no-plugins", plugins: false);
    Check(VlcRuntimeLocator.Select([empty], app, p => p == local) == local, "incomplete installed VLC does not suppress the local fallback");
    calls.Clear();
    Check(VlcRuntimeLocator.Select([system, system], app, p => { calls.Add(p); return false; }) is null && calls.Count(p => p == system) == 1,
        "duplicate candidates are probed once and exhausted candidates report missing VLC");
    var chosen = Fixture("custom path with spaces");
    Check(VlcRuntimeLocator.Select([system], app, _ => true, chosen) == chosen, "a folder chosen by the user is attempted explicitly");
    var flat = Fixture("flat");
    Check(VlcRuntimeLocator.Select([], flat, _ => true) == flat, "libraries beside the executable remain a supported fallback");
    File.WriteAllBytes(Path.Combine(system, "libvlc.dll"), [0, 1, 2]);
    Check(VlcRuntimeLocator.Select([system], app, _ => true) == local, "malformed native files are rejected before loading");
    Check(VlcRuntimeLocator.Select([], Path.Combine(root, "missing"), _ => true) is null, "missing VLC is reported to the startup prompt");

    if (args.Length == 2)
    {
        void Probe(string path, int expected)
        {
            var start = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--vlc-runtime-probe"); start.ArgumentList.Add(path);
            start.Environment["CYRUS_IPTV_DATA_ROOT"] = Path.Combine(root, "unused-account-data");
            using var child = Process.Start(start) ?? throw new Exception("Could not start production VLC probe.");
            if (!child.WaitForExit(15000)) { child.Kill(entireProcessTree: true); throw new Exception("VLC probe timed out."); }
            Check(child.ExitCode == expected, "production native probe " + Path.GetFileName(path) + " exits " + expected);
        }
        Probe(x86, 1);
        Probe(chosen, 1); // Valid-looking PE headers cannot substitute for working native DLLs.
        Probe(empty, 1);
        Probe(args[1], 0);
        Probe(args[1], 0); // Failed candidates above cannot contaminate a subsequent native load.
        Environment.SetEnvironmentVariable("CYRUS_IPTV_DATA_ROOT", Path.Combine(root, "unused-account-data"));
        VlcRuntime.Initialize();
        using var native = new LibVLCSharp.Shared.LibVLC("--ignore-config", "--no-video-title-show");
        using var mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(native);
        Check(native.Version.StartsWith("3.", StringComparison.Ordinal), "production runtime selection works in a project-reference harness without the player apphost");
        Check(!Directory.Exists(Path.Combine(root, "unused-account-data")), "production probes never create or load account data");
    }

    Exception? uiError = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var retry in new[] { true, false })
            {
                var window = new VlcRequiredWindow();
                Exception? eventError = null;
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    try
                    {
                        var content = (StackPanel)window.Content;
                        var buttons = ((WrapPanel)content.Children[1]).Children.OfType<Button>().ToArray();
                        Check(buttons.Select(b => (string)b.Content).SequenceEqual(["Download VLC", "Choose folder", "Retry", "Cancel"]), "rendered prompt exposes all four recovery actions");
                        Check(buttons.All(b => b.IsVisible && b.ActualWidth > 0), "prompt recovery buttons are rendered");
                        if (retry)
                        {
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            var visual = new DrawingVisual();
                            using (var drawing = visual.RenderOpen())
                            {
                                var rectangle = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                                drawing.DrawRectangle(SystemColors.WindowBrush, null, rectangle);
                                drawing.DrawRectangle(new VisualBrush(content), null, rectangle);
                            }
                            bitmap.Render(visual);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            var evidence = Environment.GetEnvironmentVariable("CYRUS_VLC_PROMPT_EVIDENCE");
                            if (!string.IsNullOrEmpty(evidence)) { using var file = File.Create(evidence); encoder.Save(file); }
                        }
                        buttons.Single(b => (string)b.Content == (retry ? "Retry" : "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception error) { eventError = error; window.Close(); }
                }));
                var result = window.ShowDialog();
                if (eventError is not null) throw eventError;
                Check(result == retry, retry ? "Retry returns to runtime discovery" : "Cancel stops startup");
            }
            application.Shutdown();
        }
        catch (Exception error) { uiError = error; }
    });
    uiThread.IsBackground = true;
    uiThread.SetApartmentState(ApartmentState.STA); uiThread.Start();
    if (!uiThread.Join(TimeSpan.FromSeconds(20))) throw new Exception("Prompt checks timed out.");
    if (uiError is not null) throw uiError;
    Console.WriteLine("ALL PASS VLC runtime selection");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
finally { Directory.Delete(root, recursive: true); }
