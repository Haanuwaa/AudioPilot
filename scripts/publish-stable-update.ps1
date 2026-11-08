[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SignedBuildRoot,
    [Parameter(Mandatory)][string]$ReleaseRoot,
    [string]$AuthorityDirectory,
    [Parameter(Mandatory)][string]$Publisher,
    [string]$CertificateThumbprint,
    [string]$SignTool,
    [Parameter(Mandatory)][string]$PublisherVerifier,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Resume
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/StableRelease.ps1')
. (Join-Path $PSScriptRoot 'lib/StagingPublicationLease.ps1')
. (Join-Path $PSScriptRoot 'lib/GitHubRelease.ps1')
$output = [IO.Path]::GetFullPath($OutputDirectory)
for ($current=$output; $current; $current=[IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected stable publication output.' }
}
if ($Resume) {
    if (-not (Test-Path -LiteralPath (Join-Path $output 'approved-packages.json')) -or -not (Test-Path -LiteralPath (Join-Path $output 'publication-input.json'))) { throw 'Resume needs the retained approval manifest and publication receipt. An incomplete local signing/export step requires authority recovery; do not sign the same version again.' }
}
else {
    if (Test-Path -LiteralPath $output) { throw 'Use a fresh stable publication directory, or Resume its retained signed export.' }
    if (-not $AuthorityDirectory -or -not $CertificateThumbprint -or -not $SignTool) { throw 'New publication requires the local authority, certificate thumbprint and signing tool.' }
}
$build = Get-Content -LiteralPath (Join-Path $SignedBuildRoot 'update-build.json') -Raw | ConvertFrom-Json
$proposal = Get-Content -LiteralPath (Join-Path $SignedBuildRoot 'update-targets.json') -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $ReleaseRoot 'release-manifest.json') -Raw | ConvertFrom-Json
$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../packaging/updater/stable/update-policy.json') -Raw | ConvertFrom-Json
if ($build.schema -ne 1 -or $build.channel -cne 'stable' -or $build.sourceDirty -isnot [bool] -or $build.sourceDirty -or
    $build.sourceCommit -cnotmatch '^[a-f0-9]{40}$' -or -not $build.publicLiveUpdates -or $proposal.schema -ne 1 -or $proposal.targets.Count -ne 9) { throw 'Use a clean signed stable build with all nine proposed variants.' }
& (Join-Path $PSScriptRoot 'validate-release-integrity.ps1') -ReleaseRoot $ReleaseRoot -ExpectedCommit $build.sourceCommit -RequireSignedBootstrap -PublisherVerifier $PublisherVerifier
$releases = (& gh api 'repos/Haanuwaa/AudioPilot/releases?per_page=100' --paginate --slurp | Out-String) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not read the matching application release.' }
$release = @($releases | ForEach-Object { $_ } | Where-Object tag_name -CEQ "v$($manifest.version)")
if ($release.Count -ne 1 -or $release[0].prerelease) { throw 'Upload the matching signed application draft before publishing its stable feed.' }
Assert-AudioPilotRemoteReleaseHashes -Release $release[0] -Directory $ReleaseRoot
$approvalManifest = Join-Path $output 'approved-packages.json'
$inputReceipt = Join-Path $output 'publication-input.json'
$checksumsHash = (Get-FileHash -LiteralPath (Join-Path $ReleaseRoot 'SHA256SUMS.txt') -Algorithm SHA256).Hash
$approved = @()
if ($Resume) {
    $receipt = Get-Content -LiteralPath $inputReceipt -Raw | ConvertFrom-Json
    $saved = Get-Content -LiteralPath $approvalManifest -Raw | ConvertFrom-Json -AsHashtable
    if ($receipt.schema -ne 1 -or $receipt.channel -cne 'stable' -or $receipt.version -cne $manifest.version -or
        $receipt.sourceCommit -cne $build.sourceCommit -or $receipt.bootstrapHash -cne $policy.BootstrapSha256 -or
        $receipt.checksumsHash -cne $checksumsHash -or $receipt.approvalHash -cne (Get-FileHash -LiteralPath $approvalManifest -Algorithm SHA256).Hash -or
        $saved.schema -ne 1 -or $saved.channel -cne 'stable' -or $saved.packages.Count -ne 9) { throw 'The retained publication does not match this exact approved release.' }
    $approved = @($saved.packages)
}
else { [IO.Directory]::CreateDirectory($output) | Out-Null }
$targets = @{}
$paths = @{}
foreach ($record in $proposal.targets) {
    $identity = $record.identity
    if ($identity.version -cne $manifest.version -or $identity.channel -cne 'stable' -or $identity.windowsPublisherSha256 -cne $policy.PublisherSha256 -or
        (Get-Item -LiteralPath $record.path).Length -ne $record.length -or (Get-FileHash -LiteralPath $record.path -Algorithm SHA256).Hash -cne $record.sha256) { throw 'Proposed stable package differs from its signed build.' }
    if ($Resume) {
        $matches = @($approved | Where-Object name -CEQ $record.name)
        if ($matches.Count -ne 1 -or $matches[0].path -cne $record.path -or $matches[0].sha256 -cne $record.sha256) { throw 'A resumed package differs from the retained approval.' }
        $package = $matches[0]
    }
    else {
        $approval = Join-Path $output "approval-$($approved.Count).json"
        $parameters = @{Package=$record.path;Kind=$identity.kind;Version=$manifest.version;Architecture=$identity.architecture;Runtime=$identity.runtime;MinimumHelperVersion=$identity.minimumHelperVersion;
            CertificateThumbprint=$CertificateThumbprint;SignTool=$SignTool;OutputManifest=$approval;Verifier=$PublisherVerifier;BootstrapSha256=$policy.BootstrapSha256;Channel='stable'}
        if ($identity.kind -ceq 'msi') {
            $portable = @($proposal.targets | Where-Object { $_.identity.kind -ceq 'portable' -and $_.identity.architecture -ceq $identity.architecture -and $_.identity.runtime -ceq 'self-contained' })
            if ($portable.Count -ne 1) { throw 'MSI approval requires its exact self-contained payload archive.' }
            $parameters.PayloadArchive = $portable[0].path
        }
        & (Join-Path $PSScriptRoot 'approve-update-package.ps1') @parameters | Out-Null
        $package = (Get-Content -LiteralPath $approval -Raw | ConvertFrom-Json -AsHashtable).packages[0]
        $approved += $package
    }
    if ($package.name -cne $record.name -or $targets.ContainsKey($package.name)) { throw 'Duplicate or mismatched approved target.' }
    foreach ($property in $package.identity.Keys) {
        if ($package.identity[$property] -cne $identity.$property) { throw "Approved package identity differs: $property." }
    }
    $paths[$package.name] = $package.path
    $targets[$package.name] = @{length=$record.length;hashes=@{sha256=$package.sha256.ToLowerInvariant()};custom=$package.identity}
}
$null = Assert-AudioPilotStableReleasePackages -ReleaseRoot $ReleaseRoot -Targets $targets -PackagePaths $paths -PublisherSha256 $policy.PublisherSha256
if (-not $Resume) {
    @{schema=1;channel='stable';packages=$approved} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $approvalManifest -Encoding utf8NoBOM
    @{schema=1;channel='stable';version=$manifest.version;sourceCommit=$build.sourceCommit;bootstrapHash=$policy.BootstrapSha256;checksumsHash=$checksumsHash;approvalHash=(Get-FileHash -LiteralPath $approvalManifest -Algorithm SHA256).Hash} |
        ConvertTo-Json | Set-Content -LiteralPath $inputReceipt -Encoding utf8NoBOM
}
function Read-VerifiedCatalog([string]$Directory, [bool]$MetadataOnly) {
    $request = @{operation='check';channel='stable';repository=[IO.Path]::GetFullPath($Directory);bootstrapHash=$policy.BootstrapSha256;metadataOnly=$MetadataOnly} | ConvertTo-Json -Compress
    $result = ($request | & $Publisher) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $result.channel -cne 'stable') { throw 'The retained signed catalog is incomplete or invalid. Recover the committed authority export without re-signing this version.' }
    $timestamp = Get-Content -LiteralPath (Join-Path $Directory 'metadata/timestamp.json') -Raw | ConvertFrom-Json -AsHashtable
    $snapshot = Get-Content -LiteralPath (Join-Path $Directory "metadata/$($timestamp.signed.meta['snapshot.json'].version).snapshot.json") -Raw | ConvertFrom-Json -AsHashtable
    $catalog = Get-Content -LiteralPath (Join-Path $Directory "metadata/$($snapshot.signed.meta['targets.json'].version).targets.json") -Raw | ConvertFrom-Json -AsHashtable
    return @{targets=$catalog.signed.targets;version=$timestamp.signed.version}
}
function Test-ApprovedCatalog($Catalog) {
    if (@($Catalog.targets.GetEnumerator() | Where-Object {$_.Value.custom.version -ceq $manifest.version}).Count -ne 9) { return $false }
    foreach ($package in $approved) {
        if (-not $Catalog.targets.ContainsKey($package.name)) { return $false }
        $target = $Catalog.targets[$package.name]
        if ($target.length -ne $targets[$package.name].length -or $target.hashes.sha256 -cne $package.sha256.ToLowerInvariant() -or
            $target.custom.Count -ne $package.identity.Count) { return $false }
        foreach ($property in $package.identity.Keys) { if ($target.custom[$property] -cne $package.identity[$property]) { return $false } }
    }
    return $true
}
$lease = Enter-StagingPublicationLease -Channel stable
try {
    if ($Resume) {
        $publishedFeed = (& gh api 'repos/Haanuwaa/AudioPilot/releases/tags/updater-stable-feed') | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $publishedFeed.draft -or -not $publishedFeed.prerelease) { throw 'Could not inspect the public stable feed for recovery.' }
        $publishedTimestamp = @($publishedFeed.assets | Where-Object name -CEQ 'timestamp.json')
        if ($publishedTimestamp.Count -eq 0 -or ($publishedTimestamp.Count -eq 1 -and $publishedTimestamp[0].state -ceq 'starter' -and $publishedTimestamp[0].size -eq 0)) {
            $retained = Read-VerifiedCatalog (Join-Path $output 'feed') $false
            if (-not (Test-ApprovedCatalog $retained)) { throw 'Timestamp recovery needs the exact approved signed export.' }
            & (Join-Path $PSScriptRoot 'publish-github-update-staging.ps1') -Channel stable -FeedDirectory (Join-Path $output 'feed') -BootstrapHash $policy.BootstrapSha256 -Publisher $Publisher -ReportPath (Join-Path $output 'publication.json') -ExistingLeaseSha $lease
        }
    }
    $checkpoint = Join-Path $output $(if ($Resume) { 'checkpoint-resume-' + [guid]::NewGuid().ToString('N') } else { 'checkpoint' })
    & (Join-Path $PSScriptRoot 'renew-update-staging.ps1') -Channel stable -Publisher $Publisher -BootstrapHash $policy.BootstrapSha256 -OutputDirectory $checkpoint -CheckOnly | Out-Null
    Assert-StagingPublicationLease $lease -Channel stable
    $feed = Join-Path $output 'feed'
    $current = Read-VerifiedCatalog $checkpoint $true
    if ($Resume -and (Test-ApprovedCatalog $current)) {
        @{schema=1;channel='stable';publicLiveUpdates=$true;bootstrapHash=$policy.BootstrapSha256;metadataVersion=$current.version;resumed=$true;alreadyPublished=$true} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'publication.json') -Encoding utf8NoBOM
    }
    else {
        if (-not $Resume) { & (Join-Path $PSScriptRoot 'publish-update-staging.ps1') -Operation Publish -Channel stable -AuthorityDirectory $AuthorityDirectory -Publisher $Publisher -PackageManifest $approvalManifest -PublicRepository $checkpoint -OutputDirectory $feed }
        $retained = Read-VerifiedCatalog $feed $false
        if (-not (Test-ApprovedCatalog $retained)) { throw 'The retained signed export differs from the nine approved release variants.' }
        Assert-StagingPublicationLease $lease -Channel stable
        & (Join-Path $PSScriptRoot 'publish-github-update-staging.ps1') -Channel stable -FeedDirectory $feed -BootstrapHash $policy.BootstrapSha256 -Publisher $Publisher -ReportPath (Join-Path $output 'publication.json') -ExistingLeaseSha $lease
    }
    $verification = Join-Path $output $(if ($Resume) { 'remote-verification-resume-' + [guid]::NewGuid().ToString('N') } else { 'remote-verification' })
    & (Join-Path $PSScriptRoot 'validate-stable-release.ps1') -ReleaseRoot $ReleaseRoot -Publisher $Publisher -OutputDirectory $verification
} finally { Exit-StagingPublicationLease $lease -Channel stable }
