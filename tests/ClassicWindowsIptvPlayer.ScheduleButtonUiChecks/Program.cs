using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;

internal static class Program
{
    private static MainWindow window = null!;
    private static string root = string.Empty;
    private static string accountId = string.Empty;

    private static object? Field(string name) => typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        File.AppendAllText(Path.Combine(root, "evidence.txt"), "PASS " + message + Environment.NewLine);
    }

    private static async Task WaitUntil(Func<bool> condition, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(failure);
    }

    private static T? FindDescendant<T>(DependencyObject rootElement, Func<T, bool> predicate) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(rootElement); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(rootElement, index);
            if (child is T match && predicate(match)) return match;
            var nested = FindDescendant(child, predicate);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }

    private static double Contrast(Brush foreground, Brush background)
    {
        if (foreground is not SolidColorBrush fg || background is not SolidColorBrush bg)
            throw new InvalidOperationException("Expected solid theme brushes for contrast checks.");
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var a = Luminance(fg.Color);
        var b = Luminance(bg.Color);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void ApplyDarkThemeForContrastChecks()
    {
        var type = typeof(MainWindow).Assembly.GetType("ClassicWindowsIptvPlayer.Windows.ThemeManager", throwOnError: true)!;
        type.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [true]);
    }

    private static async Task SaveViaGuideSlot(Channel channel, EpgProgramme programme, string destination)
    {
        var scheduleCallback = (Action<Channel, EpgProgramme>)typeof(MainWindow)
            .GetMethod("ScheduleProgramme", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action<Channel, EpgProgramme>), window);
        var guide = new EpgGuide(new Dictionary<string, IReadOnlyList<EpgProgramme>>
        {
            [programme.ChannelId] = [programme]
        });
        var guideType = typeof(MainWindow).Assembly.GetType("ClassicWindowsIptvPlayer.Windows.GuideGridWindow", throwOnError: true)!;
        var guideWindow = (Window)Activator.CreateInstance(guideType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [guide, new[] { channel }, channel, channel, (Func<Channel, string?>)(_ => programme.ChannelId), 0, false, null, scheduleCallback], null)!;

        var phase = 0;
        Exception? automationFailure = null;
        RoutedEventHandler loaded = (_, args) =>
        {
            if (args.OriginalSource is not Window loadedWindow || automationFailure is not null) return;
            try
            {
                if (phase == 0 && loadedWindow.GetType().Name == "ProgrammeDetailsWindow")
                {
                    phase = 1;
                    (FindDescendant<Button>(loadedWindow, button => AutomationProperties.GetName(button) == "Schedule this programme")
                        ?? throw new InvalidOperationException("Programme details did not render its Schedule button."))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (phase == 1 && loadedWindow is ScheduledRecordingEditorWindow editor)
                {
                    phase = 2;
                    var notice = FindDescendant<TextBlock>(editor,
                        text => text.Text.StartsWith("Important:", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Schedule limitation notice did not render.");
                    var noticeBorder = FindVisualAncestor<Border>(notice)
                        ?? throw new InvalidOperationException("Schedule limitation notice has no containing box.");
                    Check(Contrast(notice.Foreground, noticeBorder.Background) >= 4.5,
                        "dark-mode schedule limitation box has readable text contrast");
                    ((TextBox?)editor.FindName("DestinationBox")
                        ?? throw new InvalidOperationException("Schedule editor did not render its destination field.")).Text = destination;
                    var limitation = FindDescendant<TextBlock>(editor, text => text.Text.Contains("matching live HLS channel is already playing", StringComparison.OrdinalIgnoreCase));
                    Check(limitation is { IsVisible: true }, "schedule editor shows its matching live HLS capture limitation");
                    ((Button?)editor.FindName("ScheduleButton")
                        ?? throw new InvalidOperationException("Schedule editor did not render its Schedule button."))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            catch (Exception exception)
            {
                automationFailure = exception;
                loadedWindow.Close();
            }
        };
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, loaded, handledEventsToo: true);

        guideWindow.Owner = window;
        guideWindow.Show();
        await guideWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var slot = FindDescendant<Button>(guideWindow, button =>
            button.DataContext?.GetType().Name == "GuideSlot" &&
            button.Content?.ToString()?.Contains(programme.Title, StringComparison.Ordinal) == true);
        if (slot is null)
        {
            guideWindow.Close();
            throw new InvalidOperationException("The production guide did not render the fixture programme as a clickable slot.");
        }
        slot.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        guideWindow.Close();
        if (automationFailure is not null) throw new InvalidOperationException("The guide-slot schedule flow failed.", automationFailure);
        Check(phase == 2, "real guide-slot and programme-details clicks opened and saved through the production schedule editor");
    }

    private static async Task SaveViaBrowseNext(Channel channel, EpgProgramme programme, string destination)
    {
        var guide = new EpgGuide(new Dictionary<string, IReadOnlyList<EpgProgramme>>
        {
            [programme.ChannelId] = [programme]
        });
        var state = Field("_state") ?? throw new InvalidOperationException("Production app state is unavailable.");
        state.GetType().GetProperty("EpgEnabled")!.SetValue(state, true);
        typeof(MainWindow).GetMethod("UpdateEpgEnabledUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        var browseState = Field("_playbackState") ?? throw new InvalidOperationException("Production browse state is unavailable.");
        browseState.GetType().GetMethod("Select")!.Invoke(browseState, [channel]);
        typeof(MainWindow).GetField("_epgGuide", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, guide);
        typeof(MainWindow).GetMethod("UpdateBrowseGuide", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

        var phase = 0;
        Exception? automationFailure = null;
        RoutedEventHandler loaded = (_, args) =>
        {
            if (args.OriginalSource is not Window loadedWindow || automationFailure is not null) return;
            try
            {
                if (phase == 0 && loadedWindow.GetType().Name == "ProgrammeDetailsWindow")
                {
                    phase = 1;
                    (FindDescendant<Button>(loadedWindow, button => AutomationProperties.GetName(button) == "Schedule this programme")
                        ?? throw new InvalidOperationException("Browse Next programme details did not render Schedule."))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (phase == 1 && loadedWindow is ScheduledRecordingEditorWindow editor)
                {
                    phase = 2;
                    ((TextBox?)editor.FindName("DestinationBox")
                        ?? throw new InvalidOperationException("Browse Next schedule editor did not render its destination field.")).Text = destination;
                    ((Button?)editor.FindName("ScheduleButton")
                        ?? throw new InvalidOperationException("Browse Next schedule editor did not render Schedule."))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            catch (Exception exception)
            {
                automationFailure = exception;
                loadedWindow.Close();
            }
        };
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, loaded, handledEventsToo: true);

        var button = (Button?)window.FindName("BrowseNextButton")
            ?? throw new InvalidOperationException("Production Browse Next button was not found.");
        Check(button.IsEnabled && button.IsVisible && button.IsHitTestVisible,
            "production Browse Next button is enabled, visible, and hit-testable with a selected channel and loaded guide");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (automationFailure is not null) throw new InvalidOperationException("Browse Next schedule flow failed.", automationFailure);
        Check(phase == 2, "real Browse Next and programme-details clicks opened and saved through the production schedule editor");
    }

    private static async Task ExerciseScheduleButtons(Channel channel, ConfigStore store)
    {
        await WaitUntil(() => Field("_scheduledRecordings") is not null && Field("_tuner") is not null,
            "production player did not initialize its schedule coordinator");
        Check(window.FindName("SchedulesButton") is null,
            "production toolbar no longer exposes the removed Schedules button");
        var moreButton = (Button?)window.FindName("MorePlaybackButton")
            ?? throw new InvalidOperationException("Production More button was not found.");
        var moreMenu = moreButton.ContextMenu ?? throw new InvalidOperationException("Production More menu was not found.");
        Check(!moreMenu.Items.OfType<MenuItem>().Any(item => string.Equals(item.Header?.ToString(), "Multi-view", StringComparison.OrdinalIgnoreCase)),
            "production More menu no longer exposes Multi-view");
        ApplyDarkThemeForContrastChecks();
        var darkErrorText = (Brush)Application.Current.Resources["ErrorTextBrush"];
        var darkWindowBackground = (Brush)Application.Current.Resources["Bg0Brush"];
        Check(Contrast(darkErrorText, darkWindowBackground) >= 4.5,
            "dark-mode validation text palette has readable contrast");
        var outputFolder = store.Load().RecordingFolder;
        if (string.IsNullOrWhiteSpace(outputFolder))
            outputFolder = Path.Combine(root, "Recordings");
        Directory.CreateDirectory(outputFolder);

        var first = new EpgProgramme(channel.EpgId ?? channel.Id, "UI guide-slot schedule fixture",
            "Future fixture programme", "Fixture", DateTimeOffset.UtcNow.AddMinutes(25), DateTimeOffset.UtcNow.AddMinutes(72));
        var firstPath = Path.Combine(outputFolder, "guide-slot-fixture.ts");
        await SaveViaGuideSlot(channel, first, firstPath);

        var second = new EpgProgramme(channel.EpgId ?? channel.Id, "UI Browse Next schedule fixture",
            "Future Browse Next fixture programme", "Fixture", DateTimeOffset.UtcNow.AddMinutes(100), DateTimeOffset.UtcNow.AddMinutes(140));
        var secondPath = Path.Combine(outputFolder, "browse-next-fixture.ts");
        await SaveViaBrowseNext(channel, second, secondPath);

        var coordinator = Field("_scheduledRecordings")!;
        var jobsProperty = coordinator.GetType().GetProperty("Jobs")!;
        await WaitUntil(() => ((IEnumerable<ScheduledRecordingJob>)jobsProperty.GetValue(coordinator)!).Count(job =>
            job.ProgrammeTitle is "UI guide-slot schedule fixture" or "UI Browse Next schedule fixture") == 2,
            "both production UI schedule entry points did not persist their jobs");
        var jobs = (IEnumerable<ScheduledRecordingJob>)jobsProperty.GetValue(coordinator)!;
        var firstJob = jobs.Single(job => job.ProgrammeTitle == first.Title);
        var secondJob = jobs.Single(job => job.ProgrammeTitle == second.Title);
        var index = store.LoadScheduledRecordingIndex(accountId);
        var firstPersisted = index?.Find(accountId, firstJob.Id);
        var secondPersisted = index?.Find(accountId, secondJob.Id);
        Check(firstPersisted is not null && firstPersisted.Status == ScheduledRecordingStatus.Scheduled &&
              firstPersisted.ChannelId == channel.Id && firstPersisted.ProgrammeTitle == first.Title &&
              string.Equals(Path.GetFullPath(firstPersisted.DestinationPath), Path.GetFullPath(firstPath), StringComparison.OrdinalIgnoreCase),
            "guide-slot schedule persists its programme, channel, status, and selected destination");
        Check(secondPersisted is not null && secondPersisted.Status == ScheduledRecordingStatus.Scheduled &&
              secondPersisted.ChannelId == channel.Id && secondPersisted.ProgrammeTitle == second.Title &&
              string.Equals(Path.GetFullPath(secondPersisted.DestinationPath), Path.GetFullPath(secondPath), StringComparison.OrdinalIgnoreCase),
            "Browse Next schedule persists its programme, channel, status, and selected destination");

        Check(index is not null && index.Find(accountId, firstJob.Id) is not null && index.Find(accountId, secondJob.Id) is not null,
            "both jobs created through remaining guide and Browse Next actions persist without the removed toolbar button");
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (!args.Contains("--child", StringComparer.Ordinal))
        {
            root = Path.Combine(Path.GetTempPath(), "cyrus-schedule-buttons-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var directory in Directory.GetDirectories(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, directory);
                var top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (top.Equals("cache", StringComparison.OrdinalIgnoreCase) || top.Equals("logs", StringComparison.OrdinalIgnoreCase) || top.Equals("Recordings", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.Combine(root, relative));
            }
            foreach (var source in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, source);
                var top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (top.Equals("cache", StringComparison.OrdinalIgnoreCase) || top.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
                    top.Equals("Recordings", StringComparison.OrdinalIgnoreCase) || top.Equals("accounts.json", StringComparison.OrdinalIgnoreCase) ||
                    relative.Contains("\\accounts.", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!CreateHardLink(target, source, IntPtr.Zero)) throw new IOException("Could not stage an immutable test binary: " + relative);
            }
            var executable = Path.Combine(root, Path.GetFileName(Environment.ProcessPath!));
            using var child = Process.Start(new ProcessStartInfo(executable, "--child") { WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden })
                ?? throw new InvalidOperationException("Could not launch isolated production app process.");
            if (!child.WaitForExit(90_000))
            {
                try { child.Kill(entireProcessTree: true); } catch { }
                Console.WriteLine("FAIL schedule UI fixture timed out");
                Environment.ExitCode = 1;
                return;
            }
            var evidence = Path.Combine(root, "evidence.txt");
            Console.WriteLine(File.Exists(evidence) ? File.ReadAllText(evidence) : "FAIL app exited without evidence");
            Environment.ExitCode = child.ExitCode;
            return;
        }

        root = AppContext.BaseDirectory;
        try
        {
            var store = new ConfigStore();
            var state = store.Load();
            var saved = state.EnsureSelectedAccount();
            accountId = saved.Id;
            var account = new AccountSettings { ServerUrl = "http://127.0.0.1:1", Username = "schedule-fixture", Password = "invented" };
            saved.Name = "Schedule UI fixture";
            saved.Settings = account.Clone();
            state.Account = account.Clone();
            state.CheckForUpdatesOnStartup = false;
            state.RemoteControlEnabled = false;
            store.Save(state);
            var channel = new Channel
            {
                Id = "schedule-ui-fixture-live", Name = "Schedule UI fixture channel", Group = "Local fixtures",
                EpgId = "schedule-ui-fixture-guide", Url = "http://127.0.0.1:1/live.m3u8", MediaKind = MediaKind.Live
            };
            store.SaveChannelCache(accountId, [channel]);

            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            var startup = typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(object), typeof(StartupEventArgs)], null)!;
            app.Startup -= (StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler), app);
            window = new MainWindow(new LoginResult { Account = account, AccountId = accountId, UpdatePlaylist = false });
            app.MainWindow = window;
            window.ContentRendered += async (_, _) =>
            {
                try
                {
                    await ExerciseScheduleButtons(channel, store);
                    File.AppendAllText(Path.Combine(root, "evidence.txt"), "ALL PASS" + Environment.NewLine);
                }
                catch (Exception exception)
                {
                    File.AppendAllText(Path.Combine(root, "evidence.txt"), "FAIL " + exception + Environment.NewLine);
                    Environment.ExitCode = 1;
                }
                finally
                {
                    try { typeof(MainWindow).GetMethod("StopPlayback", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(window, null); } catch { }
                    await Task.Delay(300);
                    try { window.Close(); } catch { }
                    app.Shutdown();
                }
            };
            window.Show();
            app.Run();
        }
        catch (Exception exception)
        {
            File.AppendAllText(Path.Combine(root, "evidence.txt"), "FAIL startup " + exception + Environment.NewLine);
            Environment.ExitCode = 1;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
}
