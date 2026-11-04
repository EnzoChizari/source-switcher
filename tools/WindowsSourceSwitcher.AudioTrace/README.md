# Audio stream rate research diagnostic

This standalone prototype records Windows audio **ETW metadata**, including event names, process IDs and decoded fields. It does not capture audio samples, modify endpoint formats, install drivers, inject code, elevate itself, or control playback in other applications. It is separate from the production tray app.

The Microsoft audio team enables the same Client and Streaming provider GUIDs in [its diagnostic profile](https://github.com/microsoft/audio/blob/main/CollectAudioLogs/audio-info-manual.wprp). [AudioClientInitialize](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/audio-measures) reports stream initialization. Any observed format is the application's submitted PCM format, which may already differ from native media content.

From the repository root with a .NET 8 SDK:

```powershell
dotnet restore tools/WindowsSourceSwitcher.AudioTrace/WindowsSourceSwitcher.AudioTrace.csproj --configfile NuGet.Config
dotnet build tools/WindowsSourceSwitcher.AudioTrace/WindowsSourceSwitcher.AudioTrace.csproj -c Release --no-restore
& ./tools/WindowsSourceSwitcher.AudioTrace/bin/Release/net8.0-windows10.0.19041.0/WindowsSourceSwitcher.AudioTrace.exe C:/path/to/new-audio-trace.jsonl 15 --self-test
```

The output path must not already exist; its directory must exist. Duration defaults to 15 seconds and is limited to 1–120 seconds (minimum 4 with `--self-test`). At 25,000 events the capture stops early. Only the Client provider is enabled; buffer-level Streaming and legacy providers were removed after the initial exploratory capture because of their volume. Binary payloads are omitted; strings and collections are bounded. This still records application/device metadata, so review before sharing.

Optional `--capture-state` sends the provider a standard ETW state request, to test whether it can enumerate existing streams. The provider must implement that behavior; a successful request alone does not mean existing formats were discovered.

`--self-test` initializes and plays only **silent** 44.1, 48 and 96 kHz shared streams on the default multimedia output, requesting Windows conversion. It logs requested formats and before/after mix-format reads. It never writes the device format. Without this switch, the diagnostic only observes naturally occurring events. Already-open streams may not emit an initialization event until they are reopened.

Controlling this ETW session normally requires an elevated terminal or Performance Log Users membership. The tool reports `access_denied` and exits 5 if unauthorized; it never requests elevation. It owns one stable named session and refuses to replace an existing session. Ctrl+C stops its own capture. Exit codes: 0 capture completed (check individual self-test records), 1 trace error, 5 access denied, 7 session already exists, 64 arguments, 74 output file error.

Dependencies are pinned: Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8 (MIT) and NAudio.Wasapi 2.2.1 (MIT). NuGet metadata was checked before selecting versions. This source is distributed under the repository's GPL-3.0-or-later license.
