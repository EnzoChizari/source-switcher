# Requires PowerShell 7. Local publishing uses Git Credential Manager.
param(
    [Parameter(Mandatory)][string]$PortableDirectory,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [switch]$FromActions
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$slug = 'EnzoChizari/source-switcher'
$api = "https://api.github.com/repos/$slug"
$project = Join-Path $repo 'systemwide/WindowsSourceSwitcher.Tray/WindowsSourceSwitcher.Tray.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part application version.' }
$prefix = "SourceSwitcher-$version-win-x64"
$portable = (Resolve-Path -LiteralPath $PortableDirectory).Path
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
if ([IO.Path]::GetFileName($portable) -cne $prefix -or
    (Get-Content -LiteralPath (Join-Path $portable 'source-switcher-portable.txt') -Raw) -cne $version) {
    throw "Use the complete friend distribution in a folder named $prefix."
}
$packageName = "SourceSwitcher-v$version-win-x64.zip"
$package = Join-Path $packageRoot $packageName
$source = Join-Path $portable 'Corresponding-source.zip'
if ((Test-Path -LiteralPath $package) -or (Test-Path -LiteralPath $source)) { throw 'Choose a fresh package folder and portable build.' }

if ($FromActions) {
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REPOSITORY -cne $slug -or $env:GITHUB_REF -cne 'refs/heads/main') {
        throw 'Automatic publishing is restricted to the private repository main branch.'
    }
    $token = $env:GITHUB_TOKEN
    $commit = $env:GITHUB_SHA
} else {
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'Never'
    $record = "protocol=https`nhost=github.com`npath=$slug.git`n`n" | git credential fill
    if ($LASTEXITCODE -ne 0) { throw 'Sign in to GitHub using Git Credential Manager before publishing.' }
    $token = ($record | Where-Object { $_.StartsWith('password=') }) -replace '^password=', ''
}
if (!$token) { throw 'GitHub credentials are unavailable.' }
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
$repository = Invoke-RestMethod $api -Headers $headers -TimeoutSec 30
if (!$repository.private -or $repository.full_name -cne $slug) { throw 'The intended repository is unavailable or is no longer private.' }
if (!$FromActions) {
    $commit = (Invoke-RestMethod "$api/git/ref/heads/main" -Headers $headers -TimeoutSec 30).object.sha
}
if ($commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Missing GitHub source commit.' }
$tag = "v$version"
$latest = Invoke-RestMethod "$api/releases/latest" -Headers $headers -TimeoutSec 30
if ($latest.tag_name -ceq $tag) {
    Write-Output "$tag is already published. Source changes are on GitHub."
    return
}
if ($latest.tag_name -cnotmatch '^v\d+\.\d+\.\d+$' -or [version]$version -le [version]$latest.tag_name.Substring(1)) {
    throw "The release version must be newer than $($latest.tag_name)."
}
$changelog = Join-Path $repo 'SOURCE_SWITCHER_CHANGELOG.md'
$notes = [regex]::Match((Get-Content -LiteralPath $changelog -Raw),
    "(?ms)^## $([regex]::Escape($tag))\r?\n(?<notes>.*?)(?=^## |\z)").Groups['notes'].Value.Trim()
if (!$notes) { throw "Add release notes for $tag to SOURCE_SWITCHER_CHANGELOG.md." }
$paths = @(git -C $repo ls-tree -r --name-only $commit)
if ($LASTEXITCODE -ne 0) { throw 'The published commit is missing locally.' }
$changed = @(git -C $repo diff --name-only $commit -- $paths)
if ($LASTEXITCODE -ne 0 -or $changed) { throw 'Build from the source revision on private main.' }

git -C $repo archive --format=zip --prefix=SourceSwitcher-source/ "--output=$source" $commit
if ($LASTEXITCODE -ne 0) { throw 'Could not archive the corresponding source.' }
try {
    [IO.Compression.ZipFile]::CreateFromDirectory($portable, $package, [IO.Compression.CompressionLevel]::Optimal, $true)
} finally {
    Remove-Item -LiteralPath $source
}
$digest = 'sha256:' + (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$draft = @{ tag_name = $tag; target_commitish = $commit; name = "Source Switcher $tag"; draft = $true;
    prerelease = $false; body = $notes }
$releases = Invoke-RestMethod "$api/releases?per_page=100" -Headers $headers -TimeoutSec 30
$release = $releases | Where-Object { $_.tag_name -ceq $tag } | Select-Object -First 1
if ($release) {
    if (!$release.draft -or $release.target_commitish -cne $commit) {
        throw "An existing $tag release belongs to a different build. Review it before publishing."
    }
} else {
    $release = Invoke-RestMethod "$api/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body ($draft | ConvertTo-Json) -TimeoutSec 30
}
$assetNames = @($packageName)
# Transition releases remain discoverable by builds that know only the previous asset name.
if ($version -in '1.3.1', '1.3.2') { $assetNames += 'SourceSwitcher-win-x64.zip' }
$packageBytes = (Get-Item -LiteralPath $package).Length
foreach ($assetName in $assetNames) {
    $asset = $release.assets | Where-Object { $_.name -ceq $assetName } | Select-Object -First 1
    if ($asset -and ($asset.state -ne 'uploaded' -or $asset.digest -ne $digest)) {
        Invoke-RestMethod "$api/releases/assets/$($asset.id)" -Method Delete -Headers $headers -TimeoutSec 30
        $asset = $null
    }
    if (!$asset) {
        $upload = "https://uploads.github.com/repos/$slug/releases/$($release.id)/assets?name=$assetName"
        if ($assetName -cne $packageName) { $upload += '&label=Compatibility%20copy%20for%20older%20updaters' }
        $asset = Invoke-RestMethod $upload -Method Post -Headers $headers -InFile $package -ContentType 'application/zip' -TimeoutSec 300
    }
    if ($asset.name -cne $assetName -or $asset.state -ne 'uploaded' -or $asset.size -ne $packageBytes -or $asset.digest -ne $digest) {
        throw "Verification failed for $assetName. Release $tag remains a draft."
    }
}
$release = Invoke-RestMethod "$api/releases/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"make_latest":"true"}' -TimeoutSec 30
$latest = Invoke-RestMethod "$api/releases/latest" -Headers $headers -TimeoutSec 30
$verified = @($latest.assets | Where-Object { $_.name -cin $assetNames -and $_.digest -ceq $digest -and $_.size -eq $packageBytes })
if ($latest.id -ne $release.id -or $latest.tag_name -cne $tag -or $verified.Count -ne $assetNames.Count) {
    throw 'GitHub did not return the verified release as latest.'
}
[pscustomobject]@{ Version = $tag; Commit = $commit; Release = $release.html_url; Package = $package; Digest = $digest }
