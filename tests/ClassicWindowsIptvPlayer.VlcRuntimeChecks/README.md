# VLC startup checks

Run from the public repository:

```powershell
dotnet run --project tests/ClassicWindowsIptvPlayer.VlcRuntimeChecks -c Release
```

The checks use invented PE files and an injected native probe to verify system-first selection,
failed-system fallback, architecture checks, missing plugins, duplicate candidates, custom folders,
local library layouts and exhaustion. The rendered WPF prompt checks its four actions and exercises
Retry and Cancel. It does not click Download or open the folder picker.

To also check a published production player with real native libraries, pass its executable and
the library directory containing `libvlc.dll`, `libvlccore.dll` and `plugins`:

```powershell
dotnet run --project tests/ClassicWindowsIptvPlayer.VlcRuntimeChecks -c Release -- "path/to/full/Classic Windows IPTV Player.exe" "path/to/full/libvlc/win-x64"
```

These checks reject malformed, incomplete and 32-bit candidates in separate production probe
processes, load the actual bundled native runtime, and exercise automatic production selection.
The production `--vlc-runtime-probe` entry point skips settings, logs, account-data leases and
updater acknowledgement. No streams or provider requests occur. An optional
`CYRUS_VLC_PROMPT_EVIDENCE` path saves a PNG of the rendered prompt content.
