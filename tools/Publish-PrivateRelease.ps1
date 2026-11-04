# Requires PowerShell 7 and a Git Credential Manager sign-in with repository write access.
param(
    [Parameter(Mandatory)][string]$PortableDirectory,
    [Parameter(Mandatory)][string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$slug = 'EnzoChizari/source-switcher'
$remote = "https://github.com/$slug.git"
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
$package = Join-Path $packageRoot 'SourceSwitcher-win-x64.zip'
$source = Join-Path $portable 'Corresponding-source.zip'
if ((Test-Path -LiteralPath $package) -or (Test-Path -LiteralPath $source)) { throw 'Choose a fresh package folder and portable build.' }

$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'Never'
$record = "protocol=https`nhost=github.com`npath=$slug.git`n`n" | git credential fill
if ($LASTEXITCODE -ne 0) { throw 'Sign in to GitHub using Git Credential Manager before publishing.' }
$token = ($record | Where-Object { $_.StartsWith('password=') }) -replace '^password=', ''
if (!$token) { throw 'GitHub credentials are unavailable.' }
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
$repository = Invoke-RestMethod $api -Headers $headers -TimeoutSec 30
if (!$repository.private -or $repository.full_name -cne $slug) { throw 'The intended repository is unavailable or is no longer private.' }
$ref = git -C $repo ls-remote $remote refs/heads/main
if ($LASTEXITCODE -ne 0 -or !$ref) { throw 'Push the release source to the private main branch first.' }
$commit = ($ref -split '\s+')[0]
$projectRelative = $project.Substring($repo.Length + 1).Replace('\','/')
$publishedProject = git -C $repo show "${commit}:$projectRelative"
if ($LASTEXITCODE -ne 0 -or ($publishedProject -join "`n") -cne (Get-Content -LiteralPath $project -Raw).TrimEnd()) {
    throw 'The release project file differs from private main.'
}
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
$tag = "v$version"
$draft = @{ tag_name = $tag; target_commitish = $commit; name = "Source Switcher $tag"; draft = $true;
    prerelease = $false; body = "Windows x64 portable release. Download SourceSwitcher-win-x64.zip and extract the entire folder. See START HERE.txt for private update access." }
$release = Invoke-RestMethod "$api/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body ($draft | ConvertTo-Json) -TimeoutSec 30
$assetName = 'SourceSwitcher-win-x64.zip'
$upload = "https://uploads.github.com/repos/$slug/releases/$($release.id)/assets?name=$assetName"
$asset = Invoke-RestMethod $upload -Method Post -Headers $headers -InFile $package -ContentType 'application/zip' -TimeoutSec 300
if ($asset.state -ne 'uploaded' -or $asset.size -ne (Get-Item -LiteralPath $package).Length -or $asset.digest -ne $digest) {
    throw "Uploaded asset verification failed. Release $tag remains a draft."
}
$release = Invoke-RestMethod "$api/releases/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"make_latest":"true"}' -TimeoutSec 30
$latest = Invoke-RestMethod "$api/releases/latest" -Headers $headers -TimeoutSec 30
if ($latest.id -ne $release.id -or $latest.tag_name -cne $tag -or !($latest.assets | Where-Object { $_.id -eq $asset.id -and $_.digest -eq $digest })) {
    throw 'GitHub did not return the verified release as latest.'
}
[pscustomobject]@{ Version = $tag; Commit = $commit; Release = $release.html_url; Package = $package; Digest = $digest }
