# Resource targets and verification

Normal-use targets are approximately 100–150 MiB total process working set and 0–0.5% whole-machine CPU. These are measurement targets, not hard memory limits or guarantees on every PC. Updates, device probing and first-use player integrations can have higher transient costs.

The final v1.5.1 build uses .NET's supported `System.GC.ConserveMemory=5` setting. Detector deadlines, audio evidence checks and mixer cadence stay unchanged. It does not impose a hard heap cap, periodically force garbage collection, trim the Windows working set, change process priority, or reduce sample-rate checks to meet a display number. Microsoft's [runtime guidance](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#conserve-memory) explains the possible tradeoff of more collections and longer pauses; this setting was selected after functional, UI and live browser checks.

Changes:

- Reuse browser grants between observations; invalidate on permission events or explicit changes. In-flight evidence is discarded across a permission revision. Frame documents are still checked afresh.
- Check a player's native window before entering UI Automation, so absent/minimized players do not load its optional assemblies.
- Stop exact abandoned product traces only after checking their owner PID is dead twice. Skip reported live or inaccessible PIDs and unrelated sessions. New live traces have a process-generation identifier so PID reuse cannot give a new process an old trace name.
- Use a local shutdown signal for maintenance, and always attempt audio-trace disposal even if other shutdown cleanup fails.

Local evidence from Windows 11 x64, 16 logical processors:

| Check | Total working set | Whole-machine CPU | Scope |
| --- | --- | --- | --- |
| Healthy hidden controller after reclaiming 16 abandoned traces | 125–127 MiB | 0.020% average | 25-second live sample; v1.5.0 before code changes |
| v1.5.1 UI stress scenario | up to 121 MiB | 0.36% average | 25 seconds, real popup tabs refreshed at 20 Hz, UI Automation loaded, real read-only audio/media observers |
| Installed v1.5.1, hidden, initial default-GC candidate | 119–132 MiB | 0.024% average | 60-second process-counter sample after graceful restart and browser regression |
| Installed v1.5.1, active browser link, initial default-GC candidate | 134–152 MiB | 0.082% average | 50 seconds, 171 native requests, actual WAV/MSE fixtures and normal automatic source changes |
| Memory-conservation-5 candidate, active browser link | 119–138 MiB | 0.150% average | 50 seconds, actual WAV/MSE fixtures and normal automatic controller; about 138 MiB after an additional eight-stream probe |

These figures cover the controller process; browser/helper processes are separate. The initial hidden sample had a brief one-second CPU peak of 0.58%. Initial active-browser candidate peaks were 1.26% with default GC and 1.55% with memory conservation, mostly during startup/source changes. Averages do not establish a fixed CPU ceiling. The normal controller selected the browser and then returned to Discord; its automatic source policy was enabled. Process-counter sampling and the fixture do not explicitly write endpoint formats.

The UI stress scenario's p99 50 ms timer interval was 79 ms. This measures its UI event loop, not DAC interruption time or audible recovery. Two rounds of the production detector probe observed 44.1/48/96/192 kHz correctly; the probe submits silent shared PCM and does not change the output format.

An earlier 234 MiB sample had more optional components loaded and is not an identical before/after workload. Memory conservation saved only about 1 MiB in the UI-only scenario (p99 timer interval still about 79 ms), but the live browser and subsequent stream-burst workload retained less memory. A default-GC process reached about 168 MiB after the burst; the memory-conservation candidate stayed near 138 MiB. These bounded runs have different histories and do not establish a universal percentage reduction or a memory ceiling.

From the source checkout, use `tools/Measure-ProcessResources.ps1 -Executable <full-exe-path> -ReportPath <new-json-file> -Seconds 120` for a longer read-only check. Reports distinguish working set from private committed bytes. Resource results do not establish correctness by themselves: observer health, format detection, browser integration and shutdown/restart behavior must also pass.
