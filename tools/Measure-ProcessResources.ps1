param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ReportPath,
    [ValidateRange(10,3600)][int]$Seconds = 120,
    [ValidateRange(250,10000)][int]$IntervalMs = 1000,
    [string]$ModeLabel = 'unchanged live app'
)
$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
$destination = [IO.Path]::GetFullPath($ReportPath)
if (Test-Path -LiteralPath $destination) { throw 'Use a new report path to preserve earlier evidence.' }
$candidates = @(Get-Process | Where-Object { $_.Path -eq $resolvedExecutable })
if ($candidates.Count -ne 1) { throw 'Start exactly one instance from the supplied executable path.' }
$measuredApp = $candidates[0]
$started = $measuredApp.StartTime.ToUniversalTime()
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Dx5ProcessGuiResources {
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
}
'@
$samples = [Collections.Generic.List[object]]::new()
$watch = [Diagnostics.Stopwatch]::StartNew()
$failure = $null
try {
    do {
        $measuredApp.Refresh()
        if ($measuredApp.HasExited -or $measuredApp.StartTime.ToUniversalTime() -ne $started) { throw 'Measured process exited or changed.' }
        $samples.Add([pscustomobject]@{
            ElapsedMs = $watch.Elapsed.TotalMilliseconds
            CpuMs = $measuredApp.TotalProcessorTime.TotalMilliseconds
            WorkingSetBytes = $measuredApp.WorkingSet64
            PrivateCommittedBytes = $measuredApp.PrivateMemorySize64
            Handles = $measuredApp.HandleCount
            Threads = $measuredApp.Threads.Count
            GdiObjects = [Dx5ProcessGuiResources]::GetGuiResources($measuredApp.Handle, 0)
            UserObjects = [Dx5ProcessGuiResources]::GetGuiResources($measuredApp.Handle, 1)
        })
        $remaining = $Seconds * 1000 - $watch.Elapsed.TotalMilliseconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Min($IntervalMs, [Math]::Ceiling($remaining))) }
    } while ($remaining -gt 0)
} catch { $failure = $_.Exception.Message }
finally {
    $elapsed = 0.0
    $cpu = 0.0
    if ($samples.Count -gt 0) {
        $elapsed = $samples[-1].ElapsedMs - $samples[0].ElapsedMs
        $cpu = $samples[-1].CpuMs - $samples[0].CpuMs
    }
    $summary = [ordered]@{}
    foreach ($field in @('WorkingSetBytes','PrivateCommittedBytes','Handles','Threads','GdiObjects','UserObjects')) {
        if ($samples.Count -gt 0) {
            $values = $samples | Measure-Object -Property $field -Minimum -Maximum -Average
            $summary[$field] = @{ First=$samples[0].$field; Last=$samples[-1].$field; Minimum=$values.Minimum; Maximum=$values.Maximum; Average=$values.Average }
        }
    }
    [void][IO.Directory]::CreateDirectory((Split-Path $destination))
    [pscustomobject]@{
        ModeLabel=$ModeLabel; Executable=$resolvedExecutable; ProcessId=$measuredApp.Id
        ProcessStartedUtc=$started.ToString('o'); SecondsRequested=$Seconds; ElapsedMs=$elapsed
        LogicalProcessors=[Environment]::ProcessorCount
        CpuPercentOneLogicalCore=$(if ($elapsed -gt 0) {100 * $cpu / $elapsed} else {$null})
        CpuPercentWholeMachine=$(if ($elapsed -gt 0) {100 * $cpu / $elapsed / [Environment]::ProcessorCount} else {$null})
        Failure=$failure; Summary=$summary; Samples=$samples
        Method='Read-only process counters; no UI, playback, setting, priority, GC or working-set changes. Private committed bytes are not private working set. Background app work continues.'
        Limits='One bounded local sample. Stable counters do not establish leak freedom, a memory ceiling, physical audibility, latency or source-rate correctness.'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $destination -Encoding utf8NoBOM
    $measuredApp.Dispose()
}
if ($failure) { throw $failure }
Write-Output ('Resource report: ' + $destination)
