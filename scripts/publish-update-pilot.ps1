[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApprovalManifest,
    [Parameter(Mandatory)][string]$PolicyDirectory,
    [Parameter(Mandatory)][string]$AuthorityDirectory,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/StagingPublicationLease.ps1')
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh pilot publication directory.' }
for ($current=$output; $current; $current=[IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected pilot publication output.' }
}
$policy = Get-Content -LiteralPath (Join-Path $PolicyDirectory 'pilot-policy.json') -Raw | ConvertFrom-Json
$approval = Get-Content -LiteralPath $ApprovalManifest -Raw | ConvertFrom-Json
if ($approval.schema -ne 1 -or $approval.channel -cne 'staging' -or $approval.packages.Count -ne 2) { throw 'Approve exactly one MSI and one portable pilot package.' }
$kinds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($package in $approval.packages) {
    $identity = $package.identity
    if (-not $kinds.Add($identity.kind) -or $identity.kind -cnotin @('portable','msi') -or $identity.version -cne $policy.CandidateVersion -or
        $identity.architecture -cne 'win-x64' -or $identity.runtime -cne 'self-contained' -or $identity.windowsPublisherSha256 -cne $policy.PublisherSha256 -or $identity.windowsTrust -cne 'self-signed-staging') { throw 'The approved package is outside the fixed pilot policy.' }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$lease = Enter-StagingPublicationLease
try {
    $checkpoint = Join-Path $output 'public-checkpoint'
    & (Join-Path $PSScriptRoot 'renew-update-staging.ps1') -Publisher $Publisher -BootstrapHash '85D114D65F2367E8A587765BDF56580E08E9F4D0D644559C73B991209453F61F' -OutputDirectory $checkpoint -CheckOnly
    Assert-StagingPublicationLease $lease
    $feed = Join-Path $output 'feed'
    & (Join-Path $PSScriptRoot 'publish-update-staging.ps1') -Operation Publish -AuthorityDirectory $AuthorityDirectory -Publisher $Publisher -PackageManifest $ApprovalManifest -PublicRepository $checkpoint -OutputDirectory $feed
    Assert-StagingPublicationLease $lease
    & (Join-Path $PSScriptRoot 'publish-github-update-staging.ps1') -FeedDirectory $feed -BootstrapHash '85D114D65F2367E8A587765BDF56580E08E9F4D0D644559C73B991209453F61F' -Publisher $Publisher -ReportPath (Join-Path $output 'publication.json') -ExistingLeaseSha $lease
    & (Join-Path $PSScriptRoot 'renew-update-staging.ps1') -Publisher $Publisher -BootstrapHash '85D114D65F2367E8A587765BDF56580E08E9F4D0D644559C73B991209453F61F' -OutputDirectory (Join-Path $output 'https-verified') -CheckOnly
} finally { Exit-StagingPublicationLease $lease }
