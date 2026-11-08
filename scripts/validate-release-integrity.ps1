[CmdletBinding()]
param(
    [string]$ReleaseRoot = "artifacts/release",
    [string]$ExpectedCommit,
    [switch]$AllowDirtySource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "lib/Msi.ps1")
. (Join-Path $PSScriptRoot 'lib/ReleaseDependencies.ps1')
. (Join-Path $PSScriptRoot 'lib/ReleaseMetadata.ps1')

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $ExpectedCommit = & git -C $repoRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine expected release commit.' }
}
if ($ExpectedCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid expected release commit.' }
$releaseRootPath = [IO.Path]::GetFullPath($ReleaseRoot, $repoRoot.Path)

if (-not (Test-Path $releaseRootPath)) {
    throw "Release root not found: $releaseRootPath"
}

$manifestPath = Join-Path $releaseRootPath "release-manifest.json"
$checksumsPath = Join-Path $releaseRootPath "SHA256SUMS.txt"

if (-not (Test-Path $manifestPath)) {
    throw "Release manifest not found: $manifestPath"
}

if (-not (Test-Path $checksumsPath)) {
    throw "Checksums file not found: $checksumsPath"
}

[object]$manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
if ($null -eq $manifest.packages -or $manifest.packages.Count -eq 0) {
    throw "No packages listed in manifest: $manifestPath"
}

$checksumMap = @{}
$checksumLines = Get-Content -Path $checksumsPath
foreach ($line in $checksumLines) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    if ($line -notmatch '^([a-fA-F0-9]{64})\s+\*(.+)$') {
        throw "Invalid checksum line format: '$line'"
    }

    $hash = $Matches[1].ToLowerInvariant()
    $fileName = $Matches[2].Trim()
    if ($checksumMap.ContainsKey($fileName)) { throw "Duplicate checksum entry: $fileName" }
    $checksumMap[$fileName] = $hash
}

if ($checksumMap.Count -eq 0) {
    throw "No checksum entries found in: $checksumsPath"
}

$manifestPackageCount = [int]$manifest.packageCount
if ($manifestPackageCount -ne $manifest.packages.Count) {
    throw "Manifest packageCount mismatch: packageCount=$manifestPackageCount actual=$($manifest.packages.Count)"
}

$installerCount = 0
if ($null -ne $manifest.installers) {
    $installerCount = $manifest.installers.Count
}

$wingetManifestCount = 0
if ($null -ne $manifest.wingetManifests) {
    $wingetManifestCount = $manifest.wingetManifests.Count
}

$metadataArtifactCount = 0
if ($null -ne $manifest.metadataArtifacts) {
    $metadataArtifactCount = $manifest.metadataArtifacts.Count
}
if ($metadataArtifactCount -ne 2 -or @($manifest.metadataArtifacts | Where-Object artifactType -eq 'sbom').Count -ne 1 -or
    @($manifest.metadataArtifacts | Where-Object artifactType -eq 'provenance').Count -ne 1) {
    throw 'Release must contain exactly one SBOM and one provenance artifact.'
}

if ([int]$manifest.installerCount -ne $installerCount) {
    throw "Manifest installerCount mismatch: installerCount=$($manifest.installerCount) actual=$installerCount"
}

if ([int]$manifest.wingetManifestCount -ne $wingetManifestCount) {
    throw "Manifest wingetManifestCount mismatch: wingetManifestCount=$($manifest.wingetManifestCount) actual=$wingetManifestCount"
}

if ($null -eq $manifest.metadataArtifactCount) {
    throw "Manifest is missing metadataArtifactCount."
}

if ([int]$manifest.metadataArtifactCount -ne $metadataArtifactCount) {
    throw "Manifest metadataArtifactCount mismatch: metadataArtifactCount=$($manifest.metadataArtifactCount) actual=$metadataArtifactCount"
}

$expectedChecksumCount = $manifest.packages.Count + $installerCount + $wingetManifestCount + $metadataArtifactCount + 1
if ($checksumMap.Count -ne $expectedChecksumCount) {
    throw "Checksum entry count mismatch: checksums=$($checksumMap.Count) manifestArtifacts=$expectedChecksumCount"
}

if (-not $checksumMap.ContainsKey("release-manifest.json")) {
    throw "No checksum entry found for release-manifest.json"
}

