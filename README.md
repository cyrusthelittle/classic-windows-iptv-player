# Classic Windows IPTV Player

A portable IPTV player for Windows 10 and 11, built with WPF, .NET, LibVLC, and LibVLCSharp. Watch live TV, movies, and series from Xtream Codes accounts or M3U/M3U8 playlists, with XMLTV programme guide support, fast library browsing, favorites, subtitles, full-screen playback, automatic reconnects, and a phone-friendly local remote.

> Use only playlists and IPTV services that you are authorized to access. This application does not include paid channels or private provider credentials.

## Highlights

- **Two account types:** Xtream server URL with username/password, or a direct M3U/M3U8 playlist URL
- **Live TV, movies, and series:** automatic media classification plus on-demand Xtream series and episode loading
- **Large-library navigation:** instant multi-word search, category folders, A-Z/0-9 browsing, item view, breadcrumbs, and channel logos
- **Personal library:** persistent favorites and the 20 most recently played items
- **Built-in LibVLC player:** play/pause, stop, previous/next, seeking, restart, full screen, volume up to 150%, mute, and mouse-wheel volume control
- **Live-stream controls:** pause tracking, behind-live time, and one-click **Go Live**
- **Programme guide:** opt-in XMLTV/XMLTV.GZ EPG with current and next programmes, descriptions, categories, and manual refresh
- **Subtitles:** select embedded subtitle tracks or add local `.srt`, `.sub`, `.ass`, and `.ssa` files
- **Playback diagnostics:** state, resolution, bandwidth, FPS, video/audio codecs, audio format, buffer, and current source URL
- **Resilient streaming:** configurable 1/3/6/10-second network cache, 5/10/15/20 reconnect attempts, and stream restart
- **Local-network remote:** control search, filters, navigation, playback, full screen, and volume from a phone or another browser
- **Portable and self-contained:** no installer, .NET runtime, or separate VLC installation required
- **Multiple saved accounts:** add, edit, remove, and switch providers without restarting the application; each account has its own compressed playlist cache
- **Light and dark themes:** persistent theme, volume, mute, buffer, remote, EPG, and on-screen-display settings
- **GitHub update checks:** optional startup checks, manual checks, and direct links to the exact release page

## Download and run

1. Open the [latest release](../../releases/latest).
2. Download `Classic-Windows-IPTV-Player-Windows-x64-v*.zip`.
3. Extract the entire ZIP to a folder you can keep.
4. Run `Classic Windows IPTV Player.exe`.

Do not copy only the EXE. Keep `libvlc.dll`, `libvlccore.dll`, and the `plugins` folder beside it.

### Requirements

- Windows 10 or Windows 11, 64-bit
- About 600 MB of free disk space
- An internet connection

The release is self-contained and bundles the .NET runtime and 64-bit LibVLC. Windows SmartScreen may warn on first launch because the app is unsigned; use **More info > Run anyway** if you trust the downloaded release.

## First use

New installations contain two credential-free sample accounts:

