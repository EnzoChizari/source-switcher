param([string]$BuildDirectory, [ValidateSet('Release','Debug')][string]$Configuration='Release')
$ErrorActionPreference = 'Stop'
if (!$BuildDirectory) {
    $BuildDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../work/apple-probe-build'))
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools discovery is unavailable.' }
$vsRoot = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsRoot) { throw 'The x64 MSVC toolchain is unavailable.' }
& (Join-Path $vsRoot 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$cmake = Join-Path $vsRoot 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
$ctest = Join-Path $vsRoot 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/ctest.exe'
& $cmake -S $PSScriptRoot -B $BuildDirectory -A x64
if ($LASTEXITCODE -ne 0) { throw 'Native probe configuration failed.' }
& $cmake --build $BuildDirectory --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw 'Native probe build failed.' }
& $ctest --test-dir $BuildDirectory -C $Configuration --output-on-failure
if ($LASTEXITCODE -ne 0) { throw 'Native probe fixture tests failed.' }
Write-Output "Native diagnostic output: $BuildDirectory/$Configuration"
Write-Output 'No Apple process was attached by the build or fixture tests.'