$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifestHash -ne ([string]$checksumMap["release-manifest.json"])) {
    throw "Checksum mismatch against SHA256SUMS for release-manifest.json"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$publishedDependencies = [Collections.Generic.List[object]]::new()
$artifactHashes = @{}
$profiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if ($manifest.packages.Count -ne 6 -or $installerCount -ne 3) { throw 'Release requires six portable profiles and three installers.' }

foreach ($package in $manifest.packages) {
    $fileName = [string]$package.packageFile
    if ([string]::IsNullOrWhiteSpace($fileName) -or [IO.Path]::GetFileName($fileName) -cne $fileName -or $package.file -cne $fileName) {
        throw "Manifest contains a package entry with empty packageFile."
    }

    $zipPath = Join-Path $releaseRootPath $fileName
    if (-not (Test-Path $zipPath)) {
        throw "Manifest package file missing: $zipPath"
    }

    if (-not $checksumMap.ContainsKey($fileName)) {
        throw "No checksum entry found for manifest package: $fileName"
    }

    $actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedFromChecksums = [string]$checksumMap[$fileName]
    $expectedFromManifest = ([string]$package.sha256).ToLowerInvariant()

    if ($actualHash -ne $expectedFromChecksums) {
        throw "Checksum mismatch against SHA256SUMS for $fileName"
    }

    if ($actualHash -ne $expectedFromManifest) {
        throw "Checksum mismatch against manifest for $fileName"
    }

    $mode = [string]$package.mode
    $rid = [string]$package.runtimeIdentifier
    if ($mode -notin @('SelfContained', 'FrameworkDependent') -or $rid -notin @('win-x86', 'win-x64', 'win-arm64') -or
        $package.profile -cne "$mode-$rid" -or -not $profiles.Add($package.profile)) { throw 'Invalid or duplicate portable profile.' }
    if ((Get-Item -LiteralPath $zipPath).Length -ne $package.sizeBytes) { throw "Package size mismatch: $fileName" }
    $artifactHashes[$fileName] = @{ SHA256 = $actualHash }
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ("AudioPilot-release-" + [Guid]::NewGuid().ToString('N'))
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if (-not $name.StartsWith('AudioPilot/', [StringComparison]::Ordinal) -or $name.Contains('\') -or
                @($name.TrimEnd('/') -split '/' | Where-Object { $_ -in @('', '.', '..') -or $_.Contains(':') }).Count -or
                -not $names.Add($name) -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) {
                throw "Unsafe or duplicate archive entry: $fileName -> $name"
            }
        }
        [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $temporary)
        $payload = Join-Path $temporary 'AudioPilot'
        & (Join-Path $PSScriptRoot 'validate-publish-payload.ps1') -PublishDir $payload -RuntimeIdentifier $rid `
            -Version $manifest.version -DeploymentMode $mode -ExpectedCommit $ExpectedCommit -FromArchive
        foreach ($dependency in @(Get-AudioPilotPublishedDependencies -Directory $payload -Profile $package.profile)) {
            $publishedDependencies.Add($dependency)
        }
    }
    finally {
        $archive.Dispose()
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    }
}

function Assert-ArtifactChecksum {
    param(
        [string]$FileName,
        [string]$ExpectedHash
    )

    if ([IO.Path]::GetFileName($FileName) -cne $FileName) { throw "Invalid artifact filename: $FileName" }
    $path = Join-Path $releaseRootPath $FileName
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Manifest artifact missing: $path"
    }

    if (-not $checksumMap.ContainsKey($FileName)) {
        throw "No checksum entry found for artifact: $FileName"
    }

    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedFromChecksums = [string]$checksumMap[$FileName]
    $expectedFromManifest = $ExpectedHash.ToLowerInvariant()

    if ($actualHash -ne $expectedFromChecksums) {
        throw "Checksum mismatch against SHA256SUMS for $FileName"
    }

    if ($actualHash -ne $expectedFromManifest) {
        throw "Checksum mismatch against manifest for $FileName"
    }

    $artifactHashes[$FileName] = @{ SHA256 = $actualHash }
    return $path
}

function Assert-MsiInstallerMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion,

        [Parameter(Mandatory = $true)]
        [ValidateSet("x86", "x64", "arm64")]
        [string]$ExpectedArchitecture
    )

    Assert-AudioPilotMsiIdentity -Path $Path -Version $ExpectedVersion -Architecture $ExpectedArchitecture

    $requiredGuidProperties = @("ProductCode", "UpgradeCode")
    foreach ($propertyName in $requiredGuidProperties) {
        $value = Get-AudioPilotMsiProperty -Path $Path -PropertyName $propertyName -AllowMissing
        if ([string]::IsNullOrWhiteSpace($value) -or $value -notmatch '^\{[0-9A-Fa-f-]{36}\}$') {
            throw "MSI '$([IO.Path]::GetFileName($Path))' has invalid or missing $propertyName property: '$value'"
        }
    }

    $installDirProperty = Get-AudioPilotMsiProperty -Path $Path -PropertyName "WIXUI_INSTALLDIR" -AllowMissing
    if ($installDirProperty -ne "INSTALLFOLDER") {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must set WIXUI_INSTALLDIR to INSTALLFOLDER. Actual value: '$installDirProperty'"
    }

    $arpNoRepairValue = Get-AudioPilotMsiProperty -Path $Path -PropertyName "ARPNOREPAIR" -AllowMissing
    if ($arpNoRepairValue -ne "1") {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must set ARPNOREPAIR=1."
    }

    if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``Property`` FROM ``AppSearch`` WHERE ``Property``='INSTALLFOLDER'")) {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must persist and rediscover INSTALLFOLDER for repair, upgrade, and uninstall."
    }

    if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``Property`` FROM ``AppSearch`` WHERE ``Property``='AUDIOPILOT_CLEAN_UNINSTALL_FOLDER'")) {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must rediscover the clean-uninstall data folder from registry."
    }

    foreach ($preferenceProperty in @("PREVIOUS_AUDIOPILOT_DATA_FOLDER", "PREVIOUS_INSTALLDESKTOPSHORTCUT", "PREVIOUS_INSTALLSTARTMENUSHORTCUT", "PREVIOUS_ADD_CLI_TO_PATH")) {
        if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``Property`` FROM ``AppSearch`` WHERE ``Property``='$preferenceProperty'")) {
            throw "MSI '$([IO.Path]::GetFileName($Path))' must rediscover persisted installer preference $preferenceProperty during repair and upgrade."
        }
    }

    foreach ($preferenceValue in @("InstallDesktopShortcut", "InstallStartMenuShortcut", "AddCliToPath")) {
        if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``Registry`` FROM ``Registry`` WHERE ``Name``='$preferenceValue'")) {
            throw "MSI '$([IO.Path]::GetFileName($Path))' must persist installer preference $preferenceValue."
        }
    }

    if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``Registry`` FROM ``Registry`` WHERE ``Name``='DataFolder' AND ``Value``='[AUDIOPILOT_DATA_FOLDER]'")) {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must persist the mutable user data root separately from INSTALLFOLDER."
    }

    if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``CloseApplication`` FROM ``Wix4CloseApplication`` WHERE ``Target``='AudioPilot.exe'")) {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must close AudioPilot.exe during upgrade and uninstall to avoid locked-file failures."
    }

    if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``RemoveFolderEx`` FROM ``Wix4RemoveFolderEx`` WHERE ``Property``='AUDIOPILOT_CLEAN_UNINSTALL_FOLDER' AND ``Condition``='AUDIOPILOT_CLEAN_UNINSTALL=""1"" AND NOT UPGRADINGPRODUCTCODE'")) {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must provide an explicit clean-uninstall path gated by AUDIOPILOT_CLEAN_UNINSTALL=1."
    }

    if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``Control`` FROM ``Control`` WHERE ``Dialog_``='CleanUninstallDlg' AND ``Control``='DeleteUserDataCheckBox' AND ``Property``='AUDIOPILOT_CLEAN_UNINSTALL'")) {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must expose a maintenance uninstall checkbox for optional user-data cleanup."
    }

    if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``Event`` FROM ``ControlEvent`` WHERE ``Dialog_``='MaintenanceTypeDlg' AND ``Control_``='RemoveButton' AND ``Event``='NewDialog' AND ``Argument``='CleanUninstallDlg'")) {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must route maintenance Remove through the clean-uninstall choice dialog."
    }

    if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``Event`` FROM ``ControlEvent`` WHERE ``Dialog_``='VerifyReadyDlg' AND ``Control_``='Back' AND ``Event``='NewDialog' AND ``Argument``='CleanUninstallDlg'")) {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must route VerifyReady Back through the clean-uninstall choice dialog during removal."
    }

    if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``Shortcut`` FROM ``Shortcut`` WHERE ``Shortcut``='UninstallShortcut' AND ``Arguments``='/i [ProductCode]'")) {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must expose a Start Menu maintenance shortcut so the clean-uninstall option is visible."
    }

    if (-not (Test-AudioPilotMsiQueryHasRows -Path $Path -Query "SELECT ``Shortcut`` FROM ``Shortcut`` WHERE ``Shortcut``='CleanUninstallShortcut' AND ``Arguments``='/x [ProductCode] AUDIOPILOT_CLEAN_UNINSTALL=1'")) {
        throw "MSI '$([IO.Path]::GetFileName($Path))' must expose an explicit clean-uninstall Start Menu shortcut."
    }

    $wildcardRemoveRows = @(Get-AudioPilotMsiWildcardRemoveFileRows -Path $Path)
    if ($wildcardRemoveRows.Count -gt 0) {
        $rowSummary = ($wildcardRemoveRows | ForEach-Object { "$($_.FileKey)@$($_.DirProperty)" }) -join ", "
        throw "MSI '$([IO.Path]::GetFileName($Path))' contains wildcard RemoveFile rows that can delete user-created settings/logs: $rowSummary"
    }
}

$architectures = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($installer in @($manifest.installers)) {
    $fileName = [string]$installer.installerFile
    if ([string]::IsNullOrWhiteSpace($fileName) -or $installer.file -cne $fileName -or -not $architectures.Add($installer.architecture)) {
        throw "Manifest contains an installer entry with empty installerFile."
    }

    $installerPath = Assert-ArtifactChecksum -FileName $fileName -ExpectedHash ([string]$installer.sha256)
    if ([IO.Path]::GetExtension($installerPath) -ne ".msi") {
        throw "Installer artifact does not have an .msi extension: $fileName"
    }

    if ((Get-Item -LiteralPath $installerPath).Length -ne $installer.sizeBytes) { throw "Installer size mismatch: $fileName" }
    Assert-MsiInstallerMetadata -Path $installerPath -ExpectedVersion ([string]$manifest.version) -ExpectedArchitecture ([string]$installer.architecture)
    $portable = @($manifest.packages | Where-Object { $_.mode -eq 'SelfContained' -and $_.runtimeIdentifier -eq "win-$($installer.architecture)" })
    if ($portable.Count -ne 1) { throw 'Each MSI must have one matching self-contained release archive.' }
    Assert-AudioPilotMsiPayload -Path $installerPath -PublishArchive (Join-Path $releaseRootPath $portable[0].packageFile)
}

if ($installerCount -gt 0) {
    Assert-AudioPilotMsiComponentBitness -Paths @($manifest.installers | ForEach-Object { Join-Path $releaseRootPath $_.installerFile })
}

foreach ($wingetManifest in @($manifest.wingetManifests)) {
    $relativePath = [string]$wingetManifest.relativePath
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        throw "Manifest contains a winget manifest entry with empty relativePath."
    }

    $normalizedRelativePath = $relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
    $artifactPath = Join-Path $releaseRootPath $normalizedRelativePath
    $fileName = [IO.Path]::GetFileName($artifactPath)
    if ([IO.Path]::GetExtension($artifactPath) -ne ".yaml") {
        throw "Winget manifest artifact does not have a .yaml extension: $relativePath"
    }

    if (-not (Test-Path -LiteralPath $artifactPath)) {
        throw "Manifest winget file missing: $artifactPath"
    }

    $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $checksumMap.ContainsKey($fileName)) {
        throw "No checksum entry found for winget manifest: $fileName"
    }

    if ($actualHash -ne ([string]$checksumMap[$fileName])) {
        throw "Checksum mismatch against SHA256SUMS for winget manifest: $relativePath"
    }

    if ($actualHash -ne ([string]$wingetManifest.sha256).ToLowerInvariant()) {
        throw "Checksum mismatch against manifest for winget manifest: $relativePath"
    }
}

foreach ($metadataArtifact in @($manifest.metadataArtifacts)) {
    $fileName = [string]$metadataArtifact.file
    if ([string]::IsNullOrWhiteSpace($fileName)) {
        throw "Manifest contains a metadata artifact entry with empty file."
    }

    $metadataPath = Assert-ArtifactChecksum -FileName $fileName -ExpectedHash ([string]$metadataArtifact.sha256)
    $artifactType = [string]$metadataArtifact.artifactType

    if ((Get-Item -LiteralPath $metadataPath).Length -ne $metadataArtifact.sizeBytes) { throw "Metadata size mismatch: $fileName" }
    $rawText = Get-Content -LiteralPath $metadataPath -Raw
    $metadata = $rawText | ConvertFrom-Json
    $artifacts = @($manifest.packages) + @($manifest.installers)
    if ($artifactType -eq 'sbom') {
        if (@($manifest.dependencies).Count -ne $manifest.dependencyCount) { throw 'Release dependency inventory count mismatch.' }
        Assert-AudioPilotReleaseSbom -Sbom $metadata -RawText $rawText -Version $manifest.version -Artifacts $artifacts `
            -Hashes $artifactHashes -PublishedDependencies $publishedDependencies.ToArray() -Dependencies @($manifest.dependencies)
    }
    elseif ($artifactType -eq 'provenance') {
        Assert-AudioPilotReleaseProvenance -Provenance $metadata -Version $manifest.version -ExpectedCommit $ExpectedCommit `
            -Artifacts $artifacts -AllowDirtySource ([bool]$AllowDirtySource)
    }
    else { throw "Unknown metadata artifact type: $artifactType" }
}

& (Join-Path $PSScriptRoot "validate-winget-manifests.ps1") `
    -ManifestRoot (Join-Path $releaseRootPath "winget") `
    -ReleaseRoot $releaseRootPath `
    -Version ([string]$manifest.version)

Write-Host "Release integrity validation passed."
Write-Host "Validated $expectedChecksumCount artifacts in $releaseRootPath"
