$ErrorActionPreference = 'Stop'
$branch = git symbolic-ref --quiet --short HEAD
if ($branch -cne 'codex/source-switcher') { exit 0 }

try {
    $slug = 'EnzoChizari/source-switcher'
    $remote = @(git remote get-url --push --all private)
    if ($LASTEXITCODE -ne 0 -or $remote.Count -ne 1 -or $remote[0] -cne "https://github.com/$slug.git") {
        throw 'The private remote does not match Source Switcher.'
    }
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'Never'
    $record = "protocol=https`nhost=github.com`npath=$slug.git`n`n" | git credential fill
    if ($LASTEXITCODE -ne 0) { throw 'GitHub sign-in is unavailable.' }
    $token = ($record | Where-Object { $_.StartsWith('password=') }) -replace '^password=', ''
    if (!$token) { throw 'GitHub sign-in is unavailable.' }
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json' }
    $repo = Invoke-RestMethod "https://api.github.com/repos/$slug" -Headers $headers -TimeoutSec 20
    if (!$repo.private -or $repo.full_name -cne $slug) { throw 'GitHub did not confirm repository privacy.' }
    git push private HEAD:refs/heads/main
    if ($LASTEXITCODE -ne 0) { throw 'GitHub rejected the push.' }
} catch {
    Write-Warning 'Automatic GitHub upload failed. The commit is saved locally. Retry tools/git-hooks/Push-PrivateMain.ps1 after resolving the GitHub connection or branch conflict.'
    exit 1
}
