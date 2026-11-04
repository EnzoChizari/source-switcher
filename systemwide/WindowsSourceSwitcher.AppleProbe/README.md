# Experimental Apple Music source-format probe

This is a **bounded diagnostic experiment**, separate from the tray app. It observes encoded ALAC/QLAC source descriptors and ALAC configuration metadata before the Windows submitted-PCM stage. It does not switch a sample rate, initialize a renderer, capture PCM, force quality, or claim that an observed converter belongs to the currently audible track. Prefetched tracks can decode ahead. Every output record explicitly says `currentTrackVerified:false`.

The source audit is [APPLE_MUSIC_SOURCE_RATE_FEASIBILITY_2026-10-21.md](../../research/APPLE_MUSIC_SOURCE_RATE_FEASIBILITY_2026-10-21.md). Attribution and licenses are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Build and fixture tests

Use Windows x64, VS2022 C++ Build Tools, the Windows SDK and bundled CMake ≥3.24:

```powershell
& .\systemwide\WindowsSourceSwitcher.AppleProbe\Build-Probe.ps1
```

The default output is the workspace `work/apple-probe-build/Release` directory, separate from .NET outputs. An explicit `-BuildDirectory` is supported. The build has no network fetch, installation, scheduled task, browser change or Apple process attachment.

On 2026-10-22, all four CTest groups passed: 104 source parser checks, 337 converter lifecycle/loss/freshness checks, 62 native hook ABI fixture checks and the loader's unrelated-process rejection. The real detour fixture operates only on our `FixtureCoreAudioToolbox.dll` inside its own test process. Opaque invalid PCM pointers prove that the observer preserves, rather than reads/replaces, the original callback/data arguments. Fixture-only exports exist only in the distinct `dx5-apple-source-probe-fixture.dll`; the production DLL has no exported fixture API.

Only `dx5-apple-source-probe.exe` and `dx5-apple-source-probe.dll`, together with these notices, are relevant to a reviewed diagnostic run. Do not distribute the fixture modules as the production probe.

## Read-only target check

The loader accepts only the official `AMPLibraryAgent.exe` process associated with package `AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa`, with matching Windows user/session and an unchanged process lifetime. It rejects ordinary processes, AppleMusic.exe itself, other package versions and an agent already carrying this probe or an AMExclusive hook. It does not request elevation.

Before deciding to attach, inspect the chosen PID without changing it:

```powershell
& 'C:\absolute\work\apple-probe-build\Release\dx5-apple-source-probe.exe' --inspect-pid 12345
```

Replace the example PID and path with the actual inspected helper and build directory. There is no automatic target selection.

## Exact opt-in mutation scope

An explicit attachment requires all three arguments and a new absolute output file:

```powershell
& 'C:\absolute\work\apple-probe-build\Release\dx5-apple-source-probe.exe' --pid 12345 --seconds 10 --output 'C:\absolute\work\apple-source-probe-new.jsonl'
```

This command is **not run by builds/tests or tray startup**. Before an actual Apple attachment, review the following concrete changes:

1. The loader creates a current-user-only diagnostic memory mapping with a medium integrity label and a new local JSONL file. The mapping carries target/loader PID and start time, a random session nonce and a hard deadline. Maximum duration is 30 seconds; output is capped at one MiB. No audio samples, track titles, content identifiers or credentials are recorded or sent externally.
2. It allocates one bounded DLL-path buffer in that exact process and uses a one-time remote `LoadLibraryW` thread. The Windows loader address is resolved against the actual owning system module at its target-process base; no Windows DLL is overwritten. The path buffer is freed after the load completes. A timed-out loader thread is never terminated; that bounded path allocation may remain until process exit.
3. The injected DLL patches seven **Apple CoreAudioToolbox** exported entry points in that process's memory using MinHook: AudioConverter New, NewSpecific, SetProperty, GetProperty, FillComplexBuffer, Reset and Dispose. It leaves their original parameters, callback/data pointers and return codes intact. It does not patch Windows CoCreateInstance or any other Windows/driver function, change Apple/Windows/driver files, write a registry/startup entry, or install a component.
4. Callbacks copy only bounded descriptors/configuration metadata to a 128-slot table and drop observations on contention. There is no callback IPC, allocation, blocking lock or PCM processing. On successful Fill, a bounded periodic query can ask Apple's original GetProperty for its source/output description; the query's internal locking/timing is undocumented and remains an experimental risk. Failed queries have a 250 ms retry backoff and cannot keep stale metadata valid.

MinHook temporarily coordinates native threads while installing detours. A successful owned fixture does not prove that Apple playback will be uninterrupted. Package/driver compatibility, actual pre-resampling source reporting and track association must be checked separately on an explicitly chosen live run.

## Stop and unload

At the deadline or when the loader exits, observations stop, the worker exits and diagnostic handles/views close. The DLL and its **pass-through detours/trampolines remain resident until that exact AMPLibraryAgent process exits**. They are deliberately not freed while an Apple callback could still return through them. Stopping the loader alone is not a full DLL unload; the loader rejects another probe in the same agent lifetime.

To fully restore the process, close Apple Music and ensure the exact recorded helper process exits, then start Apple Music normally. The probe creates no persistence, so the new process loads without it. The probe does not itself kill/restart Apple Music, alter playback position or run a remote FreeLibrary. Any playback restart and position restoration belong to the person conducting the diagnostic.

## Evidence semantics and remaining limits

`sourceRateHz` comes only from a recognized encoded ALAC/QLAC input ASBD, corroborated against a cookie when available. `decodedRateHz` is separate. PCM-only converters are unknown for source-native purposes. Cookie/source disagreement, malformed metadata, reset/dispose, pointer reuse, dropped lifetime observations, table/report overflow and stale activity/metadata suppress an aggregate source candidate. A loss epoch requires fresh source/activity evidence again; table capacity exhaustion remains ambiguous for the bounded run.

An unambiguous recent source rate is still a **source-stream candidate**, never verified current-track evidence. Active Fill calls can decode a future item, quality can change during playback, and multiple converters can coexist. No integration with automatic tray format changes is enabled by this experiment. Native 44.1/48/96/192 kHz live Apple reporting, lower-quality switches, prefetch and pause/restart behavior remain to be validated.

Protocol version 2 adds a `raw` diagnostic object: counts for all seven observed APIs, nonempty/empty/error Fill totals, and metadata-refresh attempts/failures. The last native Fill/getter status is `null` until an actual call supplies one; these are Apple OSStatus values, not Windows HRESULTs. Thus no API calls, empty successful calls and native failures can be distinguished even when no valid source evidence exists. Counters stop with observations and do not affect source confidence.

The first root-controlled 15-second live run of protocol version 1 reached Observing and Stopped without a worker error, but produced **no source evidence** (74 records; revision and active converter count remained zero). The later Apple UI loading problem required a process restart; causation has not been established, and hook setup alone is not a stability or reporting success. Version 2's raw counters are locally fixture-tested and built separately in `work/apple-probe-build-v2`; they have not established live Apple source-format reporting.
