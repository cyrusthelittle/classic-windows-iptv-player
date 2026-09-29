using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using ClassicWindowsIptvPlayer.Core;
using ClassicWindowsIptvPlayer.Windows;
using LibVLCSharp.Shared;
using CaptureOutcome = ClassicWindowsIptvPlayer.Windows.RecordingOutcome;

internal static class Program
{
    private static MainWindow window = null!;
    private static string root = string.Empty;
    private static string accountId = string.Empty;
    private static object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
    private static object? Service => Field("_recordingService");
    private static object? CallService(string name, params object?[] args)
    {
        var service = Service ?? throw new InvalidOperationException("MainWindow recording service was not initialized.");
        var method = service.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public).Single(item => item.Name == name && item.GetParameters().Length == args.Length);
        return method.Invoke(service, args);
    }
    private static object? Call(string name, params object?[] args) => typeof(MainWindow)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(method => method.Name == name && method.GetParameters().Length == args.Length)
        .Invoke(window, args);
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        File.AppendAllText(Path.Combine(root, "evidence.txt"), "PASS " + name + Environment.NewLine);
    }
    private static async Task WaitUntil(Func<bool> condition, string failure, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < (timeout ?? TimeSpan.FromSeconds(15)))
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(failure);
    }
    private static async Task WaitPlaying()
    {
        await WaitUntil(() => Field("_mediaPlayer") is MediaPlayer { IsPlaying: true }, "production playback did not start", TimeSpan.FromSeconds(20));
    }

    private static T? FindDescendant<T>(DependencyObject rootElement, Func<T, bool> predicate) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(rootElement);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(rootElement, index);
            if (child is T match && predicate(match)) return match;
            var nested = FindDescendant(child, predicate);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static Window? FindOpenWindow(string typeName) => Application.Current.Windows
        .OfType<Window>().FirstOrDefault(item => item.GetType().Name == typeName);

    private static async Task ExerciseGuideScheduleManagerFlow(Channel channel, ConfigStore store)
    {
        var start = DateTimeOffset.UtcNow.AddDays(2);
        start = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, TimeSpan.Zero);
        var programme = new EpgProgramme(channel.EpgId ?? channel.Id, "UI flow scheduled fixture",
            "Harness-created future programme", "Fixture", start, start.AddMinutes(47));
        var guide = new EpgGuide(new Dictionary<string, IReadOnlyList<EpgProgramme>>
        {
            [programme.ChannelId] = [programme]
        });
        var scheduleCallback = (Action<Channel, EpgProgramme>)typeof(MainWindow)
            .GetMethod("ScheduleProgramme", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action<Channel, EpgProgramme>), window);
        var guideType = typeof(MainWindow).Assembly.GetType("ClassicWindowsIptvPlayer.Windows.GuideGridWindow", throwOnError: true)!;
        var guideWindow = (Window)Activator.CreateInstance(guideType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [guide, new[] { channel }, channel, channel, (Func<Channel, string?>)(_ => programme.ChannelId), 0, false, null, scheduleCallback], culture: null)!;
        var openProgramme = guideType.GetMethod("OpenProgramme", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var outputFolder = store.Load().RecordingFolder;
        if (string.IsNullOrWhiteSpace(outputFolder) || !Directory.Exists(outputFolder))
            throw new InvalidOperationException("The harness recording folder was not available to the schedule UI.");

        Exception? automationFailure = null;
        var phase = 0;
        var sourceLimitationVisible = false;
        RoutedEventHandler scheduleWindowLoaded = (_, args) =>
        {
            if (args.OriginalSource is not Window loadedWindow || automationFailure is not null) return;
            try
            {
                if (phase == 0 && loadedWindow.GetType().Name == "ProgrammeDetailsWindow")
                {
                    phase = 1;
                    var button = FindDescendant<Button>(loadedWindow,
                        item => AutomationProperties.GetName(item) == "Schedule this programme")
                        ?? throw new InvalidOperationException("Guide programme details did not render the Schedule action.");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (phase == 1 && loadedWindow is ScheduledRecordingEditorWindow editor)
                {
                    phase = 2;
                    var warning = FindDescendant<TextBlock>(editor, item =>
                        item.Text.Contains("matching live HLS channel is already playing", StringComparison.OrdinalIgnoreCase) &&
                        item.Text.Contains("will not switch channels", StringComparison.OrdinalIgnoreCase) &&
                        item.Text.Contains("wake the PC", StringComparison.OrdinalIgnoreCase));
                    sourceLimitationVisible = warning is { IsVisible: true };
                    Check(sourceLimitationVisible,
                        "schedule editor visibly warns before saving that matching live HLS playback is required and it will not switch, open another stream, or wake the PC");
                    var destination = (TextBox?)editor.FindName("DestinationBox")
                        ?? throw new InvalidOperationException("Schedule editor did not expose its output path field.");
                    destination.Text = Path.Combine(outputFolder, "ui-flow-scheduled-fixture.ts");
                    ((ComboBox?)editor.FindName("PrePaddingBox")!).SelectedItem = 5;
                    ((ComboBox?)editor.FindName("PostPaddingBox")!).SelectedItem = 10;
                    ((Button?)editor.FindName("ScheduleButton"))!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            catch (Exception exception)
            {
                automationFailure = exception;
                loadedWindow.Close();
            }
        };
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, scheduleWindowLoaded, handledEventsToo: true);
        guideWindow.Owner = window;
        guideWindow.Show();
        try
        {
            openProgramme.Invoke(guideWindow, [channel, programme]);
        }
        finally
        {
            guideWindow.Close();
        }
        if (automationFailure is not null) throw new InvalidOperationException("The production guide-to-editor UI automation failed.", automationFailure);
        Check(phase == 2 && sourceLimitationVisible,
            "production guide programme details opened the real schedule editor, displayed its source limitation, and saved via its Schedule button");

        var coordinator = Field("_scheduledRecordings") ?? throw new InvalidOperationException("MainWindow schedule coordinator was not initialized.");
        var jobsProperty = coordinator.GetType().GetProperty("Jobs")!;
        await WaitUntil(() => ((IEnumerable<ScheduledRecordingJob>)jobsProperty.GetValue(coordinator)!).Any(job => job.ProgrammeTitle == programme.Title),
            "production schedule coordinator did not persist the guide-created job", TimeSpan.FromSeconds(10));
        var job = ((IEnumerable<ScheduledRecordingJob>)jobsProperty.GetValue(coordinator)!).Single(item => item.ProgrammeTitle == programme.Title);
        var reloadedIndex = store.LoadScheduledRecordingIndex(accountId);
        var persisted = reloadedIndex?.Find(accountId, job.Id);
        var expectedPath = Path.Combine(outputFolder, "ui-flow-scheduled-fixture.ts");
        Check(persisted is not null && persisted.Id == job.Id && persisted.AccountId == accountId &&
              persisted.ChannelId == channel.Id && persisted.ChannelName == channel.Name &&
              persisted.ProgrammeTitle == programme.Title && persisted.ProgrammeStartUtc == programme.Start &&
              persisted.ProgrammeEndUtc == programme.Stop && persisted.PrePadding == TimeSpan.FromMinutes(5) &&
              persisted.PostPadding == TimeSpan.FromMinutes(10) &&
              persisted.RequestedCaptureStartUtc == programme.Start.AddMinutes(-5) &&
              persisted.RequestedCaptureEndUtc == programme.Stop.AddMinutes(10) &&
              string.Equals(Path.GetFullPath(persisted.DestinationPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase) &&
              persisted.Status == ScheduledRecordingStatus.Scheduled,
            "guide-created schedule persists programme, channel/account, UTC window, padding, status, and selected output path");

        Exception? managerFailure = null;
        var managerSawJob = false;
        var managerCancelledJob = false;
        RoutedEventHandler managerLoaded = (_, args) =>
        {
            if (args.OriginalSource is not ScheduledRecordingsWindow manager) return;
            manager.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                try
                {
                    var grid = (DataGrid?)manager.FindName("JobsGrid")
                        ?? throw new InvalidOperationException("Schedule manager did not render its jobs grid.");
                    ((Button?)manager.FindName("RefreshButton"))!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var row = grid.Items.Cast<object>().SingleOrDefault(item =>
                        (string?)item.GetType().GetProperty("ProgrammeTitle")?.GetValue(item) == programme.Title &&
                        (string?)item.GetType().GetProperty("ChannelName")?.GetValue(item) == channel.Name &&
                        (string?)item.GetType().GetProperty("StatusText")?.GetValue(item) == ScheduledRecordingStatus.Scheduled.ToString());
                    managerSawJob = row is not null;
                    if (row is not null)
                    {
                        grid.SelectedItem = row;
                        var cancelButton = (Button?)manager.FindName("CancelJobButton")
                            ?? throw new InvalidOperationException("Schedule manager did not render its Cancel action.");
                        Check(cancelButton.IsEnabled, "schedule manager enables Cancel for the selected scheduled job");
                        var confirmTask = ClickYesOnNextCancelDialogAsync(TimeSpan.FromSeconds(5));
                        cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await confirmTask;
                        await WaitUntil(() => ((IEnumerable<ScheduledRecordingJob>)jobsProperty.GetValue(coordinator)!)
                            .Any(item => item.Id == job.Id && item.Status == ScheduledRecordingStatus.Cancelled),
                            "schedule manager Cancel action did not update the persisted job state", TimeSpan.FromSeconds(5));
                        managerCancelledJob = true;
                    }
                }
                catch (Exception exception) { managerFailure = exception; }
                finally { manager.Close(); }
            }));
        };
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, managerLoaded, handledEventsToo: true);
        var showManager = typeof(MainWindow).GetMethod("OpenScheduledRecordings_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
        showManager.Invoke(window, [window, new RoutedEventArgs()]);
        if (managerFailure is not null) throw new InvalidOperationException("The production schedule manager UI automation failed.", managerFailure);
        Check(managerSawJob, "production schedule manager opens from MainWindow and visibly lists the persisted guide-created programme");
        Check(managerCancelledJob && store.LoadScheduledRecordingIndex(accountId)?.Find(accountId, job.Id)?.Status == ScheduledRecordingStatus.Cancelled,
            "user-facing schedule manager confirmation cancels the selected job and persists the cancelled state");
    }

    private static async Task ClickYesOnNextCancelDialogAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var dialog = FindWindow(null, "Cancel scheduled recording");
            if (dialog != IntPtr.Zero)
            {
                var yes = GetDlgItem(dialog, 6); // IDYES
                if (yes != IntPtr.Zero)
                {
                    SendMessage(yes, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
                    return;
                }
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
        throw new TimeoutException("The schedule cancellation confirmation dialog did not appear.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int controlId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    private static void CreateHardLinkOrThrow(string target, string source)
    {
        if (CreateHardLink(target, source, IntPtr.Zero)) return;
        var error = Marshal.GetLastWin32Error();
        throw new IOException($"Could not stage immutable harness file as a same-volume NTFS hardlink. Source: '{source}', target: '{target}', Win32 error: {error}. No copy fallback was attempted.", new System.ComponentModel.Win32Exception(error));
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (!args.Contains("--child", StringComparer.Ordinal))
        {
            root = Path.Combine(Path.GetTempPath(), "cyrus-recording-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var directory in Directory.GetDirectories(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relativeDirectory = Path.GetRelativePath(AppContext.BaseDirectory, directory);
                var firstDirectory = relativeDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (firstDirectory.Equals("cache", StringComparison.OrdinalIgnoreCase) || firstDirectory.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
                    firstDirectory.Equals("Recordings", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.Combine(root, relativeDirectory));
            }
            foreach (var source in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppContext.BaseDirectory, source);
                var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (first.Equals("cache", StringComparison.OrdinalIgnoreCase) || first.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
                    first.Equals("Recordings", StringComparison.OrdinalIgnoreCase) || first.Equals("accounts.json", StringComparison.OrdinalIgnoreCase) ||
                    relative.Contains("\\accounts.", StringComparison.OrdinalIgnoreCase) || relative.Contains("/accounts.", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CreateHardLinkOrThrow(target, source);
            }
            Console.WriteLine("Isolated app directory: " + root);
            var executable = Path.Combine(root, Path.GetFileName(Environment.ProcessPath!));
            using var child = Process.Start(new ProcessStartInfo(executable, "--child") { WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden });
            if (child is null) throw new InvalidOperationException("Could not launch isolated production-window process.");
            if (!child.WaitForExit(120_000))
            {
                try { child.Kill(entireProcessTree: true); } catch { }
                Console.WriteLine("FAIL production-window fixture exceeded 120 seconds");
                Environment.ExitCode = 1;
                return;
            }
            var evidence = Path.Combine(root, "evidence.txt");
            Console.WriteLine(File.Exists(evidence) ? File.ReadAllText(evidence) : "FAIL child exited before recording checks");
            Environment.ExitCode = child.ExitCode;
            return;
        }

        root = AppContext.BaseDirectory;
        try
        {
            GenerateAvi.Run(Path.Combine(root, "fixture.avi"));
            CreateSilentWave(Path.Combine(root, "capture-fixture.wav"));
            using var fixture = new Fixture(File.ReadAllBytes(Path.Combine(root, "fixture.avi")), File.ReadAllBytes(Path.Combine(root, "capture-fixture.wav")));
            var store = new ConfigStore();
            var state = store.Load();
            var persistedRecordingFolder = Path.Combine(root, "persistent-recordings");
            Directory.CreateDirectory(persistedRecordingFolder);
            state.RecordingFolder = persistedRecordingFolder;
            store.Save(state);
            var reloadedFolder = store.Load().RecordingFolder;
            Check(string.Equals(Path.GetFullPath(reloadedFolder), Path.GetFullPath(persistedRecordingFolder), StringComparison.OrdinalIgnoreCase),
                "default recording folder persists across ConfigStore reload");
            Check(typeof(AppState).GetProperty("RecordingFolder")?.PropertyType == typeof(string),
                "recording destination preference is part of app settings");
            var namingTime = new DateTimeOffset(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);
            var firstName = RecordingPolicy.ChooseFileName(persistedRecordingFolder, "Fixture / Channel", namingTime,
                RecordingContainerKind.Ts, path => File.Exists(path));
            File.WriteAllBytes(Path.Combine(persistedRecordingFolder, firstName.Value), [1]);
            var collisionName = RecordingPolicy.ChooseFileName(persistedRecordingFolder, "Fixture / Channel", namingTime,
                RecordingContainerKind.Ts, path => File.Exists(path));
            var repeatedCollisionName = RecordingPolicy.ChooseFileName(persistedRecordingFolder, "Fixture / Channel", namingTime,
                RecordingContainerKind.Ts, path => File.Exists(path));
            Check(firstName.Status == RecordingFilenameStatus.Ok && collisionName.Status == RecordingFilenameStatus.Renamed &&
                  collisionName.Value == repeatedCollisionName.Value && collisionName.Value != firstName.Value,
                "default-folder recording names are deterministic and avoid existing-file collisions");
            var saved = state.EnsureSelectedAccount();
            accountId = saved.Id;
            var account = new AccountSettings { ServerUrl = fixture.Origin, Username = "recording-fixture", Password = "invented" };
            saved.Name = "Recording fixture account";
            saved.Settings = account.Clone();
            saved.LastPlaylistUpdatedUtc = DateTime.UtcNow;
            state.Account = account.Clone();
            state.CheckForUpdatesOnStartup = false;
            state.RemoteControlEnabled = false;
            store.Save(state);
            var channel = new Channel
            {
                Id = "recording-fixture-live", Name = "Generated Recording Fixture", Group = "Local fixtures",
                EpgId = "recording-fixture-guide", Url = fixture.StreamUrl, MediaKind = MediaKind.Live
            };
            store.SaveChannelCache(accountId, [channel]);

            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            var startup = typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(object), typeof(StartupEventArgs)], null)!;
            app.Startup -= (StartupEventHandler)startup.CreateDelegate(typeof(StartupEventHandler), app);
            window = new MainWindow(new LoginResult { Account = account, AccountId = accountId, UpdatePlaylist = false });
            app.MainWindow = window;
            window.ContentRendered += async (_, _) =>
            {
                try
                {
                    await RunAsync(fixture, channel, store);
                    File.AppendAllText(Path.Combine(root, "evidence.txt"), "ALL PASS" + Environment.NewLine);
                }
                catch (Exception exception)
                {
                    File.AppendAllText(Path.Combine(root, "evidence.txt"), "FAIL " + exception + Environment.NewLine);
                    Environment.ExitCode = 1;
                }
                finally
                {
                    // Stop capture and playback while their WPF/native owners are still alive.
                    try
                    {
                        var service = Service;
                        if (service is not null)
                        {
                            var stopAll = service.GetType().GetMethods().Single(method => method.Name == "StopAllAsync");
                    var task = (Task)stopAll.Invoke(service, [RecordingStopReason.Shutdown, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token])!;
                            await task.WaitAsync(TimeSpan.FromSeconds(15));
                        }
                    }
                    catch (Exception exception) { File.AppendAllText(Path.Combine(root, "evidence.txt"), "CLEANUP capture: " + exception.Message + Environment.NewLine); Environment.ExitCode = 1; }
                    try { Call("StopPlayback"); } catch { }
                    await Task.Delay(750);
                    try { window.Close(); } catch { }
                    await Task.Delay(250);
                    app.Shutdown();
                }
            };
            window.Show();
            app.Run();
        }
        catch (Exception exception)
        {
            File.AppendAllText(Path.Combine(root, "evidence.txt"), "FAIL startup " + exception + Environment.NewLine);
            Console.Error.WriteLine(exception);
            Environment.ExitCode = 1;
        }
    }

    private static async Task<bool> TryTranscodeFixtureToMpegTsAsync(string aviPath, string tsPath)
    {
        const string options = ":sout=#transcode{vcodec=mp2v,vb=600,fps=10,acodec=mpga,ab=96,channels=2,samplerate=44100}:std{access=file,mux=ts,dst='";
        const string suffix = "'}";
        const int boundedTimeoutMs = 12_000;
        const long maxFixtureBytes = 32 * 1024 * 1024;
        var log = new StringBuilder();
        using var timeout = new CancellationTokenSource(boundedTimeoutMs);
        MediaPlayer? player = null;
        Media? media = null;
        TaskCompletionSource? ended = null;
        EventHandler<EventArgs>? endHandler = null;
        EventHandler<EventArgs>? errorHandler = null;
        EventHandler<LogEventArgs>? logHandler = null;
        try
        {
            var libvlc = (LibVLC?)Field("_libVlc") ?? throw new InvalidOperationException("Production LibVLC instance is unavailable.");
            logHandler = (_, entry) =>
            {
                if (entry.Level >= LogLevel.Warning) lock (log) log.AppendLine(entry.Message);
            };
            libvlc.Log += logHandler;
            player = new MediaPlayer(libvlc);
            media = new Media(libvlc, aviPath, FromType.FromPath);
            media.AddOption(options + tsPath.Replace("\\", "/", StringComparison.Ordinal) + suffix);
            media.AddOption(":sout-keep");
            media.AddOption(":no-sout-display");
            ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            endHandler = (_, _) => ended.TrySetResult();
            errorHandler = (_, _) => ended.TrySetResult();
            player.EndReached += endHandler;
            player.EncounteredError += errorHandler;
            if (!player.Play(media)) throw new InvalidOperationException("LibVLC rejected the fixture transcode media.");
            try { await ended.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                player.Stop();
                await Task.Delay(500);
            }
            var valid = File.Exists(tsPath) && new FileInfo(tsPath).Length >= 188L * 10 &&
                new FileInfo(tsPath).Length <= maxFixtureBytes;
            if (!valid)
            {
                var detail = log.ToString();
                File.AppendAllText(Path.Combine(root, "evidence.txt"),
                    $"LibVLC transcode detail (bounded {boundedTimeoutMs}ms): {detail}{Environment.NewLine}");
                if (File.Exists(tsPath)) File.Delete(tsPath);
            }
            return valid;
        }
        catch (Exception exception)
        {
            File.AppendAllText(Path.Combine(root, "evidence.txt"),
                $"LibVLC transcode preflight exception (bounded {boundedTimeoutMs}ms): {exception}{Environment.NewLine}");
            if (File.Exists(tsPath)) File.Delete(tsPath);
            return false;
        }
        finally
        {
            timeout.Cancel();
            if (player is not null)
            {
                if (endHandler is not null) player.EndReached -= endHandler;
                if (errorHandler is not null) player.EncounteredError -= errorHandler;
                player.Dispose();
            }
            media?.Dispose();
            if (logHandler is not null && Field("_libVlc") is LibVLC activeLibVlc)
                activeLibVlc.Log -= logHandler;
        }
    }

    private static async Task RunAsync(Fixture fixture, Channel channel, ConfigStore store)
    {
        await WaitUntil(() => Service is not null && Field("_tuner") is not null, "production MainWindow did not initialize LibVLC and RecordingService", TimeSpan.FromSeconds(40));
        var transcodePath = Path.Combine(root, "transcode-fixture.ts");
        var transcoded = await Application.Current.Dispatcher.InvokeAsync(
            () => TryTranscodeFixtureToMpegTsAsync(Path.Combine(root, "fixture.avi"), transcodePath)).Task.Unwrap();
        if (transcoded) fixture.SetMpegTs(File.ReadAllBytes(transcodePath));
        File.AppendAllText(Path.Combine(root, "evidence.txt"), transcoded
            ? "PASS packaged LibVLC transcode produced a bounded MPEG-TS fixture: " + new FileInfo(transcodePath).Length + " bytes." + Environment.NewLine
            : "LIMITATION packaged LibVLC MPEG-TS transcode preflight failed; retaining the marker/WAV fixture and explicitly not claiming decoder-level HLS playback." + Environment.NewLine);
        Call("PlayChannel", channel, null);
        await WaitPlaying();
        await Application.Current.Dispatcher.InvokeAsync(() => ExerciseGuideScheduleManagerFlow(channel, store)).Task.Unwrap();
        Check(window.FindName("RecordButton") is Button, "real production MainWindow rendered recording controls");
        Check(((Button?)window.FindName("StopRecordingButton"))?.Visibility == Visibility.Collapsed,
            "Stop recording remains hidden before capture starts");
        var normalizeFolder = typeof(MainWindow).GetMethod("NormalizeRecordingFolder", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check(string.Equals((string?)normalizeFolder.Invoke(null, [root]), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase),
            "existing saved recording folder is normalized and reused");
        Check(normalizeFolder.Invoke(null, [Path.Combine(root, "missing-recording-folder")]) is null,
            "missing saved recording folder safely falls back to the default");
        var savedOutcome = new CaptureOutcome
        {
            Accepted = true, Result = ClassicWindowsIptvPlayer.Core.RecordingOutcome.Stopped,
            FilePath = Path.Combine(root, "already-finalized.ts"), ByteSize = 1024
        };
        Check(savedOutcome.HasPlayableFile && savedOutcome.Succeeded,
            "user-stopped capture with finalized bytes is classified as a successful playable recording");
        Check(fixture.StreamRequests > 0, "cached invented live channel played from loopback HTTP fixture");

        var budget = (ConnectionBudget)Field("_recordingBudget")!;
        var service = Service!;
        // Preserve RecordingPolicy's thresholds while making this harness independent
        // of the machine volume's real free-space amount.
        service.GetType().GetProperty("FreeSpaceProbe")!.SetValue(service,
            (Func<string, VolumeSpace>)(_ => new VolumeSpace(true, 16L * 1024 * 1024 * 1024)));
        var profile = new AccountProfile { Username = "fixture", Status = "Active", ActiveConnections = "0", MaxConnections = "2", Timezone = "UTC" };
        typeof(MainWindow).GetField("_recordingProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, profile);
        var finished = new TaskCompletionSource<CaptureOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishedEvent = service.GetType().GetEvent("CaptureFinished")!;
        var handlerType = finishedEvent.EventHandlerType!;
        var invoke = handlerType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(parameter => System.Linq.Expressions.Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
        var outcomeArgument = System.Linq.Expressions.Expression.Convert(parameters[^1], typeof(CaptureOutcome));
        var callback = System.Linq.Expressions.Expression.Lambda(handlerType, System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Constant(finished), typeof(TaskCompletionSource<CaptureOutcome>).GetMethod("TrySetResult")!, outcomeArgument), parameters).Compile();
        finishedEvent.AddEventHandler(service, callback);

        await RunSharedHlsSourceCheckAsync(fixture, channel, profile, store, service, budget, finishedEvent, callback, finished);
    }

    // Keep the production WPF AVI smoke test, and separately exercise the real
    // SharedHlsSource loopback endpoint with LibVLC's decoded-video callbacks.
    private static async Task RunSharedHlsSourceCheckAsync(Fixture fixture, Channel channel,
        AccountProfile profile, ConfigStore store, object service, ConnectionBudget budget,
        EventInfo finishedEvent, Delegate finishedCallback, TaskCompletionSource<CaptureOutcome> finished)
    {
        await using var source = new SharedHlsSource(new Uri(fixture.SharedHlsPlaylistUrl),
            pollInterval: TimeSpan.FromMilliseconds(500), maximumSegmentBytes: 8 * 1024 * 1024,
            maximumBufferedBytesPerConsumer: 16 * 1024 * 1024, maximumPlaybackCacheBytes: 32 * 1024 * 1024);
        var tsFixture = fixture.MpegTs ?? throw new InvalidOperationException("LibVLC did not produce the valid MPEG-TS fixture required for decoder integration.");
        using var decodeProbe = new DecodeProbe();
        using var decoder = new MediaPlayer((LibVLC)Field("_libVlc")!);
        decoder.SetVideoFormat("RV32", 160, 90, 160 * 4);
        decoder.SetVideoCallbacks(decodeProbe.Lock, decodeProbe.Unlock, decodeProbe.Display);
        var playbackConsumer = source.Attach(SharedHlsConsumerKind.Playback);
        var sourcePlaybackUrl = await source.StartPlaybackAsync();
        await source.WaitUntilPlaybackReadyAsync(TimeSpan.FromSeconds(10));
        using var relayMedia = new Media((LibVLC)Field("_libVlc")!, sourcePlaybackUrl.AbsoluteUri, FromType.FromLocation);
        relayMedia.AddOption(":network-caching=100");
        Check(decoder.Play(relayMedia), "LibVLC accepts the production SharedHlsSource loopback playlist URL");
        await WaitUntil(() => decodeProbe.Frames > 0, "LibVLC did not decode any video frame from SharedHlsSource.PlaybackUrl", TimeSpan.FromSeconds(12));
        Check(decodeProbe.Frames > 0, $"LibVLC decodes TS frames through the production loopback HLS relay (frames={decodeProbe.Frames})");
        var firstPlaybackSegment = await playbackConsumer.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Check(firstPlaybackSegment is { Data.Length: > 0 },
            "playback consumer receives media from the local-loopback shared HLS source");
        Check(firstPlaybackSegment!.Data.Span.SequenceEqual(tsFixture),
            "local HLS origin segment body is the generated valid MPEG-TS fixture without marker corruption");
        var sourceTaskField = typeof(SharedHlsSource).GetField("_runTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var upstreamTask = sourceTaskField.GetValue(source);
        var initialPlaylistCount = fixture.SharedPlaylistRequests;
        var initialSegmentCount = fixture.SharedSegmentRequests;
        await Task.Delay(2200);
        var beforePollDelta = fixture.SharedPlaylistRequests - initialPlaylistCount;
        var beforeSegmentDelta = fixture.SharedSegmentRequests - initialSegmentCount;
        Check(beforePollDelta > 0 && beforeSegmentDelta > 0 &&
              ReferenceEquals(sourceTaskField.GetValue(source), upstreamTask),
            "production shared source polls and fetches continuously before recording attaches");
        var playerBeforeAttach = (MediaPlayer)Field("_mediaPlayer")!;
        Check(decoder.IsPlaying && decodeProbe.Frames > 0,
            "dedicated LibVLC decoder continues receiving frames from the shared HLS relay before Record");
        var baselineSequence = fixture.SharedLatestSequence;

        var tuner = (ChannelTuner)Field("_tuner")!;
        var tunerSourceField = typeof(ChannelTuner).GetField("_activeSharedHlsSource", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousTunerSource = tunerSourceField.GetValue(tuner);
        // The production WPF smoke player remains on AVI. The dedicated decoder above
        // verifies the source's real loopback URL without mutating production playback.
        tunerSourceField.SetValue(tuner, source);
        var recordingConsumer = source.Attach(SharedHlsConsumerKind.Recording);
        Check(recordingConsumer.Kind == SharedHlsConsumerKind.Recording,
            "recording consumer attaches directly to the active shared live HLS source");
        var sourceField = typeof(SharedHlsConsumer).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check(ReferenceEquals(sourceField.GetValue(playbackConsumer), source) &&
              ReferenceEquals(sourceField.GetValue(recordingConsumer), source),
            "tuner playback and recording consumers point to the exact same SharedHlsSource instance");
        var attachedPollBaseline = fixture.SharedPlaylistRequests;
        await Task.Delay(2200);
        var afterAttachWindowPollCount = fixture.SharedPlaylistRequests;
        Check(afterAttachWindowPollCount > attachedPollBaseline &&
              ReferenceEquals(sourceTaskField.GetValue(source), upstreamTask),
            "recording consumer attaches without adding a second upstream poller");
        Check(ReferenceEquals(Field("_mediaPlayer"), playerBeforeAttach) && playerBeforeAttach.IsPlaying,
            "tuner recording attachment leaves the same WPF MediaPlayer playing");
        Check(decoder.IsPlaying, "dedicated relay decoder remains active when the recording consumer attaches");

        // Exercise cancellation after the capture session is accepted but before its
        // recording consumer can receive the next HLS segment.
        finishedEvent.RemoveEventHandler(service, finishedCallback);
        var framesBeforeCancelledStartup = decodeProbe.Frames;
        fixture.PauseSharedSegmentResponses();
        var stalledConsumer = source.Attach(SharedHlsConsumerKind.Recording);
        var startupCancelPath = Path.Combine(root, "cancelled-startup.ts");
        var startupCancelRequest = new RecordingStartRequest
        {
            StreamUrl = string.Empty,
            DestinationPath = startupCancelPath,
            AccountId = accountId,
            ChannelId = channel.Id,
            ChannelKey = ItemIdentity.For(channel),
            ChannelName = channel.Name + " startup cancellation",
            Profile = profile,
            Container = RecordingContainerKind.Ts,
            RequestedStartUtc = DateTimeOffset.UtcNow,
            BufferMs = 200
        };
        using (var startupCancellation = new CancellationTokenSource())
        {
            var blockedSegmentCount = fixture.SharedSegmentRequests;
            var cancelledStart = (Task<CaptureOutcome>)service.GetType()
                .GetMethod("StartSharedHlsAsync")!.Invoke(service,
                    [startupCancelRequest, stalledConsumer, startupCancellation.Token])!;
            try
            {
                await WaitUntil(() => ((System.Collections.ICollection)service.GetType()
                            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!).Count > 0 &&
                        File.Exists(startupCancelPath),
                    "startup-cancellation capture did not create its pending session and empty file", TimeSpan.FromSeconds(5));
                await Task.Delay(150);
                Check(!cancelledStart.IsCompleted && fixture.SharedSegmentRequests == blockedSegmentCount,
                    "shared recording startup is blocked waiting for its first segment before cancellation");

                var cancellationAt = Stopwatch.StartNew();
                startupCancellation.Cancel();
                try
                {
                    await cancelledStart.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("StartSharedHlsAsync unexpectedly completed successfully after startup cancellation.");
                }
                catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested)
                {
                    Check(cancellationAt.Elapsed < TimeSpan.FromSeconds(3),
                        $"shared recording startup cancellation completes promptly ({cancellationAt.Elapsed})");
                }
            }
            finally
            {
                fixture.ResumeSharedSegmentResponses();
                if (!cancelledStart.IsCompleted)
                {
                    startupCancellation.Cancel();
                    try { await cancelledStart.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) { }
                }
            }
        }

        await WaitUntil(() => ((System.Collections.ICollection)service.GetType()
                    .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!).Count == 0,
            "cancelled startup session remained registered after finalization", TimeSpan.FromSeconds(5));
        var consumerSourceField = typeof(SharedHlsConsumer).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var consumersField = typeof(SharedHlsSource).GetField("_consumers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var remainingConsumers = ((System.Collections.ICollection)consumersField.GetValue(source)!).Count;
        Check(remainingConsumers == 2 && !File.Exists(startupCancelPath),
            "cancelled startup leaves no recording capture file or registered recording consumer");
        await WaitUntil(() => decodeProbe.Frames > framesBeforeCancelledStartup,
            "shared playback decoder did not resume after cancelled recording startup", TimeSpan.FromSeconds(10));
        Check(ReferenceEquals(consumerSourceField.GetValue(playbackConsumer), source) && decoder.IsPlaying && decodeProbe.Frames > framesBeforeCancelledStartup &&
              ReferenceEquals(sourceTaskField.GetValue(source), upstreamTask) && ReferenceEquals(Field("_mediaPlayer"), playerBeforeAttach) && playerBeforeAttach.IsPlaying,
            "startup cancellation leaves the shared playback source and player healthy");
        Check(fixture.SharedPlaylistRequests > attachedPollBaseline && budget.LocalHeld == 0 && budget.Leases.Count == 0,
            "shared source continues polling after cancelled startup without acquiring a provider lease");
        finishedEvent.AddEventHandler(service, finishedCallback);

        var destination = Path.Combine(root, "shared-source-fixture.ts");
        var request = new RecordingStartRequest
        {
            StreamUrl = string.Empty,
            DestinationPath = destination,
            AccountId = accountId,
            ChannelId = channel.Id,
            ChannelKey = ItemIdentity.For(channel),
            ChannelName = channel.Name,
            Profile = profile,
            Container = RecordingContainerKind.Ts,
            RequestedStartUtc = DateTimeOffset.UtcNow,
            BufferMs = 200
        };
        var activePlayer = (MediaPlayer)Field("_mediaPlayer")!;
        Check(ReferenceEquals(activePlayer, tuner.CurrentPlayer) && activePlayer.IsPlaying,
            "production WPF player is active before shared Record attachment");
        var beforeRecordPollCount = fixture.SharedPlaylistRequests;
        var beforeRecordSegmentCount = fixture.SharedSegmentRequests;
        var startTask = (Task<CaptureOutcome>)service.GetType()
            .GetMethod("StartSharedHlsAsync")!.Invoke(service, [request, recordingConsumer, CancellationToken.None])!;
        try
        {
            await WaitUntil(() => ((System.Collections.ICollection)service.GetType()
                    .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!).Count > 0,
                "RecordingService did not accept the shared-HLS session", TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Shared-HLS start failed before acceptance: {exception.Message}", startTask.Exception ?? exception);
        }
        var start = await startTask.WaitAsync(TimeSpan.FromSeconds(10));
        Check(start.Accepted && start.Entry is { IsActive: true },
            $"shared-source recording starts with no separate stream URL (failure={start.Failure}; reason={start.FailureReason})");
        var sessions = (System.Collections.IEnumerable)service.GetType()
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var session = sessions.Cast<object>().Single(item =>
            string.Equals(item.GetType().GetProperty("Id")!.GetValue(item)?.ToString(), start.RecordingId, StringComparison.Ordinal));
        var sessionRequest = (RecordingStartRequest)session.GetType().GetProperty("Request")!.GetValue(session)!;
        Check(sessionRequest.StreamUrl.Length == 0 && ReferenceEquals(sessionRequest.SharedHlsConsumer, recordingConsumer) &&
              ReferenceEquals(sourceField.GetValue(sessionRequest.SharedHlsConsumer), source),
            "recording session retains the exact tuner consumer and source instance");
        Check(ReferenceEquals(sourceTaskField.GetValue(source), upstreamTask),
            "record attachment keeps the original shared HLS upstream poller task");
        Check(ReferenceEquals(Field("_mediaPlayer"), activePlayer) && ReferenceEquals(tuner.CurrentPlayer, activePlayer) && activePlayer.IsPlaying,
            "Record leaves the production MediaPlayer instance playing without a retune");
        var framesBeforeRecord = decodeProbe.Frames;
        await WaitUntil(() => decodeProbe.Frames > framesBeforeRecord, "LibVLC decoded no new frame while recording was attached", TimeSpan.FromSeconds(8));
        Check(decoder.IsPlaying && decodeProbe.Frames > framesBeforeRecord,
            "LibVLC continues decoding new shared-relay video frames during recording");
        Check(budget.LocalHeld == 0 && budget.Leases.Count == 0,
            "shared-source recording does not acquire a second connection lease");
        Check(start.Entry?.StartedUtc == request.RequestedStartUtc,
            "shared-source recording index timestamp begins at the Record request");
        Check(budget.LocalHeld == 0 && budget.Leases.Count == 0,
            "shared-source record starts without acquiring an additional connection lease");

        var path = start.FilePath;
        await WaitUntil(() => File.Exists(path) && new FileInfo(path).Length > 0,
            "shared-source recording produced no post-click bytes", TimeSpan.FromSeconds(15));
        await WaitUntil(() => fixture.SharedSegmentRequests > beforeRecordSegmentCount,
            $"shared HLS fixture did not deliver a new post-click segment (playlist={fixture.SharedPlaylistRequests}; segments={fixture.SharedSegmentRequests}; sourceTaskSame={ReferenceEquals(sourceTaskField.GetValue(source), upstreamTask)})", TimeSpan.FromSeconds(10));
        await Task.Delay(2200);
        var growingSize = new FileInfo(path).Length;
        Check(growingSize > 0, "shared-source recording file grows from newly arriving segments");
        var liveSnapshot = service.GetType().GetMethod("Find")!.Invoke(service, [start.RecordingId]);
        var liveElapsed = liveSnapshot?.GetType().GetProperty("Elapsed")?.GetValue(liveSnapshot) as TimeSpan?;
        Check(liveElapsed.HasValue && liveElapsed.Value > TimeSpan.Zero,
            $"active shared-source recording snapshot reports elapsed time (elapsed={liveElapsed})");
        var duringPollCount = fixture.SharedPlaylistRequests;
        var duringSegmentCount = fixture.SharedSegmentRequests;
        Check(duringPollCount > attachedPollBaseline && duringSegmentCount > beforeRecordSegmentCount,
            "upstream poll and segment request counts continue while recording is attached");
        Check(duringPollCount > afterAttachWindowPollCount &&
              ReferenceEquals(sourceTaskField.GetValue(source), upstreamTask),
            $"playlist polling continues while recording uses the original shared-source task (delta={duringPollCount - afterAttachWindowPollCount}; sameTask={ReferenceEquals(sourceTaskField.GetValue(source), upstreamTask)})");
        Check(fixture.SharedSegmentRequests == fixture.SharedSegmentFetches.Values.Sum() &&
              fixture.SharedSegmentFetches.Values.All(count => count == 1),
            "each shared-source HLS segment is fetched upstream exactly once");

        var stopClickedUtc = DateTimeOffset.UtcNow;
        var stopped = await ((Task<CaptureOutcome?>)service.GetType()
            .GetMethod("StopAsync")!.Invoke(service, [start.RecordingId, RecordingStopReason.User, CancellationToken.None])!)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Check(stopped is { Accepted: true, ByteSize: > 0 } && File.Exists(path) && new FileInfo(path).Length == stopped.ByteSize,
            "shared-source Stop flushes the complete output file");
        var firstSegment = new byte[tsFixture.Length];
        await using (var input = File.OpenRead(path))
            await input.ReadExactlyAsync(firstSegment);
        Check(firstSegment.AsSpan().SequenceEqual(tsFixture) && fixture.SharedSegmentFetches.Keys.Max() > baselineSequence,
            "recorded file begins with a complete unmodified MPEG-TS segment and the source advanced beyond the Record edge");
        Check(stopped?.Elapsed > TimeSpan.Zero && stopped.Entry?.RequestedStartUtc == request.RequestedStartUtc &&
              stopped.Entry.StoppedUtc is DateTimeOffset stoppedAt && Math.Abs((stoppedAt - stopClickedUtc).TotalMilliseconds) < 1000,
            "shared-source start and stop timestamps align with the Record and Stop actions");
        var finalExpectedElapsed = (stopped!.Entry!.StoppedUtc!.Value - request.RequestedStartUtc).Duration();
        Check(Math.Abs((stopped.Elapsed - finalExpectedElapsed).TotalMilliseconds) < 1000,
            $"final elapsed uses the captured Stop UTC delta (elapsed={stopped.Elapsed}; expected={finalExpectedElapsed})");
        await WaitUntil(() => store.LoadRecordingIndex(accountId)?.Find(accountId, start.Entry!.Id) is
                { Outcome: ClassicWindowsIptvPlayer.Core.RecordingOutcome.Stopped, ByteSize: > 0 },
            "shared-source recording index did not persist its stopped row", TimeSpan.FromSeconds(10));
        Check(store.LoadRecordingIndex(accountId)?.Find(accountId, start.Entry!.Id) is
                { Outcome: ClassicWindowsIptvPlayer.Core.RecordingOutcome.Stopped, ByteSize: > 0 },
            "shared-source stopped row and byte count persist in the account index");
        var completion = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(completion.RecordingId == start.RecordingId && completion.HasPlayableFile,
            "CaptureFinished observes flushed shared-source output");
        Check(decoder.IsPlaying && decodeProbe.Frames > 0,
            "shared-relay LibVLC decoder remains playing after recording stops");
        finishedEvent.RemoveEventHandler(service, finishedCallback);
        Check(budget.LocalHeld == 0 && budget.Leases.Count == 0,
            "Stop releases no extra lease because shared recording never acquired one");
        await Task.Delay(2200);
        Check(fixture.SharedPlaylistRequests > duringPollCount && fixture.SharedSegmentRequests > duringSegmentCount,
            "upstream poll and segment request counts continue after recording detaches");
        Check(fixture.SharedPlaylistRequests > duringPollCount &&
              ReferenceEquals(sourceTaskField.GetValue(source), upstreamTask),
            "Stop detaches only the recording consumer and preserves the single playback poller");
        Check(ReferenceEquals(Field("_mediaPlayer"), activePlayer) && ReferenceEquals(tuner.CurrentPlayer, activePlayer) && activePlayer.IsPlaying,
            "Stop leaves the same production MediaPlayer playing");
        decoder.Stop();
        using var reopenProbe = new DecodeProbe();
        using var verifyPlayer = new MediaPlayer((LibVLC)Field("_libVlc")!);
        verifyPlayer.SetVideoFormat("RV32", 160, 90, 160 * 4);
        verifyPlayer.SetVideoCallbacks(reopenProbe.Lock, reopenProbe.Unlock, reopenProbe.Display);
        using var recordedMedia = new Media((LibVLC)Field("_libVlc")!, path, FromType.FromPath);
        Check(verifyPlayer.Play(recordedMedia), "LibVLC accepts the stopped recording file for playback verification");
        await WaitUntil(() => reopenProbe.Frames > 0 || verifyPlayer.State is VLCState.Ended or VLCState.Error,
            "timed out waiting for stopped TS decode verification", TimeSpan.FromSeconds(12));
        Check(reopenProbe.Frames > 0,
            $"LibVLC decodes recorded MPEG-TS frames (frames={reopenProbe.Frames}; state={verifyPlayer.State})");
        var reopenedFrameBaseline = reopenProbe.Frames;
        await WaitUntil(() => reopenProbe.Frames > reopenedFrameBaseline || verifyPlayer.State is VLCState.Ended or VLCState.Error,
            "recorded TS stopped producing frames immediately after opening", TimeSpan.FromSeconds(8));
        Check(reopenProbe.Frames > reopenedFrameBaseline && verifyPlayer.State != VLCState.Error,
            $"LibVLC continues decoding the stopped recording (newFrames={reopenProbe.Frames - reopenedFrameBaseline}; state={verifyPlayer.State})");
        verifyPlayer.Stop();
        await playbackConsumer.DisposeAsync();
        tunerSourceField.SetValue(tuner, previousTunerSource);
        await source.DisposeAsync();

        // Use the actual tuner-owned HLS source for the coordinator integration.
        // The preceding relay decoder deliberately used a separate source, so it
        // cannot prove the coordinator's account/channel attach path by itself.
        tuner.Play(new TuneRequest(channel.Id, channel.Name, fixture.SharedHlsPlaylistUrl,
            "recording-ui-synthetic-hls", IsLive: true, BufferMs: 200, AccountId: accountId));
        await WaitUntil(() => tuner.CurrentPlayer is { IsPlaying: true } && tunerSourceField.GetValue(tuner) is SharedHlsSource,
            "production tuner did not start the synthetic live HLS source for scheduled capture", TimeSpan.FromSeconds(20));
        var scheduledPlayer = tuner.CurrentPlayer!;
        var scheduledSource = (SharedHlsSource)tunerSourceField.GetValue(tuner)!;
        var scheduledRunTask = sourceTaskField.GetValue(scheduledSource);
        Check(scheduledRunTask is Task,
            "production tuner owns a running shared-HLS poller before scheduled capture");

        // Exercise the real production pause/resume handlers against the tuner-owned
        // HLS player. The relay should keep fetching into its timeshift buffer while
        // LibVLC is paused, without reopening the provider URL or replacing the player.
        var pausePlayer = (MediaPlayer?)Field("_mediaPlayer");
        Check(ReferenceEquals(pausePlayer, scheduledPlayer) && pausePlayer is { IsPlaying: true },
            "production MainWindow is attached to the active tuner-owned HLS MediaPlayer before pause");
        var streamRequestsBeforePause = fixture.StreamRequests;
        var pausePlaylistBaseline = fixture.SharedPlaylistRequests;
        var pauseSegmentBaseline = fixture.SharedSegmentRequests;
        var pauseWindowBaseline = scheduledSource.InspectTimeshiftWindow();
        Call("PauseCurrentPlayback");
        await WaitUntil(() => pausePlayer!.State == VLCState.Paused,
            $"production MediaPlayer did not enter Paused state (state={pausePlayer!.State})", TimeSpan.FromSeconds(5));
        var pausedMediaTime = pausePlayer!.Time;
        var pausedMediaLength = pausePlayer.Length;
        var pausedMediaPosition = pausePlayer.Position;
        var pausedMediaSeekable = pausePlayer.IsSeekable;
        File.AppendAllText(Path.Combine(root, "evidence.txt"),
            $"INFO live-HLS player position telemetry while paused: timeMs={pausedMediaTime}; lengthMs={pausedMediaLength}; position={pausedMediaPosition}; seekable={pausedMediaSeekable}.{Environment.NewLine}");
        Check(ReferenceEquals(Field("_mediaPlayer"), pausePlayer) && ReferenceEquals(tuner.CurrentPlayer, pausePlayer) &&
              pausePlayer.State == VLCState.Paused,
            "production pause uses the existing MediaPlayer instance and reaches LibVLC Paused state");
        await WaitUntil(() => scheduledSource.InspectTimeshiftWindow().LiveSequence > pauseWindowBaseline.LiveSequence,
            "shared-HLS disk buffer did not advance while the production player was paused", TimeSpan.FromSeconds(8));
        var pauseWindowAfter = scheduledSource.InspectTimeshiftWindow();
        Check(pauseWindowAfter.BufferedBytes > 0 && pauseWindowAfter.LiveSequence > pauseWindowBaseline.LiveSequence,
            $"shared-HLS timeshift buffer advances while paused (sequence={pauseWindowBaseline.LiveSequence}->{pauseWindowAfter.LiveSequence}; bytes={pauseWindowAfter.BufferedBytes})");
        var pausedMediaTimeAfterBuffer = pausePlayer.Time;
        File.AppendAllText(Path.Combine(root, "evidence.txt"),
            $"INFO live-HLS player position after buffer advanced while paused: timeMs={pausedMediaTimeAfterBuffer}; lengthMs={pausePlayer.Length}; position={pausePlayer.Position}; seekable={pausePlayer.IsSeekable}.{Environment.NewLine}");
        Check(fixture.SharedPlaylistRequests > pausePlaylistBaseline && fixture.SharedSegmentRequests > pauseSegmentBaseline &&
              fixture.StreamRequests == streamRequestsBeforePause &&
              ReferenceEquals(sourceTaskField.GetValue(scheduledSource), scheduledRunTask) &&
              fixture.SharedSegmentRequests == fixture.SharedSegmentFetches.Values.Sum() &&
              fixture.SharedSegmentFetches.Values.All(count => count == 1),
            "paused HLS playback keeps one existing shared poller/fetcher and starts no second provider-stream response");

        var pauseResumePlaylistCount = fixture.SharedPlaylistRequests;
        var pauseResumeSegmentCount = fixture.SharedSegmentRequests;
        Call("ResumePausedPlayback");
        await WaitUntil(() => pausePlayer!.State == VLCState.Playing,
            $"production MediaPlayer did not leave Paused after explicit resume (state={pausePlayer!.State})", TimeSpan.FromSeconds(5));
        await WaitUntil(() => fixture.SharedPlaylistRequests > pauseResumePlaylistCount,
            "existing shared-HLS poller did not complete another poll after resume", TimeSpan.FromSeconds(5));
        Check(ReferenceEquals(Field("_mediaPlayer"), pausePlayer) && ReferenceEquals(tuner.CurrentPlayer, pausePlayer),
            "explicit SetPause(false) resumes the same production MediaPlayer instance");
        Check(fixture.StreamRequests == streamRequestsBeforePause &&
              fixture.SharedSegmentRequests >= pauseResumeSegmentCount &&
              ReferenceEquals(sourceTaskField.GetValue(scheduledSource), scheduledRunTask),
            $"resume adds no new direct provider-stream request or poller and preserves the original source task (direct={fixture.StreamRequests}/{streamRequestsBeforePause}; sourceTaskSame={ReferenceEquals(sourceTaskField.GetValue(scheduledSource), scheduledRunTask)})");

        var rewindWindow = scheduledSource.InspectTimeshiftWindow();
        var rewindPoller = sourceTaskField.GetValue(scheduledSource);
        var rewindPlayer = tuner.CurrentPlayer;
        var rewindDirectRequests = fixture.StreamRequests;
        Media? notifiedReplacement = null;
        void OnActiveMediaChanged(MediaPlayer changedPlayer, Media changedMedia, TuneRequest changedRequest)
        {
            if (ReferenceEquals(changedPlayer, rewindPlayer) && changedRequest.ChannelId == channel.Id)
                notifiedReplacement = changedMedia;
        }
        tuner.ActiveMediaChanged += OnActiveMediaChanged;
        var rewind = await tuner.SeekLiveTimeshiftAsync(TimeSpan.FromSeconds(3)).WaitAsync(TimeSpan.FromSeconds(15));
        Check(rewind.Success && rewind.SelectedSequence is long selectedOldSequence && selectedOldSequence < rewindWindow.LiveSequence,
            $"production tuner accepts an explicit buffered rewind to an older segment (selected={rewind.SelectedSequence}; edge={rewindWindow.LiveSequence}; reason={rewind.FailureReason})");
        if (rewind.SelectedSequence is long requestedOldSequence)
            await WaitUntil(() => scheduledSource.GetLocalPlaybackSegmentRequestCount(requestedOldSequence) > 0,
                $"LibVLC did not request selected buffered segment {requestedOldSequence}", TimeSpan.FromSeconds(8));
        Check(ReferenceEquals(tuner.CurrentPlayer, rewindPlayer) && rewindPlayer is { IsPlaying: true } &&
              rewind.SelectedSequence is long decodedSequence && scheduledSource.GetLocalPlaybackSegmentRequestCount(decodedSequence) > 0,
            $"selected buffered HLS input is actively playing on the same production MediaPlayer and its old segment was requested locally (state={rewindPlayer?.State}; sequence={rewind.SelectedSequence})");
        Check(notifiedReplacement is not null && ReferenceEquals(tuner.GetType().GetField("_activeMedia", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tuner), notifiedReplacement),
            "ActiveMediaChanged publishes the replacement Media synchronously before the prior Media is retired");
        Check(ReferenceEquals(Field("_currentMedia"), notifiedReplacement) && ReferenceEquals(Field("_mediaPlayer"), rewindPlayer),
            "MainWindow receives the active Media handoff before the previous Media is retired without replacing its player");
        Check(ReferenceEquals(tunerSourceField.GetValue(tuner), scheduledSource) &&
              ReferenceEquals(sourceTaskField.GetValue(scheduledSource), rewindPoller) && fixture.StreamRequests == rewindDirectRequests &&
              fixture.SharedSegmentRequests == fixture.SharedSegmentFetches.Values.Sum() &&
              fixture.SharedSegmentFetches.Values.All(count => count == 1),
            "rewind retains one shared source/poller and performs no duplicate/direct provider request");
        var liveReturn = await tuner.ReturnToLiveAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Check(liveReturn.Success && liveReturn.SelectedSequence is long returnedLiveSequence &&
              rewind.SelectedSequence is long rewoundSequence && returnedLiveSequence >= rewoundSequence &&
              ReferenceEquals(tuner.CurrentPlayer, rewindPlayer) && rewindPlayer is { IsPlaying: true } &&
              ReferenceEquals(tunerSourceField.GetValue(tuner), scheduledSource) &&
              ReferenceEquals(sourceTaskField.GetValue(scheduledSource), rewindPoller),
            $"return-to-live switches the same player to the latest buffered edge (sequence={liveReturn.SelectedSequence}; reason={liveReturn.FailureReason})");
        tuner.ActiveMediaChanged -= OnActiveMediaChanged;

        var scheduledPlaylistBaseline = fixture.SharedPlaylistRequests;
        var scheduledSegmentBaseline = fixture.SharedSegmentRequests;
        var scheduledStartUtc = DateTimeOffset.UtcNow;
        var scheduledEndUtc = scheduledStartUtc.AddSeconds(4);
        var scheduledPath = Path.Combine(root, "scheduled-coordinator-fixture.ts");
        using (var coordinator = new ScheduledRecordingCoordinator(accountId, store, tuner, (RecordingService)service))
        {
            var job = new ScheduledRecordingJob
            {
                Id = "ui-harness-scheduled-" + Guid.NewGuid().ToString("N"),
                AccountId = accountId,
                ChannelId = channel.Id,
                ChannelName = channel.Name,
                ProgrammeTitle = "Synthetic scheduled fixture",
                ProgrammeStartUtc = scheduledStartUtc,
                ProgrammeEndUtc = scheduledEndUtc,
                PrePadding = TimeSpan.Zero,
                PostPadding = TimeSpan.Zero,
                RequestedCaptureStartUtc = scheduledStartUtc,
                RequestedCaptureEndUtc = scheduledEndUtc,
                DestinationPath = scheduledPath
            };
            await coordinator.AddAsync(job);
            await coordinator.ProcessDueAsync(scheduledStartUtc);
            await WaitUntil(() => coordinator.Jobs.Single(item => item.Id == job.Id).Status == ScheduledRecordingStatus.Recording,
                "scheduled coordinator did not attach and reach Recording on the active synthetic HLS stream", TimeSpan.FromSeconds(12));
            var recordingJob = coordinator.Jobs.Single(item => item.Id == job.Id);
            Check(!string.IsNullOrWhiteSpace(recordingJob.RecordingId),
                "scheduled coordinator records the production RecordingService session ID");
            Check(ReferenceEquals(tuner.CurrentPlayer, scheduledPlayer) && scheduledPlayer.IsPlaying &&
                  ReferenceEquals(tunerSourceField.GetValue(tuner), scheduledSource) &&
                  ReferenceEquals(sourceTaskField.GetValue(scheduledSource), scheduledRunTask),
                "scheduled attachment preserves the active tuner player, shared source, and original poller");
            await WaitUntil(() => File.Exists(scheduledPath) && new FileInfo(scheduledPath).Length > 0,
                "scheduled coordinator capture did not write any HLS segment bytes", TimeSpan.FromSeconds(12));
            Check(fixture.SharedPlaylistRequests > scheduledPlaylistBaseline &&
                  fixture.SharedSegmentRequests > scheduledSegmentBaseline &&
                  fixture.SharedSegmentRequests == fixture.SharedSegmentFetches.Values.Sum() &&
                  fixture.SharedSegmentFetches.Values.All(count => count == 1),
                "scheduled attachment uses the existing upstream poller and each synthetic HLS segment is fetched once");
            var untilEnd = scheduledEndUtc - DateTimeOffset.UtcNow;
            if (untilEnd > TimeSpan.Zero) await Task.Delay(untilEnd);
            await coordinator.ProcessDueAsync(scheduledEndUtc);
            var completedJob = coordinator.Jobs.Single(item => item.Id == job.Id);
            Check(completedJob.Status == ScheduledRecordingStatus.Completed,
                $"scheduled coordinator marks the job Completed at its requested end (status={completedJob.Status}; reason={completedJob.Reason})");
            var finalSize = File.Exists(scheduledPath) ? new FileInfo(scheduledPath).Length : 0;
            var finalSnapshot = service.GetType().GetMethod("Find")!.Invoke(service, [completedJob.RecordingId!]);
            var reportedSize = (long?)finalSnapshot?.GetType().GetProperty("ByteSize")?.GetValue(finalSnapshot);
            Check(finalSize > 0 && reportedSize == finalSize,
                $"scheduled completion flushes the playable file before returning (file={finalSize}; service={reportedSize})");
            Check(ReferenceEquals(tuner.CurrentPlayer, scheduledPlayer) && scheduledPlayer.IsPlaying &&
                  ReferenceEquals(tunerSourceField.GetValue(tuner), scheduledSource) &&
                  ReferenceEquals(sourceTaskField.GetValue(scheduledSource), scheduledRunTask),
                "scheduled completion leaves the same tuner player and single HLS poller active");
        }

        // Exercise the non-HLS route through the production tuner and capture
        // service. The callbacks must retune the one input instead of opening a
        // second provider stream, then restore ordinary playback on Stop.
        var retunedRequest = new TuneRequest(channel.Id, channel.Name, fixture.ProgressiveTsUrl,
            "recording-ui-synthetic-progressive", IsLive: true, BufferMs: 200, AccountId: accountId);
        tuner.Play(retunedRequest);
        await WaitUntil(() => tuner.CurrentPlayer is { IsPlaying: true } &&
                              fixture.ProgressiveTsRequests >= 1 && tuner.CanRecordActiveStream,
            "production tuner did not restore the synthetic progressive stream", TimeSpan.FromSeconds(20));
        var ordinaryPlayer = tuner.CurrentPlayer!;
        var ordinaryRequestCount = fixture.ProgressiveTsRequests;
        var progressiveEventStart = fixture.ProgressiveTsEvents.Count;
        Check(ordinaryPlayer.IsPlaying && ReferenceEquals(Field("_mediaPlayer"), ordinaryPlayer),
            "production WPF player is active on the non-HLS fixture before retuned Record");
        Check(budget.LocalHeld == 0 && budget.Leases.Count == 0,
            "recording budget has no pre-existing lease before retuned Record");

        var retunedPath = Path.Combine(root, "retuned-output-fixture.ts");
        var retunedRequestStart = DateTimeOffset.UtcNow;
        var activeTsBeforeRetune = fixture.ActiveProgressiveTsConnections;
        var maxTsBeforeRetune = fixture.MaximumActiveProgressiveTsConnections;
        var retunedStartTask = (Task<CaptureOutcome>)service.GetType()
            .GetMethod("StartRetunedAsync")!.Invoke(service,
                [new RecordingStartRequest
                {
                    StreamUrl = fixture.ProgressiveTsUrl,
                    DestinationPath = retunedPath,
                    AccountId = accountId,
                    ChannelId = channel.Id,
                    ChannelKey = ItemIdentity.For(channel),
                    ChannelName = channel.Name,
                    Profile = profile,
                    Container = RecordingContainerKind.Ts,
                    RequestedStartUtc = retunedRequestStart,
                    BufferMs = 200
                },
                (Func<string, CancellationToken, Task<bool>>)tuner.StartRecordingOutputAsync,
                (Func<CancellationToken, Task<bool>>)tuner.StopRecordingOutputAsync,
                CancellationToken.None])!;
        var retunedStart = await retunedStartTask.WaitAsync(TimeSpan.FromSeconds(50));
        Check(retunedStart.Accepted && retunedStart.Entry is { IsActive: true },
            $"production non-HLS retuned recording starts (failure={retunedStart.Failure}; reason={retunedStart.FailureReason})");
        await WaitUntil(() => ((Button?)window.FindName("StopRecordingButton"))?.Visibility == Visibility.Visible,
            "Stop recording did not appear after the capture became active", TimeSpan.FromSeconds(8));
        Check(true, "Stop recording appears only after the production service starts a capture");
        await WaitUntil(() => tuner.CurrentPlayer is { IsPlaying: true } &&
                              !ReferenceEquals(tuner.CurrentPlayer, ordinaryPlayer) &&
                              File.Exists(retunedPath) && new FileInfo(retunedPath).Length >= 188,
            "retuned LibVLC output did not create a new playing tuner and MPEG-TS data", TimeSpan.FromSeconds(20));
        var recordingPlayer = tuner.CurrentPlayer!;
        Check(!ReferenceEquals(recordingPlayer, ordinaryPlayer) && recordingPlayer.IsPlaying,
            "Record briefly replaces the tuner MediaPlayer and resumes playback on the new identity");
        Check(fixture.ProgressiveTsRequests - ordinaryRequestCount == 1,
            $"retuned Record opens exactly one replacement provider request (delta={fixture.ProgressiveTsRequests - ordinaryRequestCount})");
        Check(budget.LocalHeld == 0 && budget.Leases.Count == 0,
            "retuned recording acquires no additional ConnectionBudget lease");
        await WaitUntil(() => new FileInfo(retunedPath).Length > 188,
            "retuned TS output did not grow after its first packet", TimeSpan.FromSeconds(8));
        var growingRetunedSize = new FileInfo(retunedPath).Length;
        await Task.Delay(700);
        Check(new FileInfo(retunedPath).Length > growingRetunedSize,
            "retuned TS recording continues growing while the production tuner plays");

        var retunedFinished = new TaskCompletionSource<CaptureOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<CaptureOutcome> typedFinishedHandler = outcome =>
        {
            if (outcome.RecordingId == retunedStart.RecordingId) retunedFinished.TrySetResult(outcome);
        };
        finishedEvent.AddEventHandler(service, typedFinishedHandler);
        var retunedStopAt = DateTimeOffset.UtcNow;
        var retunedStopTask = (Task<CaptureOutcome?>)service.GetType()
            .GetMethod("StopAsync")!.Invoke(service,
                [retunedStart.RecordingId, RecordingStopReason.User, CancellationToken.None])!;
        var retunedStopped = await retunedStopTask.WaitAsync(TimeSpan.FromSeconds(50));
        var retunedCompletion = await retunedFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => ((Button?)window.FindName("StopRecordingButton"))?.Visibility == Visibility.Collapsed,
            "Stop recording remained visible after the capture finished", TimeSpan.FromSeconds(8));
        Check(true, "Stop recording disappears after the capture stops");
        finishedEvent.RemoveEventHandler(service, typedFinishedHandler);
        Check(retunedStopped is { Accepted: true, ByteSize: > 188 } &&
              File.Exists(retunedPath) && new FileInfo(retunedPath).Length == retunedStopped.ByteSize,
            "Stop flushes the retuned TS output and reports its final byte size");
        Check(retunedCompletion.HasPlayableFile && retunedCompletion.ByteSize == new FileInfo(retunedPath).Length,
            "CaptureFinished fires only after retuned output is finalized and flushed");
        Check(retunedStopped?.Entry?.StoppedUtc is DateTimeOffset stoppedUtc &&
              Math.Abs((stoppedUtc - retunedStopAt).TotalMilliseconds) < 1000,
            "retuned Stop timestamp records the user stop edge");
        await WaitUntil(() => tuner.CurrentPlayer is { IsPlaying: true } &&
                              !ReferenceEquals(tuner.CurrentPlayer, recordingPlayer) &&
                              fixture.ProgressiveTsRequests >= ordinaryRequestCount + 2,
            "Stop did not restore ordinary playback on a fresh tuner player", TimeSpan.FromSeconds(20));
        var restoredPlayer = tuner.CurrentPlayer!;
        Check(!ReferenceEquals(restoredPlayer, recordingPlayer) && restoredPlayer.IsPlaying &&
              ReferenceEquals(Field("_mediaPlayer"), restoredPlayer),
            "Stop restores playback on a distinct production WPF player");
        Check(fixture.ProgressiveTsRequests - ordinaryRequestCount == 2,
            $"Record and Stop each make one sequential provider request, never concurrent ones (progressive TS delta={fixture.ProgressiveTsRequests - ordinaryRequestCount})");
        Check(fixture.MaximumActiveProgressiveTsConnections <= Math.Max(1, activeTsBeforeRetune),
            $"progressive TS concurrency does not exceed the already-active baseline or one (max={fixture.MaximumActiveProgressiveTsConnections}; active-before={activeTsBeforeRetune}; max-before={maxTsBeforeRetune}; events={string.Join(" | ", fixture.ProgressiveTsEvents.Skip(progressiveEventStart))})");
        Check(fixture.ActiveProgressiveTsConnections == 1,
            $"exactly one progressive TS provider response remains active after playback restore (active={fixture.ActiveProgressiveTsConnections})");
        Check(budget.LocalHeld == 0 && budget.Leases.Count == 0,
            "retuned Stop leaves ConnectionBudget lease count unchanged");

        var retunedProbe = new DecodeProbe();
        using (var verificationPlayer = new MediaPlayer((LibVLC)Field("_libVlc")!))
        {
            verificationPlayer.SetVideoFormat("RV32", 160, 90, 160 * 4);
            verificationPlayer.SetVideoCallbacks(retunedProbe.Lock, retunedProbe.Unlock, retunedProbe.Display);
            using var verifyRetunedMedia = new Media((LibVLC)Field("_libVlc")!, retunedPath, FromType.FromPath);
            Check(verificationPlayer.Play(verifyRetunedMedia), "LibVLC opens the stopped retuned TS for verification");
            await WaitUntil(() => retunedProbe.Frames > 0 || verificationPlayer.State is VLCState.Ended or VLCState.Error,
                "retuned TS produced no decodable frame", TimeSpan.FromSeconds(15));
            Check(retunedProbe.Frames > 0,
                $"LibVLC decodes the non-HLS retuned output when the fixture transcode permits it (frames={retunedProbe.Frames})");
            verificationPlayer.Stop();
        }
        retunedProbe.Dispose();
        File.AppendAllText(Path.Combine(root, "evidence.txt"),
            $"PASS test free-space probe returned 16 GiB; production policy still required {RecordingPolicy.MinFreeBytes} byte floor and {RecordingPolicy.SafetyMarginBytes} byte margin." + Environment.NewLine);
        File.AppendAllText(Path.Combine(root, "evidence.txt"),
            $"PASS decoder integration used a dedicated LibVLC player on SharedHlsSource.PlaybackUrl (decoded frames={decodeProbe.Frames}) while the existing WPF AVI smoke player remained independent." + Environment.NewLine);
    }

    private static void CreateSilentWave(string path)
    {
        const int sampleRate = 16_000;
        const int seconds = 15;
        const int sampleCount = sampleRate * seconds;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + sampleCount * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(sampleCount * 2);
        for (var i = 0; i < sampleCount; i++) writer.Write((short)0);
    }

    private sealed class DecodeProbe : IDisposable
    {
        private const int Width = 160;
        private const int Height = 90;
        private readonly byte[] pixels = new byte[Width * Height * 4];
        private readonly GCHandle pinned;
        private long frames;

        public DecodeProbe() => pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        public long Frames => Interlocked.Read(ref frames);

        public IntPtr Lock(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, pinned.AddrOfPinnedObject());
            return pinned.AddrOfPinnedObject();
        }

        public void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }

        public void Display(IntPtr opaque, IntPtr picture) => Interlocked.Increment(ref frames);

        public void Dispose()
        {
            if (pinned.IsAllocated) pinned.Free();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly byte[] video;
        private readonly byte[] wave;
        private byte[]? mpegTs;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task acceptLoop;
        public string Origin { get; }
        public string StreamUrl => Origin + "/live.avi";
        public string ProgressiveTsUrl => Origin + "/live.ts";
        public int StreamRequests => Volatile.Read(ref streamRequests);
        private int streamRequests;
        public int ProgressiveTsRequests => Volatile.Read(ref progressiveTsRequests);
        private int progressiveTsRequests;
        public int ActiveProgressiveTsConnections => Volatile.Read(ref activeProgressiveTsConnections);
        public int MaximumActiveProgressiveTsConnections => Volatile.Read(ref maximumActiveProgressiveTsConnections);
        public ConcurrentQueue<string> ProgressiveTsEvents { get; } = new();
        private int activeProgressiveTsConnections;
        private int maximumActiveProgressiveTsConnections;
        public int ActiveStreamConnections => Volatile.Read(ref activeStreamConnections);
        public int MaximumActiveStreamConnections => Volatile.Read(ref maximumActiveStreamConnections);
        private int activeStreamConnections;
        private int maximumActiveStreamConnections;
        private int sharedPlaylistRequests;
        private int sharedLatestSequence = 500;
        private int sharedSegmentRequests;
        private int pauseSharedSegmentResponses;
        private readonly ConcurrentDictionary<long, int> sharedSegmentFetches = new();
        public string SharedHlsPlaylistUrl => Origin + "/shared.m3u8";
        public int SharedPlaylistRequests => Volatile.Read(ref sharedPlaylistRequests);
        public int SharedSegmentRequests => Volatile.Read(ref sharedSegmentRequests);
        public long SharedLatestSequence => Volatile.Read(ref sharedLatestSequence);
        public IReadOnlyDictionary<long, int> SharedSegmentFetches => sharedSegmentFetches;

        public void PauseSharedSegmentResponses() => Interlocked.Exchange(ref pauseSharedSegmentResponses, 1);
        public void ResumeSharedSegmentResponses() => Interlocked.Exchange(ref pauseSharedSegmentResponses, 0);
        public byte[]? MpegTs => Volatile.Read(ref mpegTs);

        public void SetMpegTs(byte[] data)
        {
            if (data.Length < 188 || data[0] != 0x47) throw new InvalidDataException("Fixture is not an MPEG transport stream.");
            if (data.Length % 188 != 0) throw new InvalidDataException("Fixture MPEG-TS length is not packet aligned.");
            Volatile.Write(ref mpegTs, data);
        }

        public Fixture(byte[] data, byte[] captureData)
        {
            video = data;
            wave = captureData;
            listener.Start();
            Origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            acceptLoop = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    _ = RespondAsync(client);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (cancellation.IsCancellationRequested) { break; }
            }
        }

        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(cancellation.Token);
                    if (requestLine is null) return;
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation.Token))) { }
                    if (requestLine.Contains("/player_api.php", StringComparison.Ordinal))
                    {
                        var json = Encoding.UTF8.GetBytes("{\"user_info\":{\"status\":\"Active\",\"max_connections\":\"2\",\"active_cons\":\"0\"},\"server_info\":{\"timezone\":\"UTC\"}}");
                        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(response, cancellation.Token);
                        await stream.WriteAsync(json, cancellation.Token);
                        return;
                    }
                    if (requestLine.Contains("/shared.m3u8", StringComparison.Ordinal))
                    {
                        var poll = Interlocked.Increment(ref sharedPlaylistRequests);
                        var sequence = 500 + poll / 3 + 3;
                        Interlocked.Exchange(ref sharedLatestSequence, sequence);
                        var firstSequence = Math.Max(500, sequence - 2);
                        var entries = string.Join('\n', Enumerable.Range(0, checked((int)(sequence - firstSequence + 1)))
                            .Select(offset => $"#EXTINF:1.0,\nshared-segments/{firstSequence + offset}.ts"));
                        var playlist = Encoding.UTF8.GetBytes($"#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:{firstSequence}\n{entries}\n");
                        var sharedPlaylistHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/vnd.apple.mpegurl\r\nContent-Length: {playlist.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(sharedPlaylistHeader, cancellation.Token);
                        await stream.WriteAsync(playlist, cancellation.Token);
                        return;
                    }
                    if (requestLine.Contains("/shared-segments/", StringComparison.Ordinal))
                    {
                        var start = requestLine.IndexOf("/shared-segments/", StringComparison.Ordinal) + "/shared-segments/".Length;
                        var end = requestLine.IndexOf(".ts", start, StringComparison.Ordinal);
                        if (end < start || !long.TryParse(requestLine.AsSpan(start, end - start), out var sequence)) return;
                        Interlocked.Increment(ref sharedSegmentRequests);
                        sharedSegmentFetches.AddOrUpdate(sequence, 1, (_, count) => count + 1);
                        while (Volatile.Read(ref pauseSharedSegmentResponses) != 0)
                            await Task.Delay(25, cancellation.Token);
                        var tsData = MpegTs;
                        if (tsData is null)
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellation.Token);
                            return;
                        }
                        var data = tsData;
                        var sharedSegmentHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(sharedSegmentHeader, cancellation.Token);
                        await stream.WriteAsync(data, cancellation.Token);
                        return;
                    }
                    if (requestLine.Contains("/live.ts", StringComparison.Ordinal))
                    {
                        var data = MpegTs;
                        if (data is null)
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellation.Token);
                            return;
                        }

                        Interlocked.Increment(ref progressiveTsRequests);
                        var tsActive = Interlocked.Increment(ref activeProgressiveTsConnections);
                        UpdateMaximum(ref maximumActiveProgressiveTsConnections, tsActive);
                        ProgressiveTsEvents.Enqueue($"+{DateTimeOffset.UtcNow:O} active={tsActive}");
                        try
                        {
                            var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nConnection: keep-alive\r\n\r\n");
                            await stream.WriteAsync(header, cancellation.Token);
                            while (!cancellation.IsCancellationRequested)
                            {
                                await stream.WriteAsync(data, cancellation.Token);
                                await Task.Delay(100, cancellation.Token);
                            }
                        }
                        finally
                        {
                            var remaining = Interlocked.Decrement(ref activeProgressiveTsConnections);
                            ProgressiveTsEvents.Enqueue($"-{DateTimeOffset.UtcNow:O} active={remaining}");
                        }
                        return;
                    }
                    if (!requestLine.Contains("/live.avi", StringComparison.Ordinal))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellation.Token);
                        return;
                    }

                    Interlocked.Increment(ref streamRequests);
                    var active = Interlocked.Increment(ref activeStreamConnections);
                    UpdateMaximum(ref maximumActiveStreamConnections, active);
                    try
                    {
                        var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/x-msvideo\r\nConnection: keep-alive\r\n\r\n");
                        await stream.WriteAsync(header, cancellation.Token);
                        var offset = 0;
                        while (!cancellation.IsCancellationRequested)
                        {
                            var count = Math.Min(64 * 1024, video.Length - offset);
                            await stream.WriteAsync(video.AsMemory(offset, count), cancellation.Token);
                            offset = (offset + count) % video.Length;
                            await Task.Delay(35, cancellation.Token);
                        }
                    }
                    finally { Interlocked.Decrement(ref activeStreamConnections); }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            listener.Stop();
            try { acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            cancellation.Dispose();
        }

        private static void UpdateMaximum(ref int target, int candidate)
        {
            var observed = Volatile.Read(ref target);
            while (candidate > observed)
            {
                var prior = Interlocked.CompareExchange(ref target, candidate, observed);
                if (prior == observed) return;
                observed = prior;
            }
        }
    }
}
