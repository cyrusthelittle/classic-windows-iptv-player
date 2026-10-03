# Update prompt fixture checks

Run from the public repository:

```powershell
dotnet run --project tests/ClassicWindowsIptvPlayer.UpdatePromptUiChecks -c Release -p:DefaultItemExcludesInProjectFolder=**/artifacts/**
dotnet run --project tests/ClassicWindowsIptvPlayer.Core.RegressionTests -c Release -p:DefaultItemExcludesInProjectFolder=**/artifacts/** -- --update-release
```

The WPF fixture initializes production App resources only and constructs hidden UpdateWindow instances. It never runs App startup, reads settings/accounts, initializes playback, calls GitHub or opens a browser. It measures/arranges the production content at 592x510, 432x272 and 336x256 DIP, checks action-footer containment and body scroll extent, scrolls long literal notes to the end, and verifies focus/tab-stop/automation labels, missing metadata fallbacks and dynamic light/dark brushes. These checks passed on 3 October 2026. The third content viewport approximates a short high-scaling work area; it does not change actual monitor DPI.

The Core fixture parses synthetic release JSON and streams, covering exact offered release/version and URL matching, missing/blank/wrong-type notes/name/date, literal HTML/Markdown, surrogate-safe long-note truncation, invalid root/JSON, oversized response and cancellation. No HTTP request occurs.

Remaining manual checks: actual rendered modal prompt, real Tab/Page Up/Page Down/selection/copy and Narrator, 100/150/200-percent monitor scaling, release link launch failure/success, offline/timeouts through Check for updates, and each reminder/update button. The existing GitHub-download update action remains unchanged; its behavior is not established by these hidden checks.
