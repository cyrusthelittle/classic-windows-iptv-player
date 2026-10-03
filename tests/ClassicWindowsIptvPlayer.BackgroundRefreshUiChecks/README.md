# Remembered background playlist refresh WPF checks

```powershell
dotnet build tests/ClassicWindowsIptvPlayer.BackgroundRefreshUiChecks/BackgroundRefreshUiChecks.csproj -c Release -p:DefaultItemExcludesInProjectFolder=**/artifacts/**
& tests/ClassicWindowsIptvPlayer.BackgroundRefreshUiChecks/bin/Release/net10.0-windows/win-x64/BackgroundRefreshUiChecks.exe
```

This runner copies immutable runtime files into a unique temporary folder and starts a hidden child process. It creates invented accounts/caches there, disables EPG, update checking and remote control, and uses only a controlled loopback HTTP provider. HTTP response gates keep a playlist request blocked until the test releases it. Runtime evidence and logs stay in the printed temporary folder; normal user data is never read.

Checks exercise the production account editor Save and actual modal Cancel (the editor is fully transparent), Settings checkbox clicks, production `LoadChannelsAsync`, and actual WPF ListBox bindings and Cancel/Retry buttons. The main window is never shown and native playback is not initialized.

Coverage includes default-off/per-account preference persistence, canceled draft edits, immediate cached browsing with a blocked provider, nonblocking overlay behavior, same-task/request deduplication, success with protected cache and timestamp replacement, HTTP failure and missing saved media category retaining verified data/timestamp, Cancel/Retry, saving edits to the current account without a new refresh, obsolete work on A → B → A, token cleanup and mandatory initial loading for an uncached account. The partial-result fixture specifically omits the previously saved Movie media category; it does not exercise Xtream API partial-result flags.

The final local run on 3 October 2026 passed all checks with zero-warning Release build. The two-item cached list was bound and usable after 146.6 ms while the provider response remained blocked. Preference persistence is verified by new ConfigStore/account editor instances, not a separate process restart. These checks do not establish rendered-frame timing, actual main-window startup or complete Change Account dialog flow, native playback, recording continuity, Narrator or keyboard accessibility.
