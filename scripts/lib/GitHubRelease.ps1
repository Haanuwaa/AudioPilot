function Assert-AudioPilotReleaseMutable {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Version, [switch]$RequireDraft, [string]$ExpectedCommit)
    if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Version -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Invalid release identity.' }
    $token = if ($env:GH_TOKEN) { $env:GH_TOKEN } else { $env:GITHUB_TOKEN }
    $headers = @{Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
    if ($token) { $headers.Authorization = "Bearer $token" }
    try { $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/tags/v$Version" -Headers $headers -TimeoutSec 30 }
    catch {
        $response = $_.Exception.PSObject.Properties['Response']
        if ($null -eq $response -or $null -eq $response.Value -or [int]$response.Value.StatusCode -ne 404) { throw }
        if (-not $RequireDraft) { return $null }
        $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases?per_page=100" -Headers $headers -TimeoutSec 30
        $candidates = @($releases | Where-Object tag_name -CEQ "v$Version")
        if ($candidates.Count -ne 1) { throw 'The signed draft could not be identified uniquely.' }
        $release = $candidates[0]
    }
    if ($ExpectedCommit) {
        if ($ExpectedCommit -cnotmatch '^[a-f0-9]{40}$') { throw 'Invalid expected release commit.' }
        $reference = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/git/ref/tags/v$Version" -Headers $headers -TimeoutSec 30
        $target = $reference.object
        for ($depth=0; $target.type -ceq 'tag' -and $depth -lt 8; $depth++) {
            if ($target.sha -cnotmatch '^[a-f0-9]{40}$') { throw 'Invalid annotated tag identity.' }
            $target = (Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/git/tags/$($target.sha)" -Headers $headers -TimeoutSec 30).object
        }
        if ($target.type -cne 'commit' -or $target.sha -cne $ExpectedCommit) { throw 'The release tag does not identify the tested source commit.' }
    }
    if ($release.tag_name -cne "v$Version" -or $release.draft -isnot [bool] -or -not $release.draft -or $release.prerelease) {
        throw 'Published application versions are immutable. Choose a new version; updater feed containers use separate publication tooling.'
    }
    return $release
}

function Assert-AudioPilotRemoteReleaseHashes {
    param([Parameter(Mandatory)]$Release, [Parameter(Mandatory)][string]$Directory)
    $paths = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Extension -in @('.zip','.msi','.json','.txt') })
    if ($paths.Count -ne 13 -or $Release.assets.Count -ne 13) { throw 'Expected exactly the final thirteen public release assets.' }
    foreach ($file in $paths) {
        $assets = @($Release.assets | Where-Object name -CEQ $file.Name)
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($assets.Count -ne 1 -or $assets[0].size -ne $file.Length -or $assets[0].digest -cne "sha256:$hash") { throw "Draft asset changed or lacks a verified server digest: $($file.Name)" }
    }
}
