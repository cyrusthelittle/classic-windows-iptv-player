# Cyrus IPTV

A modern IPTV player for Windows, built with WPF and LibVLC on a shared core library.

## Download

Grab the latest ready-to-run build from the [Releases page](../../releases/latest) — download `CyrusIptv-Windows-x64.zip`, extract it anywhere, and run `Cyrus IPTV Modern.exe`.

No installation is required. Keep the extracted folder together — the EXE needs `libvlc.dll`, `libvlccore.dll`, and the `plugins` folder next to it.

### Requirements to run (fresh PC)

- Windows 10 or 11, 64-bit
- ~600 MB free disk space
- Internet connection (for your IPTV playlist/streams)
- Nothing else — the .NET runtime and LibVLC are bundled in the download

> **Note:** Windows SmartScreen may warn on first run because the app is unsigned. Click **More info → Run anyway**.

## Projects

```text
src/CyrusIptv.Core      Shared IPTV logic (no UI)
src/CyrusIptv.Windows   Windows WPF shell
```

### CyrusIptv.Core

Reusable IPTV logic:

- account settings and app state
- playlist download and M3U/M3U Plus parsing
- live/movie/series classification
- XMLTV programme-guide download, parsing, and channel matching
- account info lookup
- stream candidate builder
- compressed playlist cache
- fast search index
- stream probing

### CyrusIptv.Windows

Windows WPF app:

- login window with playlist update / login from cache
- live/movie/series filter, search, channel list, favorites
- built-in LibVLC player with full-screen window
- stream information (state, resolution, bandwidth, bitrate, buffer, source)
- source selector, buffer selector, VOD seek bar, volume/mute
- current and next programme display for live channels, with full descriptions in tooltips
- right-click playback menu (windowed and full-screen)
- remote-control toggle

## Requirements to build from source

- Windows 10/11 x64
- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)

## Build

There is one way to build the app:

```bat
scripts\BUILD_WINDOWS_MODERN_RELEASE.bat
```

This publishes a self-contained build to:

```text
release\windows-modern
```

Keep that folder together — the EXE needs `libvlc.dll`, `libvlccore.dll`, and the `plugins` folder next to it.

## Run during development

```bat
scripts\RUN_WINDOWS_MODERN.bat
```

## Programme guide (EPG)

EPG is disabled by default. Turn it on with **Settings → Programme guide (EPG)**. Xtream accounts then automatically use the provider's `xmltv.php` guide. For a direct M3U account, enter an XMLTV or XMLTV `.gz` URL in the optional **EPG URL** field. The guide matches channels by `tvg-id` first and falls back to the channel/display name.

The current and next programmes appear below the playback controls. Hover a programme to see its category and description. Use **Playlist → Refresh programme guide** to fetch it again without reloading the playlist.

## Troubleshooting

If startup fails, check the log file:

```text
%LOCALAPPDATA%\CyrusIptvModern\startup-crash.log
```

If LibVLC files are missing from the release folder, run:

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\RepairWindowsLibVlc.ps1
```

## Legal note

This app is for legal IPTV accounts and playlists only. It does not include channels, credentials, or IPTV content.
