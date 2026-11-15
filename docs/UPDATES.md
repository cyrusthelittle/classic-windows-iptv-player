# Application updates

The app checks the latest GitHub release. When a newer version is available, **Update now** downloads the update for the edition you are running, checks it, and changes to **Restart to update**. A Lite installation always receives the Lite update and a Full installation the Full update. No signing key is involved.

The global Settings > Updates preference **Download updates automatically (ask before restart)** defaults off. Update checks remain independent of it. Automatic offers are modeless, so library browsing and playback controls stay available during the download. Cancel stops downloading or checking, Retry starts a fresh download, and Later leaves the current app running. A checked download is reused for the same offer while the process stays open; a later process downloads it again rather than trusting old stage metadata. Release notes and **Open this release on GitHub** stay available.

Restart is refused while a tune or playback request (including paused or buffering), a multi-view window, an active recording or a recording transition is present. Stop playback, finish recording or close multi-view, then check for updates and choose Restart. Install failures are shown once on the next normal startup with retained-data guidance. The helper starts only after that explicit action and waits for the app to close normally; it never terminates the old app. Runtime and installation leases reject competing processes, including processes using different data roots with the same installation.

## Moving from 0.8.5 and earlier

Version 0.8.5 and earlier only install signed updates. Releases from 0.9.0 on are unsigned, so those versions show the update and its GitHub link but cannot install it themselves. Install 0.9.0 manually once; from then on updates install automatically.

## Release assets

Every release carries, per edition:

| Edition | Package | Manifest |
|---|---|---|
| Full | `ClassicWindowsIPTVPlayer-vVERSION-win-x64-full.zip` | `update-manifest-full.json` |
| Lite | `ClassicWindowsIPTVPlayer-vVERSION-win-x64-lite.zip` | `update-manifest-lite.json` |

The exact tag, including an optional lower-case v, is part of the package name. The manual downloads (`...Full.zip`, `...Lite.zip`) are separate assets for new installs and are not used by the updater. The repository is `cyrusthelittle/classic-windows-iptv-player`.

The edition is stamped into the build: publishing with `BundleVlc=false` (the Lite script) marks the app `lite`, any other build `full`. The app requests only its own edition's manifest; the helper records the edition in its job and refuses a package named for the other edition.

## Manifest contract

JSON property names are PascalCase: Format=2, Repository, Tag, Version, Runtime=`win-x64`, Package, PackageSize, PackageSha256, DataSchema, and Files. Each Files record contains Path, Size and Sha256 (hexadecimal SHA-256). Version must exactly match the offered numeric release tag, exceed the running version and keep the current saved-data schema. Prerelease suffixes are not supported for installation. Limits: manifest 1 MiB, compressed package 1 GiB, expanded files 2 GiB / 20,000 files.

Integrity comes from the manifest: the package size and hash, every file's size and hash, and complete file-list equality (including case-insensitive duplicates) are checked before anything is installed, and the helper checks them again on its own copy. Authenticity relies on downloading over HTTPS from this repository's GitHub release; anyone able to publish releases to the repository can publish an update.

ZIPs contain files only, with forward-slash relative paths. Traversal, Windows reserved names and alternate data streams, links and reparse paths, explicit directory entries, arbitrary executables and saved-data paths are refused. Required files are `Classic Windows IPTV Player.exe`, `ClassicWindowsIptvPlayer.Updater.exe` and `ClassicWindowsIptvPlayer.Core.dll`. Application and native DLLs, PDBs, icon and image assets, deps/runtimeconfig JSON, the self-contained runtime's `createdump.exe`, LibVLC's two `libvlc/win-x64/plugins/access/libbluray-{awt-j2se-,j2se-}1.4.0.jar` files and the Lite guide `Read Me First!!!.txt` are allowed. Accounts, cache, logs, updates, Recordings, the portable marker and arbitrary data files are never valid payload files, so the tracked free-to-air account seed is never part of an update.

