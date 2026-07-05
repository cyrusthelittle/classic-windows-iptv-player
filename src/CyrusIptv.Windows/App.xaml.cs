using CyrusIptv.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;

namespace CyrusIptv.Windows;

public partial class App : WpfApplication
{
    public App()
    {
        AppLogger.Info("Windows app constructed. Log file: " + AppLogger.CurrentLogPath);

        DispatcherUnhandledException += (_, e) =>
        {
            AppLogger.Error("Dispatcher unhandled exception.", e.Exception);
            WriteRuntimeCrashLog(e.Exception);
            MessageBox.Show(
                e.Exception.ToString(),
                "Cyrus IPTV Modern error",
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

    private void OnStartup(object sender, StartupEventArgs e)
    {
        AppLogger.Info("Windows app startup begin.");

        // Important: while the login dialog is the only window, WPF's default
        // ShutdownMode=OnLastWindowClose can end the app immediately when the
        // login dialog closes. Keep the application alive until we explicitly
        // decide whether to open the main window or exit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            ThemeManager.Apply(new ConfigStore().Load().DarkMode);

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
                ex.ToString(),
                "Cyrus IPTV Modern startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
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
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CyrusIptvModern");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), ex.ToString());
        }
        catch
        {
            // Ignore logging errors. The message box above still shows the issue.
        }
    }
}
