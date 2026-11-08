Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = Join-Path $repo "artifacts/testresults/stable-resume/$([guid]::NewGuid().ToString('N'))"
$scripts = Join-Path $root 'scripts'
foreach ($directory in @($scripts,(Join-Path $scripts 'lib'),(Join-Path $root 'build'),(Join-Path $root 'release'),(Join-Path $root 'packaging/updater/stable'))) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
Copy-Item -LiteralPath (Join-Path $repo 'scripts/publish-stable-update.ps1') -Destination $scripts
Copy-Item -LiteralPath (Join-Path $repo 'packaging/updater/stable/update-policy.json') -Destination (Join-Path $root 'packaging/updater/stable')
$policy = Get-Content -LiteralPath (Join-Path $root 'packaging/updater/stable/update-policy.json') -Raw | ConvertFrom-Json
$proposal = @()
$approved = @()
$catalog = @{}
foreach ($architecture in @('x64','x86','arm64')) {
    foreach ($mode in @('SelfContained','FrameworkDependent','MSI')) {
        $msi = $mode -ceq 'MSI'
        $name = if ($msi) { "AudioPilot-1.2.3-win-$architecture.msi" } else { "AudioPilot-1.2.3-$mode-win-$architecture.update.zip" }
        $path = Join-Path $root "build/$name"
        Set-Content -LiteralPath $path -Value "$architecture-$mode"
        $identity = @{product='AudioPilot';channel='stable';version='1.2.3';kind=$(if($msi){'msi'}else{'portable'});architecture="win-$architecture";runtime=$(if($mode -ceq 'FrameworkDependent'){'framework-dependent'}else{'self-contained'});minimumHelperVersion='1.0.0';windowsPublisherSha256=$policy.PublisherSha256;windowsTrust='self-signed'}
        $hash = (Get-FileHash -LiteralPath $path).Hash
        $length = (Get-Item -LiteralPath $path).Length
        $proposal += @{name=$name;path=$path;sha256=$hash;length=$length;identity=$identity}
        $approved += @{name=$name;path=$path;sha256=$hash;identity=$identity}
        $catalog[$name] = @{length=$length;hashes=@{sha256=$hash.ToLowerInvariant()};custom=$identity}
    }
}
@{schema=1;channel='stable';sourceCommit=('a'*40);sourceDirty=$false;publicLiveUpdates=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'build/update-build.json')
@{schema=1;targets=$proposal} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'build/update-targets.json')
@{version='1.2.3'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'release/release-manifest.json')
Set-Content -LiteralPath (Join-Path $root 'release/SHA256SUMS.txt') -Value 'synthetic checksum fixture'
@'
function Assert-AudioPilotStableReleasePackages {
    param($ReleaseRoot,$Targets,$PackagePaths,$PublisherSha256)
    if ($Targets.Count -ne 9 -or $PackagePaths.Count -ne 9) { throw 'Fixture approval lost a variant.' }
    foreach ($name in $Targets.Keys) {
        if ((Get-FileHash -LiteralPath $PackagePaths[$name]).Hash.ToLowerInvariant() -cne $Targets[$name].hashes.sha256) { throw 'Fixture approval bytes changed.' }
    }
    return 9
}
'@ | Set-Content -LiteralPath (Join-Path $scripts 'lib/StableRelease.ps1')
'function Assert-AudioPilotRemoteReleaseHashes { param($Release,$Directory); if ($Release.id -ne 1000) { throw "Wrong application release." } }' |
    Set-Content -LiteralPath (Join-Path $scripts 'lib/GitHubRelease.ps1')
