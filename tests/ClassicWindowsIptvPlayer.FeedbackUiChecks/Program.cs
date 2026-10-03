using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;
using Delivery = ClassicWindowsIptvPlayer.Windows.FeedbackDeliveryResult;

internal static class Program
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "cyrus-feedback-ui-" + Guid.NewGuid().ToString("N"));
    static int checks;
    static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        checks++; File.AppendAllText(Path.Combine(Root, "evidence.txt"), "PASS " + name + Environment.NewLine);
    }
    static T Control<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    static IEnumerable<Button> Buttons(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is Button button) yield return button;
            foreach (var descendant in Buttons(child)) yield return descendant;
        }
    }
    static void Click(Window window, string label) => Buttons(window).Single(b => b.Content?.ToString() == label)
        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(Root, name + ".png")); png.Save(file);
        foreach (var button in Buttons(window))
        {
            var at = button.TransformToAncestor(window).Transform(new Point());
            Check(button.ActualWidth > 0 && button.ActualHeight > 0 && at.X >= 0 && at.Y >= 0 &&
                at.X + button.ActualWidth <= window.ActualWidth && at.Y + button.ActualHeight <= window.ActualHeight,
                name + " action visible within window: " + button.Content);
        }
    }
    static bool? Modal(Window window, Func<Task> actions)
    {
        Exception? failure = null;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timeout.Tick += (_, _) => { failure = new TimeoutException("Dialog deadline"); window.Close(); };
        window.ContentRendered += async (_, _) =>
        {
            try { await actions(); }
            catch (Exception ex) { failure = ex; window.Close(); }
        };
        timeout.Start();
        var result = window.ShowDialog(); timeout.Stop();
        if (failure is not null) throw failure;
        return result;
    }
    static async Task Wait(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++) { if (predicate()) return; await Task.Delay(20); }
        throw new TimeoutException("Dialog action did not finish");
    }
    [STAThread]
    static int Main()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable(AppDataPaths.OverrideVariable, Path.Combine(Root, "data"));
        try
        {
            var prior = Path.Combine(AppDataPaths.Logs, "session-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(prior);
            File.WriteAllText(Path.Combine(prior, "runtime-crash.log"), "password=fixture-startup-secret\ncontrolled abnormal exit");
            new CrashSessionMarker(AppDataPaths.Logs).BeginSession(prior);
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
            var startup = typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(object), typeof(StartupEventArgs)], null)!;
            app.Startup -= (StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler), app);
            StartupChecks(app);
            Run();
            MenuRetryChecks(app);
            app.Shutdown();
            File.AppendAllText(Path.Combine(Root, "evidence.txt"), $"ALL PASS {checks}" + Environment.NewLine);
            Console.WriteLine(File.ReadAllText(Path.Combine(Root, "evidence.txt"))); return 0;
        }
        catch (Exception ex) { File.AppendAllText(Path.Combine(Root, "evidence.txt"), "FAIL " + ex); Console.Error.WriteLine(ex); return 1; }
        finally { Console.WriteLine("Isolated evidence: " + Root); }
    }
    static void MenuRetryChecks(App app)
    {
        var state = new ConfigStore().Load(); var account = state.EnsureSelectedAccount();
        using var handler = new MockDelivery(); using var client = new HttpClient(handler);
        using var outbox = new FeedbackOutbox(Path.Combine(AppDataPaths.Cache, "feedback-outbox.dat"), new Uri("https://fixture.invalid/feedback"), client);
        outbox.EnqueueFeedbackAsync("Saved message without logs").GetAwaiter().GetResult();
        var main = new MainWindow(new LoginResult { Account = account.Settings.Clone(), AccountId = account.Id }, updateHealthProbe: true);
        typeof(MainWindow).GetField("_feedbackOutbox", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, outbox);
        main.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        Exception? failure = null; bool acted = false;
        timer.Tick += (_, _) =>
        {
            var dialog = app.Windows.OfType<FeedbackWindow>().FirstOrDefault();
            if (dialog is null || !dialog.IsVisible || acted) return;
            acted = true;
            try
            {
                Check(Control<TextBox>(dialog, "MessageBox").Text == "Saved message without logs", "actual Feedback menu restores queued message");
                Check(Control<CheckBox>(dialog, "IncludeLogsCheckBox").IsChecked == false, "actual Feedback menu retains excluded logs when reopened for retry");
                Check(handler.Calls == 0, "reopening queued feedback does not send automatically");
                Click(dialog, "Send");
            }
            catch (Exception ex) { failure = ex; dialog.Close(); }
        };
        try
        {
            var host = new Window { Width = 100, Height = 100, ShowInTaskbar = false, Content = "Fixture host" };
            Modal(host, () =>
            {
                timer.Start();
                var help = ((Menu)main.FindName("AppMenu")).Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "Help");
                help.Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "Feedback...").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                timer.Stop();
                if (failure is not null) throw failure;
                Check(acted && handler.Calls == 1 && outbox.GetPendingAsync().GetAwaiter().GetResult().Count == 0, "actual MainWindow feedback send delivers queued item once and clears it");
                using var payload = JsonDocument.Parse(handler.Payloads.Single());
                Check(!payload.RootElement.TryGetProperty("log", out _), "actual MainWindow retry sends no log after user excluded it");
                host.Close(); return Task.CompletedTask;
            });
        }
        finally { timer.Stop(); main.Close(); }
    }
    static void StartupChecks(App app)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var store = new ConfigStore();
        var state = store.Load();
        var bootstrap = Path.Combine(AppContext.BaseDirectory, "feedback-endpoint.bootstrap");
        // The fixture owns this new build directory; never overwrite an existing bootstrap.
        using (var file = new FileStream(bootstrap, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(file)) writer.Write("https://fixture.invalid/feedback");
        typeof(App).GetMethod("ImportFeedbackEndpointBootstrap", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [store, state]);
        Check(!File.Exists(bootstrap) && store.Load().FeedbackEndpoint == "https://fixture.invalid/feedback", "production bootstrap secures endpoint and removes plaintext fixture");
        Check(!File.ReadAllText(Path.Combine(AppDataPaths.Root, "accounts.json")).Contains("fixture.invalid"), "bootstrapped endpoint is protected on disk");
        var offer = typeof(App).GetMethod("OfferPendingCrashReportAsync", flags)!;
        using var queued = new FeedbackOutbox(Path.Combine(AppDataPaths.Cache, "feedback-outbox.dat"));
        foreach (var action in new[] { "Later", "Don't send" })
        {
            var host = new Window { Width = 100, Height = 100, ShowInTaskbar = false, Content = "Fixture host" };
            var drive = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            bool clicked = false;
            Exception? driveFailure = null;
            drive.Tick += (_, _) =>
            {
                var dialog = app.Windows.OfType<CrashReportWindow>().FirstOrDefault();
                if (dialog is null || !dialog.IsVisible || clicked) return;
                clicked = true;
                try
                {
                    Check(!Control<TextBox>(dialog, "LogPreview").Text.Contains("fixture-startup-secret"), "production next-launch prompt redacts prior crash log");
                    Capture(dialog, "startup-" + (action == "Later" ? "later" : "dismiss"));
                    Click(dialog, action);
                }
                catch (Exception ex) { driveFailure = ex; dialog.Close(); }
            };
            try
            {
                Modal(host, async () =>
                {
                    drive.Start();
                    await (Task)offer.Invoke(app, [store, state])!;
                    if (driveFailure is not null) throw driveFailure;
                    drive.Stop(); host.Close();
                });
            }
            finally { drive.Stop(); }
            Check(clicked && queued.GetPendingAsync().GetAwaiter().GetResult().Count == (action == "Later" ? 1 : 0),
                "actual startup " + action + " retains or dismisses crash queue");
        }
    }
    static void Run()
    {
        using var handler = new MockDelivery(); using var client = new HttpClient(handler);
        using var outbox = new FeedbackOutbox(Path.Combine(Root, "outbox.dat"), new Uri("https://fixture.invalid/feedback"), client);
        Delivery Convert(ClassicWindowsIptvPlayer.Core.FeedbackDeliveryResult result) =>
            new(result.Status == FeedbackDeliveryStatus.Sent, result.UserMessage, result.Status == FeedbackDeliveryStatus.QueuedForRetry);
        var initialCalls = handler.Calls;
        var cancel = new FeedbackWindow((_, _) => throw new Exception("Cancel attempted delivery"));
        Modal(cancel, () => { Check(Control<CheckBox>(cancel, "IncludeLogsCheckBox").IsChecked == true, "feedback logs default selected"); Capture(cancel, "feedback-light"); Click(cancel, "Cancel"); return Task.CompletedTask; });
        Check(handler.Calls == initialCalls, "opening and canceling feedback sends nothing");
        string? feedbackId = null;
        FeedbackWindow? feedback = null;
        feedback = new FeedbackWindow(async (message, includeLogs) =>
        {
            var log = includeLogs ? "password=fixture-secret https://fixture.invalid/live/user/pass/1.ts" : null;
            if (feedbackId is null) feedbackId = (await outbox.EnqueueFeedbackAsync(message, log)).Id;
            else await outbox.UpdateFeedbackAsync(feedbackId, message, log);
            return Convert(await outbox.SendAsync(feedbackId));
        });
        var sent = Modal(feedback, async () =>
        {
            Click(feedback, "Send");
            Check(Control<TextBlock>(feedback, "StatusText").Text.Contains("Write a message"), "empty feedback rejected visibly");
            Check(handler.Calls == initialCalls, "empty feedback does not send");
            Control<TextBox>(feedback, "MessageBox").Text = "  Fixture feedback  ";
            Control<CheckBox>(feedback, "IncludeLogsCheckBox").IsChecked = false;
            handler.Fail = true; Click(feedback, "Send");
            await Wait(() => Control<Button>(feedback, "SendButton").IsEnabled);
            Check(Control<TextBlock>(feedback, "StatusText").Text.Contains("queued for retry"), "failed feedback displays retained retry status");
            Check((await outbox.GetPendingAsync()).Single().Log is null, "unchecked log choice survives queued failure");
            Capture(feedback, "feedback-failed");
            handler.Fail = false; Click(feedback, "Send");
        });
        Check(sent == true && handler.Calls == initialCalls + 2 && outbox.GetPendingAsync().GetAwaiter().GetResult().Count == 0, "explicit feedback retry succeeds and removes queue item");
        using (var payload = JsonDocument.Parse(handler.Payloads.Last()))
            Check(payload.RootElement.GetProperty("type").GetString() == "feedback" && !payload.RootElement.TryGetProperty("log", out _) &&
                payload.RootElement.GetProperty("message").GetString() == "Fixture feedback", "actual dialog emits message-only payload with trimmed text");

        feedbackId = null;
        var withLogs = new FeedbackWindow(async (message, include) =>
        {
            Check(include, "default feedback action includes logs");
            var item = await outbox.EnqueueFeedbackAsync(message, "password=fixture-secret https://fixture.invalid/live/user/pass/1.ts");
            return Convert(await outbox.SendAsync(item.Id));
        }, "With logs");
        Modal(withLogs, () => { Click(withLogs, "Send"); return Task.CompletedTask; });
        Check(handler.Payloads.Last().Contains("\"log\"") && !handler.Payloads.Last().Contains("fixture-secret") &&
            !handler.Payloads.Last().Contains("/user/pass/"), "feedback checkbox sends redacted log payload");

        var crashItem = outbox.EnqueueCrashAsync(null, "password=fixture-crash-secret\ncontrolled crash").GetAwaiter().GetResult();
        foreach (var action in new[] { "Later", "Don't send" })
        {
            var before = handler.Calls;
            var crash = new CrashReportWindow(crashItem.Log!, (_, _) => throw new Exception("Dismiss attempted delivery"));
            Modal(crash, () => { Check(Control<TextBox>(crash, "LogPreview").IsReadOnly && !Control<TextBox>(crash, "LogPreview").Text.Contains("fixture-crash-secret"), "crash preview read-only and redacted"); Capture(crash, "crash-" + (action == "Later" ? "later" : "dismiss")); Click(crash, action); return Task.CompletedTask; });
            Check(crash.Choice == (action == "Later" ? CrashReportChoice.Later : CrashReportChoice.DontSend) && handler.Calls == before, action + " records explicit choice without sending");
            if (crash.Choice == CrashReportChoice.DontSend) outbox.DismissAsync(crashItem.Id).GetAwaiter().GetResult();
            Check(outbox.GetPendingAsync().GetAwaiter().GetResult().Count == (action == "Later" ? 1 : 0), action + " retains or explicitly dismisses stored report");
        }
        crashItem = outbox.EnqueueCrashAsync(null, "controlled second crash").GetAwaiter().GetResult();
        var report = new CrashReportWindow(crashItem.Log!, async (_, message) =>
        { await outbox.UpdateMessageAsync(crashItem.Id, message); return Convert(await outbox.SendAsync(crashItem.Id)); });
        var crashResult = Modal(report, async () =>
        {
            Control<TextBox>(report, "MessageBox").Text = " Optional fixture message "; handler.Fail = true;
            Click(report, "Send"); await Wait(() => Control<Button>(report, "SendButton").IsEnabled);
            Check(report.Choice == CrashReportChoice.Later && (await outbox.GetPendingAsync()).Count == 1, "failed crash send stays open with report retained");
            Capture(report, "crash-failed"); handler.Fail = false; Click(report, "Send");
        });
        Check(crashResult == true && report.Choice == CrashReportChoice.Sent && outbox.GetPendingAsync().GetAwaiter().GetResult().Count == 0, "crash retry records Sent and clears delivered report");
        using var crashPayload = JsonDocument.Parse(handler.Payloads.Last());
        Check(crashPayload.RootElement.GetProperty("type").GetString() == "crash" && crashPayload.RootElement.GetProperty("message").GetString() == "Optional fixture message", "crash payload includes trimmed optional message");
        var applyTheme = typeof(App).Assembly.GetType("ClassicWindowsIptvPlayer.Windows.ThemeManager")!.GetMethod("Apply")!;
        applyTheme.Invoke(null, [true]);
        var darkFeedback = new FeedbackWindow((_, _) => Task.FromResult(new Delivery(false, "A long controlled failure message that must wrap and leave every action reachable.", true))) { Width = 440 };
        Modal(darkFeedback, async () =>
        {
            Control<TextBox>(darkFeedback, "MessageBox").Text = "Dark fixture";
            Click(darkFeedback, "Send"); await Wait(() => Control<Button>(darkFeedback, "SendButton").IsEnabled);
            Capture(darkFeedback, "feedback-dark-minimum"); Click(darkFeedback, "Cancel");
        });
        var darkCrash = new CrashReportWindow("Controlled log", (_, _) => Task.FromResult(new Delivery(false, "A long controlled failure message that must wrap and leave every action reachable.", true))) { Width = 560, Height = 440 };
        Modal(darkCrash, async () =>
        {
            Click(darkCrash, "Send"); await Wait(() => Control<Button>(darkCrash, "SendButton").IsEnabled);
            Capture(darkCrash, "crash-dark-minimum"); Click(darkCrash, "Later");
        });
        applyTheme.Invoke(null, [false]);
    }
    sealed class MockDelivery : HttpMessageHandler
    {
        public bool Fail; public int Calls; public List<string> Payloads = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; Payloads.Add(await request.Content!.ReadAsStringAsync(token)); return new(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent); }
    }
}
