# Install the verified private release in the dedicated folder configured in local Git.
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ScratchDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($Version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Specify the published three-part version.' }
$configured = git -C $repo config --local --get sourceswitcher.installPath
if ($LASTEXITCODE -ne 0 -or ![IO.Path]::IsPathFullyQualified($configured)) { throw 'Configure the dedicated Source Switcher install folder in local Git first.' }
$install = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($configured))
$existing = Get-Item -LiteralPath $install
if (!$existing.PSIsContainer -or $existing.Name -notlike 'SourceSwitcher-*' -or $existing.FullName -ne $install -or
    !(Test-Path -LiteralPath (Join-Path $install 'WindowsSourceSwitcher.Tray.runtimeconfig.json') -PathType Leaf)) {
    throw 'The configured folder is not an existing Source Switcher portable install.'
}
for ($directory = $existing; $directory; $directory = $directory.Parent) {
    if ($directory.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'The install path must not go through a link.' }
}
if (Get-ChildItem -LiteralPath $install -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) {
    throw 'The install folder contains a link.'
}
$scratchRoot = (Resolve-Path -LiteralPath $ScratchDirectory).Path
if ($scratchRoot -eq $install -or $scratchRoot.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose scratch space outside the install.'
}
$scratch = New-Item -ItemType Directory -Path (Join-Path $scratchRoot ("install-$Version-" + [guid]::NewGuid().ToString('N')))
$launcher = Join-Path $install 'SourceSwitcher.exe'
if (!(Test-Path -LiteralPath $launcher -PathType Leaf)) { throw 'The installed launcher is missing.' }
$helper = Join-Path $install 'browser-helper/WindowsSourceSwitcher.BrowserHost.exe'
$restoreHelium = Test-Path -LiteralPath (Join-Path $install 'browser-helper/local.dx5_source_switcher.chromium.json') -PathType Leaf

$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'Never'
$slug = 'EnzoChizari/source-switcher'
$record = "protocol=https`nhost=github.com`npath=$slug.git`n`n" | git credential fill
if ($LASTEXITCODE -ne 0) { throw 'GitHub sign-in is unavailable.' }
$token = ($record | Where-Object { $_.StartsWith('password=') }) -replace '^password=', ''
if (!$token) { throw 'GitHub sign-in is unavailable.' }
$api = "https://api.github.com/repos/$slug"
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json' }
$private = Invoke-RestMethod $api -Headers $headers -TimeoutSec 30
if (!$private.private -or $private.full_name -cne $slug) { throw 'GitHub did not confirm repository privacy.' }
$release = Invoke-RestMethod "$api/releases/latest" -Headers $headers -TimeoutSec 30
if ($release.tag_name -cne "v$Version" -or $release.draft -or $release.prerelease) { throw 'The requested version is not the latest stable release.' }
$assetName = "SourceSwitcher-v$Version-win-x64.zip"
$asset = @($release.assets | Where-Object { $_.name -ceq $assetName -and $_.state -eq 'uploaded' })
if ($asset.Count -eq 0) {
    $asset = @($release.assets | Where-Object { $_.name -ceq 'SourceSwitcher-win-x64.zip' -and $_.state -eq 'uploaded' })
}
if ($asset.Count -ne 1 -or $asset[0].size -le 0 -or $asset[0].size -gt 512MB -or $asset[0].digest -cnotmatch '^sha256:[0-9a-f]{64}$') {
    throw 'The verified Windows package is missing.'
}
$asset = $asset[0]
$zip = Join-Path $scratch.FullName $asset.name
$headers.Accept = 'application/octet-stream'
Invoke-WebRequest "$api/releases/assets/$($asset.id)" -Headers $headers -OutFile $zip -TimeoutSec 300
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Item -LiteralPath $zip).Length -ne $asset.size -or "sha256:$hash" -cne $asset.digest) { throw 'The download failed verification.' }
$unpacked = Join-Path $scratch.FullName 'extracted'
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $unpacked)
$payload = Join-Path $unpacked "SourceSwitcher-$Version-win-x64"
$payload = (Get-Item -LiteralPath $payload).FullName
if (!$payload.StartsWith($unpacked + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Get-Content -LiteralPath (Join-Path $payload 'source-switcher-portable.txt') -Raw) -cne $Version -or
    [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'WindowsSourceSwitcher.Tray.dll')).Version.ToString(3) -cne $Version) {
    throw 'The extracted package version does not match the release.'
}
foreach ($required in 'SourceSwitcher.exe','coreclr.dll','WindowsSourceSwitcher.Tray.runtimeconfig.json') {
    if (!(Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) { throw "The package lacks $required." }
}

$processes = Get-Process SourceSwitcher,WindowsSourceSwitcher.BrowserHost -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -in @($launcher, $helper) }
foreach ($process in $processes) {
    if ($process.CloseMainWindow()) { [void]$process.WaitForExit(1500) }
    if (!$process.HasExited) {
        Stop-Process -InputObject $process -Force
        if (!$process.WaitForExit(10000)) { throw 'The running copy did not exit.' }
    }
}
Remove-Item -LiteralPath $install -Recurse -Force
Move-Item -LiteralPath $payload -Destination $install

$registrationError = $false
if ($restoreHelium -and (Test-Path -LiteralPath $helper -PathType Leaf)) {
    $report = Join-Path $scratch.FullName 'registration.json'
    $registration = Start-Process -FilePath $launcher -ArgumentList "--register-browser-helper `"$report`"" -WindowStyle Hidden -Wait -PassThru
    $registrationError = $registration.ExitCode -ne 0
}
$app = Start-Process -FilePath $launcher -ArgumentList '--background' -WorkingDirectory $install -WindowStyle Hidden -PassThru
if ($app.WaitForExit(2500)) { throw 'Source Switcher exited after startup.' }
if ($registrationError) { Write-Warning 'Register the Helium browser helper again from Settings.' }
[pscustomobject]@{ Version = "v$Version"; ProcessId = $app.Id; InstallPath = $install; Sha256 = $hash }
