# Cyrus IPTV Modern Migration Plan - v4

## Current status

- Stable Windows prototype: WinForms v22.
- Modern shared core: started in `CyrusIptv.Core`.
- Android MAUI starter: started in `CyrusIptv.Maui`.
- Android SDK setup now works.
- Current blocker resolved in scripts: `XA0010: No available device` means no Android phone/emulator is connected.

## Architecture

```text
CyrusIptv.Core
  Shared account, playlist, search, cache, stream URL, account info, and probe logic.

CyrusIptv.Maui
  Android/mobile UI and playback shell.

CyrusIptv.WinUI / Avalonia shell
  Future modern Windows UI using the same Core.
```

## v4 device workflow

1. Install MAUI workload.
2. Install Android SDK.
3. Connect a real Android phone or create/start emulator.
4. Run Android app.

Scripts:

```text
scripts\INSTALL_MAUI_WORKLOAD_WINDOWS.bat
scripts\INSTALL_ANDROID_SDK_AUTO_WINDOWS.bat
scripts\CHECK_ANDROID_ENV_WINDOWS.bat
scripts\CHECK_ANDROID_DEVICES_WINDOWS.bat
scripts\CREATE_ANDROID_EMULATOR_WINDOWS.bat
scripts\START_ANDROID_EMULATOR_WINDOWS.bat
scripts\RUN_ANDROID_WINDOWS.bat
```

## Next development steps

1. Confirm Android app starts on emulator/device.
2. Test login and playlist cache on Android.
3. Improve Android player layout.
4. Add Android full-screen controls and remote-friendly navigation.
5. Start modern Windows shell after Android bootstrapping is stable.


### v6 fix

The WPF shell now explicitly calls `LibVLCSharp.Shared.Core.Initialize()` so it does not conflict with the shared `CyrusIptv.Core` namespace.


## Phase 2 v8 update

- Fixed WPF shutdown behavior after the login dialog.
- Added startup crash logging for the modern Windows shell.
- Windows modern shell version: 0.1.2.


## v8 fix

- Fixed unreadable ComboBox text in the modern Windows WPF shell.
- ComboBox selected text and dropdown items now use the dark theme correctly.


## Phase 2 v11

- Corrected Windows ComboBox colors: light background and dark text for readable selected values/dropdowns.
- Windows modern shell version: 0.1.3.


## Phase 2 v11 update

- Ports the missing WinForms v22 playback features into the modern Windows shell.
- Adds true separate full-screen playback window with transparent bottom controls.
- Full-screen controls auto-hide after 3 seconds and reappear on mouse movement.
- Adds stream information: state, resolution, bandwidth, bitrate, buffer, source, and channel.
- Adds restart stream, test stream, copy URL, favorites toggle, volume/mute, VOD seek bar, source selector, buffer selector, and remote-control toggle.
- Keeps the stable WinForms v22 app as fallback while the WPF modern shell matures.


## Phase 2 v11 startup fix

- Fixed modern Windows startup crash caused by `VolumeSlider.ValueChanged` firing while XAML was loading.
- Main window account/state is now initialized before `InitializeComponent()`.
- Volume and mute handlers now avoid stream-info updates until the window is loaded.

## Phase 2 v12

Windows shell version `0.3.0` focuses on UI usability and player interaction:

- Hardens ComboBox styling with white background / black text.
- Fixes the full-screen white-surface issue by attaching the LibVLC player after full-screen load.
- Adds right-click video context menus for playback and stream information.


## Phase 2 v13

- Fixed Windows modern shell compile error: `Channel.Kind` -> `Channel.MediaKind`.
- Kept v12 right-click menu, full-screen fix, and combo box readability changes.

## Phase 2 v14

Bug-fix release for the modern Windows shell:

- Replaced the fragile ComboBox styling with a custom light template to force white background and dark text.
- Switched the full-screen player window to a WinForms LibVLC surface hosted inside WPF for more reliable full-screen rendering.
- Kept the right-click stream options menu on normal and full-screen video.

Windows shell version: `0.3.2`.
