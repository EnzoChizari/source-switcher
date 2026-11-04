Current patch **0.10.3**: short-window navigation stays reachable with adaptive spacing, a hidden footer when constrained, and keyboard/focus scrolling. Reliability checks cover actual native session creation/closure and already-playing subscription; resource observations retain their measured limits.

# DX5 Source Switcher 0.10.3 — architecture and audio evidence

This GPL-3.0-or-later extension uses WindowsLosslessSwitcher as its foundation while keeping a separate host. The original application lives in `src/`; the shared controller lives in `systemwide/WindowsSourceSwitcher.Tray`. Build from [systemwide/README.md](systemwide/README.md) and run `WindowsSourceSwitcher.Tray.exe`. Current usage and limits are in [PORTABLE_README.md](PORTABLE_README.md).

## Components

| Component | Responsibility |
| --- | --- |
| WindowsSourceSwitcher.Core | Persistent settings, source identities and deterministic rate/priority decisions without Windows audio writes. |
| WindowsSourceSwitcher.WindowsShared | Reuses selected upstream endpoint format/capability and Apple metadata services; retains their notices/license. |
| TrayContext | Serializes refreshes and policy application, owns device/session/playback/stream monitors, validates epochs before writes and bounds recovery work. |
| SourcePopup and section views | Native WinForms source selection, mixer, settings, diagnostics and tray lifecycle. Presentation never independently changes Windows audio. |
| Audio session enumeration/control | Resolves endpoint/session/process identities, reads peak/volume/mute and permits explicit writes only for verified current ownership. |
| WASAPI stream observer | Reads Windows audio ETW evidence, with bounded Service rundown to recover streams opened before the observer. Unsupported or ambiguous evidence remains unknown. |
| Power/device notifications | Invalidate obsolete decisions and refresh the selected endpoint's state while retaining preferences; no automatic switch to another DAC. |

## What a rate means

Windows audio-session enumeration does not expose every app's original media sample rate. `IAudioClient::GetMixFormat` describes the shared audio engine, and is never treated as proof of native track rate. This host observes per-app submitted PCM where the Windows trace schema and ownership evidence support it. The app may have already decoded/resampled before that point. Browser tabs can share processes and audio-session identities and cannot be reliably split here.

Evidence is labeled: observed **stream**, Apple catalog **estimate**, **manual**, held/paused or unknown. A catalog maximum is not a measurement of a delivered asset. Known Apple track-rate mismatches remain unresolved; work is parked. No browser adapter or extension receiver is enabled. A manual override can express a known target when original rate is not observable.

## Master selection and shared format

Manual selection persists by full executable path, survives ordinary app restarts and holds the last confirmed rate on pause or unknown data. Fresh process/session identity is required after restart. ShareX is excluded. Output format changes preserve bit depth and channel count where supported, validate exact capability, throttle repeated attempts and verify Windows readback. Errors are held/reported rather than manufacturing successful detection.

Optional source priority uses fullscreen → favorites → pinned → regular. All ordinary automatic destinations, including Apple Music, wait 500 ms. Fullscreen/manual timing remains unchanged. Higher tiers may preempt lower tiers; a playing peer retains priority. Unknown current playback holds conservatively. Release stops automatic following. Preferences and manual overrides do not automatically alter volume or launch an application.

Other shared-mode apps can continue mixing/resampling at the endpoint format, but a format change can invalidate their streams. Client recovery and audible gaps depend on Windows, the device and each player. The existing Apple live-stream switch guard is retained; no new player restart/pause/seek or exclusive-mode integration is added. Exclusive WASAPI, ASIO, native DSD and bit-perfect output are not promised.

## Mixer ownership, timing and lifetime

Volume/mute writes revalidate endpoint, executable, process start and session instance at dispatch. Protected, ambiguous or shared ownership fails read-only. Group controls label mixed volume/mute states; no automatic volume profiles exist. Meter leases are acquired/reconciled from a fresh census on one MTA thread. Direct per-source bindings avoid high-frequency identity hashing; every sample still checks identity and reads current peak. Stale generations, missing members and invalid peaks cannot become successful silence.

