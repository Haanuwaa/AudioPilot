$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../lib/UpdateFeedCleanup.ps1')

$now = [DateTimeOffset]::Parse('2026-11-22T12:00:00Z', [Globalization.CultureInfo]::InvariantCulture)
$current = 'current.zip'
$expired = 'expired.msi'
$grace = 'in-grace.zip'
$recent = 'recent-upload.msi'
$unexpired = 'unexpired.zip'
$unknown = 'unknown.zip'
$hash = 'a' * 64
function New-Catalog([DateTimeOffset]$Expires, [string[]]$Names) {
    $targets = @{}
    foreach ($name in $Names) { $targets[$name] = @{ hashes = @{ sha256 = $hash } } }
    @{ expires = $Expires; targets = $targets }
}

$catalogs = @{
    ([long]1) = (New-Catalog $now.AddDays(-21) @($current, $expired, $recent, $unexpired))
    ([long]2) = (New-Catalog $now.AddDays(-2) @($grace))
    ([long]3) = (New-Catalog $now.AddDays(14) @($current))
    ([long]4) = (New-Catalog $now.AddDays(10) @($unexpired))
}
$assets = @(foreach ($name in @($current, $expired, $grace, $recent, $unexpired, $unknown, '1.root.json')) {
    $isMetadata = $name -ceq '1.root.json'
    @{
        name = $(if ($isMetadata) { $name } else { "$hash.$name" })
        size = 1024
        digest = "sha256:$hash"
        updated_at = $(if ($name -ceq $recent) { $now.AddDays(-1) } else { $now.AddDays(-20) })
    }
})

$plan = Get-AudioPilotUpdateFeedCleanupPlan -Assets $assets -Catalogs $catalogs -CurrentCatalogVersion 3 -Now $now
$expected = @{
    "$hash.$current" = 'current-catalog'
    "$hash.$expired" = 'expired-history'
    "$hash.$grace" = 'historical-catalog-or-grace'
    "$hash.$recent" = 'historical-catalog-or-grace'
    "$hash.$unexpired" = 'historical-catalog-or-grace'
    "$hash.$unknown" = 'no-catalog-history'
    '1.root.json' = 'metadata-or-unrecognized'
}
foreach ($file in $plan.files) {
    if ($expected[$file.name] -cne $file.reason) { throw "Unexpected disposition for $($file.name)." }
}
if ($plan.files.Count -ne $expected.Count -or $plan.currentPackages -ne 1 -or $plan.candidateCount -ne 1 -or
    $plan.candidateBytes -ne 1024 -or $plan.protectedHistoricalPackages -ne 3 -or
    $plan.nextReviewAfter -ne $now.AddDays(5)) {
    throw 'Incorrect package retention totals or review date.'
}
$currentOnly = @($assets | Where-Object { $_.name -ceq "$hash.$current" })
$plan = Get-AudioPilotUpdateFeedCleanupPlan -Assets $currentOnly -Catalogs $catalogs -CurrentCatalogVersion 3 -Now $now
if ($plan.candidateCount -ne 0 -or $plan.candidateBytes -ne 0 -or $plan.protectedHistoricalBytes -ne 0 -or
    $null -ne $plan.nextReviewAfter) {
    throw 'A current-only feed should have no retention candidates or review date.'
}
$incomplete = @($assets | Where-Object { $_.name -cne "$hash.$current" })
$failure = try {
    Get-AudioPilotUpdateFeedCleanupPlan -Assets $incomplete -Catalogs $catalogs -CurrentCatalogVersion 3 -Now $now | Out-Null
} catch { $_.Exception.Message }
if ($failure -cne 'The inventory is missing a current package.') { throw 'An incomplete inventory was accepted.' }
'Feed cleanup retention checks passed.'
