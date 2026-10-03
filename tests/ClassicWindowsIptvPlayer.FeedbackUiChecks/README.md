# Isolated crash and feedback UI checks

Build from public with .NET 10:

```powershell
dotnet build tests/ClassicWindowsIptvPlayer.FeedbackUiChecks/FeedbackUiChecks.csproj -c Release --artifacts-path C:/Projects/cyrus-feedback-build -p:NuGetAudit=false -p:DefaultItemExcludesInProjectFolder=**/artifacts/**
$check = Start-Process C:/Projects/cyrus-feedback-build/bin/FeedbackUiChecks/release_win-x64/FeedbackUiChecks.exe -WindowStyle Hidden -Wait -PassThru
$check.ExitCode
```

Use a fresh dedicated artifact directory. The fixture writes a prior abnormal-session marker only under that build's executable-relative logs directory and sets a fresh isolated data-root override before constructing App. It creates an invented HTTPS endpoint bootstrap with CreateNew, invokes the real bootstrap import, and verifies protected settings plus removal of the plaintext fixture. It drives the actual startup crash-offer path with Later and Don't send, checking retained/dismissed protected queue state.

The production modal FeedbackWindow and CrashReportWindow are shown and their routed button actions are exercised. Send/retry callbacks use the production FeedbackOutbox with an injected in-memory HTTP handler: no real endpoint or provider is contacted. Checks cover explicit consent, empty-message validation, default/unchecked log attachment, redaction and actual JSON payloads, failed delivery with retained data, successful explicit retry and queue removal, and optional crash messages. Minimum-size dark-mode and ordinary light-mode WPF bitmap captures verify action containment after long failure text. The fixture exposed clipped footer buttons, which were fixed by reserving a separate action row. It also routes the actual MainWindow Help > Feedback menu with an injected fixture outbox, verifies the real send callback, and catches loss of an excluded-log choice on reopening. Reopening now restores that saved choice; new feedback remains checked by default.

Evidence and PNGs are retained under the printed cyrus-feedback-ui directory. The final 3 October 2026 run passed 54 assertions with exit 0. These routed-event and WPF bitmap checks do not establish physical mouse/keyboard input, Narrator, monitor DPI changes, real webhook delivery, or a separate-process abnormal-exit/reopen flow. The startup fixture seeds an interrupted-session marker before constructing the real App and calls its actual crash-offer method; it does not crash a normal user process. The endpoint bootstrap and queue checks use invented settings only.
