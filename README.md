# Classic Windows IPTV Player

A classic Windows IPTV player built with WPF and LibVLC on a shared core library.

## Download

Grab the latest ready-to-run build from the [Releases page](../../releases/latest), download the `Classic-Windows-IPTV-Player-Windows-x64-v*.zip` asset, extract it anywhere, and run `Classic Windows IPTV Player.exe`.

No installation is required. Keep the extracted folder together—the EXE needs `libvlc.dll`, `libvlccore.dll`, and the `plugins` folder next to it.

The app is portable. `accounts.json`, the `logs` folder, and the `cache` folder are stored beside the executable. Keep `accounts.json` private because accounts may contain provider credentials.

### Requirements to run

- Windows 10 or 11, 64-bit
- About 600 MB of free disk space
- An internet connection
- Nothing else—the .NET runtime and LibVLC are bundled

Windows SmartScreen may warn on first run because the app is unsigned. Click **More info → Run anyway**.

## First-run account

New installations include a credential-free **Free account** using the public [IPTV-org](https://github.com/iptv-org/iptv) country playlist. You can remove it or add your own Xtream or M3U account from the login window.

## Projects

```text
src/ClassicWindowsIptvPlayer.Core      Shared IPTV logic (no UI)
src/ClassicWindowsIptvPlayer.Windows   Windows WPF shell
```

### Core library

- account settings and app state
- playlist download and M3U/M3U Plus parsing
- live/movie/series classification
- XMLTV programme-guide download, parsing, and channel matching
- account information lookup
- stream candidate building and probing
- compressed playlist cache and fast search index

### Windows app

- account login with playlist caching
- live/movie/series filters, search, folders, favorites, and recent items
- built-in LibVLC player with full-screen playback
- stream information, source and buffer selection, seeking, volume, and mute
- current and next programme display
- local-network remote control

## Build from source

Requirements:

- Windows 10 or 11, 64-bit
- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)

Run:

```bat
scripts\BUILD_WINDOWS_RELEASE.bat
```

The self-contained app is published to:

```text
release\classic-windows-iptv-player
```

For development:

```bat
scripts\RUN_WINDOWS.bat
```

## Programme guide

EPG is disabled by default. Turn it on with **Settings → Programme guide (EPG)**. Xtream accounts automatically use the provider's `xmltv.php` guide. For a direct M3U account, enter an XMLTV or XMLTV `.gz` URL in the optional **EPG URL** field.

## Troubleshooting

If startup fails, check the `logs` folder beside the executable:

```text
logs\startup-crash.log
```

If LibVLC files are missing, run:

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\RepairWindowsLibVlc.ps1
```

## Legal

Use only playlists and IPTV services you are authorized to access. The application bundles no paid channels or private credentials.
