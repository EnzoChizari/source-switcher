param(
    [string]$OutputDirectory,
    [string]$Dotnet = 'dotnet',
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [switch]$IncludeLegacyBrowserTools,
    [switch]$FriendDistribution,
    [switch]$IncludeHelium
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($FriendDistribution -and ($FrameworkDependent -or $Runtime -ne 'win-x64' -or $IncludeLegacyBrowserTools)) {
    throw 'The shareable distribution requires self-contained win-x64. Use -IncludeHelium for the current extension.'
}
$project = Join-Path $repo 'systemwide\WindowsSourceSwitcher.Tray\WindowsSourceSwitcher.Tray.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo (('dist\SourceSwitcher-' +  + '-') + $Runtime + $(if ($FrameworkDependent) { '-framework-dependent' })) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output folder. Mixing old runtime files can break the app.' }
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$legacyBrowser = if ($IncludeLegacyBrowserTools) { 'true' } else { 'false' }
$friend = if ($FriendDistribution) { 'true' } else { 'false' }
$releaseProperties = if ($FriendDistribution) { @('-p:DebugType=none', '-p:DebugSymbols=false', '-p:IncludeSourceRevisionInInformationalVersion=false') } else { @() }
& $Dotnet publish (Join-Path $repo 'systemwide\WindowsSourceSwitcher.Tray\WindowsSourceSwitcher.Tray.csproj') -c Release -r $Runtime --self-contained $selfContained -p:PublishReadyToRun=false -p:IncludeLegacyBrowserTools=$legacyBrowser -p:FriendDistribution=$friend @releaseProperties -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tray publish failed.' }
$launcher = Join-Path $OutputDirectory 'WindowsSourceSwitcher.Tray.exe'
if ($FriendDistribution) {
    $portableLauncher = Join-Path $OutputDirectory 'SourceSwitcher.exe'
    Move-Item -LiteralPath $launcher -Destination $portableLauncher
    $launcher = $portableLauncher
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'source-switcher-portable.txt'), [string]$version)
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $repo 'FRIEND_START_HERE.txt') -Destination (Join-Path $OutputDirectory 'START HERE.txt')
    & (Join-Path $PSScriptRoot 'Test-PortablePayload.ps1') -Directory $OutputDirectory
}
# Separate runtime closures: the trace library and the metadata helper may use different assembly versions.
if ($IncludeLegacyBrowserTools -or $IncludeHelium) {
    & $Dotnet publish (Join-Path $repo 'systemwide\WindowsSourceSwitcher.BrowserHost\WindowsSourceSwitcher.BrowserHost.csproj') -c Release -r $Runtime --self-contained $selfContained @releaseProperties -o (Join-Path $OutputDirectory 'browser-helper') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Browser helper publish failed.' }
}
if ($IncludeHelium) {
    $extension = Join-Path $OutputDirectory 'browser\chromium'
    New-Item -ItemType Directory -Path $extension -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repo 'browser\chromium') -File |
        Where-Object { $_.Extension -in '.js', '.json', '.html', '.css', '.md' } |
        Copy-Item -Destination $extension
}
Write-Output ('Run ' + $launcher)



