[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('stable','staging')][string]$Channel,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$BootstrapHash,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 32)][long]$MinimumRootVersion = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/UpdateFeedCleanup.ps1')
$repository = 'Haanuwaa/AudioPilot'
$tag = "updater-$Channel-feed"
$root = [IO.Path]::GetFullPath($OutputDirectory)
for ($directory = [IO.DirectoryInfo]::new($root); $null -ne $directory; $directory = $directory.Parent) {
    if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Redirected cleanup report directory.'
    }
}
if (Test-Path -LiteralPath $root) { throw 'Choose a fresh cleanup report directory.' }

function Read-FeedInventory {
    $release = (& gh api "repos/$repository/releases/tags/$tag") | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $release.tag_name -cne $tag -or $release.draft -or -not $release.prerelease) {
        throw 'Could not identify the public updater release.'
    }
    $commit = & gh api "repos/$repository/git/ref/tags/$tag" --jq '.object.sha'
    if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[a-f0-9]{40}$') { throw 'Could not identify the updater tag.' }
    $raw = & gh api --paginate --slurp "repos/$repository/releases/$($release.id)/assets?per_page=100"
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the complete updater asset inventory.' }
    $pages = ($raw -join "`n") | ConvertFrom-Json -NoEnumerate
    $assets = @(foreach ($page in $pages) { $page })
    $assets = @($assets | Sort-Object id | Select-Object id,name,digest,size,state,updated_at)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($asset in $assets) {
        if (-not $names.Add($asset.name) -or $asset.name -cnotmatch '^[A-Za-z0-9._-]+$' -or
            $asset.state -cne 'uploaded' -or $asset.name -cmatch '\.(pending|previous|lock)$' -or
            $asset.size -lt 1 -or $asset.digest -cnotmatch '^sha256:[a-f0-9]{64}$' -or -not $asset.updated_at) {
            throw 'Feed assets appear incomplete or ambiguous. Retry after publication has settled.'
        }
    }
    [ordered]@{ releaseId = $release.id; tagCommit = $commit; assets = $assets }
}

$before = Read-FeedInventory
$verifiedDirectory = Join-Path $root 'verified'
& (Join-Path $PSScriptRoot 'renew-update-staging.ps1') -CheckOnly -Channel $Channel -Publisher $Publisher `
    -BootstrapHash $BootstrapHash -MinimumRootVersion $MinimumRootVersion -OutputDirectory $verifiedDirectory | Out-Null
$health = Get-Content -LiteralPath (Join-Path $verifiedDirectory 'health.json') -Raw | ConvertFrom-Json
$timestamp = (Get-Content -LiteralPath (Join-Path $verifiedDirectory 'metadata/timestamp.json') -Raw | ConvertFrom-Json).signed
$snapshotVersion = [long]$timestamp.meta.'snapshot.json'.version
$snapshot = (Get-Content -LiteralPath (Join-Path $verifiedDirectory "metadata/$snapshotVersion.snapshot.json") -Raw | ConvertFrom-Json).signed
$currentCatalog = [long]$snapshot.meta.'targets.json'.version
if ($currentCatalog -lt 1 -or $timestamp.version -ne $health.metadataVersion) { throw 'Invalid verified catalog version.' }
foreach ($name in @('timestamp.json', "$snapshotVersion.snapshot.json", "$currentCatalog.targets.json")) {
    $expected = @($before.assets | Where-Object name -CEQ $name)
    $actual = (Get-FileHash -LiteralPath (Join-Path $verifiedDirectory "metadata/$name")).Hash.ToLowerInvariant()
    if ($expected.Count -ne 1 -or $expected[0].digest -cne "sha256:$actual") { throw 'The verified feed changed during inventory.' }
}

$history = Join-Path $root 'history'
[IO.Directory]::CreateDirectory($history) | Out-Null
& gh release download $tag --repo $repository --dir $history --pattern '*.targets.json' --pattern '*.snapshot.json'
if ($LASTEXITCODE -ne 0) { throw 'Could not download metadata history.' }
$catalogs = @{}
$snapshots = [Collections.Generic.List[object]]::new()
foreach ($asset in $before.assets | Where-Object name -CMATCH '\.(targets|snapshot)\.json$') {
    $match = [regex]::Match($asset.name, '^([1-9][0-9]{0,5})\.(targets|snapshot)\.json$')
    if (-not $match.Success -or $asset.size -gt 131072) {
        throw 'Invalid historical metadata asset.'
    }
    $version = [long]$match.Groups[1].Value
    $role = $match.Groups[2].Value
    $file = Get-Item -LiteralPath (Join-Path $history $asset.name)
    if ($file.Length -ne $asset.size -or ('sha256:'+(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()) -cne $asset.digest) {
        throw 'Historical metadata digest or length differs.'
    }
    $signed = (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable).signed
    if ($signed._type -cne $role -or $signed.version -ne $version) { throw 'Historical metadata identity differs.' }
    if ($role -ceq 'targets') { $catalogs[$version] = $signed }
    else { $snapshots.Add($signed.meta['targets.json']) }
}
foreach ($reference in $snapshots) {
    $catalogName = "$([long]$reference.version).targets.json"
    $catalogAsset = @($before.assets | Where-Object name -CEQ $catalogName)
    if ($catalogAsset.Count -ne 1 -or $catalogAsset[0].size -ne $reference.length -or
        $catalogAsset[0].digest -cne "sha256:$($reference.hashes.sha256)") {
        throw 'A historical snapshot references an unavailable or mismatched catalog.'
    }
}
$after = Read-FeedInventory
if (($before | ConvertTo-Json -Depth 5 -Compress) -cne ($after | ConvertTo-Json -Depth 5 -Compress)) {
    throw 'The feed changed while preparing the report. Retry after publication has settled.'
}

$plan = Get-AudioPilotUpdateFeedCleanupPlan -Assets $after.assets -Catalogs $catalogs -CurrentCatalogVersion $currentCatalog -Now ([DateTimeOffset]::UtcNow)
[pscustomobject]@{ channel = $Channel; tag = $tag; tagCommit = $after.tagCommit; releaseId = $after.releaseId
    timestampVersion = $timestamp.version; currentCatalogVersion = $currentCatalog; plan = $plan } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'cleanup-plan.json') -Encoding utf8NoBOM
"$Channel dry run: $($plan.currentPackages) current packages, $($plan.protectedHistoricalPackages) protected historical packages, $($plan.candidateCount) candidates for review."
