param([Parameter(Mandatory)][string]$Directory)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
$application = 'WindowsSourceSwitcher.Tray'
$config = Get-Content -LiteralPath (Join-Path $root "$application.runtimeconfig.json") -Raw | ConvertFrom-Json
$includedFrameworks = @($config.runtimeOptions.includedFrameworks | ForEach-Object Name)
foreach ($framework in 'Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') {
    if ($includedFrameworks -notcontains $framework) { throw "Missing bundled framework: $framework" }
}

$deps = Get-Content -LiteralPath (Join-Path $root "$application.deps.json") -Raw | ConvertFrom-Json -AsHashtable
$target = $deps.runtimeTarget.name
if ($target -notlike '*/win-x64') { throw "Unexpected runtime target: $target" }
$assetCount = 0
foreach ($library in $deps.targets[$target].Values) {
    foreach ($kind in 'runtime','native','resources','runtimeTargets') {
        if (-not $library.Contains($kind)) { continue }
        foreach ($entry in $library[$kind].GetEnumerator()) {
            if ($kind -eq 'runtimeTargets' -and $entry.Value.rid -and $entry.Value.rid -ne 'win-x64') { continue }
            $asset = $entry.Key
            $name = Split-Path $asset -Leaf
            $paths = @((Join-Path $root $asset))
            if ($kind -eq 'resources') {
                $paths += Join-Path $root (Join-Path $entry.Value.locale $name)
            } else {
                $paths += Join-Path $root $name
            }
            if (-not ($paths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })) {
                throw "Missing runtime asset: $asset"
            }
            $assetCount++
        }
    }
}

$nativeFiles = @('SourceSwitcher.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'vcruntime140_cor3.dll', 'amd64/KernelTraceControl.dll', 'amd64/msdia140.dll')
foreach ($name in $nativeFiles) {
    $path = Join-Path $root $name
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($path))
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid executable: $name" }
        $reader.BaseStream.Position = 0x3c
        $reader.BaseStream.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "Expected an x64 executable or library: $name"
        }
    } finally { $reader.Dispose() }
}
Write-Output "Portable x64 payload verified: $assetCount manifest assets and $($nativeFiles.Count) native binaries."
