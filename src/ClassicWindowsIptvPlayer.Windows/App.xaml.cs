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
    private readonly bool _isUpdateHealthProbe = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(UpdateInstaller.HealthVariable));
    private readonly string _sessionDirectory = string.Empty;
    private readonly Exception? _startupDataError;
    private readonly AppDataLease? _dataLease;
    private readonly InstallationLease? _installationLease;
    private readonly CrashSessionMarker? _sessionMarker;
    private readonly string? _previousAbnormalSessionDirectory;

    public App()
    {
        try
        {
            _installationLease = new InstallationLease(AppContext.BaseDirectory);
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, UpdateInstaller.PendingMarker)) && !IsAuthenticatedHealthProbe())
                throw new IOException("An update was interrupted. Retain its backups and run the saved updater job with --recover before opening this installation.");
            _dataLease = AppDataLease.Acquire(AppDataPaths.Root);
        }
        catch (Exception error) { _startupDataError = error; return; }
        _sessionDirectory = Path.GetDirectoryName(AppLogger.CurrentLogPath)
            ?? AppDataPaths.Logs;
        var logsRoot = Directory.GetParent(_sessionDirectory)?.FullName
            ?? AppDataPaths.Logs;
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
        if (_startupDataError is not null)
        {
            if (_isUpdateHealthProbe) { Shutdown(1); return; }
            if (_startupDataError is MissingAccountSettingsException missing)
            {
                var choice = MessageBox.Show("Account settings and their backup are missing. You can close the app and restore a backup, or start fresh and enter your account details again. Existing caches, logs and recordings will be retained.\n\nStart fresh?",
                    "Saved data recovery", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (choice == MessageBoxResult.Yes)
                {
                    try
                    {
                        missing.StartFresh();
                        MessageBox.Show("Fresh account settings were created. Reopen the app to enter your account details. Existing caches, logs and recordings were retained.",
                            "Saved data recovery", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception error)
                    {
                        MessageBox.Show("Fresh settings could not be created. Existing files were retained.\n\n" + error.Message,
                            "Saved data recovery", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                Shutdown();
                return;
            }
            MessageBox.Show("Saved data could not be prepared. Original files were retained. Restore a known good accounts backup or retry after resolving folder permissions.\n\n" + _startupDataError.Message,
                "Saved data recovery", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
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
            if (_isUpdateHealthProbe)
            {
                ThemeManager.Apply(startupState.DarkMode);
                _ = new LoginWindow(); // Load actual account-dialog XAML without showing it.
                var account = startupState.EnsureSelectedAccount();
                _ = new MainWindow(new LoginResult { Account = account.Settings.Clone(), AccountId = account.Id }, updateHealthProbe: true);
                LibVLCSharp.Shared.Core.Initialize();
                using (var native = new LibVLCSharp.Shared.LibVLC("--no-video-title-show"))
                using (var player = new LibVLCSharp.Shared.MediaPlayer(native)) { } // No streams, providers or recordings.
                AcknowledgeUpdateStartup();
                Shutdown(0);
                return;
            }
            ImportFeedbackEndpointBootstrap(startupStore, startupState);
            ThemeManager.Apply(startupState.DarkMode);
            if (startupStore.RecoveryNotice is { } notice)
                MessageBox.Show(notice, "Saved data recovery", MessageBoxButton.OK, MessageBoxImage.Information);

            ShowUpdateResult();
            await OfferPendingCrashReportAsync(startupStore, startupState);

            var lastAccount = startupState.Accounts.FirstOrDefault(account =>
                string.Equals(account.Id, startupState.SelectedAccountId, StringComparison.OrdinalIgnoreCase) &&
                (account.HasLoggedIn || account.LastPlaylistUpdatedUtc is not null));
            if (lastAccount is not null)
            {
                var hasCache = startupStore.HasChannelCacheForAccount(lastAccount.Id);
                var mustUpdate = lastAccount.LastPlaylistUpdatedUtc is null || !hasCache;
                var login = new LoginResult
                {
                    Account = lastAccount.Settings.Clone(),
                    AccountId = lastAccount.Id,
                    UpdatePlaylist = mustUpdate || lastAccount.BackgroundPlaylistRefresh
                };
                AppLogger.Info("Automatically opening last logged-in profile. accountId=" + lastAccount.Id + "; updatePlaylist=" + login.UpdatePlaylist);
                var lastProfileWindow = new MainWindow(login);
                MainWindow = lastProfileWindow;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                lastProfileWindow.Show();
                lastProfileWindow.Activate();
                return;
            }

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
            if (_isUpdateHealthProbe) { Shutdown(1); return; }
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
        _sessionMarker?.MarkNormalExit();
        _dataLease?.Dispose();
        _installationLease?.Dispose();
        base.OnExit(e);
    }

    private static void ShowUpdateResult()
    {
        if (!Directory.Exists(AppDataPaths.Updates)) return;
        try
        {
            AuthenticatedUpdate.RejectLinks(AppDataPaths.Updates);
            foreach (var stage in Directory.EnumerateDirectories(AppDataPaths.Updates, "stage-*").Take(1000))
            {
                AuthenticatedUpdate.RejectLinks(stage);
                var result = Path.Combine(stage, "update-result.txt");
                if (!File.Exists(result)) continue;
                AuthenticatedUpdate.RejectLinks(result);
                var info = new FileInfo(result);
                if (info.Length > 8192) continue;
                var text = AppLogger.SanitizeText(File.ReadAllText(result));
                MessageBox.Show(text + "\n\nThe previous application was reopened. Your saved accounts and recordings were retained. Open the release on GitHub or retry after resolving permissions/file locks.", "Update could not be installed", MessageBoxButton.OK, MessageBoxImage.Warning);
                File.Move(result, Path.Combine(stage, "update-result.acknowledged.txt"), true);
            }
        }
        catch (IOException) { /* A diagnostic problem must not prevent ordinary startup. */ }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
    }

    private static bool IsAuthenticatedHealthProbe()
    {
        var health = Environment.GetEnvironmentVariable(UpdateInstaller.HealthVariable);
        if (string.IsNullOrEmpty(health)) return false;
        var pending = Path.Combine(AppContext.BaseDirectory, UpdateInstaller.PendingMarker);
        AuthenticatedUpdate.RejectLinks(pending);
        var journal = System.Text.Json.JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllBytes(pending));
        return journal is not null && Path.GetFullPath(health).Equals(Path.Combine(Path.GetFullPath(journal.WorkDirectory), "ready.txt"), StringComparison.OrdinalIgnoreCase);
    }

    private static void AcknowledgeUpdateStartup()
    {
        var path = Environment.GetEnvironmentVariable(UpdateInstaller.HealthVariable);
        Environment.SetEnvironmentVariable(UpdateInstaller.HealthVariable, null);
        if (string.IsNullOrWhiteSpace(path)) return;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(AppDataPaths.Updates).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid update startup check path.");
        AuthenticatedUpdate.RejectLinks(full);
        using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(System.Text.Encoding.UTF8.GetBytes("ready")); stream.Flush(true);
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

            if (AppState.TryValidateFeedbackEndpoint(state.FeedbackEndpoint, out var current) &&
                Uri.Equals(current, endpoint))
            {
                File.Delete(bootstrapPath);
                return;
            }

            // A build-supplied endpoint is the app's managed destination and
            // takes precedence over an older locally saved destination.
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
            var cacheDirectory = AppDataPaths.Cache;
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
                if (!AppState.TryValidateFeedbackEndpoint(state.FeedbackEndpoint, out _))
                    return new FeedbackDeliveryResult(false, "The report is saved on this device. Delivery is unavailable; try again later.", true);

                var result = await outbox.SendAsync(crash.Id);
                return result.Status switch
                {
                    FeedbackDeliveryStatus.Sent => new FeedbackDeliveryResult(true, "Crash report sent."),
                    FeedbackDeliveryStatus.EndpointNotConfigured => new FeedbackDeliveryResult(false, "The report is saved on this device. Delivery is unavailable; try again later.", true),
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
                ?? AppDataPaths.Logs;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), AppLogger.SanitizeException(ex));
        }
        catch
        {
            // Ignore logging errors. The message box above still shows the issue.
        }
    }
}
