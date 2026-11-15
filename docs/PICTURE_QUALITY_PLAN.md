# Playback stability and picture quality plan (Steps 32–37)

Planned 4 November 2026. Roadmap entries: [ROADMAP.md](../ROADMAP.md), section "Planned — playback stability and picture quality".

This document is an implementation script. Each step lists the exact files, the exact text to find, the exact code to insert, the tests to add and the checks to run. Follow it literally. Do not redesign, rename or "improve" anything that is not listed.

## How to use this plan

1. Do **one step per session**, in order: 32 → 33 → 34 → 35 → 36 → 37. Later steps depend on code from earlier steps.
2. Before you start a step, read `AGENTS.md` in the parent folder (`C:\Projects\doing\cyrus-iptv\AGENTS.md`) and the step's whole section here.
3. Every "Find" block is text that exists in the file. Use the Edit tool with that text as `old_string`. If a Find block is not found exactly, **stop and report**. Do not guess another location.
4. After each step: build, run the checks, publish the lite build if the step says so, update `ROADMAP.md`, then report to the user. Do not continue to the next step in the same session.
5. **Stop and report** (do not improvise) if:
   - the build fails and the cause is not an obvious typo in code you just pasted;
   - a regression check that passed before your change now fails;
   - a LibVLCSharp member named in this plan does not exist (compiler error CS1061/CS0117 on a LibVLC type).

## Background (verified 4 November 2026)