@'
function Enter-StagingPublicationLease { param($Channel); if ($Channel -cne 'stable') { throw 'Cross-channel lease.' }; $global:resumeFixture.entered=$true; return ('a'*40) }
function Assert-StagingPublicationLease { param($Lease,$Channel); if ($Channel -cne 'stable' -or $Lease -cne ('a'*40)) { throw 'Cross-channel lease.' } }
function Exit-StagingPublicationLease { param($Lease,$Channel); Assert-StagingPublicationLease $Lease $Channel; $global:resumeFixture.released=$true }
'@ | Set-Content -LiteralPath (Join-Path $scripts 'lib/StagingPublicationLease.ps1')
'$global:resumeFixture.privateCalls++; throw "Resume must not open signing authority material."' |
    Set-Content -LiteralPath (Join-Path $scripts 'publish-update-staging.ps1')
'$global:resumeFixture.privateCalls++; throw "Resume must not call the package signer."' |
    Set-Content -LiteralPath (Join-Path $scripts 'approve-update-package.ps1')
'param($ReleaseRoot,$ExpectedCommit,[switch]$RequireSignedBootstrap,$PublisherVerifier); if (-not $RequireSignedBootstrap -or $ExpectedCommit -cne ("a"*40)) { throw "Release verification was bypassed." }' |
    Set-Content -LiteralPath (Join-Path $scripts 'validate-release-integrity.ps1')
