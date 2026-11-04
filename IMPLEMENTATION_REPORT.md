Current patch **0.10.3**: short-window navigation stays reachable with adaptive spacing, a hidden footer when constrained, and keyboard/focus scrolling. Reliability checks cover actual native session creation/closure and already-playing subscription; resource observations retain their measured limits.

# DX5 Source Switcher 0.10.3 implementation report

The Windows feature, GUI and measured optimization pass is implemented on the canonical WindowsLosslessSwitcher fork. The original GPL-3.0-or-later license, upstream attribution and selected Core Audio/Apple metadata services remain. The separate tray host does not start the upstream Apple-only executable.

## Resulting behavior

Sources enumerates the selected output's current Windows audio sessions and remembers a master by executable path. Shared-mode format selection follows fresh observed application PCM where available, a manual override, or an explicitly labeled Apple Music catalog estimate. Pauses preserve the last confirmed rate. Restarted processes must establish fresh identities and observations. Unknown evidence holds rather than inventing a rate. ShareX is excluded.

Optional automatic source priority is fullscreen → favorites → pinned → regular. All ordinary automatic destinations, including Apple Music, use 500 ms. Release suspends automatic following. Fullscreen/manual timing and the existing format-write recovery grace are separate policies. Windows media-session playing/paused evidence is stronger than generic stream activity.

Mixer exposes native session volume/mute and recent signal peaks. Native identities include endpoint, executable, PID/start time and session instance. Shared/protected/unverifiable ownership fails read-only. Group peaks use the largest complete valid sample; unavailable readings are not silence. The 50 ms visible meter period is preserved and metering stops when hidden. New orange thin-track/round-thumb drawing inherits native trackbar input and accessibility; painting never writes volume.

## Reliability changes

The popup's owned HWND receives power suspend/resume notifications even while hidden. The host invalidates pending epochs, observations and queued mixer writes during suspension; resume serially rebuilds device/session notification bindings and requests fresh capabilities/formats/sessions. Healthy ETW tracing is retained and existing streams are reread. Duplicated automatic/user resume notifications do not cause double recovery. Preferences, pinned endpoint, master and held rate persist. Diagnostics distinguishes power recovery from device availability.

New work is blocked during suspension, and worker dispatch checks its captured epoch before touching native mixer state. An already-entered native call cannot be retroactively cancelled. Existing missing-device holding, no automatic DAC fallback, callback coalescing, bounded event history and repeated-format-write safeguards remain.

## GUI and resource work

Native WinForms sections now share a compact left navigation rail, clear headings and rounded charcoal cards inspired by the owner's SCSkiller reference. Controls/settings are grouped; Sources retains its process/evidence list; Diagnostics keeps read-only format/history details. Ctrl+Tab changes sections and Close/Escape hides the popup. A normal launch opens the app, while `--background` and the installed sign-in task start hidden. Duplicate background launches exit quietly.

The repeated meter-row session comparison avoids LINQ allocations. The sampler reconciles native leases once per fresh census and binds direct references for each sample, retaining full identity validation, current peak reads, generation suppression and single-MTA ownership. The source-list width handler also avoids redundant native column writes. No forced collection, working-set trimming, reduced meter precision or new audio engine was introduced. Standard IL publishing is retained after the local ReadyToRun comparison showed no consistent advantage.

## Evidence, delivery and limits

The [current validation report](research/VALIDATION_0.10.3_2026-11-02.md) records408core/754tray tests, six native session lifecycles, the constrained layout reproduction/fix and resource evidence. Earlier actual48/96/192kHz tests retain their dated scope. The [changed-file manifest](research/CHANGED_FILES_0.10.3.txt) compares with delivered0.10.2.

Use [portable instructions](PORTABLE_README.md) and [build/publish steps](systemwide/README.md). The complete portable folder, framework-dependent alternative, GPL corresponding source and artifact hashes accompany the release. The requested installation folder keeps its old `0.8.0` name with the new version inside and the existing sign-in task updated to `--background`.

Apple track-rate estimation remains parked and known mismatches are not fixed. The prior live-stream format guard/manual controls remain; no new player transport, popup reader, injection or exclusive playback path is added. Browser/web integration, application/multi-DAC profiles, Rust and PEQ are excluded. No OS DLL or TOPPING driver is modified. Shared-mode interruptions, original-media-rate verification, physical USB/sleep/listening checks, future sign-in, every display/DPI configuration and unlimited soak remain practical limits, as detailed in the measured report.