Meters target 50 ms while visible, stop/release when hidden and report Windows signal peaks rather than physical post-mute audibility. Coalesced notification work, bounded caches/history and hidden fallback polling limit background load. No forced GC or working-set trim is used. Measured resource conditions and the standard-IL versus ReadyToRun comparison are in [validation](research/VALIDATION_0.10.3_2026-11-02.md).

Suspension blocks new work and invalidates queued observations/decisions/writes. Resume refreshes native endpoint/session subscriptions, formats and capabilities; healthy tracing remains and already-open streams are reread. Duplicate resume events are handled. An in-flight native call cannot be undone. Missing pinned devices hold settings until recovery; no backup DAC/profile is chosen. Diagnostics reports availability, evidence, switch/recovery history and power recovery separately.

## GUI, startup and verification

The charcoal sidebar/card UI remains native WinForms. Orange sliders preserve native keyboard/range/UIA semantics. One left click opens/focuses the popup; Close/Escape hides it. `--background` starts only the tray icon and quietly exits if an instance exists. Publishing never registers startup. The owner-requested deployment separately updates a current-user Interactive/Highest sign-in task for tracing rights.

See [implementation report](IMPLEMENTATION_REPORT.md), [current roadmap](DEVELOPMENT_ROADMAP.md), [changed files](research/CHANGED_FILES_0.10.3.txt) and [measured tests](research/VALIDATION_0.10.3_2026-11-02.md). Physical listening, USB unplug/replug, actual PC sleep/resume, future sign-in, all DPI/display combinations and unbounded resource behavior remain separate from software fixture results. Browser/web integration, profiles, Rust and PEQ are excluded. No Windows OS DLL or TOPPING driver is replaced.

## Private releases and versioning

The portable Windows x64 build checks the private GitHub release feed once at launch. Settings > Updates allows another check or an explicit install. The app checks the asset size and GitHub SHA-256 digest, stages the complete bundle, then restarts from the same path. Use a dedicated folder for the extracted app. Keep Windows settings and any personal files outside that folder.

Use vX.Y.Z with a patch increment for fixes and a minor increment for feature releases. Set the new version in the tray project and commit the reviewed changes. In the canonical Windows checkout, the post-commit hook uploads commits from `codex/source-switcher` to private main. If a push fails, the commit stays local. Resolve the cause and run `./tools/git-hooks/Push-PrivateMain.ps1` to retry.

On each push to private main, GitHub Actions runs the core, tray, and Helium tests and builds the self-contained x64 app. When the version is newer, the workflow packages the exact tested source and publishes it to the private release feed. Changes using the current version still appear in source history. Check the workflow run for its result.

For a manual release fallback, use a fresh build and package directory. From PowerShell 7 on Windows with the .NET SDK, run:

```powershell
./tools/Publish-Systemwide.ps1 -FriendDistribution -IncludeHelium -OutputDirectory C:/release/SourceSwitcher-1.1.0-win-x64
./tools/Publish-PrivateRelease.ps1 -PortableDirectory C:/release/SourceSwitcher-1.1.0-win-x64 -PackageDirectory C:/release
```

The release script uses the Git Credential Manager sign-in, includes corresponding source from the pushed revision, and verifies the uploaded archive before it publishes the draft. See START HERE.txt for private update access on another PC.

## Bit depth and output capabilities

Settings > Rate detection includes **Follow source bit depth**, off by default. It uses valid bits from unambiguous integer WASAPI PCM or the width of a submitted 32-bit float stream. With Apple Music catalog estimates enabled, it follows the estimated bit depth paired with that track's estimated sample rate. Browser-only rate evidence and manual rate overrides retain the current depth. When enabled with usable evidence, the controller chooses the exact source depth if available, then the smallest supported higher depth, then the highest supported lower depth at the same sample rate and channel count. An unsupported rate leaves the format unchanged.

Settings > Output capabilities shows the highest PCM rate Windows reports for each depth at the output's current channel count. The probes cover standard rates from 8 to 768 kHz. Windows must still accept and confirm a shared-mode format change. These readings describe Windows and the driver, and cannot verify native track precision or bit-perfect DAC playback.