function Write-FixtureCatalog([string]$Directory, [int]$Version, [hashtable]$Targets) {
    [IO.Directory]::CreateDirectory((Join-Path $Directory 'metadata')) | Out-Null
    @{signed=@{version=$Version;meta=@{'snapshot.json'=@{version=$Version}}}} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Directory 'metadata/timestamp.json')
    @{signed=@{version=$Version;meta=@{'targets.json'=@{version=$Version}}}} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Directory "metadata/$Version.snapshot.json")
    @{signed=@{version=$Version;targets=$Targets}} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Directory "metadata/$Version.targets.json")
}
@'
param($Channel,$Publisher,$BootstrapHash,$OutputDirectory,[switch]$CheckOnly)
if ($Channel -cne 'stable' -or -not $CheckOnly -or -not $global:resumeFixture.hasTimestamp) { throw 'Invalid read-only checkpoint.' }
Write-FixtureCatalog $OutputDirectory $global:resumeFixture.version $global:resumeFixture.current
'@ | Set-Content -LiteralPath (Join-Path $scripts 'renew-update-staging.ps1')
@'
param($Channel,$FeedDirectory,$BootstrapHash,$Publisher,$ReportPath,$ExistingLeaseSha)
if ($Channel -cne 'stable' -or $ExistingLeaseSha -cne ('a'*40) -or -not (Test-Path -LiteralPath (Join-Path $FeedDirectory 'metadata/timestamp.json'))) { throw 'Invalid retained export.' }
$global:resumeFixture.published++
$global:resumeFixture.current=$global:resumeFixture.approved
$global:resumeFixture.version=2
$global:resumeFixture.hasTimestamp=$true
'@ | Set-Content -LiteralPath (Join-Path $scripts 'publish-github-update-staging.ps1')
@'
param($ReleaseRoot,$Publisher,$OutputDirectory)
if (-not $global:resumeFixture.hasTimestamp -or $global:resumeFixture.current.Count -ne 9) { throw 'Remote verification preceded complete publication.' }
$global:resumeFixture.verified++
'@ | Set-Content -LiteralPath (Join-Path $scripts 'validate-stable-release.ps1')
@'
$request = ($input | Out-String) | ConvertFrom-Json
if ($request.operation -cne 'check' -or $request.channel -cne 'stable') { throw 'Resume attempted a signing operation.' }
$global:resumeFixture.trustChecks++
$timestamp=Join-Path $request.repository 'metadata/timestamp.json'
if (-not (Test-Path -LiteralPath $timestamp)) { $global:LASTEXITCODE=1; return }
$global:LASTEXITCODE=0
@{channel='stable';version=(Get-Content -LiteralPath $timestamp -Raw | ConvertFrom-Json).signed.version} | ConvertTo-Json
'@ | Set-Content -LiteralPath (Join-Path $root 'verifier.ps1')
function gh {
    $global:LASTEXITCODE=0
    if ($args[0] -cne 'api') { throw 'Unexpected network command in resume fixture.' }
    if ($args[1] -ceq 'repos/Haanuwaa/AudioPilot/releases?per_page=100') { return (@(@{id=1000;tag_name='v1.2.3';prerelease=$false}) | ConvertTo-Json -AsArray) }
    if ($args[1] -ceq 'repos/Haanuwaa/AudioPilot/releases/tags/updater-stable-feed') {
        return (@{draft=$false;prerelease=$true;assets=@(if($global:resumeFixture.hasTimestamp){@{name='timestamp.json';state='uploaded';size=500}})} | ConvertTo-Json -Depth 5)
    }
    throw 'Unexpected feed or remote in resume fixture.'
}
foreach ($case in @(
    @{name='already-renewed';failed=$false;publishes=0},
    @{name='retry-export';failed=$false;publishes=1},
    @{name='missing-timestamp';failed=$false;publishes=1},
    @{name='incomplete-export';failed=$true;publishes=0;error='incomplete or invalid'},
    @{name='wrong-retained-catalog';failed=$true;publishes=0;error='differs from the nine'},
    @{name='changed-input';failed=$true;publishes=0;error='exact approved release'}
)) {
    $output=Join-Path $root $case.name
    [IO.Directory]::CreateDirectory($output) | Out-Null
    @{schema=1;channel='stable';packages=$approved} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'approved-packages.json')
    @{schema=1;channel='stable';version='1.2.3';sourceCommit=('a'*40);bootstrapHash=$policy.BootstrapSha256;checksumsHash=$(if($case.name -ceq 'changed-input'){'wrong'}else{(Get-FileHash -LiteralPath (Join-Path $root 'release/SHA256SUMS.txt')).Hash});approvalHash=(Get-FileHash -LiteralPath (Join-Path $output 'approved-packages.json')).Hash} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'publication-input.json')
    $oldCatalog=$catalog.Clone()
    $oldCatalog.Remove($proposal[0].name)
    $global:resumeFixture=@{entered=$false;released=$false;privateCalls=0;trustChecks=0;published=0;verified=0;hasTimestamp=($case.name -cne 'missing-timestamp');version=$(if($case.name -ceq 'already-renewed'){3}else{1});current=$(if($case.name -ceq 'already-renewed'){$catalog}else{$oldCatalog});approved=$catalog}
    if ($case.name -cnotin @('already-renewed','incomplete-export')) {
        Write-FixtureCatalog (Join-Path $output 'feed') 2 $(if($case.name -ceq 'wrong-retained-catalog'){$oldCatalog}else{$catalog})
    }
    $global:LASTEXITCODE=0
    $failed=$false;$failure=$null
    try { & (Join-Path $scripts 'publish-stable-update.ps1') -SignedBuildRoot (Join-Path $root 'build') -ReleaseRoot (Join-Path $root 'release') -Publisher (Join-Path $root 'verifier.ps1') -PublisherVerifier 'trusted-fixture-verifier' -OutputDirectory $output -Resume | Out-Null }
    catch { $failed=$true;$failure=$_.Exception.Message }
    if ($failed -ne $case.failed -or ($failed -and $failure -notlike "*$($case.error)*") -or $global:resumeFixture.privateCalls -ne 0 -or $global:resumeFixture.published -ne $case.publishes) { throw "Resume scenario failed: $($case.name): $failure" }
    if ($global:resumeFixture.entered -and -not $global:resumeFixture.released) { throw 'Resume leaked its publication lease.' }
    if (-not $failed -and ($global:resumeFixture.verified -ne 1 -or $global:resumeFixture.trustChecks -lt 1)) { throw 'Resume skipped verification.' }
}
Write-Output 'Stable publication resume checks passed: 6 scenarios; no signing authority or real network used.'