Downloads start at the exact repository/tag GitHub HTTPS asset path and accept at most five redirects, limited to that repository download path or `release-assets.githubusercontent.com`, with no userinfo or nondefault ports. Response sizes are bounded as they stream. The whole download has a fifteen-minute deadline; cancellation also interrupts package hashing and ZIP checks. The helper extracts to a fresh private apply directory with its own five-minute verification deadline and rechecks each extracted file immediately before installing it.

## Making a release

Run from `public`, using fresh, separate absolute directories outside the repository:

```powershell
dotnet publish src/ClassicWindowsIptvPlayer.Windows -c Release -r win-x64 --self-contained true  -p:BundleVlc=true  -p:PublishSingleFile=false -o C:\RELEASE\full
dotnet publish src/ClassicWindowsIptvPlayer.Windows -c Release -r win-x64 --self-contained false -p:BundleVlc=false -p:PublishSingleFile=false -o C:\RELEASE\lite
dotnet run --project tools/ClassicWindowsIptvPlayer.ReleasePackager -c Release -- package C:\RELEASE\full C:\RELEASE\assets v0.9.0 full
dotnet run --project tools/ClassicWindowsIptvPlayer.ReleasePackager -c Release -- package C:\RELEASE\lite C:\RELEASE\assets v0.9.0 lite
```

Publish from fresh folders: the packager includes every file in the payload and refuses user-data files rather than silently skipping them, so never package a folder that has been run (it would contain accounts, logs or cache). The packager checks that the tag matches the executable's file version and that the payload is really the named edition (Full carries the .NET host, Lite does not), validates its output with the production checks, and writes the package and manifest. It does not upload anything. Upload both packages and both manifests to the GitHub release together with the manual Full and Lite ZIPs.

The scripts `scripts\BUILD_WINDOWS_FULL.bat` and `scripts\BUILD_WINDOWS_LITE.bat` build the manual folders and add the tracked free-to-air account seed; that seed belongs only in the manual downloads.

## Apply, rollback and recovery

A job under the pinned data root's updates folder records StageDirectory, InstallDirectory, DataRoot, ParentProcessId, CurrentVersion, Tag and Edition. `ClassicWindowsIptvPlayer.Updater.exe --apply ABSOLUTE-update-job.json` waits for that process to exit, acquires the install/data leases, checks again, completes application-only backups, and durably writes `.update-pending.json` before replacing any file. Replacements use flushed temporary files and per-file rename. Original application backup bytes are hashed; restoration hashes and copies each source through the same locked stream. Old unlisted application files remain in place. User-data files and recording files never take part in backup, replacement or deletion.

The durable marker remains throughout verification of the installed app. A special noninteractive probe child opens protected settings, creates the actual unshown Login/Main XAML and native LibVLC/MediaPlayer without providers, streams, recording or user interaction, writes ready and exits successfully. The helper requires both success and the exact acknowledgment before moving the marker to retained history and launching the normal app with the same absolute `CYRUS_IPTV_DATA_ROOT`. Only a stuck noninteractive probe may be terminated. The probe does not establish painted UI, actual playback, network interoperability or user interaction. Same-schema-only installation permits immediate binary rollback without feeding a newer schema to an old app; future schema-changing updates must add the separate compatible-snapshot policy in SAVED_DATA.md.

If checking, applying or the probe fails, old application files are restored and the old app is relaunched when restoration succeeds. Failed restoration keeps the blocking marker and backups; no mixed installation is relaunched. Power interruption is recoverable through the retained job and copied runner, but is not silently repaired by normal startup. After resolving permissions or file locks, run the copied runner with `--recover ABSOLUTE-update-job.json`; it holds both leases, validates and restores the recorded backups, removes the marker and relaunches the old app. Keep all stage, journal and backup files until recovery succeeds. `update-result.txt` under the stage records failure guidance.

Retained download and application backup artifacts are not pruned automatically yet. They contain application files, not copied account state. Update backups are separate from Step 30's protected saved-data migration snapshots. Preserve later recordings and newer saved data during any manual rollback.
