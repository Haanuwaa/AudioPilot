[CmdletBinding()]
param(
    [string]$Version,
    [string]$PackageIdentifier = "Haanuwaa.AudioPilot",
    [string]$Publisher = "Haanuwaa",
    [string]$PackageName = "AudioPilot",
    [string]$RepositoryOwner = "Haanuwaa",
    [string]$RepositoryName = "AudioPilot",
    [string]$ManifestVersion = "1.12.0",
    [string]$MinimumOSVersion = "10.0.19041.0",
    [string]$OutputRoot,
    [string]$X64InstallerPath,
    [string]$Arm64InstallerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\..\scripts\lib\Msi.ps1")

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

function Get-VersionFromProps {
    param([string]$RepoRoot)

    $versionPropsPath = Join-Path $RepoRoot "Version.props"
    [xml]$versionProps = Get-Content -Path $versionPropsPath
    return $versionProps.Project.PropertyGroup.AudioPilotVersion
}

function Ensure-Directory {
    param([string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function ConvertTo-YamlScalar {
    param([string]$Value)

    if ($null -eq $Value) {
        return "''"
    }

    return "'" + $Value.Replace("'", "''") + "'"
}

function New-YamlLine {
    param(
        [string]$Key,
        [string]$Value
    )

    return "$Key`: $(ConvertTo-YamlScalar -Value $Value)"
}

$repoRoot = Get-RepoRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot "manifests"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-VersionFromProps -RepoRoot $repoRoot
}

if ([string]::IsNullOrWhiteSpace($X64InstallerPath)) {
    $X64InstallerPath = Join-Path $repoRoot "AudioPilot.Installer\bin\x64\Release\AudioPilot-$Version-x64.msi"
}

if ([string]::IsNullOrWhiteSpace($Arm64InstallerPath)) {
    $Arm64InstallerPath = Join-Path $repoRoot "AudioPilot.Installer\bin\arm64\Release\AudioPilot-$Version-arm64.msi"
}

if (-not (Test-Path -Path $X64InstallerPath)) {
    throw "x64 installer not found: $X64InstallerPath"
}

if (-not (Test-Path -Path $Arm64InstallerPath)) {
    throw "arm64 installer not found: $Arm64InstallerPath"
}

$x64Hash = (Get-FileHash -Path $X64InstallerPath -Algorithm SHA256).Hash.ToUpperInvariant()
$arm64Hash = (Get-FileHash -Path $Arm64InstallerPath -Algorithm SHA256).Hash.ToUpperInvariant()
$x64ProductCode = Get-AudioPilotMsiProperty -Path $X64InstallerPath -PropertyName "ProductCode"
$arm64ProductCode = Get-AudioPilotMsiProperty -Path $Arm64InstallerPath -PropertyName "ProductCode"
$upgradeCode = Get-AudioPilotMsiProperty -Path $X64InstallerPath -PropertyName "UpgradeCode"
$arm64UpgradeCode = Get-AudioPilotMsiProperty -Path $Arm64InstallerPath -PropertyName "UpgradeCode"
if ($arm64UpgradeCode -ne $upgradeCode) {
    throw "x64 and arm64 MSI UpgradeCode values differ. x64=$upgradeCode arm64=$arm64UpgradeCode"
}

$releaseTag = "v$Version"
$repository = "$RepositoryOwner/$RepositoryName"
$releaseBaseUrl = "https://github.com/$repository/releases/download/$releaseTag"
$packageSegments = $PackageIdentifier -split '\.'
if ($packageSegments.Count -lt 2) {
    throw "PackageIdentifier must contain publisher and package segments. Actual value: $PackageIdentifier"
}

$manifestDirectory = Join-Path $OutputRoot $packageSegments[0].Substring(0, 1).ToLowerInvariant()
foreach ($segment in $packageSegments) {
    $manifestDirectory = Join-Path $manifestDirectory $segment
}

$manifestDirectory = Join-Path $manifestDirectory $Version
Ensure-Directory -Path $manifestDirectory

$versionManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.yaml"
$installerManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.installer.yaml"
$localeManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.locale.en-US.yaml"

$versionManifestLines = @(
    New-YamlLine -Key "PackageIdentifier" -Value $PackageIdentifier
    New-YamlLine -Key "PackageVersion" -Value $Version
    New-YamlLine -Key "DefaultLocale" -Value "en-US"
    New-YamlLine -Key "ManifestType" -Value "version"
    New-YamlLine -Key "ManifestVersion" -Value $ManifestVersion
)

$installerManifestLines = @(
    New-YamlLine -Key "PackageIdentifier" -Value $PackageIdentifier
    New-YamlLine -Key "PackageVersion" -Value $Version
    "Platform:"
    "- $(ConvertTo-YamlScalar -Value 'Windows.Desktop')"
    New-YamlLine -Key "MinimumOSVersion" -Value $MinimumOSVersion
    New-YamlLine -Key "InstallerType" -Value "msi"
    New-YamlLine -Key "Scope" -Value "user"
    New-YamlLine -Key "UpgradeBehavior" -Value "uninstallPrevious"
    "Installers:"
    "- Architecture: $(ConvertTo-YamlScalar -Value 'x64')"
    "  InstallerUrl: $(ConvertTo-YamlScalar -Value "$releaseBaseUrl/AudioPilot-$Version-x64.msi")"
    "  InstallerSha256: $(ConvertTo-YamlScalar -Value $x64Hash)"
    "  ProductCode: $(ConvertTo-YamlScalar -Value $x64ProductCode)"
    "  AppsAndFeaturesEntries:"
    "  - DisplayName: $(ConvertTo-YamlScalar -Value $PackageName)"
    "    DisplayVersion: $(ConvertTo-YamlScalar -Value $Version)"
    "    Publisher: $(ConvertTo-YamlScalar -Value $Publisher)"
    "    ProductCode: $(ConvertTo-YamlScalar -Value $x64ProductCode)"
    "    UpgradeCode: $(ConvertTo-YamlScalar -Value $upgradeCode)"
    "- Architecture: $(ConvertTo-YamlScalar -Value 'arm64')"
    "  InstallerUrl: $(ConvertTo-YamlScalar -Value "$releaseBaseUrl/AudioPilot-$Version-arm64.msi")"
    "  InstallerSha256: $(ConvertTo-YamlScalar -Value $arm64Hash)"
    "  ProductCode: $(ConvertTo-YamlScalar -Value $arm64ProductCode)"
    "  AppsAndFeaturesEntries:"
    "  - DisplayName: $(ConvertTo-YamlScalar -Value $PackageName)"
    "    DisplayVersion: $(ConvertTo-YamlScalar -Value $Version)"
    "    Publisher: $(ConvertTo-YamlScalar -Value $Publisher)"
    "    ProductCode: $(ConvertTo-YamlScalar -Value $arm64ProductCode)"
    "    UpgradeCode: $(ConvertTo-YamlScalar -Value $upgradeCode)"
    New-YamlLine -Key "ManifestType" -Value "installer"
    New-YamlLine -Key "ManifestVersion" -Value $ManifestVersion
)

$localeManifestLines = @(
    New-YamlLine -Key "PackageIdentifier" -Value $PackageIdentifier
    New-YamlLine -Key "PackageVersion" -Value $Version
    New-YamlLine -Key "PackageLocale" -Value "en-US"
    New-YamlLine -Key "Publisher" -Value $Publisher
    New-YamlLine -Key "PublisherUrl" -Value "https://github.com/$RepositoryOwner"
    New-YamlLine -Key "PublisherSupportUrl" -Value "https://github.com/$repository/issues"
    New-YamlLine -Key "PackageName" -Value $PackageName
    New-YamlLine -Key "PackageUrl" -Value "https://github.com/$repository"
    New-YamlLine -Key "License" -Value "MIT"
    New-YamlLine -Key "LicenseUrl" -Value "https://github.com/$repository/blob/main/LICENSE"
    New-YamlLine -Key "ReleaseNotesUrl" -Value "https://github.com/$repository/releases/tag/$releaseTag"
    New-YamlLine -Key "Copyright" -Value "Copyright (c) 2026 Haanuwaa"
    New-YamlLine -Key "ShortDescription" -Value "Audio switcher with hotkeys, mixers, routines, and profiles for Windows."
    New-YamlLine -Key "Moniker" -Value "audiopilot"
    "Tags:"
    "- $(ConvertTo-YamlScalar -Value 'audio')"
    "- $(ConvertTo-YamlScalar -Value 'hotkeys')"
    "- $(ConvertTo-YamlScalar -Value 'mixer')"
    "- $(ConvertTo-YamlScalar -Value 'profiles')"
    "- $(ConvertTo-YamlScalar -Value 'routines')"
    "- $(ConvertTo-YamlScalar -Value 'sound')"
    "- $(ConvertTo-YamlScalar -Value 'windows')"
    New-YamlLine -Key "ManifestType" -Value "defaultLocale"
    New-YamlLine -Key "ManifestVersion" -Value $ManifestVersion
)

Set-Content -Path $versionManifestPath -Value $versionManifestLines -Encoding UTF8
Set-Content -Path $installerManifestPath -Value $installerManifestLines -Encoding UTF8
Set-Content -Path $localeManifestPath -Value $localeManifestLines -Encoding UTF8

Write-Host "Generated winget manifests in $manifestDirectory"