- **Free Account 1:** the public [IPTV-org country playlist](https://github.com/iptv-org/iptv)
- **Free Account 2:** the BestIPTV all-channels playlist

Select an account, leave **Update playlist before opening** enabled, and choose **Continue**. You can remove either sample or add your own account.

### Add an Xtream account

1. In the account window, choose **Add**.
2. Enter an account name.
3. Select **Server URL, username and password**.
4. Enter the provider's server URL, username, and password.
5. Optionally enter a custom EPG URL. When it is blank, the player derives the provider's `xmltv.php` URL.
6. Choose **Save Account**, then **Continue**.

Xtream accounts can load live streams, VOD movies, series, episode lists, provider categories, and account information when the provider exposes those APIs.

### Add an M3U/M3U8 account

1. In the account window, choose **Add**.
2. Enter an account name.
3. Select **M3U playlist URL**.
4. Paste the HTTP or HTTPS M3U/M3U8 URL.
5. Optionally add an XMLTV or compressed XMLTV `.gz` URL.
6. Choose **Save Account**, then **Continue**.

The parser supports standard M3U and M3U Plus metadata such as channel names, groups, logos, and EPG IDs. It classifies entries as live TV, movies, or series from their metadata and URLs.

### Use the playlist cache

Keep **Update playlist before opening** selected when you want fresh provider data. Clear it to load that account's saved compressed playlist for a faster startup. If no cache exists, the player downloads the playlist automatically.

Use **Playlist > Update playlist** to refresh at any time, or **Playlist > Clear cache** to remove only the selected account's cache.

## Browse the library

The three filter rows can be combined:

- **Folders / A-Z / Items:** browse playlist categories, alphabetic buckets, or a flat item list. A-Z includes A-Z, 0-9, and `#` for other names.
- **All media / Live TV / Movies / Series:** select a media type.
- **All / Favorites / Recent:** select the full library, saved favorites, or up to 20 recent items.

Search matches every word against the item name, group, and media type. Double-click an item or highlight it and press `Enter` to play it. Series entries open an episode list on demand; use **Back** or the clickable breadcrumb to return.

## Playback controls

- Double-click a channel, movie, or episode to play it.
- Use the seek bar for seekable movies and episodes. Live streams show **Live** instead of a duration.
- Pause and resume VOD at the same position. If a provider drops an idle connection, the player reconnects and seeks back automatically when possible.
- Pausing live TV tracks how far playback is behind the live edge. Choose **Go Live** to restart at the live edge.
- Use **Previous** and **Next** to move through the current filtered list.
- Use the volume slider, `+`/`-`, or the mouse wheel over video. Volume supports 0-150% and is remembered with mute state.
- Double-click the video or press `F11` for full screen. Full-screen controls hide automatically and reappear when the pointer moves.
- Right-click the video for playback actions, subtitles, favorites, URL copying, and detailed stream information.

### Subtitles

Open **Subtitles** from the menu or video context menu to choose an embedded subtitle track or turn subtitles off. Choose **Add SRT...** to attach a local `.srt`, `.sub`, `.ass`, or `.ssa` file to the current stream.

### Buffering and reconnects

- Select **Settings > Buffer** or use the player buffer selector to choose 1, 3, 6, or 10 seconds. A larger buffer can smooth unstable streams but makes channel changes slower. Restart the stream to apply it fully.
- Select **Settings > Reconnect attempts** to choose 5, 10, 15, or 20 attempts. The default is 10.
- Choose **Restart stream** to reopen the current stream immediately.

### Stream information and URL

The status area reports playback state, video resolution, measured or estimated bandwidth, FPS, video codec, audio codec and format, buffer size, source, and channel name. Use **Show stream information** for the full details or **Copy stream URL** to copy the active playlist URL.

Treat copied URLs as sensitive: provider URLs can contain usernames, passwords, or access tokens.

## Programme guide (EPG)

EPG is disabled by default because provider XMLTV feeds can be very large or unreliable.

1. Enable **Settings > Programme guide (EPG)**.
2. For Xtream, leave the account EPG field empty to use the provider's `xmltv.php` feed, or enter a custom URL.
3. For M3U, enter an XMLTV or XMLTV `.gz` URL in the account settings.
4. Use **Playlist > Refresh programme guide** whenever you want to reload it.

The player matches XMLTV channel IDs and display names to playlist entries and displays the current and next programme for live channels. Hover the programme text for its category and description.

## Phone and browser remote control

1. Put the Windows PC and remote device on the same local network.
2. Choose **Settings > Remote control on/off**.
3. Open one of the displayed URLs on the phone or browser, normally `http://<computer-ip>:53177/`.
4. If Windows asks, allow the app through the firewall on trusted private networks only.

The responsive remote can search, switch folder/A-Z/item modes, filter live TV/movies/series and all/favorites/recent, move through the list, select or go back, change channels, play/pause, stop, show/hide channels, toggle full screen, adjust volume, and mute.

Remote control listens on the local network without authentication while enabled. Use it only on a trusted network and turn it off when it is not needed.

## Keyboard and mouse shortcuts

| Input | Action |
| --- | --- |
| `Enter` | Open the selected folder, series, or media item |
| `Space` | Play or pause |
| `S` | Stop |
| `Left` / `Right` | Previous / next item |
| `Up` / `Down` | Move the library selection |
| `+` / `-` | Raise / lower volume by 5% |
| `M` | Mute or unmute |
| `F11` | Enter or leave full screen |
| `Esc` | Leave full screen |
| `Ctrl+L` | Show or hide the channel sidebar |
| Double-click video | Enter or leave full screen |
| Mouse wheel over video | Raise or lower volume |
| Right-click video | Open the playback context menu |

In full screen, `Up`/`Down` also control volume, and `Left`/`Right` change the channel.

## Accounts, appearance, and updates

- Use **Account > Logout / Change account** to add, edit, remove, or switch accounts without restarting the application. At least one account is always kept.
- Use **Account > Information** to request status, expiry, trial, connection, server-time, and timezone details from a compatible Xtream provider.
- Toggle **Settings > Dark mode** for a persistent dark theme.
- Change **Settings > OSD opacity** for the full-screen controls and volume display, using presets or a custom percentage.
- Toggle **Settings > Check for updates on startup**. You can always run **Help > Check for updates...** manually.
- When an update is found, choose **Remind me later**, **Never remind me**, or **Update now**. Updating opens the exact GitHub release page; it never installs software silently.

## Portable data and privacy

The application stores its data beside the executable:

```text
accounts.json                 Accounts and application settings
cache/channels-<account>.json.gz
                              Per-account compressed playlist caches
logs/                         Runtime and crash logs
```

Keep `accounts.json` private because it may contain provider credentials. Playlist caches and logs may also contain provider or channel information. Moving the entire extracted folder moves the application and its saved data together.

## Build from source

Requirements:

- Windows 10 or Windows 11, 64-bit
- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)

Create a self-contained release:

```bat
scripts\BUILD_WINDOWS_RELEASE.bat
```

The output is written to:

```text
release\classic-windows-iptv-player
```

Run a development build:

```bat
scripts\RUN_WINDOWS.bat
```

Project layout:

```text
src/ClassicWindowsIptvPlayer.Core      Playlist, account, EPG, cache, search, and playback logic
src/ClassicWindowsIptvPlayer.Windows   WPF interface, LibVLC player, remote, themes, and updates
scripts/                               Windows run, release, and LibVLC repair scripts
```

## Troubleshooting

### The app does not start

Check the `logs` folder beside the executable. Startup failures are written to:

```text
logs\startup-crash.log
```

Runtime UI and task failures are written to `logs\runtime-crash.log`.

### LibVLC files are missing

From the repository root, run:

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\RepairWindowsLibVlc.ps1
```

For a downloaded release, extract the ZIP again and keep the complete folder together.

### A playlist will not load

- Verify the URL and credentials with the provider.
- For Xtream, enter the server root plus username and password; do not paste a generated stream URL into the server field.
- Try **Playlist > Clear cache**, then **Playlist > Update playlist**.
- Review the `logs` folder for the provider response or network error.

### Playback stutters or disconnects

- Increase the buffer from 1 second to 3, 6, or 10 seconds.
- Increase reconnect attempts.
- Restart the stream.
- Check the detailed stream information for bandwidth and codec details.

## Legal

This project is a media player, not an IPTV service. It provides no paid subscriptions, private playlists, or copyrighted streams. You are responsible for complying with your provider's terms and all applicable laws.
