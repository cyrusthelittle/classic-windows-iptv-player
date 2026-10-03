# Isolated updater process checks

From `public`, build and run:

```powershell
dotnet build tests/ClassicWindowsIptvPlayer.UpdaterProcessChecks/UpdaterProcessChecks.csproj -c Release -p:DefaultItemExcludesInProjectFolder=**/artifacts/**
dotnet tests/ClassicWindowsIptvPlayer.UpdaterProcessChecks/bin/Release/net10.0-windows/win-x64/UpdaterProcessChecks.dll C:\Projects\doing\cyrus-iptv\public
dotnet tests/ClassicWindowsIptvPlayer.UpdaterProcessChecks/bin/Release/net10.0-windows/win-x64/UpdaterProcessChecks.dll --ui
```

The runner creates a unique temp fixture tree, generates an RSA3072 key in memory, and builds the production updater/Core with only that fixture's public trust anchor (`UpdateTrustPath`). The private fixture key is never written. Builds go into the temp tree using `--artifacts-path`; this does not add a production runtime trust bypass or authorize real release signing.

Each helper is a real child process receiving an explicit absolute `CYRUS_IPTV_DATA_ROOT`. All invented installs, signed packages, jobs and DPAPI account/cache files stay in that fixture tree. No normal application data, provider, GitHub, browser, private project or release signing material is accessed.

Cases exercise authenticated apply and actual probe-process relaunch/health acknowledgment; corrupted signature; package hash tampering; a signed package with an unsafe extra archive entry; a real Windows file handle preventing Core DLL replacement; invalid executable launch with automatic restoration; a probe writing readiness then exiting unsuccessfully; and recovery from a constructed durable pending journal representing an interrupted replacement. Locked replacement deliberately needs recovery after the injected external lock is released. Assertions compare every original application file and saved-data byte, and the relaunched probe checks the exact inherited root, same-user credential decryption, favorites, history, playlist cache, preferences and recording destination. An external invented recording is also byte-compared.

The optional hidden WPF check uses production MainWindow and UpdateWindow against invented local settings. It checks the Settings menu checkbox starts off and persists changes, and exercises the actual restart predicate with idle, invented active tune, Stop with retained title, incomplete/completed recording transition/stop tasks, and shutdown. The ready offer has explicit Restart/Later choices and defaults to Later. Modeless Restart, Later and Never handlers close without attempting a modal DialogResult. An invalid release tag fails before the network call; two attempts establish Retry controls and cancellation-source cleanup. It never invokes helper start, opens a browser, shows a window or initializes native playback. The fixture caught and drove correction of the retained-title Stop/restart gate.

The original interruption case reconstructs a valid journal/backup and partly changed install. The additional killed-apply case watches the creation of a 32 MiB replacement temporary file, kills that fixture's actual helper process, then runs recovery and compares original application/account/cache/recording bytes and removal of journal/temporary files. The ordinary probe is a minimal executable; the optional production mode below instead uses actual versioned WPF/LibVLC binaries. These checks do not establish rendered update interaction, native playback/recording continuity, ACL-denied folders, full disk, multiple app-instance interaction, shipped release metadata/signing, actual network download or a clean-Windows user install. Do not infer those acceptance gates from these process fixtures.

Evidence is retained at the printed `Isolated evidence` temp path. Final checks after modeless/result/lease changes on 3 October 2026 both exited zero: eight real helper cases/39 assertions in `cyrus-updater-process-74e7df690d8241a4811b5e95b2a33b61`, and 18 hidden WPF assertions in `cyrus-updater-process-45af398e0a3f4cc5b6a81069b3b37583`. The final harness Release build succeeded with zero warnings/errors using `-p:NuGetAudit=false`; dependency vulnerability auditing is not claimed. An earlier build had two NU1900 warnings because that endpoint was unavailable. Temp fixture helper/probe builds also succeeded. No production signing key was read or altered; production `UpdateTrust.pem` remained in source and fixture builds used their own explicit public-key override.

## Actual production-binary upgrade fixture

```powershell
dotnet <isolated-artifacts>/bin/UpdaterProcessChecks/release_win-x64/UpdaterProcessChecks.dll --production C:/Projects/doing/cyrus-iptv/public
```

Build the runner with --artifacts-path pointing outside the source projects to avoid running-file locks. This optional mode compiles actual 1.0.0 and 2.0.0 Windows app binaries into separate temporary artifact trees with the fixture public trust anchor. It packages/signs the new runtime files using an RSA3072 key held only in memory, installs into an invented old-version tree, runs the actual WPF/LibVLC health probe, observes the relaunched account window, and checks protected account/history/favorites/cache bytes, decrypted credentials/preferences and an external recording. Only application processes whose executable path exactly matches that fixture installation are stopped during cleanup. Update/provider checks are disabled, and no provider or GitHub request is made. The existing build helper-copy target uses default outputs; this fixture explicitly substitutes its own helper files in its temporary app outputs.

The final run on 3 October 2026 passed nine assertions, exit 0, under cyrus-updater-process-59f993ceac684728b148f95a25e94683. This is a framework-dependent local signed update using the installed .NET runtime and a fixture key. It does not exercise production-key signing, real GitHub download, a clean Windows user without .NET, or physical account-window interaction. No release is published. The runner disables output capture for the long-lived production relaunch so inherited output pipes cannot keep the helper check waiting after its exit.

Current ordinary process checks passed nine scenarios / 44 assertions, including actual apply-process termination/recovery. The rendered UI follow-up passed 24 assertions with staged light/dark/minimum-size and failed-retry PNGs. Keyboard/Narrator/monitor scaling and native playback/recording during real network staging remain manual acceptance checks.
