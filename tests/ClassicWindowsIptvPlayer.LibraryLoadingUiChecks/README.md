# Cached library WPF checks

Build and run on Windows with the .NET 10 SDK:

```powershell
dotnet build tests/ClassicWindowsIptvPlayer.LibraryLoadingUiChecks/LibraryLoadingUiChecks.csproj -c Release -p:DefaultItemExcludesInProjectFolder=**/artifacts/**
& tests/ClassicWindowsIptvPlayer.LibraryLoadingUiChecks/bin/Release/net10.0-windows/win-x64/LibraryLoadingUiChecks.exe
```

The runner copies runtime files into a unique temporary folder and starts a hidden child process. It writes invented accounts and protected caches only there, disables update checks, EPG and remote control, and points unused provider URLs at a closed loopback port. A second process reopens the same folder without regenerating fixtures. Normal app data is never used. The printed folder retains local evidence and logs for inspection.

Checks invoke production `MainWindow.LoadChannelsAsync` on a real WPF dispatcher and inspect the actual bound `ChannelList` ListBox in the flat live-items view. Coverage includes 50,000/150,000 cached items, offline A → B → A, prepared-index reference reuse, organization and disk replacement invalidation, overlapping obsolete requests, token cleanup, favorites and disk reload in a fresh window and process. A dispatcher observer samples catalog identity during the overlapping requests. Timings end after binding and the dispatcher reaches application idle; they exclude native player initialization and provider networking.

Windows are not shown. These checks do not establish rendered-frame timing, pointer/keyboard behavior, the actual account-manager dialog, episode fetching, playback, recording or a corrupt-cache recovery dialog. Cache corruption/recovery is covered separately by Core regression checks.

Final local run on 3 October 2026 passed all checks and the independent process restart. Release build had zero warnings/errors. Bound-list timings on this Windows workstation were 1,060.7 ms for cold 50k, 156.9 ms for returning to prepared A (including selection restoration), 1,198.1 ms for cold 150k and 1,164.2 ms for 50k after full process restart. Allocations for the first three loads were 53.4/5.8/168.6 MiB. Estimated retained prepared libraries reached 268,111,064 bytes across two entries, below the 256 MiB budget. Observed process private memory reached 393.9 MiB after the large load and 521.5 MiB after opening a second MainWindow; test objects and pending garbage collection contribute to those process totals. The independent restarted process reported 81.9 MiB. The retention estimate limits saved prepared entries, not total process memory.
