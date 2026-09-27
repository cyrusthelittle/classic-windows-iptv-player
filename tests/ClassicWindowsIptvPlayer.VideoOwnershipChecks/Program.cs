using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Threading;

internal static class Program
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "ownership.log");
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "ownership.avi");
    private static Window main = null!, mini = null!;
    private static Grid mainGrid = null!, miniGrid = null!;
    private static WindowsFormsHost host = null!;
    private static VideoView video = null!;
    private static LibVLC vlc = null!;
    private static MediaPlayer player = null!;
    private static Media media = null!;
    private static int destroyed;

    [STAThread]
    private static void Main()
    {
        File.WriteAllText(LogPath, "Fixture=" + Fixture + Environment.NewLine);
        GenerateAvi.Run(Fixture);
        Core.Initialize();
        vlc = new LibVLC("--quiet");
        video = new VideoView { BackColor = Color.Black };
        video.HandleCreated += (_, _) => Log("Video handle created " + Hex(video.Handle));
        video.HandleDestroyed += (_, _) => { destroyed++; Log("Video handle destroyed " + destroyed); };
        host = new WindowsFormsHost { Child = video };
        mainGrid = new Grid { Background = System.Windows.Media.Brushes.Black };
        mainGrid.Children.Add(host);
        main = new Window { Title = "Step 14 ownership probe - main", Width = 620, Height = 420,
            Left = 100, Top = 100, Content = mainGrid, WindowStartupLocation = WindowStartupLocation.Manual };
        main.Loaded += async (_, _) => await Run();
        new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }.Run(main);
    }

    private static async Task Run()
    {
        try
        {
            main.Activate();
            await Task.Delay(500);
            for (var iteration = 1; iteration <= 2; iteration++)
            {
                media = new Media(vlc, new Uri(Fixture));
                player = new MediaPlayer(media);
                video.CreateControl();
                video.MediaPlayer = player;
                Log("Play " + iteration + " handle=" + Hex(video.Handle) + " playerHwnd=" + Hex(player.Hwnd));
                player.Play();
                await Task.Delay(2400);
                Observe("main-" + iteration);
                miniGrid = new Grid { Background = System.Windows.Media.Brushes.Black };
                mini = new Window { Title = "Step 14 ownership probe - mini", Width = 360, Height = 240,
                    Left = 770, Top = 150, Content = miniGrid, WindowStartupLocation = WindowStartupLocation.Manual };
                mini.Show();
                mainGrid.Children.Remove(host);
                miniGrid.Children.Add(host);
                await Task.Delay(2200);
                Observe("mini-" + iteration);
                mini.WindowState = WindowState.Maximized;
                await Task.Delay(1800);
                Observe("fullscreen-" + iteration);
                mini.WindowState = WindowState.Normal;
                miniGrid.Children.Remove(host);
                mainGrid.Children.Add(host);
                await Task.Delay(1800);
                Observe("returned-" + iteration);
                mini.Close();
                player.Stop();
                video.MediaPlayer = null;
                await Task.Delay(600);
                Observe("stopped-" + iteration);
                player.Dispose(); media.Dispose();
            }
            Log("Shutdown start");
            main.Close();
            video.Dispose();
            vlc.Dispose();
            if (DetachedVlcWindows().Count != 0) throw new InvalidOperationException("Detached VLC window remained after shutdown.");
            Log("Shutdown complete; destroyed=" + destroyed);
            Log("PASS native WPF ownership probe");
        }
        catch (Exception ex) { Log("FAIL " + ex); Environment.ExitCode = 1; }
        finally { Application.Current.Shutdown(); }
    }

    private static void Observe(string phase)
    {
        var owner = host.Parent == miniGrid ? mini : main;
        var hwnd = new WindowInteropHelper(owner).Handle;
        var rect = new System.Windows.Rect(0, 0, video.Width, video.Height);
        var screen = host.PointToScreen(new System.Windows.Point(0, 0));
        var output = Path.Combine(AppContext.BaseDirectory, phase + ".png");
        using var bitmap = new Bitmap(Math.Max(1, video.Width), Math.Max(1, video.Height));
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)screen.X, (int)screen.Y, 0, 0, bitmap.Size);
            Log(phase + " screen capture succeeded");
        }
        catch (Exception ex)
        {
            Log(phase + " screen capture failed: " + ex.GetType().Name + " " + ex.Message);
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try { Log(phase + " PrintWindow=" + PrintWindow(video.Handle, hdc, 2)); }
            finally { graphics.ReleaseHdc(hdc); }
        }
        bitmap.Save(output);
        var pixel = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        var background = bitmap.GetPixel(bitmap.Width / 8, bitmap.Height / 8);
        var detached = DetachedVlcWindows();
        Log(phase + " root=" + Hex(hwnd) + " video=" + Hex(video.Handle) + " parent=" + Hex(GetParent(video.Handle)) +
            " rootAncestor=" + Hex(GetAncestor(video.Handle, 2)) + " playerHwnd=" + Hex(player.Hwnd) +
            " isPlaying=" + player.IsPlaying + " time=" + player.Time + " size=" + bitmap.Size +
            " center=" + pixel.ToArgb().ToString("X8") + " background=" + background.ToArgb().ToString("X8") +
            " destroyed=" + destroyed + " detached=" + detached.Count + " screenshot=" + output);
        if (detached.Count != 0) throw new InvalidOperationException("Detached VLC windows: " + string.Join(", ", detached));
        if (destroyed != 0) throw new InvalidOperationException("Video HWND was destroyed during host move.");
        if (phase.StartsWith("stopped"))
        {
            if (player.IsPlaying || player.Hwnd != IntPtr.Zero) throw new InvalidOperationException("Stop retained active playback or drawable.");
        }
        else
        {
            if (!player.IsPlaying || player.Hwnd != video.Handle || GetAncestor(video.Handle, 2) != hwnd)
                throw new InvalidOperationException("Playback or HWND ownership changed unexpectedly.");
            if (background.R < 80 && background.G < 80 && background.B < 80)
                throw new InvalidOperationException("Captured video frame was blank.");
        }
    }
    private static List<string> DetachedVlcWindows()
    {
        var found = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != Environment.ProcessId || !IsWindowVisible(hwnd)) return true;
            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.ToString().Contains("VLC", StringComparison.OrdinalIgnoreCase)) found.Add(title.ToString());
            return true;
        }, IntPtr.Zero);
        return found;
    }
    private static void Log(string value) => File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + value + Environment.NewLine);
    private static string Hex(IntPtr value) => "0x" + value.ToInt64().ToString("X");
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder title, int maxCount);
}
