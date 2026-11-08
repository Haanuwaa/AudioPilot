function Enter-StagingPublicationLease {
    param([ValidateSet('staging','stable')][string]$Channel = 'staging')
    $repo = 'Haanuwaa/AudioPilot'
    $main = (& gh api "repos/$repo/git/ref/heads/main" --jq '.object.sha').Trim()
    if ($LASTEXITCODE -ne 0 -or $main -cnotmatch '^[a-f0-9]{40}$') { throw 'Cannot identify main for the staging publication lease.' }
    $request = Join-Path ([IO.Path]::GetTempPath()) ('AudioPilot-staging-lease-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        @{tag=('updater-staging-lease-' + [Guid]::NewGuid().ToString('N'));message=('AudioPilot staging publication lease; inspect active maintenance before removing. ' + [DateTimeOffset]::UtcNow.ToString('O'));object=$main;type='commit'} | ConvertTo-Json | Set-Content -LiteralPath $request -Encoding utf8NoBOM
        $tag = (& gh api --method POST "repos/$repo/git/tags" --input $request) | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $tag.sha -cnotmatch '^[a-f0-9]{40}$') { throw 'Cannot create a unique staging publication lease identity.' }
        @{ref="refs/tags/_updater-$Channel-publication-lock";sha=$tag.sha} | ConvertTo-Json | Set-Content -LiteralPath $request -Encoding utf8NoBOM
        $null = & gh api --method POST "repos/$repo/git/refs" --input $request
        if ($LASTEXITCODE -ne 0) { throw 'Staging publication is active or interrupted. Inspect the existing lease; do not bypass it.' }
        return $tag.sha
    } finally { Remove-Item -LiteralPath $request -Force -ErrorAction SilentlyContinue }
}

function Assert-StagingPublicationLease([string]$Lease, [ValidateSet('staging','stable')][string]$Channel = 'staging') {
    if ($Lease -cnotmatch '^[a-f0-9]{40}$') { throw 'Invalid staging publication lease identity.' }
    $owner = (& gh api "repos/Haanuwaa/AudioPilot/git/ref/tags/_updater-$Channel-publication-lock" --jq '.object.sha').Trim()
    if ($LASTEXITCODE -ne 0 -or $owner -cne $Lease) { throw 'Staging publication lease ownership changed.' }
}

function Exit-StagingPublicationLease([string]$Lease, [ValidateSet('staging','stable')][string]$Channel = 'staging') {
    Assert-StagingPublicationLease $Lease -Channel $Channel
    $null = & gh api --method DELETE "repos/Haanuwaa/AudioPilot/git/refs/tags/_updater-$Channel-publication-lock"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot release the staging publication lease. Inspect it before retrying.' }
}
