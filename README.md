# Cyrus IPTV Modern Migration - Phase 2/3 v4

This package continues the rewrite from the stable WinForms prototype to a modern shared-core architecture.

## What changed in v4

- Fixed the current blocker: `XA0010: No available device` is now detected before a long run/build attempt.
- Added `CHECK_ANDROID_DEVICES_WINDOWS.bat`.
- Added `CREATE_ANDROID_EMULATOR_WINDOWS.bat`.
- Added `START_ANDROID_EMULATOR_WINDOWS.bat`.
- Added `BUILD_ANDROID_APK_ONLY_WINDOWS.bat`.
- `RUN_ANDROID_WINDOWS.bat` now stops early if no emulator/phone is connected.
- Fixed the Core playlist parsing warning `CA2024`.
- App version updated to `0.1.3`.

## Included projects

```text
src/CyrusIptv.Core
src/CyrusIptv.Maui
```

### CyrusIptv.Core

Reusable IPTV logic:

- account settings
- app state
- playlist download
- M3U/M3U Plus parsing
- live/movie/series classification
- account info lookup
- stream candidate builder
- compressed playlist cache
- fast search index

### CyrusIptv.Maui

Android-first modern UI starter:

- login page
- update playlist / login from cache
- account info dialog
- live/movie/series filter
- search
- channel list
- built-in LibVLC player surface
- source selector
- seek bar for VOD
- volume/mute

## Required Android setup

You need three things:

1. .NET MAUI Android workload
2. Android SDK
3. A connected Android phone or running emulator

Run this first:

```bat
scripts\INSTALL_MAUI_WORKLOAD_WINDOWS.bat
```

Then install/check Android SDK:

```bat
scripts\INSTALL_ANDROID_SDK_AUTO_WINDOWS.bat
scripts\CHECK_ANDROID_ENV_WINDOWS.bat
```

## Build APK only

Use this when you do not have a phone/emulator connected yet:

```bat
scripts\BUILD_ANDROID_APK_ONLY_WINDOWS.bat
```

APK output will be under:

```text
src\CyrusIptv.Maui\bin\Debug\net10.0-android\
```

## Run on a real Android phone

1. On the phone, enable Developer Options.
2. Enable USB debugging.
3. Connect the phone by USB.
4. Unlock the phone and accept the debugging prompt.
5. Run:

```bat
scripts\CHECK_ANDROID_DEVICES_WINDOWS.bat
scripts\RUN_ANDROID_WINDOWS.bat
```

If the device says `unauthorized`, unlock the phone and approve USB debugging.

## Run on an emulator

Create the emulator once:

```bat
scripts\CREATE_ANDROID_EMULATOR_WINDOWS.bat
```

Start it:

```bat
scripts\START_ANDROID_EMULATOR_WINDOWS.bat
```

When the Android home screen appears, run:

```bat
scripts\RUN_ANDROID_WINDOWS.bat
```

## Stable Windows version

Keep the WinForms v22 app as the stable working Windows version while we finish the modern Android/Windows shell.

## Legal note

This app is for legal IPTV accounts and playlists only. It does not include channels, credentials, or IPTV content.


## Phase 2 v8 notes

- Fixed the modern Windows build error where `Core.Initialize()` was resolved against `CyrusIptv.Core` instead of `LibVLCSharp.Shared.Core`.
- Added a null guard before creating LibVLC media to remove the nullable warning in `MainWindow.xaml.cs`.
- Modern Windows shell version: `0.1.1`.

Run:

```bat
scripts\RUN_WINDOWS_MODERN.bat
```


## Phase 2 v8 fix

The modern Windows app now keeps WPF alive while the login dialog closes.
This fixes the issue where the app exited immediately after a successful login.

If startup still fails, check this log file:

```text
%LOCALAPPDATA%\CyrusIptvModern\startup-crash.log
```

Run the Windows modern shell with:

```bat
scripts\RUN_WINDOWS_MODERN.bat
```


## v8 fix

- Fixed unreadable ComboBox text in the modern Windows WPF shell.
- ComboBox selected text and dropdown items now use the dark theme correctly.


## Phase 2 v11 update

- Ports the missing WinForms v22 playback features into the modern Windows shell.
- Adds true separate full-screen playback window with transparent bottom controls.
- Full-screen controls auto-hide after 3 seconds and reappear on mouse movement.
- Adds stream information: state, resolution, bandwidth, bitrate, buffer, source, and channel.
- Adds restart stream, test stream, copy URL, favorites toggle, volume/mute, VOD seek bar, source selector, buffer selector, and remote-control toggle.
- Keeps the stable WinForms v22 app as fallback while the WPF modern shell matures.


## Phase 2 v11

Fixes a WPF startup crash where the volume slider fired `ValueChanged` during XAML loading before the main window state was fully initialized. The constructor now initializes account/state before loading XAML, and volume/mute handlers are defensive during startup. Windows shell version: `0.2.1`.

## Phase 2 v12 - Windows modern UI fixes

Windows shell version: `0.3.0`.

Fixes and additions:

- Combo boxes are forced to use white backgrounds with dark text so selected values and dropdown values are readable.
- Full-screen player now assigns the LibVLC media surface after the full-screen window loads to avoid the all-white full-screen surface seen on some systems.
- Added a right-click video menu in the main player.
- Added a right-click menu in full-screen mode.
- Right-click detection also uses the Windows mouse hook because LibVLC can swallow normal WPF mouse events.
- Right-click menu includes playback commands and stream information.

Run:

```bat
scripts\RUN_WINDOWS_MODERN.bat
```


## Phase 2 v13 - Windows compile fix

Fixes the Windows modern shell compile error where `MainWindow.xaml.cs` referenced `Channel.Kind`. The shared core model uses `Channel.MediaKind`, so the stream information menu now displays `MediaKind` correctly. Windows shell version: `0.3.1`.

## Phase 2 v14 - ComboBox and full-screen rendering fix

Fixes two issues reported in the modern Windows shell:

- Combo boxes now use a custom light control template so selected text and dropdown text stay readable: white background with black/dark text.
- Full-screen playback now uses the WinForms LibVLC video surface inside the WPF full-screen window. This avoids the white/blank full-screen surface that can happen when the WPF video surface is moved between windows.
- Right-click menu on the video/full-screen player is kept.

Windows shell version: `0.3.2`.
