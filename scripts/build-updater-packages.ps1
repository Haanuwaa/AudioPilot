[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PolicyDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SignTool,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$BaselineVersion = '1.0.1',
    [switch]$Bootstrap,
    [switch]$SandboxAcceptance,
    [switch]$SkipInstallers,
    [switch]$DirectoryOnly,
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$MinimumHelperVersion = '1.0.0',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [ValidateSet('win-x64','win-x86','win-arm64')][string[]]$Runtimes = @('win-x64'),
    [ValidateSet('SelfContained','FrameworkDependent')][string[]]$DeploymentModes = @('SelfContained')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($DirectoryOnly -and (-not $Bootstrap -or -not $SkipInstallers)) {
    throw 'Directory-only publishing requires bootstrap mode and SkipInstallers.'
}
. (Join-Path $PSScriptRoot 'lib/ReleaseDependencies.ps1')
. (Join-Path $PSScriptRoot 'lib/ReleaseSbom.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory, $repo)
if (-not $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep pilot build outputs inside the repository.' }
for ($current=$output; $current; $current=[IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected pilot output.' }
}
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh pilot build directory.' }
$policyFile = if ($Bootstrap) { 'update-policy.json' } else { 'pilot-policy.json' }
$policyPath = (Resolve-Path -LiteralPath (Join-Path $PolicyDirectory $policyFile)).Path
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$identity = Get-Content -LiteralPath (Join-Path $PolicyDirectory 'identity-receipt.json') -Raw | ConvertFrom-Json
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$($identity.certificateThumbprint)"
if (-not $certificate.HasPrivateKey -or (-not $Bootstrap -and $certificate.Subject -cne 'CN=AudioPilot updater pilot') -or
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData)) -cne $policy.PublisherSha256) { throw 'Pilot publisher identity mismatch.' }
if ($Bootstrap) {
    if (-not $Version -or ([Version]$Version).ToString(3) -cne $Version -or $policy.Schema -ne 1 -or $policy.Channel -cne 'stable' -or
        $null -ne $policy.CandidateVersion -or $policy.PublicUpdatesEnabled -isnot [bool]) { throw 'Bootstrap packaging requires an explicit version and a stable policy.' }
    if ([Version]$MinimumHelperVersion -gt [Version]$Version) { throw 'The minimum helper cannot be newer than the package.' }
    $rootBytes = [Convert]::FromBase64String($policy.BootstrapRoot)
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rootBytes)) -cne $policy.BootstrapSha256) { throw 'Bootstrap pin mismatch.' }
} elseif ($Runtimes.Count -ne 1 -or $Runtimes[0] -cne 'win-x64' -or $DeploymentModes.Count -ne 1 -or $DeploymentModes[0] -cne 'SelfContained' -or
    $policy.Schema -ne 1 -or [Version]$policy.CandidateVersion -le [Version]$BaselineVersion) { throw 'The x64 self-contained pilot must advance the baseline version.' }