- Two "Application Hang" events (Windows Event Log, type *Cross-thread*) at 11:13:33 and 11:15:43 came right after the user left a real 2160p channel. The picture had frozen (`decodedVideo=21; displayed=2` in 9 s while audio decoded). The channels marked "4K" in their names are really 1920×1080 and work fine.
- The PC is an Intel HD Graphics 520 (driver 30.0.101.1338). It cannot decode 4K 10-bit HEVC well. On 2 November the GPU also reported `DXGI_ERROR_DEVICE_REMOVED` (`hr=0x887A0005`) hundreds of times before another hang.
- Cause of the hang: `ChannelTuner` calls `player.Stop()` on a worker thread while the UI thread still polls that same player every 500 ms (`_positionTimer` → `UpdatePlaybackPosition`, `UpdateStreamInfo`). LibVLC's Stop waits for the video output, which needs the UI thread; the UI thread waits for LibVLC. Neither moves.
- A previous investigation (ROADMAP, 26 October) proved that CPU video filters (LibVLC's `adjust`) make 1080p50 pictures miss their display time on this PC. Every CPU filter in this plan must therefore be optional, off by default, and covered by the frozen-picture fallback.
- LibVLC 3 reads `--video-filter`, `--deinterlace*` and `--d3d11-*` options from the **LibVLC instance**, not from per-media `:options`. These settings apply when the player engine starts ("Restart the app to apply"), exactly like the existing "Hardware video decoding (GPU)" setting. Decoder options (`:avcodec-*`) do work per media.
- Installed VLC 3.0.23 (used first by the lite build) has the filters `postproc`, `hqdn3d`, `gradfun`, `sharpen`, `deinterlace` and the Direct3D 11 options `d3d11-upscale-mode` (`linear|point|processor|super`) and `d3d11-hdr-mode` (`auto|never|always|generate`). The **bundled** LibVLC in the full package has everything except `postproc`. The app must detect which filters exist and disable the missing ones in the menu.

## Ground rules for every step

- Work only in `C:\Projects\doing\cyrus-iptv\public`. Never read, write or copy anything in `private\`.
- Do not commit, stage, push or publish to GitHub. The user commits separately.
- The working tree already contains uncommitted user changes (for example in `ChannelTuner.cs`, `MainWindow.xaml.cs`, `ROADMAP.md`). Keep them. Only make the edits listed here.
- Do not open real provider streams. The user tests real channels. Use only the commands below.
- Never kill the player process. If it is running and blocks a publish, ask the user to close it.
- All user-visible text is English. Format numbers for LibVLC options with `CultureInfo.InvariantCulture` (the PC uses a German locale, where `0.5` would become `0,5`).

## Commands

Run from `C:\Projects\doing\cyrus-iptv\public` in PowerShell.

Build (must end with `0 Error(s)`):

```powershell
dotnet build src\ClassicWindowsIptvPlayer.Windows\ClassicWindowsIptvPlayer.Windows.csproj -c Debug --nologo -p:DefaultItemExcludesInProjectFolder=**/artifacts/**
```

Regression checks. Run once **before** your first edit of a step, then again after the step:

```powershell
dotnet run --project tests\ClassicWindowsIptvPlayer.Core.RegressionTests -c Release > $env:TEMP\cyrus-checks-before.txt
# after the step:
dotnet run --project tests\ClassicWindowsIptvPlayer.Core.RegressionTests -c Release > $env:TEMP\cyrus-checks-after.txt
Select-String -Path $env:TEMP\cyrus-checks-before.txt -Pattern 'FAIL'
Select-String -Path $env:TEMP\cyrus-checks-after.txt -Pattern 'FAIL'
```

The runner may already exit 1 because of an existing `DataMigrationRegressionChecks` failure ("default root is beside executable"). That is known. The rule is: **every FAIL line after the step must also be in the before list**, and every new check you added must print `PASS`.

Quick run of only the checks from this plan (available from Step 32):

```powershell
dotnet run --project tests\ClassicWindowsIptvPlayer.Core.RegressionTests -c Release -- --playback-quality
```

Publish the lite build for the user to test (only when the step says so). First check the app is closed:

```powershell
Get-Process "Classic Windows IPTV Player" -ErrorAction SilentlyContinue
```

If that prints a process, ask the user to close the app and wait. Then:

```powershell
dotnet publish src\ClassicWindowsIptvPlayer.Windows\ClassicWindowsIptvPlayer.Windows.csproj -c Release -r win-x64 --self-contained false -o release\lite /p:BundleVlc=false /p:PublishSingleFile=false --nologo
```

This keeps the user's `accounts`, `logs` and `Recordings` folders in `release\lite`. Do not delete anything in that folder.

Check for app hangs after the user tested (replace the time with when the test started):

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Hang'; StartTime=(Get-Date '2026-11-05 18:00')} -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'IPTV' } | Select-Object TimeCreated
```

No output means no hang.

---

## Step 32 — Switching away from a stuck stream never freezes the app

**Goal:** leaving a channel whose picture is frozen (switch channel, Stop, or an automatic reopen) must never hang the window. Two parts: (1) before stopping a player, the UI drops its references to it so its timers stop calling into it; (2) the Stop call itself gets a 5-second limit, after which the player is abandoned instead of blocking.

### 32.1 New file `src/ClassicWindowsIptvPlayer.Windows/BoundedNativeCall.cs`

Create it with exactly this content:

```csharp
using System;
using System.Threading;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>
/// Runs a native call that can block forever (LibVLC Stop on a stuck video output)
/// on its own background thread and waits only a bounded time for it.
/// </summary>
internal static class BoundedNativeCall
{
    /// <summary>
    /// Returns false when <paramref name="call"/> is still running after <paramref name="timeout"/>.
    /// The call keeps running on a background thread; it never blocks app exit.
    /// </summary>
    public static bool Run(Action call, TimeSpan timeout, Action<Exception>? onError = null)
    {
        var thread = new Thread(() =>
        {
            try { call(); }
            catch (Exception exception)
            {
                try { onError?.Invoke(exception); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "Bounded native call"
        };
        thread.Start();
        return thread.Join(timeout);
    }
}
```

### 32.2 `ChannelTuner.cs` — the stopping event

Find:

```csharp
    public event Action<MediaPlayer>? PlayerDetaching;
```

Replace with:

```csharp
    public event Action<MediaPlayer>? PlayerDetaching;

    /// <summary>
    /// Raised on the tuner worker thread just before a player that is being retired is
    /// stopped. The host must stop calling into this player (properties, tracks, stats)
    /// before returning, but must keep the video view bound until PlayerDetaching.
    /// </summary>
    public event Action<MediaPlayer>? PlayerStopping;
```

### 32.3 `ChannelTuner.cs` — fields

Find:

```csharp
    private readonly HashSet<string> _unadjustedVideoUrls = new(StringComparer.Ordinal);
```

Replace with:

```csharp
    private readonly HashSet<string> _unadjustedVideoUrls = new(StringComparer.Ordinal);
    private static readonly TimeSpan RetirementStopTimeout = TimeSpan.FromSeconds(5);
    // Players whose Stop never returned. They stay referenced and are never disposed:
    // disposing would block on the same stuck native call.
    private readonly List<(MediaPlayer Player, Media? Media)> _abandonedPlayers = new();
```

### 32.4 `ChannelTuner.cs` — bounded stop helpers

Find:

```csharp
    private static void StopPlayer(MediaPlayer player)
    {
        try { player.Stop(); }
        catch (Exception ex) { AppLogger.Warn("Tuner: player Stop failed. " + AppLogger.SanitizeText(ex.Message)); }
    }
```

Replace with:

```csharp
    private static void StopPlayer(MediaPlayer player)
    {
        try { player.Stop(); }
        catch (Exception ex) { AppLogger.Warn("Tuner: player Stop failed. " + AppLogger.SanitizeText(ex.Message)); }
    }

    // Stops a player that is about to be retired. The host first drops its own
    // references (PlayerStopping) so the UI thread cannot be inside a call to this
    // player while LibVLC waits for the video output, which needs the UI thread.
    private bool StopPlayerForRetirement(MediaPlayer player)
    {
        try { PlayerStopping?.Invoke(player); }
        catch (Exception ex) { AppLogger.Error("Tuner: PlayerStopping handler failed.", ex); }

        var stopped = BoundedNativeCall.Run(() => player.Stop(), RetirementStopTimeout,
            ex => AppLogger.Warn("Tuner: player Stop failed. " + AppLogger.SanitizeText(ex.Message)));
        if (!stopped)
            AppLogger.Warn("Tuner: LibVLC did not stop the previous player within " + RetirementStopTimeout.TotalSeconds +
                " s; abandoning it so the app stays responsive.");
        return stopped;
    }

    private void Abandon(MediaPlayer player, Media? media)
    {
        lock (_gate) _abandonedPlayers.Add((player, media));
    }
```

Do **not** change the other `StopPlayer(player);` calls in `SwitchActivePlayerInputAsync`. Those switch the input of a player that stays in use.

### 32.5 `ChannelTuner.cs` — use it when the old player is handed off

Find:

```csharp
            if (oldPlayer is not null)
            {
                StopPlayer(oldPlayer);
                RaiseDetaching(oldPlayer);
                Retire(oldPlayer, oldMedia);
            }
```

Replace with:

```csharp
            if (oldPlayer is not null)
            {
                var oldStopped = StopPlayerForRetirement(oldPlayer);
                RaiseDetaching(oldPlayer);
                if (oldStopped) Retire(oldPlayer, oldMedia);
                else Abandon(oldPlayer, oldMedia);
            }
```

### 32.6 `ChannelTuner.cs` — use it in `ReleaseAttemptAsync`

Find:

```csharp
        // HWND lets LibVLC fall back to a top-level Direct3D11 output window.
        StopPlayer(player);
```

Replace with:

```csharp
        // HWND lets LibVLC fall back to a top-level Direct3D11 output window.
        var stopped = StopPlayerForRetirement(player);
```

Then find (same method, a few lines lower):

```csharp
        if (wasActive) RaiseDetaching(player);
        Retire(player, ownedMedia);
```

Replace with:

```csharp
        if (wasActive) RaiseDetaching(player);
        if (stopped) Retire(player, ownedMedia);
        else Abandon(player, ownedMedia);
```

### 32.7 `MainWindow.xaml.cs` — remember the player being stopped

Find:

```csharp
    private MediaPlayer? _mediaPlayer;
```

Replace with:

```csharp
    private MediaPlayer? _mediaPlayer;
    // The player the tuner is stopping right now; only its Stopped event may still update the UI.
    private MediaPlayer? _stoppingPlayer;
```

### 32.8 `MainWindow.xaml.cs` — subscribe

Find:

```csharp
        _tuner.PlayerDetaching += OnTunerPlayerDetaching;
```

Replace with:

```csharp
        _tuner.PlayerDetaching += OnTunerPlayerDetaching;
        _tuner.PlayerStopping += OnTunerPlayerStopping;
```

### 32.9 `MainWindow.xaml.cs` — the handler

Find:

```csharp
    // A timeshift cursor replaces only the active Media input, not the player.
```

Replace with:

```csharp
    // Called on the tuner worker thread just before it stops a player it is retiring.
    // LibVLC's Stop can wait for the video output, which needs this UI thread. If the
    // UI thread were inside a call to the same player at that moment (the 500 ms
    // position timer reads it), both would wait forever: Windows reports
    // "Application Hang, cross-thread". Drop every UI reference except the video view
    // binding, which must stay until Stop finishes (see ReleaseAttemptAsync).
    private void OnTunerPlayerStopping(MediaPlayer player)
    {
        if (_isShuttingDown) return;
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (_isShuttingDown || !ReferenceEquals(_mediaPlayer, player)) return;
                _stoppingPlayer = player;
                _mediaPlayer = null;
                _currentMedia = null;
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Player stopping handler failed. " + AppLogger.SanitizeText(ex.Message));
        }
    }

    // A timeshift cursor replaces only the active Media input, not the player.
```

### 32.10 `MainWindow.xaml.cs` — keep the idle background working after Stop

Because `_mediaPlayer` is now null when the old player raises `Stopped`, the existing handler would ignore it and the idle background would not reappear after pressing Stop.

Find:

```csharp
        player.Stopped += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(player, _mediaPlayer)) return;
```

Replace with:

```csharp
        player.Stopped += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            var stoppedWhileRetiring = ReferenceEquals(player, _stoppingPlayer) && _mediaPlayer is null;
            if (!ReferenceEquals(player, _mediaPlayer) && !stoppedWhileRetiring) return;
```

### 32.11 `MainWindow.xaml.cs` — clear the reference when a new player attaches

Find (inside `OnTunerPlayerAttached`):

```csharp
            _mediaPlayer = player;
            _currentMedia = media;
            _preferredAudioApplied = false;
```

Replace with:

```csharp
            _mediaPlayer = player;
            _currentMedia = media;
            _stoppingPlayer = null;
            _preferredAudioApplied = false;
```

If this Find text appears more than once, use the occurrence inside `OnTunerPlayerAttached` (it is followed by `_preferredSubtitleApplied = false;` and then the comment `// Materialize and assign the persistent child HWND before Play.`).

### 32.12 Tests — new file `tests/ClassicWindowsIptvPlayer.Core.RegressionTests/PlaybackQualityRegressionChecks.cs`

Create it with exactly this content. Later steps add checks to the list marked `// Step 33 checks go here.` and so on.

```csharp
using System.Diagnostics;
using ClassicWindowsIptvPlayer.Windows;

internal static class PlaybackQualityRegressionChecks
{
    public static int Run()
    {
        var checks = new List<(string Name, Action Run)>
        {
            // Step 32
            ("bounded native call returns true when the call finishes", () =>
            {
                var ran = false;
                True(BoundedNativeCall.Run(() => ran = true, TimeSpan.FromSeconds(5)));
                True(ran);
            }),
            ("bounded native call gives up on a call that never returns", () =>
            {
                var never = new ManualResetEventSlim(false);
                var watch = Stopwatch.StartNew();
                True(!BoundedNativeCall.Run(() => never.Wait(), TimeSpan.FromMilliseconds(300)));
                True(watch.ElapsedMilliseconds < 3000, "Run waited too long: " + watch.ElapsedMilliseconds + " ms");
                never.Set();
            }),
            ("bounded native call reports an exception and still counts as finished", () =>
            {
                Exception? seen = null;
                True(BoundedNativeCall.Run(() => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5), ex => seen = ex));
                True(seen is InvalidOperationException);
            }),
            // Step 33 checks go here.
            // Step 34 checks go here.
            // Step 35 checks go here.
            // Step 36 checks go here.
            // Step 37 checks go here.
        };

        var failures = 0;
        foreach (var (name, run) in checks)
        {
            try
            {
                run();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
            }
        }
        Console.WriteLine($"{checks.Count - failures}/{checks.Count} playback quality checks passed; {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void True(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "Expected true.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected <{expected}>, got <{actual}>.");
    }
}
```

### 32.13 Tests — link the file and register the checks

In `tests/ClassicWindowsIptvPlayer.Core.RegressionTests/ClassicWindowsIptvPlayer.Core.RegressionTests.csproj`, find:

```xml
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/VideoStallDetector.cs" Link="VideoStallDetector.cs" />
```

Replace with:

```xml
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/VideoStallDetector.cs" Link="VideoStallDetector.cs" />
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/BoundedNativeCall.cs" Link="BoundedNativeCall.cs" />
```

In `tests/ClassicWindowsIptvPlayer.Core.RegressionTests/Program.cs`, find:

```csharp
if (args.Contains("--background-refresh")) return BackgroundRefreshRegressionChecks.Run();
```

Replace with:

```csharp
if (args.Contains("--background-refresh")) return BackgroundRefreshRegressionChecks.Run();
if (args.Contains("--playback-quality")) return PlaybackQualityRegressionChecks.Run();
```

Then find:

```csharp
var crashSessionMarkerResult = CrashSessionMarkerRegressionChecks.Run();
```

Replace with:

```csharp
var crashSessionMarkerResult = CrashSessionMarkerRegressionChecks.Run();
var playbackQualityResult = PlaybackQualityRegressionChecks.Run();
```

Then find:

```csharp
&& crashSessionMarkerResult == 0 ? 0 : 1;
```

Replace with:

```csharp
&& crashSessionMarkerResult == 0 && playbackQualityResult == 0 ? 0 : 1;
```

### 32.14 Build, check, publish

1. Build. Run the regression checks (before/after rule). The three Step 32 checks must print `PASS`.
2. Publish the lite build.
3. Ask the user to test, then run the hang check command for the test period:
   - Open a real 2160p channel (on the user's account: "DE: UHD1 BY ASTRA [1080p]"; the name is wrong, it is 4K). Wait about 15 seconds until the picture freezes.
   - Switch to another channel. The window must keep responding, and the new channel must start within about 6 seconds.
   - Open the 4K channel again, wait 15 seconds, press Stop. The idle background must appear.
   - Ordinary checks: switch between three normal channels quickly; press Stop on a normal channel (idle background appears); pause and resume a live channel.
4. In the newest `release\lite\logs\session-*\app.log`, note whether `abandoning it so the app stays responsive` appears. Either outcome is fine. If it appears, record it in the roadmap.

**Done when:** build has 0 errors, no new FAIL lines, and either the user confirmed no hang or the roadmap says the confirmation is pending.

**Known limit (record in the roadmap):** the shutdown path still calls `_mediaPlayer?.Stop()` on the UI thread. A stuck player at exit can still delay closing. It is left unchanged on purpose: a bounded wait on the UI thread would block the message pump that the video output needs.

---

## Step 33 — Log each stream's video format and which decoder LibVLC uses

**Goal:** the log must show, for every channel, the codec, resolution, profile and frame rate, plus whether a GPU decoder was used. Later steps make decisions from this. Also stop the log from filling with hundreds of identical LibVLC error lines.

### 33.1 New file `src/ClassicWindowsIptvPlayer.Windows/VideoFormatInfo.cs`

```csharp
using System;
using System.Globalization;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>The decoded video format of a stream, read from LibVLC's track list.</summary>
internal sealed record VideoFormatInfo(string Codec, int Width, int Height, int Profile, double FramesPerSecond)
{
    /// <summary>More pixels than 1920x1080: 1440p and 4K/UHD.</summary>
    public bool IsAboveFullHd => Width > 1920 || Height > 1080;

    /// <summary>SD and 720p. 1080-line pictures, including 1440x1080, are not below Full HD.</summary>
    public bool IsBelowFullHd => Height > 0 && Height < 1000;

    /// <summary>HEVC Main 10 (profile 2) needs 10-bit HEVC decoding hardware.</summary>
    public bool IsLikely10BitHevc => Codec == "HEVC" && Profile == 2;

    public static VideoFormatInfo? Create(uint fourCc, uint width, uint height, int profile, uint frameRateNum, uint frameRateDen)
    {
        if (width == 0 || height == 0 || width > 16384 || height > 16384) return null;
        var fps = frameRateDen == 0 ? 0 : Math.Round((double)frameRateNum / frameRateDen, 2);
        return new VideoFormatInfo(FourCcToText(fourCc), (int)width, (int)height, profile, fps);
    }

    public static string FourCcToText(uint fourCc)
    {
        Span<char> chars = stackalloc char[4];
        var length = 0;
        for (var i = 0; i < 4; i++)
        {
            var b = (byte)(fourCc >> (8 * i));
            if (b == 0) continue;
            chars[length++] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }
        var text = new string(chars[..length]).Trim().ToUpperInvariant();
        return text.Length == 0 ? "UNKNOWN" : text;
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"codec={Codec}; width={Width}; height={Height}; profile={Profile}; fps={FramesPerSecond:0.##}");
}
```

### 33.2 New file `src/ClassicWindowsIptvPlayer.Windows/RepeatedLogFilter.cs`

```csharp
using System;
using System.Collections.Generic;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>
/// Drops identical log lines that repeat within a short window (LibVLC can print the
/// same Direct3D error dozens of times per second) and reports how many were dropped.
/// Thread-safe.
/// </summary>
internal sealed class RepeatedLogFilter
{
    private const int MaxTrackedMessages = 256;
    private readonly long _windowMs;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long LoggedAtMs, int Suppressed)> _recent = new(StringComparer.Ordinal);

    public RepeatedLogFilter(long windowMs = 5000) => _windowMs = windowMs;

    /// <summary>
    /// True when the message should be written now. <paramref name="suppressedBefore"/> is the
    /// number of identical messages dropped since this message was last written.
    /// </summary>
    public bool ShouldLog(string message, long nowMs, out int suppressedBefore)
    {
        lock (_lock)
        {
            if (_recent.TryGetValue(message, out var entry) && nowMs - entry.LoggedAtMs < _windowMs)
            {
                _recent[message] = (entry.LoggedAtMs, entry.Suppressed + 1);
                suppressedBefore = 0;
                return false;
            }
            suppressedBefore = entry.Suppressed;
            if (_recent.Count >= MaxTrackedMessages && !_recent.ContainsKey(message)) _recent.Clear();
            _recent[message] = (nowMs, 0);
            return true;
        }
    }
}
```

### 33.3 `ChannelTuner.cs` — fields and the decoder notification

Find:

```csharp
    private readonly List<(MediaPlayer Player, Media? Media)> _abandonedPlayers = new();
```

Replace with:

```csharp
    private readonly List<(MediaPlayer Player, Media? Media)> _abandonedPlayers = new();
    // Format of the active player's video, once LibVLC reports it. Guarded by _gate.
    private VideoFormatInfo? _activeVideoFormat;
    // Tune generation in which LibVLC last opened a hardware video decoder.
    private int _hardwareDecoderGeneration = -1;
    private const long VideoFormatProbeMs = 10000;
```

Find:

```csharp
    public ChannelTuner(LibVLC libVlc)
    {
        _libVlc = libVlc;
    }
```

Replace with:

```csharp
    public ChannelTuner(LibVLC libVlc)
    {
        _libVlc = libVlc;
    }

    /// <summary>Called by the host from LibVLC's log callback when a hardware video decoder was opened.</summary>
    public void NotifyHardwareDecoderInUse() => Volatile.Write(ref _hardwareDecoderGeneration, Volatile.Read(ref _generation));
```

### 33.4 `ChannelTuner.cs` — reset the format when a new player is installed

Find:

```csharp
                    _livePositionClockFallback = false;
                    _activeRequest = request;
                    installed = true;
```

Replace with:

```csharp
                    _livePositionClockFallback = false;
                    _activeRequest = request;
                    _activeVideoFormat = null;
                    installed = true;
```

### 33.5 `ChannelTuner.cs` — new end reason

Find:

```csharp
        StoppedExternally,
        VideoStalled
    }
```

Replace with:

```csharp
        StoppedExternally,
        VideoStalled,
        // The format callback asked to reopen the channel (Step 37 uses this).
        ReopenRequested
    }
```

### 33.6 `ChannelTuner.cs` — `MonitorAsync` reads the video format

Find:

```csharp
    private async Task<StreamEnd> MonitorAsync(MediaPlayer player, CancellationToken cancellationToken,
        VodRecoveryPosition? vodPosition, VideoStallDetector? stallDetector = null)
    {
```

Replace with:

```csharp
    private async Task<StreamEnd> MonitorAsync(MediaPlayer player, Media media, int generation, CancellationToken cancellationToken,
        VodRecoveryPosition? vodPosition, VideoStallDetector? stallDetector = null, Func<VideoFormatInfo, bool>? onVideoFormat = null)
    {
```

Find (inside `MonitorAsync`):

```csharp
            var monitorStarted = Environment.TickCount64;
            var nextStallSample = monitorStarted;
```

Replace with:

```csharp
            var monitorStarted = Environment.TickCount64;
            var nextStallSample = monitorStarted;
            var nextFormatSample = monitorStarted;
            var videoFormatReported = false;
```

Find (inside `MonitorAsync`):

```csharp
                    if (SampleVideoStall(player, stallDetector, Environment.TickCount64 - monitorStarted))
                        outcome.TrySetResult(StreamEnd.VideoStalled);
                }
```

Replace with:

```csharp
                    if (SampleVideoStall(player, stallDetector, Environment.TickCount64 - monitorStarted))
                        outcome.TrySetResult(StreamEnd.VideoStalled);
                }

                if (onVideoFormat is not null && !videoFormatReported && Environment.TickCount64 >= nextFormatSample &&
                    Environment.TickCount64 - monitorStarted <= VideoFormatProbeMs)
                {
                    nextFormatSample = Environment.TickCount64 + 500;
                    if (TryReadVideoFormat(media) is { } format)
                    {
                        videoFormatReported = true;
                        if (onVideoFormat(format)) outcome.TrySetResult(StreamEnd.ReopenRequested);
                    }
                }
                // Step 34 inserts the graphics-device check here.
```

### 33.7 `ChannelTuner.cs` — reading the track and handling the format

Find:

```csharp
    private bool SampleVideoStall(MediaPlayer player, VideoStallDetector detector, long playingForMs)
```

Replace with:

```csharp
    private static VideoFormatInfo? TryReadVideoFormat(Media media)
    {
        try
        {
            foreach (var track in media.Tracks)
            {
                if (track.TrackType != TrackType.Video) continue;
                var video = track.Data.Video;
                var format = VideoFormatInfo.Create(track.Codec, video.Width, video.Height, track.Profile, video.FrameRateNum, video.FrameRateDen);
                if (format is not null) return format;
            }
        }
        catch
        {
            // Tracks are not ready yet, or native teardown raced the read.
        }
        return null;
    }

    // Records and logs the format. Returns true to reopen the channel (Step 37).
    private bool OnVideoFormatKnown(TuneRequest request, VideoFormatInfo format)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeRequest, request)) _activeVideoFormat = format;
        }
        AppLogger.Info("Tuner: video format. " + format.Describe() + (format.IsLikely10BitHevc ? "; 10-bit HEVC" : string.Empty) +
            "; channel=" + request.ChannelName);
        return false;
    }

    private bool SampleVideoStall(MediaPlayer player, VideoStallDetector detector, long playingForMs)
```

If the compiler says `MediaTrack` has no `Data`, `Codec` or `Profile`, or `Data.Video` has no `Width`, `Height`, `FrameRateNum` or `FrameRateDen` (LibVLCSharp 3.9.7.1 has all of these), stop and report.

### 33.8 `ChannelTuner.cs` — update the three `MonitorAsync` calls

Find:

```csharp
                var end = await MonitorAsync(player, cancellationToken,
                    request.IsLive ? null : vodPosition, watchForStall ? new VideoStallDetector() : null).ConfigureAwait(false);
```

Replace with:

```csharp
                var end = await MonitorAsync(player, media, generation, cancellationToken,
                    request.IsLive ? null : vodPosition, watchForStall ? new VideoStallDetector() : null,
                    format => OnVideoFormatKnown(request, format)).ConfigureAwait(false);
                if (end == StreamEnd.ReopenRequested)
                {
                    await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
                    retryBudget.RefundAttempt();
                    continue;
                }
                // Step 34 inserts the graphics-device handling here.
```

Find:

```csharp
                    if (stallFallback is null) end = await MonitorAsync(player, cancellationToken, null).ConfigureAwait(false);
```

Replace with:

```csharp
                    if (stallFallback is null) end = await MonitorAsync(player, media, generation, cancellationToken, null).ConfigureAwait(false);
```

Find:

```csharp
                        await MonitorAsync(player, cancellationToken, null).ConfigureAwait(false);
```

Replace with:

```csharp
                        await MonitorAsync(player, media, generation, cancellationToken, null).ConfigureAwait(false);
```

After these three edits, search `ChannelTuner.cs` for `MonitorAsync(player, cancellationToken`. There must be no matches.

### 33.9 `MainWindow.xaml.cs` — one LibVLC log handler

Find:

```csharp
        _libVlc.Log += (_, e) =>
        {
            if (e.Level < LibVLCSharp.Shared.LogLevel.Error) return;
            AppLogger.Warn("libvlc " + (e.Module ?? "core") + ": " + e.Message);
        };
```

Replace with:

```csharp
        _libVlc.Log += OnLibVlcLog;
```

Find:

```csharp
    private async Task InitializePlayerAsync()
    {
```

Replace with:

```csharp
    private readonly RepeatedLogFilter _libVlcLogFilter = new();

    // LibVLC calls this on its own threads, often several at the same time.
    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        var message = e.Message ?? string.Empty;
        var module = e.Module ?? "core";

        // Which decoder LibVLC picked, e.g. "Using D3D11VA (Intel(R) HD Graphics 520, ...) for hardware decoding".
        if (message.StartsWith("Using ", StringComparison.Ordinal) &&
            message.Contains("for hardware decoding", StringComparison.OrdinalIgnoreCase))
        {
            if (_libVlcLogFilter.ShouldLog(module + ": " + message, Environment.TickCount64, out _))
                AppLogger.Info("libvlc " + module + ": " + message);
            _tuner?.NotifyHardwareDecoderInUse();
            return;
        }

        // Step 34 inserts the graphics-device check here.
        // Step 35 inserts the picture-feature checks here.

        if (e.Level < LibVLCSharp.Shared.LogLevel.Error) return;
        var text = "libvlc " + module + ": " + message;
        if (!_libVlcLogFilter.ShouldLog(text, Environment.TickCount64, out var repeated)) return;
        AppLogger.Warn(repeated > 0 ? text + " (repeated " + repeated + " more times)" : text);
    }

    private async Task InitializePlayerAsync()
    {
```

`LogEventArgs` is `LibVLCSharp.Shared.LogEventArgs`. `MainWindow.xaml.cs` already uses `LibVLCSharp.Shared`. If the compiler cannot find `LogEventArgs`, write `LibVLCSharp.Shared.LogEventArgs`.

### 33.10 Tests

In the csproj, find:

```xml
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/BoundedNativeCall.cs" Link="BoundedNativeCall.cs" />
```

Replace with:

```xml
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/BoundedNativeCall.cs" Link="BoundedNativeCall.cs" />
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/VideoFormatInfo.cs" Link="VideoFormatInfo.cs" />
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/RepeatedLogFilter.cs" Link="RepeatedLogFilter.cs" />
```

In `PlaybackQualityRegressionChecks.cs`, find `            // Step 33 checks go here.` and replace it with:

```csharp
            // Step 33
            ("4K HEVC Main 10 is above Full HD and likely 10-bit", () =>
            {
                var format = VideoFormatInfo.Create(FourCc("hevc"), 3840, 2160, 2, 50, 1)!;
                Equal("HEVC", format.Codec);
                True(format.IsAboveFullHd);
                True(!format.IsBelowFullHd);
                True(format.IsLikely10BitHevc);
                Equal("codec=HEVC; width=3840; height=2160; profile=2; fps=50", format.Describe());
            }),
            ("1080p and 1440x1080 are neither above nor below Full HD", () =>
            {
                var full = VideoFormatInfo.Create(FourCc("h264"), 1920, 1080, 100, 25, 1)!;
                True(!full.IsAboveFullHd && !full.IsBelowFullHd);
                var anamorphic = VideoFormatInfo.Create(FourCc("h264"), 1440, 1080, 100, 25, 1)!;
                True(!anamorphic.IsAboveFullHd && !anamorphic.IsBelowFullHd);
            }),
            ("720p and SD are below Full HD", () =>
            {
                True(VideoFormatInfo.Create(FourCc("h264"), 1280, 720, 100, 50, 1)!.IsBelowFullHd);
                True(VideoFormatInfo.Create(FourCc("mpgv"), 720, 576, 4, 25, 1)!.IsBelowFullHd);
            }),
            ("unknown size gives no format and a zero frame rate denominator gives 0 fps", () =>
            {
                Equal<VideoFormatInfo?>(null, VideoFormatInfo.Create(FourCc("h264"), 0, 0, 0, 0, 0));
                Equal(0.0, VideoFormatInfo.Create(FourCc("h264"), 1920, 1080, 0, 25, 0)!.FramesPerSecond);
                Equal("UNKNOWN", VideoFormatInfo.FourCcToText(0));
            }),
            ("repeated log lines are dropped inside the window and counted", () =>
            {
                var filter = new RepeatedLogFilter(windowMs: 1000);
                True(filter.ShouldLog("a", 0, out var first));
                Equal(0, first);
                True(!filter.ShouldLog("a", 100, out _));
                True(!filter.ShouldLog("a", 200, out _));
                True(filter.ShouldLog("b", 300, out var other));
                Equal(0, other);
                True(!filter.ShouldLog("a", 900, out _));
                True(filter.ShouldLog("a", 1100, out var later));
                Equal(3, later);
            }),
            // Step 34 checks go here.
```

**Important:** that block ends with the line `// Step 34 checks go here.`, so the marker stays in place for the next step. Do the same for every later step.

Add this helper at the end of the class, just before its final closing `}`:

```csharp
    private static uint FourCc(string text) =>
        (uint)text[0] | ((uint)text[1] << 8) | ((uint)text[2] << 16) | ((uint)text[3] << 24);
```

### 33.11 Build, check, publish

1. Build, run the checks, publish the lite build.
2. Ask the user to open one normal HD channel, one SD channel (if they have one) and the 4K channel, about 20 seconds each, then close the app.
3. In the newest session log, check for:
   - `Tuner: video format. codec=...; width=...; height=...` once per channel;
   - for the 4K channel: the values, and whether `; 10-bit HEVC` appears;
   - whether any `libvlc ...: Using ... for hardware decoding` line appears right after `Tuner: playing` for each channel.
4. Record the exact 4K values and the hardware-decoder result in the roadmap. Step 34 works either way; this is evidence only.

---

## Step 34 — 4K/UHD fallback and graphics-device recovery

**Goal:** when a channel above Full HD freezes, reopen it once with lighter decoding (and software decoding if a GPU decoder was in use). If it still freezes, tell the user once. Separately, when the GPU reports "device removed" (`0x887A0005`), reopen the channel (at most twice per tune) instead of leaving a dead picture.

This step moves the existing per-URL fallback memory (`_directPlaybackUrls`, `_unadjustedVideoUrls`) into a small testable class.

### 34.1 New file `src/ClassicWindowsIptvPlayer.Windows/StallFallbackPolicy.cs`

```csharp
using System;
using System.Collections.Generic;

namespace ClassicWindowsIptvPlayer.Windows;

internal enum StallFallback
{
    None,
    /// <summary>4K/UHD: lighter decoding, plus software decoding if a GPU decoder was in use.</summary>
    FastDecode,
    /// <summary>Reopen without the CPU "adjust" filter (brightness, contrast, ...).</summary>
    WithoutAdjustments,
    /// <summary>Reopen without the CPU picture enhancements (Step 36).</summary>
    WithoutEnhancements,
    /// <summary>Reopen without the local live pause buffer.</summary>
    Direct,
    /// <summary>Nothing left to try for a 4K channel: tell the user once.</summary>
    WarnUltraHd
}

internal readonly record struct StallDecision(StallFallback Action, bool AdjustmentsSuspended, bool EnhancementsSuspended);

/// <summary>
/// Remembers, per stream URL for this app session, which fallbacks a frozen picture has
/// already used, and picks the next one. Not thread-safe: the tuner calls it under its lock.
/// </summary>
internal sealed class StallFallbackPolicy
{
    private readonly HashSet<string> _fastDecode = new(StringComparer.Ordinal);
    private readonly HashSet<string> _softwareDecode = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unadjusted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unenhanced = new(StringComparer.Ordinal);
    private readonly HashSet<string> _direct = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedUltraHd = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lowResolution = new(StringComparer.Ordinal);

    public bool UsesFastDecode(string url) => _fastDecode.Contains(url);
    public bool UsesSoftwareDecode(string url) => _softwareDecode.Contains(url);
    public bool IsUnadjusted(string url) => _unadjusted.Contains(url);
    public bool IsUnenhanced(string url) => _unenhanced.Contains(url);
    public bool IsDirectOnly(string url) => _direct.Contains(url);

    public StallDecision Decide(string url, bool aboveFullHd, bool hardwareDecoderUsed,
        bool adjustmentsActive, bool enhancementsActive, bool remuxed)
    {
        // A 4K picture is usually more than a weak PC can decode: lighten decoding and
        // drop every CPU filter at the same time instead of one reopen per cause.
        if (aboveFullHd && _fastDecode.Add(url))
        {
            if (hardwareDecoderUsed) _softwareDecode.Add(url);
            var adjustments = adjustmentsActive && _unadjusted.Add(url);
            var enhancements = enhancementsActive && _unenhanced.Add(url);
            return new(StallFallback.FastDecode, adjustments, enhancements);
        }
        if (adjustmentsActive && _unadjusted.Add(url)) return new(StallFallback.WithoutAdjustments, true, false);
        if (enhancementsActive && _unenhanced.Add(url)) return new(StallFallback.WithoutEnhancements, false, true);
        if (remuxed && _direct.Add(url)) return new(StallFallback.Direct, false, false);
        if (aboveFullHd && _warnedUltraHd.Add(url)) return new(StallFallback.WarnUltraHd, false, false);
        return new(StallFallback.None, false, false);
    }

    /// <summary>Step 36/37: whether this attempt should use the LibVLC engine with CPU picture enhancements.</summary>
    public bool ShouldUseEnhancements(string url, bool enhancementsAvailable, bool onlyBelowFullHd) =>
        enhancementsAvailable && !_unenhanced.Contains(url) && (!onlyBelowFullHd || _lowResolution.Contains(url));

    /// <summary>Step 37: true once per URL when a channel below 1080p was opened without enhancements.</summary>
    public bool ShouldReopenWithEnhancements(string url, VideoFormatInfo format, bool enhancementsAvailable,
        bool onlyBelowFullHd, bool attemptUsedEnhancements, bool recording) =>
        enhancementsAvailable && onlyBelowFullHd && !attemptUsedEnhancements && !recording &&
        format.IsBelowFullHd && !_unenhanced.Contains(url) && _lowResolution.Add(url);
}
```

### 34.2 `ChannelTuner.cs` — replace the two URL sets

Find:

```csharp
    // Live URLs whose re-muxed recording froze the picture; they play directly for this session.
    private readonly HashSet<string> _directPlaybackUrls = new(StringComparer.Ordinal);
    // Live URLs whose picture froze under the CPU video adjustment filter; it stays off for this session.
    private readonly HashSet<string> _unadjustedVideoUrls = new(StringComparer.Ordinal);
```

Replace with:

```csharp
    // Which frozen-picture fallbacks each URL already used this session. Guarded by _gate.
    private readonly StallFallbackPolicy _stallPolicy = new();
    // Tune generation in which LibVLC reported DXGI_ERROR_DEVICE_REMOVED.
    private int _videoDeviceLostGeneration = -1;
```

### 34.3 `ChannelTuner.cs` — new public members

Find:

```csharp
    public event Action<TuneRequest>? VideoAdjustmentsSuspended;
```

Replace with:

```csharp
    public event Action<TuneRequest>? VideoAdjustmentsSuspended;

    /// <summary>Raised on arbitrary threads when a 4K/UHD channel still freezes after every fallback.</summary>
    public event Action<TuneRequest>? UltraHdTooDemanding;
```

Find:

```csharp
    public void NotifyHardwareDecoderInUse() => Volatile.Write(ref _hardwareDecoderGeneration, Volatile.Read(ref _generation));
```

Replace with:

```csharp
    public void NotifyHardwareDecoderInUse() => Volatile.Write(ref _hardwareDecoderGeneration, Volatile.Read(ref _generation));

    /// <summary>Called by the host from LibVLC's log callback on DXGI_ERROR_DEVICE_REMOVED (0x887A0005).</summary>
    public void NotifyVideoDeviceLost() => Volatile.Write(ref _videoDeviceLostGeneration, Volatile.Read(ref _generation));
```

### 34.4 `ChannelTuner.cs` — read the policy when an attempt starts

Find:

```csharp
            bool directOnly, unadjusted;
            lock (_gate)
            {
                recordingOutputPath = _recordingOutputPath;
                directOnly = _directPlaybackUrls.Contains(request.Url);
                unadjusted = _unadjustedVideoUrls.Contains(request.Url);
            }
```

Replace with:

```csharp
            bool directOnly, unadjusted, fastDecode, softwareDecode;
            lock (_gate)
            {
                recordingOutputPath = _recordingOutputPath;
                directOnly = _stallPolicy.IsDirectOnly(request.Url);
                unadjusted = _stallPolicy.IsUnadjusted(request.Url);
                fastDecode = _stallPolicy.UsesFastDecode(request.Url);
                softwareDecode = _stallPolicy.UsesSoftwareDecode(request.Url);
            }
```

### 34.5 `ChannelTuner.cs` — lighter decoding options

Find (the first occurrence, in the attempt loop, right after `media.AddOption(":file-caching=" + request.BufferMs);`):

```csharp
                media.AddOption(":http-reconnect");
                if (recordingOutputPath is not null)
```

Replace with:

```csharp
                media.AddOption(":http-reconnect");
                if (fastDecode)
                {
                    // Skips the in-loop deblocking filter on every frame: much less CPU, slightly blockier picture.
                    media.AddOption(":avcodec-skiploopfilter=4");
                    media.AddOption(":avcodec-fast");
                }
                if (softwareDecode) media.AddOption(":avcodec-hw=none");
                if (fastDecode || softwareDecode)
                    AppLogger.Info("Tuner: lighter 4K decoding for this channel. fastDecode=" + fastDecode +
                        "; softwareDecode=" + softwareDecode + "; channel=" + request.ChannelName);
                if (recordingOutputPath is not null)
```

### 34.6 `ChannelTuner.cs` — counter for device-lost reopens

Find:

```csharp
        var vodPosition = new VodRecoveryPosition();
        while (retryBudget.CanAttempt)
```

Replace with:

```csharp
        var vodPosition = new VodRecoveryPosition();
        var deviceLostReopens = 0;
        while (retryBudget.CanAttempt)
```

### 34.7 `ChannelTuner.cs` — graphics-device check in `MonitorAsync`

Find:

```csharp
                // Step 34 inserts the graphics-device check here.
```

Replace with:

```csharp
                if (Volatile.Read(ref _videoDeviceLostGeneration) == generation && Environment.TickCount64 - monitorStarted >= 1000)
                    outcome.TrySetResult(StreamEnd.VideoDeviceLost);
```

Then find:

```csharp
        // The format callback asked to reopen the channel (Step 37 uses this).
        ReopenRequested
    }
```

Replace with:

```csharp
        // The format callback asked to reopen the channel (Step 37 uses this).
        ReopenRequested,
        // The GPU reported DXGI_ERROR_DEVICE_REMOVED; this video output cannot recover.
        VideoDeviceLost
    }
```

### 34.8 `ChannelTuner.cs` — handle a lost graphics device

Find:

```csharp
                // Step 34 inserts the graphics-device handling here.
```

Replace with:

```csharp
                if (end == StreamEnd.VideoDeviceLost)
                {
                    Interlocked.CompareExchange(ref _videoDeviceLostGeneration, -1, generation);
                    if (++deviceLostReopens <= 2)
                    {
                        AppLogger.Warn("Tuner: the graphics device was reset (DXGI device removed); reopening the channel. generation=" +
                            generation + "; channel=" + request.ChannelName);
                        await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
                        retryBudget.RefundAttempt();
                        continue;
                    }
                    AppLogger.Warn("Tuner: the graphics device keeps resetting; leaving playback running. channel=" + request.ChannelName);
                    end = await MonitorAsync(player, media, generation, cancellationToken, request.IsLive ? null : vodPosition).ConfigureAwait(false);
                    if (end == StreamEnd.VideoDeviceLost) end = StreamEnd.Error;
                }
```

### 34.9 `ChannelTuner.cs` — the new frozen-picture decision

Find this whole block (from `string? stallFallback = null;` to the `continue;` that ends `if (stallFallback is not null)`):

```csharp
                string? stallFallback = null;
                var adjustmentsSuspended = false;
                if (end == StreamEnd.VideoStalled)
                {
                    lock (_gate)
                    {
                        // LibVLC's "adjust" filter runs on the CPU and can make every frame of a
                        // demanding stream (1080p50) miss its display time. Rule it out first; a
                        // fresh player without it plays at full rate, unlike disabling it in place.
                        if (VideoAdjustmentsActive && _unadjustedVideoUrls.Add(request.Url))
                        {
                            adjustmentsSuspended = true;
                            stallFallback = "reopening it without video adjustments";
                        }
                        else if (fileSource is not null && _directPlaybackUrls.Add(request.Url))
                            stallFallback = "reopening it directly";
                    }
                    AppLogger.Warn("Tuner: picture froze while audio continued; " + (stallFallback ?? "no further fallback, leaving playback running") +
                        ". generation=" + generation + "; channel=" + request.ChannelName);
                    if (stallFallback is null) end = await MonitorAsync(player, media, generation, cancellationToken, null).ConfigureAwait(false);
                }
                if (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
                {
                    await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
                    return;
                }

                if (stallFallback is not null)
                {
                    await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
                    if (adjustmentsSuspended)
                    {
                        try { VideoAdjustmentsSuspended?.Invoke(request); }
                        catch (Exception ex) { AppLogger.Error("Tuner: VideoAdjustmentsSuspended handler failed.", ex); }
                    }
                    retryBudget.RefundAttempt();
                    continue;
                }
```

Replace it with:

```csharp
                string? stallFallback = null;
                var stallDecision = new StallDecision(StallFallback.None, false, false);
                if (end == StreamEnd.VideoStalled)
                {
                    VideoFormatInfo? format;
                    bool softwareDecodeNext;
                    lock (_gate)
                    {
                        // CPU filters (LibVLC's "adjust", picture enhancements) can make every frame
                        // of a demanding stream miss its display time; a fresh player without them
                        // plays at full rate, unlike disabling them in place.
                        format = _activeVideoFormat;
                        stallDecision = _stallPolicy.Decide(request.Url,
                            aboveFullHd: format is { IsAboveFullHd: true },
                            hardwareDecoderUsed: Volatile.Read(ref _hardwareDecoderGeneration) == generation,
                            adjustmentsActive: VideoAdjustmentsActive,
                            enhancementsActive: false, // Step 36 replaces false with useEnhancements.
                            remuxed: fileSource is not null);
                        softwareDecodeNext = _stallPolicy.UsesSoftwareDecode(request.Url);
                    }
                    stallFallback = stallDecision.Action switch
                    {
                        StallFallback.FastDecode => softwareDecodeNext
                            ? "reopening it with fast software decoding for 4K"
                            : "reopening it with fast decoding for 4K",
                        StallFallback.WithoutAdjustments => "reopening it without video adjustments",
                        StallFallback.WithoutEnhancements => "reopening it without picture enhancements",
                        StallFallback.Direct => "reopening it directly",
                        _ => null
                    };
                    AppLogger.Warn("Tuner: picture froze while audio continued; " + (stallFallback ?? "no further fallback, leaving playback running") +
                        ". generation=" + generation + "; channel=" + request.ChannelName +
                        (format is null ? string.Empty : "; " + format.Describe()));
                    if (stallDecision.Action == StallFallback.WarnUltraHd)
                    {
                        try { UltraHdTooDemanding?.Invoke(request); }
                        catch (Exception ex) { AppLogger.Error("Tuner: UltraHdTooDemanding handler failed.", ex); }
                    }
                    if (stallFallback is null) end = await MonitorAsync(player, media, generation, cancellationToken, null).ConfigureAwait(false);
                }
                if (!IsCurrent(generation) || cancellationToken.IsCancellationRequested)
                {
                    await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
                    return;
                }

                if (stallFallback is not null)
                {
                    await ReleaseAttemptAsync(player, media, fileSource).ConfigureAwait(false);
                    if (stallDecision.AdjustmentsSuspended)
                    {
                        try { VideoAdjustmentsSuspended?.Invoke(request); }
                        catch (Exception ex) { AppLogger.Error("Tuner: VideoAdjustmentsSuspended handler failed.", ex); }
                    }
                    // Step 36 inserts the picture-enhancement notification here.
                    retryBudget.RefundAttempt();
                    continue;
                }
```

After this edit, search `ChannelTuner.cs` for `_directPlaybackUrls` and `_unadjustedVideoUrls`. There must be no matches.

### 34.10 `MainWindow.xaml.cs` — graphics-device check in the log handler

Find:

```csharp
        // Step 34 inserts the graphics-device check here.
```

Replace with:

```csharp
        // DXGI_ERROR_DEVICE_REMOVED: the GPU was reset and this video output is dead.
        if (message.Contains("887A0005", StringComparison.OrdinalIgnoreCase))
            _tuner?.NotifyVideoDeviceLost();
```

There is no `return` here. The message is still written to the log below.

### 34.11 `MainWindow.xaml.cs` — tell the user about a too-demanding 4K channel

Find:

```csharp
            StatusText.Text = "Video adjustments turned off for this channel: the picture could not keep up with them. Reset them in Settings > Video adjustments.";
        }));
```

Replace with:

```csharp
            StatusText.Text = "Video adjustments turned off for this channel: the picture could not keep up with them. Reset them in Settings > Video adjustments.";
        }));
        _tuner.UltraHdTooDemanding += request => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isShuttingDown || !_playbackState.Accepts(request)) return;
            StatusText.Text = "This 4K/UHD channel is too demanding for this PC: the picture cannot keep up. Try the channel's HD (1080p) version.";
        }));
```

### 34.12 Tests

In the csproj, find:

```xml
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/RepeatedLogFilter.cs" Link="RepeatedLogFilter.cs" />
```

Replace with:

```xml
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/RepeatedLogFilter.cs" Link="RepeatedLogFilter.cs" />
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/StallFallbackPolicy.cs" Link="StallFallbackPolicy.cs" />
```

In `PlaybackQualityRegressionChecks.cs`, replace `            // Step 34 checks go here.` with:

```csharp
            // Step 34
            ("frozen 4K with a GPU decoder: fast software decoding first, then warn once", () =>
            {
                var policy = new StallFallbackPolicy();
                var first = policy.Decide("u", aboveFullHd: true, hardwareDecoderUsed: true, adjustmentsActive: false, enhancementsActive: false, remuxed: false);
                Equal(StallFallback.FastDecode, first.Action);
                True(policy.UsesFastDecode("u") && policy.UsesSoftwareDecode("u"));
                Equal(StallFallback.WarnUltraHd, policy.Decide("u", true, true, false, false, false).Action);
                Equal(StallFallback.None, policy.Decide("u", true, true, false, false, false).Action);
            }),
            ("frozen 4K with software decoding keeps software decoding and drops CPU filters at once", () =>
            {
                var policy = new StallFallbackPolicy();
                var first = policy.Decide("u", aboveFullHd: true, hardwareDecoderUsed: false, adjustmentsActive: true, enhancementsActive: true, remuxed: true);
                Equal(StallFallback.FastDecode, first.Action);
                True(first.AdjustmentsSuspended && first.EnhancementsSuspended);
                True(!policy.UsesSoftwareDecode("u"));
                True(policy.IsUnadjusted("u") && policy.IsUnenhanced("u"));
                Equal(StallFallback.Direct, policy.Decide("u", true, false, true, true, true).Action);
                Equal(StallFallback.WarnUltraHd, policy.Decide("u", true, false, true, true, true).Action);
            }),
            ("frozen 1080p keeps the old order: adjustments, enhancements, direct, then nothing", () =>
            {
                var policy = new StallFallbackPolicy();
                Equal(StallFallback.WithoutAdjustments, policy.Decide("u", false, false, true, true, true).Action);
                Equal(StallFallback.WithoutEnhancements, policy.Decide("u", false, false, true, true, true).Action);
                Equal(StallFallback.Direct, policy.Decide("u", false, false, true, true, true).Action);
                Equal(StallFallback.None, policy.Decide("u", false, false, true, true, true).Action);
                True(policy.IsDirectOnly("u"));
            }),
            ("fallbacks are remembered per URL", () =>
            {
                var policy = new StallFallbackPolicy();
                policy.Decide("a", false, false, true, false, false);
                True(policy.IsUnadjusted("a") && !policy.IsUnadjusted("b"));
            }),
            ("enhancement engine rules for all channels and below-1080p only", () =>
            {
                var policy = new StallFallbackPolicy();
                var sd = VideoFormatInfo.Create(FourCc("h264"), 720, 576, 0, 25, 1)!;
                var hd = VideoFormatInfo.Create(FourCc("h264"), 1920, 1080, 0, 25, 1)!;
                True(policy.ShouldUseEnhancements("sd", enhancementsAvailable: true, onlyBelowFullHd: false));
                True(!policy.ShouldUseEnhancements("sd", enhancementsAvailable: false, onlyBelowFullHd: false));
                True(!policy.ShouldUseEnhancements("sd", true, onlyBelowFullHd: true));
                True(!policy.ShouldReopenWithEnhancements("hd", hd, true, true, attemptUsedEnhancements: false, recording: false));
                True(!policy.ShouldReopenWithEnhancements("sd", sd, true, true, attemptUsedEnhancements: false, recording: true));
                True(policy.ShouldReopenWithEnhancements("sd", sd, true, true, attemptUsedEnhancements: false, recording: false));
                True(!policy.ShouldReopenWithEnhancements("sd", sd, true, true, attemptUsedEnhancements: false, recording: false));
                True(policy.ShouldUseEnhancements("sd", true, onlyBelowFullHd: true));
                policy.Decide("sd", false, false, false, enhancementsActive: true, remuxed: false);
                True(!policy.ShouldUseEnhancements("sd", true, onlyBelowFullHd: true));
            }),
            // Step 35 checks go here.
```

### 34.13 Build, check, publish

1. Build, run the checks, publish the lite build.
2. Ask the user to open the 4K channel and wait about 30 seconds without touching anything, then switch to an HD channel.
3. In the session log, check this order for the 4K channel:
   - `Tuner: video format. codec=...; height=2160...`;
   - `picture froze while audio continued; reopening it with fast ... decoding for 4K ... height=2160`;
   - `Tuner: lighter 4K decoding for this channel. fastDecode=True; softwareDecode=...`;
   - then either normal playback (no further freeze line) **or** a second freeze line, followed by the status bar message "This 4K/UHD channel is too demanding for this PC...". Ask the user which they saw.
4. Ask the user whether the 4K picture moved after the reopen and roughly how smooth it was. Record the result. Do not tune the options further in this step.

---

## Step 35 — Picture menu, settings, and the GPU-side options

**Goal:** a new top-level **Picture** menu. This step adds the options that run on the GPU or cost little: deinterlacing, GPU upscaling (including AI Super Resolution for NVIDIA RTX / Intel Arc), and SDR-to-HDR (NVIDIA RTX Video HDR). All settings are saved. They apply at the next app start, like "Hardware video decoding (GPU)". The app detects when the GPU does not support Super Resolution or RTX HDR and says so once.

### 35.1 `src/ClassicWindowsIptvPlayer.Core/Models.cs` — settings

Find:

```csharp
    // GPU (D3D11/DXVA) video decoding. Applied when the player engine starts.
    public bool HardwareDecodingEnabled { get; set; } = true;
```

Replace with:

```csharp
    // GPU (D3D11/DXVA) video decoding. Applied when the player engine starts.
    public bool HardwareDecodingEnabled { get; set; } = true;

    // Picture quality options (Picture menu). Applied when the player engine starts.
    // Values outside the listed sets are treated as the default.
    public string DeinterlaceMode { get; set; } = "default";   // default | off | yadif | yadif2x | bob
    public string UpscaleMode { get; set; } = "linear";        // linear | processor | super
    public bool RtxHdrEnabled { get; set; }
    public int DeblockQuality { get; set; }                    // 0 off, 2 light, 4 medium, 6 strong
    public int DenoiseLevel { get; set; }                      // 0 off, 1 light, 2 medium, 3 strong
    public bool DebandEnabled { get; set; }
    public int SharpenLevel { get; set; }                      // 0 off, 1 light, 2 medium, 3 strong
    public bool EnhanceOnlyBelowFullHd { get; set; } = true;   // CPU enhancements only below 1080p (Step 37)
```

### 35.2 New file `src/ClassicWindowsIptvPlayer.Windows/PictureEnhancementOptions.cs`

This file contains the CPU filter part as well; Step 36 starts using it.

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClassicWindowsIptvPlayer.Windows;

/// <summary>The saved picture-quality choices, validated by <see cref="PictureEnhancementOptions.Normalize"/>.</summary>
internal sealed record PictureEnhancementSettings(
    string DeinterlaceMode, string UpscaleMode, bool RtxHdr,
    int DeblockQuality, int DenoiseLevel, bool Deband, int SharpenLevel, bool OnlyBelowFullHd)
{
    public bool AnyCpuFilter => DeblockQuality > 0 || DenoiseLevel > 0 || Deband || SharpenLevel > 0;
}

/// <summary>Turns picture-quality settings into LibVLC start-up arguments. Pure; covered by regression checks.</summary>
internal static class PictureEnhancementOptions
{
    public static readonly string[] DeinterlaceModes = { "default", "off", "yadif", "yadif2x", "bob" };
    public static readonly string[] UpscaleModes = { "linear", "processor", "super" };
    public static readonly int[] DeblockQualities = { 0, 2, 4, 6 };
    /// <summary>LibVLC module names of the CPU filters, in the order they run.</summary>
    public static readonly string[] CpuFilterModules = { "postproc", "hqdn3d", "gradfun", "sharpen" };

    public static PictureEnhancementSettings Normalize(string? deinterlaceMode, string? upscaleMode, bool rtxHdr,
        int deblockQuality, int denoiseLevel, bool deband, int sharpenLevel, bool onlyBelowFullHd) => new(
        deinterlaceMode is not null && DeinterlaceModes.Contains(deinterlaceMode) ? deinterlaceMode : "default",
        upscaleMode is not null && UpscaleModes.Contains(upscaleMode) ? upscaleMode : "linear",
        rtxHdr,
        DeblockQualities.Contains(deblockQuality) ? deblockQuality : 0,
        denoiseLevel is >= 0 and <= 3 ? denoiseLevel : 0,
        deband,
        sharpenLevel is >= 0 and <= 3 ? sharpenLevel : 0,
        onlyBelowFullHd);

    /// <summary>Deinterlacing and Direct3D 11 output options. Cheap; applied to every channel.</summary>
    public static List<string> BuildEngineArgs(PictureEnhancementSettings settings)
    {
        var args = new List<string>();
        switch (settings.DeinterlaceMode)
        {
            case "off":
                args.Add("--deinterlace=0");
                break;
            case "yadif":
            case "yadif2x":
            case "bob":
                args.Add("--deinterlace=-1"); // only pictures flagged as interlaced
                args.Add("--deinterlace-mode=" + settings.DeinterlaceMode);
                break;
        }
        if (settings.UpscaleMode is "processor" or "super") args.Add("--d3d11-upscale-mode=" + settings.UpscaleMode);
        if (settings.RtxHdr) args.Add("--d3d11-hdr-mode=generate");
        return args;
    }

    /// <summary>CPU filter options, limited to the filters this LibVLC has. Empty when none apply.</summary>
    public static List<string> BuildCpuFilterArgs(PictureEnhancementSettings settings, IReadOnlySet<string> availableFilters)
    {
        var chain = new List<string>();
        var args = new List<string>();
        if (settings.DeblockQuality > 0 && availableFilters.Contains("postproc"))
        {
            chain.Add("postproc");
            args.Add("--postproc-q=" + settings.DeblockQuality.ToString(CultureInfo.InvariantCulture));
        }
        if (settings.DenoiseLevel > 0 && availableFilters.Contains("hqdn3d"))
        {
            // Level 2 is LibVLC's default strength; level 1 is half of it and level 3 is 1.5 times.
            var scale = settings.DenoiseLevel switch { 1 => 0.5, 2 => 1.0, _ => 1.5 };
            chain.Add("hqdn3d");
            args.Add("--hqdn3d-luma-spat=" + Format(4.0 * scale));
            args.Add("--hqdn3d-chroma-spat=" + Format(3.0 * scale));
            args.Add("--hqdn3d-luma-temp=" + Format(6.0 * scale));
            args.Add("--hqdn3d-chroma-temp=" + Format(4.5 * scale));
        }
        if (settings.Deband && availableFilters.Contains("gradfun"))
        {
            chain.Add("gradfun");
            args.Add("--gradfun-strength=1.2");
            args.Add("--gradfun-radius=16");
        }
        if (settings.SharpenLevel > 0 && availableFilters.Contains("sharpen"))
        {
            chain.Add("sharpen");
            args.Add("--sharpen-sigma=" + Format(settings.SharpenLevel switch { 1 => 0.3, 2 => 0.6, _ => 1.0 }));
        }
        if (chain.Count == 0) return new List<string>();
        args.Insert(0, "--video-filter=" + string.Join(":", chain));
        return args;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
```

### 35.3 New file `src/ClassicWindowsIptvPlayer.Windows/MainWindow.PictureQuality.cs`

```csharp
using ClassicWindowsIptvPlayer.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ClassicWindowsIptvPlayer.Windows;

public partial class MainWindow
{
    private const string PictureRestartMessage = "Picture setting saved. Restart the app to apply it.";
    // Names of the LibVLC video filters this runtime has (filled when the player engine starts).
    private HashSet<string> _availableVideoFilters = new(StringComparer.OrdinalIgnoreCase);
    // The full LibVLC start-up arguments, reused by the enhancement engine (Step 36).
    private string[] _libVlcArgs = Array.Empty<string>();
    private int _superResolutionUnsupportedReported;
    private int _rtxHdrUnsupportedReported;

    private PictureEnhancementSettings GetPictureSettings() => PictureEnhancementOptions.Normalize(
        _state.DeinterlaceMode, _state.UpscaleMode, _state.RtxHdrEnabled, _state.DeblockQuality,
        _state.DenoiseLevel, _state.DebandEnabled, _state.SharpenLevel, _state.EnhanceOnlyBelowFullHd);

    private void PictureMenu_Opened(object sender, RoutedEventArgs e) => UpdatePictureCheckmarks();

    private void UpdatePictureCheckmarks()
    {
        var settings = GetPictureSettings();
        CheckOnly(DeinterlaceMenu, "deinterlace:" + settings.DeinterlaceMode);
        CheckOnly(UpscalingMenu, "upscale:" + settings.UpscaleMode);
        RtxHdrMenuItem.IsChecked = settings.RtxHdr;
        // Step 36 adds the CPU filter menus here.
        // Step 37 adds the below-1080p option here.
    }

    private static void CheckOnly(MenuItem parent, string selectedTag)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            item.IsCheckable = true;
            item.IsChecked = string.Equals(item.Tag as string, selectedTag, StringComparison.Ordinal);
        }
    }

    // One handler for every Picture menu item. Tag format: "group:value" for choices,
    // a single word for on/off items.
    private void PictureOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        var parts = tag.Split(':', 2);
        var value = parts.Length == 2 ? parts[1] : string.Empty;
        var restartNeeded = true;
        switch (parts[0])
        {
            case "deinterlace": _state.DeinterlaceMode = value; break;
            case "upscale": _state.UpscaleMode = value; break;
            case "rtxhdr": _state.RtxHdrEnabled = !_state.RtxHdrEnabled; break;
            case "deblock": _state.DeblockQuality = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "denoise": _state.DenoiseLevel = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "deband": _state.DebandEnabled = !_state.DebandEnabled; break;
            case "sharpen": _state.SharpenLevel = int.Parse(value, CultureInfo.InvariantCulture); break;
            // Step 37 adds the "belowhd" case here.
            case "reset":
                _state.DeinterlaceMode = "default";
                _state.UpscaleMode = "linear";
                _state.RtxHdrEnabled = false;
                _state.DeblockQuality = 0;
                _state.DenoiseLevel = 0;
                _state.DebandEnabled = false;
                _state.SharpenLevel = 0;
                break;
            default:
                return;
        }
        _store.Save(_state);
        UpdatePictureCheckmarks();
        if (restartNeeded) StatusText.Text = PictureRestartMessage;
    }

    // Called from OnLibVlcLog on LibVLC threads.
    private void CheckPictureFeatureLog(string module, string message)
    {
        if (message.StartsWith("turning VSR", StringComparison.OrdinalIgnoreCase) &&
            _libVlcLogFilter.ShouldLog(module + ": " + message, Environment.TickCount64, out _))
            AppLogger.Info("libvlc " + module + ": " + message);

        if (message.Contains("Super Resolution", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("not supported", StringComparison.OrdinalIgnoreCase))
            ReportUnsupportedPictureFeature(ref _superResolutionUnsupportedReported,
                "AI Super Resolution is not supported by this graphics card or driver; standard upscaling is used.", message);

        if (message.Contains("True HDR", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
             message.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)))
            ReportUnsupportedPictureFeature(ref _rtxHdrUnsupportedReported,
                "SDR to HDR is not available on this PC: it needs an NVIDIA RTX graphics card with RTX Video HDR turned on and an HDR screen.", message);
    }

    private void ReportUnsupportedPictureFeature(ref int reported, string userMessage, string libVlcMessage)
    {
        if (Interlocked.Exchange(ref reported, 1) != 0) return;
        AppLogger.Warn("Picture feature unavailable: " + libVlcMessage);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isShuttingDown) StatusText.Text = userMessage;
        }));
    }
}
```

`restartNeeded` is always true in this step. Step 37 sets it to false for one option. Leave it as written.

### 35.4 `MainWindow.xaml` — the Picture menu

Find:

```xml
            <MenuItem Header="Settings" SubmenuOpened="SettingsMenu_Opened">
```

Replace with:

```xml
            <MenuItem x:Name="PictureMenu" Header="Picture" SubmenuOpened="PictureMenu_Opened">
                <MenuItem Header="Video adjustments..." Click="VideoAdjustments_Click" />
                <Separator />
                <MenuItem x:Name="DeinterlaceMenu" Header="Deinterlacing (old TV channels)">
                    <MenuItem Header="Automatic (VLC default)" Tag="deinterlace:default" Click="PictureOption_Click" />
                    <MenuItem Header="Yadif: best quality" Tag="deinterlace:yadif" Click="PictureOption_Click" />
                    <MenuItem Header="Yadif 2x: smoothest motion (sports), more CPU" Tag="deinterlace:yadif2x" Click="PictureOption_Click" />
                    <MenuItem Header="Bob: lowest CPU" Tag="deinterlace:bob" Click="PictureOption_Click" />
                    <MenuItem Header="Off" Tag="deinterlace:off" Click="PictureOption_Click" />
                </MenuItem>
                <MenuItem x:Name="UpscalingMenu" Header="Upscaling (GPU)">
                    <MenuItem Header="Standard (default)" Tag="upscale:linear" Click="PictureOption_Click" />
                    <MenuItem Header="GPU video processor (sharper on most graphics cards)" Tag="upscale:processor" Click="PictureOption_Click" />
                    <MenuItem Header="AI Super Resolution (NVIDIA RTX 20 series or newer, Intel Arc)" Tag="upscale:super" Click="PictureOption_Click" />
                </MenuItem>
                <MenuItem x:Name="RtxHdrMenuItem" Header="SDR to HDR (NVIDIA RTX Video HDR, needs an HDR screen)" IsCheckable="True" Tag="rtxhdr" Click="PictureOption_Click" />
                <!-- Step 36 adds the CPU enhancement menus here. -->
                <Separator />
                <MenuItem Header="Turn all picture enhancements off" Tag="reset" Click="PictureOption_Click" />
            </MenuItem>
            <MenuItem Header="Settings" SubmenuOpened="SettingsMenu_Opened">
```

Keep the existing "Video adjustments..." item in Settings as well. Do not remove anything.

### 35.5 `MainWindow.xaml.cs` — apply the options when LibVLC starts

Find:

```csharp
        var buffer = GetPlaybackBufferMs();
        var initializedVlc = await Task.Run(() =>
        {
            VlcRuntime.Initialize();
            return new LibVLC(
                "--ignore-config",
                "--input-timeshift-path=" + AppDataPaths.Temp,
                "--input-record-path=" + GetDefaultRecordingFolder(),
                "--no-video-title-show",
                "--embedded-video",
                "--freetype-background-color=0x000000",
                "--freetype-background-opacity=" + (_state.SubtitleBackgroundEnabled ? "128" : "0"),
                // "any" lets LibVLC pick D3D11VA/DXVA2 and fall back to software per stream.
                "--avcodec-hw=" + (_state.HardwareDecodingEnabled ? "any" : "none"),
                "--network-caching=" + buffer,
                "--live-caching=" + buffer,
                "--file-caching=" + buffer,
                "--http-reconnect");
        });
```

Replace with:

```csharp
        var buffer = GetPlaybackBufferMs();
        var pictureArgs = PictureEnhancementOptions.BuildEngineArgs(GetPictureSettings());
        var engineArgs = new List<string>
        {
            "--ignore-config",
            "--input-timeshift-path=" + AppDataPaths.Temp,
            "--input-record-path=" + GetDefaultRecordingFolder(),
            "--no-video-title-show",
            "--embedded-video",
            "--freetype-background-color=0x000000",
            "--freetype-background-opacity=" + (_state.SubtitleBackgroundEnabled ? "128" : "0"),
            // "any" lets LibVLC pick D3D11VA/DXVA2 and fall back to software per stream.
            "--avcodec-hw=" + (_state.HardwareDecodingEnabled ? "any" : "none"),
            "--network-caching=" + buffer,
            "--live-caching=" + buffer,
            "--file-caching=" + buffer,
            "--http-reconnect"
        };
        engineArgs.AddRange(pictureArgs);
        _libVlcArgs = engineArgs.ToArray();
        AppLogger.Info("Picture engine options: " + (pictureArgs.Count == 0 ? "none" : string.Join(" ", pictureArgs)));
        var initializedVlc = await Task.Run(() =>
        {
            VlcRuntime.Initialize();
            return new LibVLC(_libVlcArgs);
        });
```

Do not log `engineArgs`: it contains local folder paths. Only `pictureArgs` is logged.

If the compiler says `List<>` is unknown in `MainWindow.xaml.cs`, add `using System.Collections.Generic;` at the top of that file.

### 35.6 `MainWindow.xaml.cs` — record which filters exist

Find:

```csharp
        _libVlc = initializedVlc;
```

Replace with:

```csharp
        _libVlc = initializedVlc;
        _availableVideoFilters = new HashSet<string>(
            _libVlc.VideoFilters.Select(filter => filter.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        AppLogger.Info("Picture filters available: " +
            string.Join(",", PictureEnhancementOptions.CpuFilterModules.Where(_availableVideoFilters.Contains)));
```

If `Select`/`Where` are unknown, add `using System.Linq;` at the top of `MainWindow.xaml.cs`.

### 35.7 `MainWindow.xaml.cs` — picture-feature checks in the log handler

Find:

```csharp
        // Step 35 inserts the picture-feature checks here.
```

Replace with:

```csharp
        CheckPictureFeatureLog(module, message);
```

### 35.8 Tests

In the csproj, find:

```xml
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/StallFallbackPolicy.cs" Link="StallFallbackPolicy.cs" />
```

Replace with:

```xml
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/StallFallbackPolicy.cs" Link="StallFallbackPolicy.cs" />
    <Compile Include="../../src/ClassicWindowsIptvPlayer.Windows/PictureEnhancementOptions.cs" Link="PictureEnhancementOptions.cs" />
```

In `PlaybackQualityRegressionChecks.cs`, replace `            // Step 35 checks go here.` with:

```csharp
            // Step 35
            ("default picture settings add no LibVLC options", () =>
            {
                var settings = Picture();
                Equal(0, PictureEnhancementOptions.BuildEngineArgs(settings).Count);
                Equal(0, PictureEnhancementOptions.BuildCpuFilterArgs(settings, AllFilters()).Count);
                True(!settings.AnyCpuFilter);
            }),
            ("unknown saved values fall back to defaults", () =>
            {
                var settings = PictureEnhancementOptions.Normalize("weird", null, false, 3, 9, false, -1, true);
                Equal("default", settings.DeinterlaceMode);
                Equal("linear", settings.UpscaleMode);
                Equal(0, settings.DeblockQuality);
                Equal(0, settings.DenoiseLevel);
                Equal(0, settings.SharpenLevel);
            }),
            ("deinterlace, upscaling and RTX HDR options", () =>
            {
                Equal("--deinterlace=0", string.Join(" ", PictureEnhancementOptions.BuildEngineArgs(Picture(deinterlace: "off"))));
                Equal("--deinterlace=-1 --deinterlace-mode=yadif2x", string.Join(" ", PictureEnhancementOptions.BuildEngineArgs(Picture(deinterlace: "yadif2x"))));
                Equal("--d3d11-upscale-mode=super", string.Join(" ", PictureEnhancementOptions.BuildEngineArgs(Picture(upscale: "super"))));
                Equal("--d3d11-upscale-mode=processor --d3d11-hdr-mode=generate",
                    string.Join(" ", PictureEnhancementOptions.BuildEngineArgs(Picture(upscale: "processor", rtxHdr: true))));
            }),
            ("CPU filters run in a fixed order with invariant numbers", () =>
            {
                var previous = System.Globalization.CultureInfo.CurrentCulture;
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                try
                {
                    var args = PictureEnhancementOptions.BuildCpuFilterArgs(Picture(deblock: 4, denoise: 1, deband: true, sharpen: 2), AllFilters());
                    Equal("--video-filter=postproc:hqdn3d:gradfun:sharpen --postproc-q=4 --hqdn3d-luma-spat=2 --hqdn3d-chroma-spat=1.5 " +
                          "--hqdn3d-luma-temp=3 --hqdn3d-chroma-temp=2.25 --gradfun-strength=1.2 --gradfun-radius=16 --sharpen-sigma=0.6",
                        string.Join(" ", args));
                }
                finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
            }),
            ("missing CPU filters are skipped", () =>
            {
                var withoutPostproc = new HashSet<string>(new[] { "sharpen" }, StringComparer.OrdinalIgnoreCase);
                Equal("--video-filter=sharpen --sharpen-sigma=1",
                    string.Join(" ", PictureEnhancementOptions.BuildCpuFilterArgs(Picture(deblock: 6, sharpen: 3), withoutPostproc)));
                Equal(0, PictureEnhancementOptions.BuildCpuFilterArgs(Picture(deblock: 6), withoutPostproc).Count);
            }),
            // Step 36 checks go here.
```

Add these helpers at the end of the class, just before its final closing `}`:

```csharp
    private static PictureEnhancementSettings Picture(string deinterlace = "default", string upscale = "linear", bool rtxHdr = false,
        int deblock = 0, int denoise = 0, bool deband = false, int sharpen = 0, bool onlyBelowFullHd = true) =>
        PictureEnhancementOptions.Normalize(deinterlace, upscale, rtxHdr, deblock, denoise, deband, sharpen, onlyBelowFullHd);

    private static HashSet<string> AllFilters() =>
        new(PictureEnhancementOptions.CpuFilterModules, StringComparer.OrdinalIgnoreCase);
```

### 35.9 Build, check, publish

1. Build, run the checks, publish the lite build.
2. Ask the user to:
   - open the **Picture** menu and check that each choice shows a checkmark and that the checkmark moves when another choice is clicked;
   - choose **Upscaling (GPU) → GPU video processor**, restart the app and play an HD channel: the picture must look normal (a little sharper is expected);
   - choose **AI Super Resolution**, restart, play an SD or 720p channel in a large window. On this Intel HD 520 the expected result is the status message "AI Super Resolution is not supported..." and a normal picture. That proves the detection works;
   - turn on **SDR to HDR**, restart, play a channel: expected on this PC is the "SDR to HDR is not available..." message and a normal picture;
   - choose **Turn all picture enhancements off**, restart, and confirm the picture is as before.
3. In the log, check for `Picture engine options: ...` with the chosen options, `Picture filters available: postproc,hqdn3d,gradfun,sharpen` (lite build with installed VLC), and the `Picture feature unavailable:` lines.
4. If the user has an interlaced SD channel, ask them to compare "Off" and "Yadif" (horizontal comb lines on motion must disappear with Yadif).

**Note for the roadmap:** AI Super Resolution and RTX Video HDR could not be verified on an RTX or Arc GPU. Record that as a remaining check.

---

## Step 36 — CPU picture enhancements (deblocking, noise, banding, sharpening)

**Goal:** add the four CPU filters to the Picture menu. Because LibVLC 3 only reads `--video-filter` at engine start, the app creates a **second LibVLC engine with the filters** when at least one filter is on. The tuner plays channels on that engine. If a channel freezes under it, the tuner reopens that channel on the normal engine (once per channel per session) and tells the user.

### 36.1 `MainWindow.xaml` — CPU menus

Find:

```xml
                <!-- Step 36 adds the CPU enhancement menus here. -->
```

Replace with:

```xml
                <Separator />
                <MenuItem x:Name="DeblockMenu" Header="Reduce blockiness (deblocking, CPU)">
                    <MenuItem Header="Off" Tag="deblock:0" Click="PictureOption_Click" />
                    <MenuItem Header="Light" Tag="deblock:2" Click="PictureOption_Click" />
                    <MenuItem Header="Medium" Tag="deblock:4" Click="PictureOption_Click" />
                    <MenuItem Header="Strong" Tag="deblock:6" Click="PictureOption_Click" />
                </MenuItem>
                <MenuItem x:Name="DenoiseMenu" Header="Reduce noise (CPU)">
                    <MenuItem Header="Off" Tag="denoise:0" Click="PictureOption_Click" />
                    <MenuItem Header="Light" Tag="denoise:1" Click="PictureOption_Click" />
                    <MenuItem Header="Medium" Tag="denoise:2" Click="PictureOption_Click" />
                    <MenuItem Header="Strong" Tag="denoise:3" Click="PictureOption_Click" />
                </MenuItem>
                <MenuItem x:Name="DebandMenuItem" Header="Smooth color banding (CPU)" IsCheckable="True" Tag="deband" Click="PictureOption_Click" />
                <MenuItem x:Name="SharpenMenu" Header="Sharpen (CPU)">
                    <MenuItem Header="Off" Tag="sharpen:0" Click="PictureOption_Click" />
                    <MenuItem Header="Light" Tag="sharpen:1" Click="PictureOption_Click" />
                    <MenuItem Header="Medium" Tag="sharpen:2" Click="PictureOption_Click" />
                    <MenuItem Header="Strong" Tag="sharpen:3" Click="PictureOption_Click" />
                </MenuItem>
                <!-- Step 37 adds the below-1080p option here. -->
```

### 36.2 `MainWindow.PictureQuality.cs` — checkmarks and availability

Find:

```csharp
        // Step 36 adds the CPU filter menus here.
```

Replace with:

```csharp
        CheckOnly(DeblockMenu, "deblock:" + settings.DeblockQuality.ToString(CultureInfo.InvariantCulture));
        CheckOnly(DenoiseMenu, "denoise:" + settings.DenoiseLevel.ToString(CultureInfo.InvariantCulture));
        DebandMenuItem.IsChecked = settings.Deband;
        CheckOnly(SharpenMenu, "sharpen:" + settings.SharpenLevel.ToString(CultureInfo.InvariantCulture));
        SetFilterAvailability(DeblockMenu, "postproc", "Reduce blockiness (deblocking, CPU)",
            "Reduce blockiness: needs VLC 3 (64-bit) installed");
        SetFilterAvailability(DenoiseMenu, "hqdn3d", "Reduce noise (CPU)", "Reduce noise: not available in this VLC");
        SetFilterAvailability(DebandMenuItem, "gradfun", "Smooth color banding (CPU)", "Smooth color banding: not available in this VLC");
        SetFilterAvailability(SharpenMenu, "sharpen", "Sharpen (CPU)", "Sharpen: not available in this VLC");
```

Then find:

```csharp
    private static void CheckOnly(MenuItem parent, string selectedTag)
```

Replace with:

```csharp
    private void SetFilterAvailability(MenuItem item, string module, string availableHeader, string missingHeader)
    {
        var available = _availableVideoFilters.Contains(module);
        item.IsEnabled = available;
        item.Header = available ? availableHeader : missingHeader;
    }

    private static void CheckOnly(MenuItem parent, string selectedTag)
```

### 36.3 `ChannelTuner.cs` — the enhancement engine

Find:

```csharp
    public bool VideoAdjustmentsActive { get; set; }
```

Replace with:

```csharp
    public bool VideoAdjustmentsActive { get; set; }
    /// <summary>Second LibVLC engine started with the CPU picture enhancement filters, or null when none are on.</summary>
    public LibVLC? FilteredLibVlc { get; set; }
    /// <summary>Step 37: use the enhancement engine only for channels below 1080p.</summary>
    public bool CpuEnhancementsOnlyBelowFullHd { get; set; }
```

Find:

```csharp
    private int _videoDeviceLostGeneration = -1;
```

Replace with:

```csharp
    private int _videoDeviceLostGeneration = -1;
    // The engine that created the active player; its Media objects must come from the same engine. Guarded by _gate.
    private LibVLC? _activeInstance;
```

Find:

```csharp
    public event Action<TuneRequest>? UltraHdTooDemanding;
```

Replace with:

```csharp
    public event Action<TuneRequest>? UltraHdTooDemanding;

    /// <summary>Raised on arbitrary threads after a channel was reopened without CPU picture enhancements.</summary>
    public event Action<TuneRequest>? PictureEnhancementsSuspended;
```

### 36.4 `ChannelTuner.cs` — choose the engine for each attempt

Find:

```csharp
            bool directOnly, unadjusted, fastDecode, softwareDecode;
            lock (_gate)
            {
                recordingOutputPath = _recordingOutputPath;
```

Replace with:

```csharp
            bool directOnly, unadjusted, fastDecode, softwareDecode, useEnhancements;
            var filteredVlc = FilteredLibVlc;
            lock (_gate)
            {
                recordingOutputPath = _recordingOutputPath;
                useEnhancements = _stallPolicy.ShouldUseEnhancements(request.Url, filteredVlc is not null,
                    onlyBelowFullHd: false); // Step 37 replaces false with CpuEnhancementsOnlyBelowFullHd.
```

The rest of that lock block stays as it is.

Find:

```csharp
                media = new Media(_libVlc, mediaUrl, FromType.FromLocation);
```

Replace with:

```csharp
                var instance = useEnhancements ? filteredVlc! : _libVlc;
                media = new Media(instance, mediaUrl, FromType.FromLocation);
```

Find:

```csharp
                player = new MediaPlayer(_libVlc)
```

Replace with:

```csharp
                player = new MediaPlayer(instance)
```

Do **not** change `new ContinuousTimeshiftSource(_libVlc, ...)`. The pause buffer's recorder does not decode video and stays on the normal engine.

### 36.5 `ChannelTuner.cs` — remember the engine of the active player

Find:

```csharp
                    _activeRequest = request;
                    _activeVideoFormat = null;
                    installed = true;
```

Replace with:

```csharp
                    _activeRequest = request;
                    _activeVideoFormat = null;
                    _activeInstance = useEnhancements ? filteredVlc : _libVlc;
                    installed = true;
```

Find (inside `ReleaseAttemptAsync`):

```csharp
                _activePlayer = null;
                _activeMedia = null;
                _activeFileSource = null;
                _activeRequest = null;
                wasActive = true;
```

Replace with:

```csharp
                _activePlayer = null;
                _activeMedia = null;
                _activeFileSource = null;
                _activeRequest = null;
                _activeInstance = null;
                wasActive = true;
```

### 36.6 `ChannelTuner.cs` — pause-buffer input switches use the same engine

Find:

```csharp
    private Media CreateTimeshiftMedia(Uri url)
    {
        var media = new Media(_libVlc, url.AbsoluteUri, FromType.FromLocation);
```

Replace with:

```csharp
    private Media CreateTimeshiftMedia(Uri url, LibVLC instance)
    {
        var media = new Media(instance, url.AbsoluteUri, FromType.FromLocation);
```

Find (in `SwitchActivePlayerInputAsync`):

```csharp
        Media? previousMedia;
        lock (_gate)
        {
            if (!ReferenceEquals(_activePlayer, player) || !ReferenceEquals(_activeFileSource, source) || _activeMedia is null)
                return new(false, sequence, false, "The active channel changed before the timeshift switch began.");
            previousMedia = _activeMedia;
```

Replace with:

```csharp
        Media? previousMedia;
        var switchInstance = _libVlc;
        lock (_gate)
        {
            if (!ReferenceEquals(_activePlayer, player) || !ReferenceEquals(_activeFileSource, source) || _activeMedia is null)
                return new(false, sequence, false, "The active channel changed before the timeshift switch began.");
            previousMedia = _activeMedia;
            switchInstance = _activeInstance ?? _libVlc;
```

Find:

```csharp
            replacement = CreateTimeshiftMedia(url);
```

Replace with:

```csharp
            replacement = CreateTimeshiftMedia(url, switchInstance);
```

### 36.7 `ChannelTuner.cs` — frozen picture under enhancements

Find:

```csharp
                            enhancementsActive: false, // Step 36 replaces false with useEnhancements.
```

Replace with:

```csharp
                            enhancementsActive: useEnhancements,
```

Find:

```csharp
                    // Step 36 inserts the picture-enhancement notification here.
```

Replace with:

```csharp
                    if (stallDecision.EnhancementsSuspended)
                    {
                        try { PictureEnhancementsSuspended?.Invoke(request); }
                        catch (Exception ex) { AppLogger.Error("Tuner: PictureEnhancementsSuspended handler failed.", ex); }
                    }
```

### 36.8 `MainWindow.xaml.cs` — start the enhancement engine

Add a field. Find:

```csharp
    private MediaPlayer? _stoppingPlayer;
```

Replace with:

```csharp
    private MediaPlayer? _stoppingPlayer;
    // Second LibVLC engine with the CPU picture enhancement filters (null when none are on).
    private LibVLC? _filteredLibVlc;
```

Find:

```csharp
        _tuner.UltraHdTooDemanding += request => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isShuttingDown || !_playbackState.Accepts(request)) return;
            StatusText.Text = "This 4K/UHD channel is too demanding for this PC: the picture cannot keep up. Try the channel's HD (1080p) version.";
        }));
```

Replace with:

```csharp
        _tuner.UltraHdTooDemanding += request => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isShuttingDown || !_playbackState.Accepts(request)) return;
            StatusText.Text = "This 4K/UHD channel is too demanding for this PC: the picture cannot keep up. Try the channel's HD (1080p) version.";
        }));
        _tuner.PictureEnhancementsSuspended += request => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isShuttingDown || !_playbackState.Accepts(request)) return;
            StatusText.Text = "Picture enhancements turned off for this channel: this PC could not keep up. Change them in the Picture menu.";
        }));

        var cpuFilterArgs = PictureEnhancementOptions.BuildCpuFilterArgs(GetPictureSettings(), _availableVideoFilters);
        if (cpuFilterArgs.Count > 0)
        {
            try
            {
                var filteredArgs = _libVlcArgs.Concat(cpuFilterArgs).ToArray();
                _filteredLibVlc = await Task.Run(() => new LibVLC(filteredArgs));
                _filteredLibVlc.Log += OnLibVlcLog;
                _tuner.FilteredLibVlc = _filteredLibVlc;
                AppLogger.Info("Picture enhancement engine started: " + string.Join(" ", cpuFilterArgs));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Picture enhancement engine failed to start; playing without enhancements.", ex);
                _filteredLibVlc?.Dispose();
                _filteredLibVlc = null;
            }
        }
```

### 36.9 `MainWindow.xaml.cs` — dispose it at shutdown

Find:

```csharp
            _tuner?.Dispose();
            _mediaPlayer = null;
            _currentMedia = null;
            _libVlc?.Dispose();
```

Replace with:

```csharp
            _tuner?.Dispose();
            _mediaPlayer = null;
            _currentMedia = null;
            _libVlc?.Dispose();
            _filteredLibVlc?.Dispose();
```

### 36.10 Tests

The builder and policy rules were already covered in Steps 34 and 35. In `PlaybackQualityRegressionChecks.cs`, replace `            // Step 36 checks go here.` with:

```csharp
            // Step 36
            ("any CPU filter marks the settings as needing the enhancement engine", () =>
            {
                True(Picture(deblock: 2).AnyCpuFilter);
                True(Picture(denoise: 1).AnyCpuFilter);
                True(Picture(deband: true).AnyCpuFilter);
                True(Picture(sharpen: 1).AnyCpuFilter);
                True(!Picture(deinterlace: "yadif", upscale: "super", rtxHdr: true).AnyCpuFilter);
            }),
            // Step 37 checks go here.
```

### 36.11 Build, check, publish

1. Build, run the checks, publish the lite build.
2. Ask the user to:
   - set **Reduce blockiness → Medium**, **Reduce noise → Light**, **Sharpen → Light**, then restart the app;
   - play an SD or 720p channel that looks blocky: blocks in dark or fast scenes should be softer, and the picture should not be smeared;
   - play a demanding 1080p50 sports channel. If the picture freezes, the app must, after about 10 seconds, show "Picture enhancements turned off for this channel..." and continue with a moving picture;
   - zap through 5 channels and pause/resume one live channel (the pause buffer must still work);
   - choose **Turn all picture enhancements off**, restart.
3. In the log check for `Picture enhancement engine started: --video-filter=postproc:hqdn3d:sharpen ...` and, when a channel froze, `reopening it without picture enhancements`.
4. Ask whether the open Picture menu showed any item as "not available". With the lite build and installed VLC 3.0.23, all four should be available.

**Note for the roadmap:** the second engine uses extra memory (expected tens of MB). Record the app's memory in Task Manager with enhancements on and off if the user can provide it.

---

## Step 37 — Use CPU enhancements only on channels below 1080p (default on)

**Goal:** on a weak PC the CPU filters cannot keep up with 1080p, but SD/720p channels are the ones that benefit. With this option on (default), every channel first opens on the normal engine. When its video turns out to be below 1080p, the tuner reopens it once on the enhancement engine (about 1–2 seconds, once per channel per app session). HD and 4K channels never get the CPU filters. Users with a fast PC can turn the option off to enhance every channel.

### 37.1 `MainWindow.xaml` — the option

Find:

```xml
                <!-- Step 37 adds the below-1080p option here. -->
```

Replace with:

```xml
                <MenuItem x:Name="EnhanceOnlyBelowFullHdMenuItem" Header="Use CPU enhancements only on channels below 1080p (recommended)" IsCheckable="True" Tag="belowhd" Click="PictureOption_Click" />
```

### 37.2 `MainWindow.PictureQuality.cs` — checkmark and click

Find:

```csharp
        // Step 37 adds the below-1080p option here.
```

Replace with:

```csharp
        EnhanceOnlyBelowFullHdMenuItem.IsChecked = settings.OnlyBelowFullHd;
```

Find:

```csharp
            // Step 37 adds the "belowhd" case here.
```

Replace with:

```csharp
            case "belowhd":
                _state.EnhanceOnlyBelowFullHd = !_state.EnhanceOnlyBelowFullHd;
                if (_tuner is not null) _tuner.CpuEnhancementsOnlyBelowFullHd = _state.EnhanceOnlyBelowFullHd;
                restartNeeded = false;
                StatusText.Text = _state.EnhanceOnlyBelowFullHd
                    ? "CPU enhancements now apply only to channels below 1080p, from the next channel you open."
                    : "CPU enhancements now apply to every channel, from the next channel you open.";
                break;
```

### 37.3 `MainWindow.xaml.cs` — pass the setting to the tuner

Find:

```csharp
                _tuner.FilteredLibVlc = _filteredLibVlc;
```

Replace with:

```csharp
                _tuner.FilteredLibVlc = _filteredLibVlc;
                _tuner.CpuEnhancementsOnlyBelowFullHd = _state.EnhanceOnlyBelowFullHd;
```

### 37.4 `ChannelTuner.cs` — open on the normal engine first

Find:

```csharp
                    onlyBelowFullHd: false); // Step 37 replaces false with CpuEnhancementsOnlyBelowFullHd.
```

Replace with:

```csharp
                    onlyBelowFullHd: CpuEnhancementsOnlyBelowFullHd);
```

### 37.5 `ChannelTuner.cs` — reopen low-resolution channels with enhancements

Find:

```csharp
    // Records and logs the format. Returns true to reopen the channel (Step 37).
    private bool OnVideoFormatKnown(TuneRequest request, VideoFormatInfo format)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeRequest, request)) _activeVideoFormat = format;
        }
        AppLogger.Info("Tuner: video format. " + format.Describe() + (format.IsLikely10BitHevc ? "; 10-bit HEVC" : string.Empty) +
            "; channel=" + request.ChannelName);
        return false;
    }
```

Replace with:

```csharp
    // Records and logs the format. Returns true to reopen the channel on the enhancement engine.
    private bool OnVideoFormatKnown(TuneRequest request, VideoFormatInfo format, bool attemptUsedEnhancements, bool recording)
    {
        bool reopen;
        lock (_gate)
        {
            if (ReferenceEquals(_activeRequest, request)) _activeVideoFormat = format;
            reopen = _stallPolicy.ShouldReopenWithEnhancements(request.Url, format, FilteredLibVlc is not null,
                CpuEnhancementsOnlyBelowFullHd, attemptUsedEnhancements, recording);
        }
        AppLogger.Info("Tuner: video format. " + format.Describe() + (format.IsLikely10BitHevc ? "; 10-bit HEVC" : string.Empty) +
            "; channel=" + request.ChannelName);
        if (reopen)
            AppLogger.Info("Tuner: channel is below 1080p; reopening it with picture enhancements. channel=" + request.ChannelName);
        return reopen;
    }
```

Find:

```csharp
                    format => OnVideoFormatKnown(request, format)).ConfigureAwait(false);
```

Replace with:

```csharp
                    format => OnVideoFormatKnown(request, format, useEnhancements, recordingOutputPath is not null)).ConfigureAwait(false);
```

### 37.6 Tests

The rules are already covered by the Step 34 check "enhancement engine rules for all channels and below-1080p only". In `PlaybackQualityRegressionChecks.cs`, replace `            // Step 37 checks go here.` with:

```csharp
            // Step 37
            ("a channel reopened with enhancements that then freezes falls back for good", () =>
            {
                var policy = new StallFallbackPolicy();
                var sd = VideoFormatInfo.Create(FourCc("h264"), 1024, 576, 0, 25, 1)!;
                True(policy.ShouldReopenWithEnhancements("sd", sd, true, true, false, false));
                True(policy.ShouldUseEnhancements("sd", true, true));
                Equal(StallFallback.WithoutEnhancements, policy.Decide("sd", false, false, false, enhancementsActive: true, remuxed: false).Action);
                True(!policy.ShouldUseEnhancements("sd", true, true));
                True(!policy.ShouldReopenWithEnhancements("sd", sd, true, true, false, false));
            }),
```

### 37.7 Build, check, publish

1. Build, run the checks, publish the lite build.
2. Ask the user (with Medium deblocking and Light sharpening still on from Step 36) to:
   - play an SD/720p channel: after about 1–2 seconds there is one short reopen, then the enhanced picture. Switching away and back to the same channel must not reopen it again;
   - play a 1080p channel: no reopen, normal picture, and no freeze from the filters;
   - turn off **Use CPU enhancements only on channels below 1080p** and open the 1080p channel again: it now uses the enhancements (and may freeze, then fall back as in Step 36);
   - start and stop a recording on an SD channel: the recording must not be interrupted by a reopen.
3. In the log check for `Tuner: channel is below 1080p; reopening it with picture enhancements` once per SD channel.

---

## Roadmap updates (every step)

After each step, update `ROADMAP.md` in the section "Planned — playback stability and picture quality":

1. Change that step's `- [ ]` to `- [x]` only when the build passed and the new checks passed.
2. Under the step, add one paragraph starting with the date, for example `Implementation evidence (5 November 2026):`. Include: files changed, build result, PASS/FAIL counts of `--playback-quality`, whether the full runner showed only the known FAIL lines, whether lite was published, what the user confirmed, and what is still unverified. Use plain facts; do not claim anything the user has not confirmed.
3. Never paste provider URLs, account names or log lines containing usernames into the roadmap. Channel names are fine.

## Reference: what each option does

| Menu option | LibVLC option | Runs on | Needs |
|---|---|---|---|
| Deinterlacing: Yadif / Yadif 2x / Bob | `--deinterlace=-1 --deinterlace-mode=…` | GPU when hardware decoding, else CPU | only affects channels flagged interlaced |
| Deinterlacing: Off | `--deinterlace=0` | n/a | n/a |
| Upscaling: GPU video processor | `--d3d11-upscale-mode=processor` | GPU | Direct3D 11 output (Windows default) |
| Upscaling: AI Super Resolution | `--d3d11-upscale-mode=super` | GPU | NVIDIA RTX 20 series or newer with "RTX Video Super Resolution" enabled in the NVIDIA Control Panel, or a supported Intel Arc GPU and driver; only works when the video is smaller than the window |
| SDR to HDR | `--d3d11-hdr-mode=generate` | GPU | NVIDIA RTX with "RTX Video HDR" enabled, Windows HDR on, an HDR screen |
| Reduce blockiness | `--video-filter=postproc --postproc-q=2/4/6` | CPU | the `postproc` plugin (installed VLC 3; not in the bundled LibVLC of the full package) |
| Reduce noise | `hqdn3d` with 0.5× / 1× / 1.5× default strength | CPU | `hqdn3d` plugin |
| Smooth color banding | `gradfun` strength 1.2, radius 16 | CPU | `gradfun` plugin |
| Sharpen | `sharpen` sigma 0.3 / 0.6 / 1.0 | CPU | `sharpen` plugin |
| Lighter 4K decoding (automatic) | `:avcodec-skiploopfilter=4 :avcodec-fast` (+ `:avcodec-hw=none` if a GPU decoder was in use) | CPU | only after a 4K channel froze |

No player can restore detail the provider removed by compressing the stream. These options reduce visible artifacts (blocks, noise, banding, combing, soft upscaling); they do not turn SD into real HD.
