Current patch **0.10.3**: short-window navigation stays reachable with adaptive spacing, a hidden footer when constrained, and keyboard/focus scrolling. Reliability checks cover actual native session creation/closure and already-playing subscription; resource observations retain their measured limits.

Current behavior and limitations: [run instructions](../PORTABLE_README.md) and [0.10.3 validation](../research/VALIDATION_0.10.3_2026-11-02.md). Power-resume recovery and the sidebar/card GUI are included; the original source-priority semantics remain. All ordinary automatic handoffs, including Apple Music, wait 0.5 seconds. Fullscreen timing is unchanged. Apple Music rate-estimation work is parked; the existing live-stream guard and estimate/manual controls remain.

# Build and run DX5 Source Switcher 0.10.3

Requires Windows and .NET 8 SDK. Automatic PCM tracing needs administrator elevation or suitable Windows ETW rights. The submitted PCM format is distinct from original media rate. Current behavior and limitations: [portable instructions](../PORTABLE_README.md), [0.10.3 validation](../research/VALIDATION_0.10.3_2026-11-02.md).

From the repository root in PowerShell:

```powershell
dotnet restore .\systemwide\WindowsSourceSwitcher.Tray\WindowsSourceSwitcher.Tray.csproj
dotnet build .\systemwide\WindowsSourceSwitcher.Tray\WindowsSourceSwitcher.Tray.csproj -c Release --no-restore
dotnet test .\tests\WindowsSourceSwitcher.Core.Tests\WindowsSourceSwitcher.Core.Tests.csproj -c Release
dotnet test .\tests\WindowsSourceSwitcher.Tray.Tests\WindowsSourceSwitcher.Tray.Tests.csproj -c Release
dotnet test .\tests\WindowsLosslessSwitcher.Tests\WindowsLosslessSwitcher.Tests.csproj -c Release
dotnet run --project .\systemwide\WindowsSourceSwitcher.Tray\WindowsSourceSwitcher.Tray.csproj -c Release --no-build
```

The tray suite uses Windows Forms and native interfaces and must run on Windows. Historical browser/backend tests remain in source, but no extension/receiver is enabled in this release. Node.js and Python are not required by the running app. The original solution also contains the upstream Apple Music app; the separate tray host references its selected services through WindowsShared.

## Publish

Self-contained, including its runtime:

```powershell
pwsh -File .\tools\Publish-Systemwide.ps1 -OutputDirectory .\dist\DX5SourceSwitcher-0.10.3-win-x64
```

Smaller framework-dependent package, requiring .NET 8 Desktop Runtime x64:

```powershell
pwsh -File .\tools\Publish-Systemwide.ps1 -FrameworkDependent -OutputDirectory .\dist\DX5SourceSwitcher-0.10.3-win-x64-framework-dependent
```

Use a new destination folder; the script refuses mixed old/new runtime files. Add `-Dotnet <SDK-executable>` if dotnet is not on PATH. Start `WindowsSourceSwitcher.Tray.exe` from the complete published folder. x64 is the locally tested target; ARM64 requires separate hardware validation.

Publishing defaults to Windows-only, without browser extension/helper payload or ReadyToRun. `-IncludeLegacyBrowserTools` exists solely for historical development tooling and does not restore the withdrawn UI/receiver. Publishing does not register a helper, install a driver or enable login startup. Keep LICENSE, notices, docs and corresponding GPL source with redistributions.

## Read-only inspection and QA

`WindowsSourceSwitcher.Tray.exe --inspect <new-report.json>` reports endpoints/formats; `--mixer-inspect`, `--meter-read` and `--stream-read` expose their named read-only diagnostics. `--smoke-test <new-report.json>` renders example UI states to PNG without changing audio settings; example screenshots do not verify playback. Inspect mode output paths may contain local process paths and device identities, so review before sharing publicly.

`tools/Measure-ProcessResources.ps1 -Executable <absolute-exe> -ReportPath <new-report.json> -Seconds 120` samples one exact existing process; it does not force GC, trim the working set or change audio settings. Working set, private committed memory and managed heap are different quantities.

The current native priority QA uses owned temporary PCM clients and restores preferences, without changing the endpoint rate. Earlier WAV playback, format/peak/volume and resource measurements remain in their dated reports. Current receipts are under `research/0.10.3-validation/`; larger fixtures and harnesses remain in the development workspace. Physical listening and physical unplug/replug require separate acceptance.

Application/multi-DAC profiles, Rust migration, browser/web integration and device PEQ are out of scope. Architecture/history remains in [SYSTEMWIDE.md](../SYSTEMWIDE.md), with older browser descriptions explicitly historical.
