using ClassicWindowsIptvPlayer.Core;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class App : WpfApplication
{
    private readonly string _sessionDirectory;
    private readonly CrashSessionMarker _sessionMarker;
    private readonly string? _previousAbnormalSessionDirectory;

    public App()
    {
        _sessionDirectory = Path.GetDirectoryName(AppLogger.CurrentLogPath)
            ?? Path.Combine(AppContext.BaseDirectory, "logs");
        var logsRoot = Directory.GetParent(_sessionDirectory)?.FullName
            ?? Path.Combine(AppContext.BaseDirectory, "logs");
        _sessionMarker = new CrashSessionMarker(logsRoot);
        try { _previousAbnormalSessionDirectory = _sessionMarker.BeginSession(_sessionDirectory); }
        catch { _previousAbnormalSessionDirectory = null; }
        AppLogger.Info("Windows app constructed.");

        DispatcherUnhandledException += (_, e) =>
        {
            AppLogger.Error("Dispatcher unhandled exception.", e.Exception);
            WriteRuntimeCrashLog(e.Exception);
            MessageBox.Show(
                AppLogger.SanitizeException(e.Exception),
                "Classic Windows IPTV Player error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLogger.Error("AppDomain unhandled exception.", ex);
                WriteRuntimeCrashLog(ex);
            }
            else
            {
                var wrapped = new InvalidOperationException("Unhandled non-exception crash: " + e.ExceptionObject);
                AppLogger.Error("AppDomain unhandled non-exception crash.", wrapped);
                WriteRuntimeCrashLog(wrapped);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLogger.Error("Unobserved task exception.", e.Exception);
            WriteRuntimeCrashLog(e.Exception);
            e.SetObserved();
        };
    }

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        AppLogger.Info("Windows app startup begin.");

        // Important: while the login dialog is the only window, WPF's default
        // ShutdownMode=OnLastWindowClose can end the app immediately when the
        // login dialog closes. Keep the application alive until we explicitly
        // decide whether to open the main window or exit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            var startupStore = new ConfigStore();
            var startupState = startupStore.Load();
            ImportFeedbackEndpointBootstrap(startupStore, startupState);
            ThemeManager.Apply(startupState.DarkMode);
            if (startupStore.RecoveryNotice is { } notice)
                MessageBox.Show(notice, "Saved data recovery", MessageBoxButton.OK, MessageBoxImage.Information);

            await OfferPendingCrashReportAsync(startupStore, startupState);

            var loginWindow = new LoginWindow();
            var result = loginWindow.ShowDialog();
            if (result != true)
            {
                AppLogger.Info("Login cancelled. Shutting down.");
                Shutdown();
                return;
            }

            AppLogger.Info("Login accepted. Opening main window. accountId=" + loginWindow.LoginResult.AccountId + "; updatePlaylist=" + loginWindow.LoginResult.UpdatePlaylist);
            var mainWindow = new MainWindow(loginWindow.LoginResult);
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
            mainWindow.Activate();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Startup failed.", ex);
            WriteStartupCrashLog(ex);
            MessageBox.Show(
                AppLogger.SanitizeException(ex),
                "Classic Windows IPTV Player startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sessionMarker.MarkNormalExit();
        base.OnExit(e);
    }

    private static void ImportFeedbackEndpointBootstrap(ConfigStore store, AppState state)
    {
        var bootstrapPath = Path.Combine(AppContext.BaseDirectory, "feedback-endpoint.bootstrap");
        if (!File.Exists(bootstrapPath)) return;

        try
        {
            if (new FileInfo(bootstrapPath).Length > AppState.FeedbackEndpointMaxLength)
                throw new InvalidDataException();
            var supplied = File.ReadAllText(bootstrapPath).Trim();
            if (!AppState.TryValidateFeedbackEndpoint(supplied, out var endpoint))
                throw new InvalidDataException();

            if (!string.IsNullOrWhiteSpace(state.FeedbackEndpoint))
            {
                if (AppState.TryValidateFeedbackEndpoint(state.FeedbackEndpoint, out var current) &&
                    Uri.Equals(current, endpoint))
                    File.Delete(bootstrapPath);
                return;
            }

            state.FeedbackEndpoint = endpoint!.AbsoluteUri;
            store.Save(state);
            var persisted = store.Load().FeedbackEndpoint;
            if (!string.Equals(persisted, state.FeedbackEndpoint, StringComparison.Ordinal))
                throw new InvalidDataException();

            // The app has now re-saved the value using this Windows user's DPAPI key.
            File.Delete(bootstrapPath);
        }
        catch
        {
            MessageBox.Show("The preconfigured feedback destination could not be secured. The app will continue; no message was sent.",
                "Feedback setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task OfferPendingCrashReportAsync(ConfigStore store, AppState state)
    {
        FeedbackOutbox? outbox = null;
        try
        {
            var cacheDirectory = Path.Combine(AppContext.BaseDirectory, "cache");
            Directory.CreateDirectory(cacheDirectory);
            outbox = CreateFeedbackOutbox(state, Path.Combine(cacheDirectory, "feedback-outbox.dat"));
            var pending = await outbox.GetPendingAsync();
            var crash = pending.FirstOrDefault(item => item.Type == "crash");
            if (crash is null && _previousAbnormalSessionDirectory is not null)
            {
                crash = await outbox.EnqueueCrashAsync(null, ReadSessionDiagnostics(_previousAbnormalSessionDirectory));
            }
            if (crash is null) return;

            CrashReportWindow? dialog = null;
            dialog = new CrashReportWindow(crash.Log ?? string.Empty, async (log, message) =>
            {
                await outbox.UpdateMessageAsync(crash.Id, message);
                if (!AppState.TryValidateFeedbackEndpoint(state.FeedbackEndpoint, out var endpoint))
                {
                    var endpointDialog = new FeedbackEndpointWindow(state.FeedbackEndpoint) { Owner = dialog! };
                    if (endpointDialog.ShowDialog() != true ||
                        !AppState.TryValidateFeedbackEndpoint(endpointDialog.Endpoint, out endpoint))
                        return new FeedbackDeliveryResult(false, "The report is saved on this device. Set an HTTPS endpoint to retry.", true);
                    state.FeedbackEndpoint = endpointDialog.Endpoint ?? string.Empty;
                    store.Save(state);
                    outbox.ConfigureEndpoint(endpoint!);
                }

                var result = await outbox.SendAsync(crash.Id);
                return result.Status switch
                {
                    FeedbackDeliveryStatus.Sent => new FeedbackDeliveryResult(true, "Crash report sent."),
                    FeedbackDeliveryStatus.EndpointNotConfigured => new FeedbackDeliveryResult(false, "The report is saved on this device. Set an HTTPS endpoint to retry.", true),
                    FeedbackDeliveryStatus.QueuedForRetry => new FeedbackDeliveryResult(false, "Delivery failed. The report is saved on this device and can be retried.", true),
                    _ => new FeedbackDeliveryResult(false, "The report remains saved on this device.", true)
                };
            });

            dialog.ShowDialog();
            if (dialog.Choice == CrashReportChoice.DontSend)
                await outbox.DismissAsync(crash.Id);
        }
        catch
        {
            MessageBox.Show("A saved crash report could not be opened. The app will continue, and the existing report data was left untouched.",
                "Crash report unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { outbox?.Dispose(); }
    }

    private static FeedbackOutbox CreateFeedbackOutbox(AppState state, string path)
    {
        Uri? endpoint = AppState.TryValidateFeedbackEndpoint(state.FeedbackEndpoint, out var configured)
            ? configured
            : null;
        return new FeedbackOutbox(path, endpoint);
    }

    private static string ReadSessionDiagnostics(string directory)
    {
        var sections = new System.Collections.Generic.List<string>();
        foreach (var name in new[] { "startup-crash.log", "runtime-crash.log", "app.log", "app.previous.log" })
        {
            var path = Path.Combine(directory, name);
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                const int maxRead = 256 * 1024;
                if (stream.Length > maxRead) stream.Seek(-maxRead, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(text)) sections.Add(text);
            }
            catch { /* Omit unreadable diagnostic files; never block startup. */ }
        }
        return sections.Count == 0 ? "No diagnostic log was available for this session." : string.Join("\n", sections);
    }

    private static void WriteStartupCrashLog(Exception ex)
    {
        WriteCrashLog("startup-crash.log", ex);
    }

    private static void WriteRuntimeCrashLog(Exception ex)
    {
        WriteCrashLog("runtime-crash.log", ex);
    }

    private static void WriteCrashLog(string fileName, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppLogger.CurrentLogPath)
                ?? Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), AppLogger.SanitizeException(ex));
        }
        catch
        {
            // Ignore logging errors. The message box above still shows the issue.
        }
    }
}
