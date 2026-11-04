Current patch **0.10.3**: short-window navigation stays reachable with adaptive spacing, a hidden footer when constrained, and keyboard/focus scrolling. Reliability checks cover actual native session creation/closure and already-playing subscription; resource observations retain their measured limits.

# DX5 Source Switcher 0.10.3

A Windows shared-mode audio controller with Sources, Mixer, Controls and Diagnostics. The dark sidebar and card layout follows the owner's SCSkiller reference; volume sliders use a thin orange track and round orange thumb.

## Run

1. Exit older DX5 instances. Extract the complete portable folder and run `WindowsSourceSwitcher.Tray.exe`. The self-contained build includes .NET. The framework-dependent build requires .NET 8 Desktop Runtime x64. Automatic WASAPI tracing requires administrator elevation or suitable ETW rights.
2. Left-click the monochrome tray icon once. In Sources, choose the TOPPING output and the application that controls its rate. Changes save under `%APPDATA%/DX5SourceSwitcher`.
3. Choose Auto or a saved per-app manual rate. Unknown, stale, conflicting or unsupported observations hold the last confirmed output. Refresh/retry refreshes already-open streams while retaining a healthy continuous observer. ShareX is excluded.
4. Use the star and Pin beside the master to save separate favorites and pins. Closed apps remain selectable without being launched. Ordinary restarts preserve their executable identity; moving/reinstalling apps can require reselection.

The header ×, Escape and native Close hide the popup. The sidebar changes pages; Ctrl+Tab and Ctrl+Shift+Tab cycle them. Narrow windows use an icon rail, and constrained screens retain scroll access. Right-click the tray icon for Open, Mixer or Exit.

## Automatic source priority

Controls → Auto-switch by source priority uses fullscreen audio (if separately enabled), then playing favorites, playing pins and regular applications. Higher tiers can preempt lower tiers; a playing peer keeps the master. All ordinary automatic handoffs, including entering and leaving Apple Music, require 0.5 seconds of continuously confirmed eligibility. Fullscreen takeover and manual selection retain their existing timing. Release stops automatic following. Unknown playback holds conservatively. Apps without media-session evidence use weaker Windows stream activity, which may persist while paused and does not prove audibility. A successful format write retains the existing recovery grace.

Global shortcuts use Ctrl+Alt+Shift: O Sources, M Mixer, Left/Right active favorites, R Release and F fullscreen priority. Enable them in Controls; conflicts are shown and can be retried.

## Mixer and evidence

Volume and mute affect the selected output's currently identified sessions. Protected, cross-process shared and unverifiable groups remain read-only; mixed settings are labeled. Sliders preserve native keyboard and accessibility behavior. Painted styling never sends a volume write. Meters retain a 50 ms target period while Mixer is visible and stop when hidden. Peaks describe Windows signal activity; muted streams can still register. They are not physical DAC/headphone or post-mute loudness measurements.

A stream rate is PCM submitted to Windows; an app may already have resampled its original media. Browser tabs remain grouped by application identity. There is no browser extension receiver.

Apple Music main/helper executables form one logical source. Its optional catalog value remains an **estimate**, and known track-rate mismatches remain unresolved. This work is parked at the owner's request. The existing live-stream switch guard remains: no new Apple popup, pause/play, seek or restart automation is added. Player silence or delayed recovery after a shared format change remains possible; the 0.5-second source handoff delay is separate from player recovery time.

## Recovery and startup

Power suspension invalidates pending rate decisions and blocks new controller work. Resume requests a fresh device/format/session census, recreates notification bindings and rereads already-open streams while retaining healthy tracing. Endpoint, source, held rate and preferences persist. An already-entered native Windows call cannot be undone by cancellation. Diagnostics reports power recovery separately from output availability. The pinned output is retained across absence; no backup DAC is selected automatically. History is bounded and repeated format writes are throttled.

Use --background in the launcher shortcut to start in the tray. Repeated background launches exit quietly while an instance is running. To start at sign-in, point a Windows startup shortcut at the portable launcher with that argument. Exit stops the app until the next manual launch or sign-in.

## Validation and limits

See [the measured report](research/VALIDATION_0.10.3_2026-11-02.md), [changed files](research/CHANGED_FILES_0.10.3.txt) and [build steps](systemwide/README.md). GPL license, notices and corresponding source accompany redistributions.

Software tests, injected power/device notifications, process counters and session peaks do not prove physical listening, real USB unplug/replug, every display/DPI configuration, a future sign-in or unbounded leak freedom. Shared format changes can interrupt other apps; their own stream recovery still matters. Exclusive mode, ASIO, native DSD and bit-perfect output are not guaranteed. Profiles, PEQ, Rust and browser/web integration remain excluded. No OS DLLs, TOPPING drivers or custom audio engine are modified or installed.