$tool = (Resolve-Path -LiteralPath $SignTool).Path
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[a-f0-9]{40}$') { throw 'Cannot identify pilot source.' }
$sourceChanges = @(& git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify pilot working-tree state.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Pilot .NET build failed.' }
}
function Sign-PilotFiles([string[]]$Paths) {
    foreach ($path in $Paths) {
        for ($current=$path; $current; $current=[IO.Path]::GetDirectoryName($current)) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected signing input.' }
        }
        & $tool sign /q /s My /sha1 $certificate.Thumbprint /fd SHA256 $path
        if ($LASTEXITCODE -ne 0) { throw 'Pilot signature creation failed.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -cne $certificate.Thumbprint -or $signature.Status -eq 'HashMismatch') { throw 'Unexpected pilot signature.' }
    }
    # This is signature creation, not package approval. Full Windows verification runs in the disposable guest.
}
$bridge = Join-Path $output 'bridge'
$bridgeArguments = if ($Bootstrap) { @{ StableRootSha256=$policy.BootstrapSha256; EnablePublicUpdates=[bool]$policy.PublicUpdatesEnabled; SandboxAcceptance=[bool]$SandboxAcceptance } } else { @{ Pilot=$true } }
& (Join-Path $PSScriptRoot 'build-tuf-bridge.ps1') -OutputRoot $bridge -Runtimes $Runtimes @bridgeArguments -ScanVulnerabilities
Invoke-DotNet @('restore',(Join-Path $repo 'AudioPilot.Installer/AudioPilot.Installer.wixproj'),'--locked-mode','--nologo')
$records = @()
$versions = if ($Bootstrap) { @($Version) } else { @($BaselineVersion,[string]$policy.CandidateVersion) }
foreach ($version in $versions) {
foreach ($runtime in $Runtimes) {
foreach ($mode in $DeploymentModes) {
    $selfContained = $mode -ceq 'SelfContained'
    $profile = "$mode-$runtime"
    $validation = if ($Bootstrap) { @{UpdateBootstrap=$true} } else { @{UpdatePilot=$true} }
    $versionRoot = Join-Path $output $(if ($Bootstrap) { "$version/$profile" } else { $version })
    $payload = Join-Path $versionRoot 'payload'
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    $properties = @('-c','Release','-r',$runtime,'--self-contained',$selfContained.ToString().ToLowerInvariant(),'--nologo',
        '-p:PublishSingleFile=false',"-p:PublishReadyToRun=$($runtime -ceq 'win-x64')",'-p:DebugSymbols=false','-p:DebugType=None','-p:RestoreLockedMode=true',
        '-p:IncludePublishReferenceFiles=true',"-p:BuildIsolationKey=UpdatePackages-$version-$profile", "-p:AudioPilotUpdatePolicy=$policyPath",
        "-p:Version=$version","-p:AssemblyVersion=$version.0","-p:FileVersion=$version.0","-p:InformationalVersion=$(if ($Bootstrap) { $version } else { "$version-pilot" })+$commit",'-p:IncludeSourceRevisionInInformationalVersion=false','-o',$payload)
    foreach ($projectPath in @('AudioPilot/AudioPilot.csproj','AudioPilot.Updater/Ui/AudioPilot.Updater.Ui.csproj')) {
        Invoke-DotNet (@('publish',(Join-Path $repo $projectPath)) + $properties)
    }
    Copy-Item -LiteralPath (Join-Path $bridge "$runtime/AudioPilot.TufBridge.exe") -Destination $payload
    Copy-Item -LiteralPath (Join-Path $bridge "$runtime/build-info.txt") -Destination (Join-Path $payload 'updater-build-info.txt')
    $notices = [Text.StringBuilder]::new()
    [void]$notices.AppendLine('Go toolchain and updater dependency licenses. The operator publisher and its private keys are not distributed.')
    foreach ($license in @((Get-Item -LiteralPath (Join-Path $bridge 'Go-LICENSE'))) + @(Get-ChildItem -LiteralPath (Join-Path $bridge 'licenses') -File -Recurse | Sort-Object FullName)) {
        [void]$notices.AppendLine(); [void]$notices.AppendLine([IO.Path]::GetRelativePath($bridge,$license.FullName)); [void]$notices.AppendLine([IO.File]::ReadAllText($license.FullName))
    }
    [IO.Directory]::CreateDirectory((Join-Path $payload 'licenses')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $payload 'licenses/UPDATER-NOTICES.txt'),$notices.ToString(),[Text.UTF8Encoding]::new($false))
    Sign-PilotFiles @(Get-ChildItem -LiteralPath $payload -File | Where-Object Name -CMatch '^AudioPilot(?:\.[A-Za-z0-9_-]+)*\.(exe|dll)$' | ForEach-Object FullName)
    & (Join-Path $PSScriptRoot 'validate-publish-payload.ps1') -PublishDir $payload -RuntimeIdentifier $runtime -DeploymentMode $mode -Version $version -ExpectedCommit $commit -WriteManifest @validation
    $zip = $null
    if (-not $DirectoryOnly) {
        $zip = Join-Path $versionRoot $(if ($Bootstrap) { "AudioPilot-$version-$profile.update.zip" } else { "AudioPilot-$version-win-x64.zip" })
        [IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip,[IO.Compression.CompressionLevel]::SmallestSize,$false)
    }
    $msi = $null
    if ($selfContained -and -not $SkipInstallers) {
    $installer = Join-Path $versionRoot 'msi'
    $platform = $runtime.Substring(4)
    Invoke-DotNet @('build',(Join-Path $repo 'AudioPilot.Installer/AudioPilot.Installer.wixproj'),'-c','Release','--no-restore','--nologo',
        "-p:Platform=$platform","-p:AppVersion=$version","-p:Version=$version","-p:AssemblyVersion=$version.0","-p:FileVersion=$version.0", "-p:InformationalVersion=$version+$commit",
        '-p:UsePrebuiltPublish=true',"-p:Update$(if ($Bootstrap) { 'Bootstrap' } else { 'Pilot' })=true","-p:PublishDirForMsi=$payload","-p:OutputPath=$installer")
    $msi = Join-Path $versionRoot "AudioPilot-$version-$runtime.msi"
    Copy-Item -LiteralPath (Join-Path $installer "AudioPilot-$version-$platform.msi") -Destination $msi
    Sign-PilotFiles @($msi)
    }
    $publishedDependencies = @(Get-AudioPilotPublishedDependencies -Directory $payload -Profile $profile)
    $dependencies = @(Get-AudioPilotReleaseDependencies -LockDependencies @() -PublishedDependencies $publishedDependencies)
    $artifacts = @(@($zip,$msi) | Where-Object { $_ } | ForEach-Object { [pscustomobject]@{file=[IO.Path]::GetFileName($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant();profile=$profile} })
    Write-ReleaseSbom -Path (Join-Path $versionRoot 'SBOM.spdx.json') -Version $version -Repository 'Haanuwaa/AudioPilot' -ReleaseTag $(if ($Bootstrap) { "v$version" } else { "updater-staging-feed" }) -ArtifactRecords $artifacts -Dependencies $dependencies
    $dependencies | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $versionRoot 'dependencies.json') -Encoding utf8NoBOM
    $records += @{version=$version;zip=$zip;msi=$msi;payload=$payload;runtime=$runtime;mode=$mode;profile=$profile}
}
}
}
$initialPackages = @()
foreach ($baseline in @($records | Where-Object { $Bootstrap -or $_.version -ceq $BaselineVersion })) {
$initialVersion = $baseline.version
$runtime = $baseline.runtime
$profile = $baseline.profile
$portable = Join-Path $output $(if ($Bootstrap) { "portable-initial-$profile/AudioPilot" } else { "portable-initial/AudioPilot" })
[IO.Directory]::CreateDirectory($portable) | Out-Null
$workspace = Join-Path $portable 'app'
[IO.Directory]::CreateDirectory($workspace) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $portable 'data')) | Out-Null
$generation = "$initialVersion`_$([Guid]::NewGuid().ToString('N'))"
$versioned = Join-Path $workspace "versions/$generation"
[IO.Directory]::CreateDirectory($versioned) | Out-Null
@{Schema=1;Kind='generation';Identity=$generation} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $versioned '.updater-owned.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $bridge "$runtime/AudioPilot.exe"),(Join-Path $bridge "$runtime/AudioPilot.Cli.exe") -Destination $portable
Sign-PilotFiles @((Join-Path $portable 'AudioPilot.exe'),(Join-Path $portable 'AudioPilot.Cli.exe'))
Get-ChildItem -LiteralPath $baseline.payload -Force | Copy-Item -Destination $versioned -Recurse
[IO.Directory]::CreateDirectory((Join-Path $portable 'licenses')) | Out-Null
foreach ($document in @('ABOUT.txt','licenses/LICENSE','licenses/THIRD-PARTY-NOTICES.txt','licenses/UPDATER-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $versioned $document) -Destination (Join-Path $portable $document)
}
[IO.File]::WriteAllText((Join-Path $workspace '.updater'),'AudioPilot updater workspace 1')
@{Schema=1;DiagnosticId=[Guid]::NewGuid().ToString('N');Managed=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace 'portable-layout.json') -Encoding utf8NoBOM
@{Schema=1;Current=$generation;Previous=$null;PendingHealth=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace 'deployment.json') -Encoding utf8NoBOM
[IO.File]::WriteAllText((Join-Path $workspace '.documents-generation'),$generation,[Text.UTF8Encoding]::new($false))
$initialZip = $null
if (-not $DirectoryOnly) {
    $initialZip = Join-Path $output $(if ($Bootstrap) { "AudioPilot-$initialVersion-$profile.zip" } else { "AudioPilot-$BaselineVersion-pilot-win-x64.zip" })
    [IO.Compression.ZipFile]::CreateFromDirectory($portable,$initialZip,[IO.Compression.CompressionLevel]::SmallestSize,$true)
}
$baselineArtifacts = @(@($baseline.zip,$baseline.msi,$initialZip) | Where-Object { $_ } | ForEach-Object {
    [pscustomobject]@{file=[IO.Path]::GetFileName($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant();profile=$profile}
})
$baselineDependencies = @(Get-Content -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($baseline.payload)) "dependencies.json") -Raw | ConvertFrom-Json)
Write-ReleaseSbom -Path (Join-Path ([IO.Path]::GetDirectoryName($baseline.payload)) "SBOM.spdx.json") -Version $initialVersion -Repository 'Haanuwaa/AudioPilot' -ReleaseTag $(if ($Bootstrap) { "v$initialVersion" } else { "updater-staging-feed" }) -ArtifactRecords $baselineArtifacts -Dependencies $baselineDependencies
if ($initialZip) { $initialPackages += $initialZip }
}
if ($Bootstrap -and -not $DirectoryOnly) {
    $targets = foreach ($record in $records) {
        foreach ($kind in @('portable','msi')) {
            $package = if ($kind -ceq 'msi') { $record.msi } else { $record.zip }
            if (-not $package) { continue }
            [ordered]@{name=[IO.Path]::GetFileName($package);path=$package;length=(Get-Item -LiteralPath $package).Length;
                sha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash;
                identity=[ordered]@{product='AudioPilot';channel='stable';version=$record.version;kind=$kind;architecture=$record.runtime;
                    runtime=$(if ($record.mode -ceq 'SelfContained') { 'self-contained' } else { 'framework-dependent' });
                    minimumHelperVersion=$MinimumHelperVersion;windowsPublisherSha256=$policy.PublisherSha256;windowsTrust='self-signed'} }
        }
    }
    @{schema=1;approved=$false;publicLiveUpdates=[bool]$policy.PublicUpdatesEnabled;targets=@($targets)} | ConvertTo-Json -Depth 7 |
        Set-Content -LiteralPath (Join-Path $output 'update-targets.json') -Encoding utf8NoBOM
}
@($records | ForEach-Object { $_.zip; if ($_.msi) { $_.msi } }) + $initialPackages | Where-Object { $_ } | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetRelativePath($output,$_).Replace('\','/'))"
} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $PolicyDirectory $(if ($Bootstrap) { 'update-publisher.cer' } else { 'pilot-publisher.cer' })) -Destination $output
@{schema=1;sourceCommit=$commit;sourceDirty=($sourceChanges.Count -gt 0);policySha256=(Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash;publisherSha256=$policy.PublisherSha256;packages=$records;portableInitial=$(if ($Bootstrap) { ,$initialPackages } else { $initialPackages[0] });channel=$(if ($Bootstrap) { "stable" } else { "staging" });publicLiveUpdates=($Bootstrap -and $policy.PublicUpdatesEnabled);approval=$(if ($DirectoryOnly) { 'Local folder build; no release archives or feed targets.' } else { 'Pending independent signature/cabinet verification and MSI/portable acceptance tests in Sandbox.' })} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output $(if ($Bootstrap) { 'update-build.json' } else { 'pilot-build.json' })) -Encoding utf8NoBOM
Get-Content -LiteralPath (Join-Path $output $(if ($Bootstrap) { 'update-build.json' } else { 'pilot-build.json' }))
