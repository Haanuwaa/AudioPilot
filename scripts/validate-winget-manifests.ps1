[CmdletBinding()]
param(
    [string]$ManifestRoot = "artifacts/release/winget",
    [string]$ReleaseRoot = "artifacts/release",
    [string]$Version,
    [string]$Repository = "Haanuwaa/AudioPilot",
    [string]$PackageIdentifier = "Haanuwaa.AudioPilot",
    [string]$ManifestVersion = "1.12.0",
    [string]$MinimumOSVersion = "10.0.19041.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "lib/Msi.ps1")

function ConvertFrom-YamlScalar {
    param([string]$Value)

    $trimmed = $Value.Trim()
    if ($trimmed.Length -ge 2 -and $trimmed.StartsWith("'") -and $trimmed.EndsWith("'")) {
        return $trimmed.Substring(1, $trimmed.Length - 2).Replace("''", "'")
    }

    if ($trimmed.Length -ge 2 -and $trimmed.StartsWith('"') -and $trimmed.EndsWith('"')) {
        return $trimmed.Substring(1, $trimmed.Length - 2).Replace('\"', '"').Replace('\\', '\')
    }

    return $trimmed
}

function Get-YamlValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    $match = [regex]::Match($Content, "(?m)^$([regex]::Escape($Key)):\s*(?<value>.+?)\s*$")
    if (-not $match.Success) {
        return $null
    }

    return ConvertFrom-YamlScalar -Value $match.Groups["value"].Value
}

function Assert-Equal {
    param(
        [string]$Actual,
        [string]$Expected,
        [string]$Description
    )

    if ($Actual -ne $Expected) {
        throw "$Description Expected '$Expected', actual '$Actual'."
    }
}

function Assert-Match {
    param(
        [string]$Value,
        [string]$Pattern,
        [string]$Description
    )

    if ($Value -notmatch $Pattern) {
        throw "$Description Value '$Value' does not match '$Pattern'."
    }
}

function Get-InstallerBlock {
    param(
        [string]$Content,
        [string]$Architecture
    )

    $escapedArchitecture = [regex]::Escape($Architecture)
    $pattern = "(?ms)^-\s+Architecture:\s*'?$escapedArchitecture'?\s*(?<block>.*?)(?=^-\s+Architecture:|^ManifestType:)"
    $match = [regex]::Match($Content, $pattern)
    if (-not $match.Success) {
        throw "Winget installer manifest is missing architecture '$Architecture'."
    }

    return $match.Value
}

function Get-IndentedYamlValue {
    param(
        [string]$Content,
        [string]$Key
    )

    $match = [regex]::Match($Content, "(?m)^\s+$([regex]::Escape($Key)):\s*(?<value>.+?)\s*$")
    if (-not $match.Success) {
        return $null
    }

    return ConvertFrom-YamlScalar -Value $match.Groups["value"].Value
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
function Resolve-RepoRelativePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return (Join-Path $repoRoot $Path)
}

$manifestRootPath = Resolve-RepoRelativePath -Path $ManifestRoot
$releaseRootPath = Resolve-RepoRelativePath -Path $ReleaseRoot

if (-not (Test-Path -LiteralPath $manifestRootPath)) {
    throw "Winget manifest root not found: $manifestRootPath"
}

$versionManifestCandidates = @(Get-ChildItem -LiteralPath $manifestRootPath -Recurse -File -Filter "$PackageIdentifier.yaml")
if ($versionManifestCandidates.Count -eq 0) {
    throw "No winget version manifest found under: $manifestRootPath"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    if ($versionManifestCandidates.Count -ne 1) {
        throw "Version was not provided and multiple winget version manifests were found under: $manifestRootPath"
    }

    $Version = Get-YamlValue -Content (Get-Content -LiteralPath $versionManifestCandidates[0].FullName -Raw) -Key "PackageVersion"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Unable to resolve winget manifest version."
}

$packageSegments = $PackageIdentifier -split '\.'
$manifestDirectory = Join-Path $manifestRootPath $packageSegments[0].Substring(0, 1).ToLowerInvariant()
foreach ($segment in $packageSegments) {
    $manifestDirectory = Join-Path $manifestDirectory $segment
}

$manifestDirectory = Join-Path $manifestDirectory $Version
if (-not (Test-Path -LiteralPath $manifestDirectory)) {
    throw "Expected winget manifest directory missing: $manifestDirectory"
}

$versionManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.yaml"
$installerManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.installer.yaml"
$localeManifestPath = Join-Path $manifestDirectory "$PackageIdentifier.locale.en-US.yaml"

foreach ($path in @($versionManifestPath, $installerManifestPath, $localeManifestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected winget manifest file missing: $path"
    }
}

$manifestFiles = @(Get-ChildItem -LiteralPath $manifestDirectory -File -Filter "*.yaml")
if ($manifestFiles.Count -ne 3) {
    throw "Expected exactly 3 winget manifest files in '$manifestDirectory', found $($manifestFiles.Count)."
}

$versionManifest = Get-Content -LiteralPath $versionManifestPath -Raw
$installerManifest = Get-Content -LiteralPath $installerManifestPath -Raw
$localeManifest = Get-Content -LiteralPath $localeManifestPath -Raw

foreach ($content in @($versionManifest, $installerManifest, $localeManifest)) {
    Assert-Equal -Actual (Get-YamlValue -Content $content -Key "PackageIdentifier") -Expected $PackageIdentifier -Description "PackageIdentifier mismatch."
    Assert-Equal -Actual (Get-YamlValue -Content $content -Key "PackageVersion") -Expected $Version -Description "PackageVersion mismatch."
    Assert-Equal -Actual (Get-YamlValue -Content $content -Key "ManifestVersion") -Expected $ManifestVersion -Description "ManifestVersion mismatch."
}

Assert-Equal -Actual (Get-YamlValue -Content $versionManifest -Key "DefaultLocale") -Expected "en-US" -Description "DefaultLocale mismatch."
Assert-Equal -Actual (Get-YamlValue -Content $versionManifest -Key "ManifestType") -Expected "version" -Description "Version manifest type mismatch."
Assert-Equal -Actual (Get-YamlValue -Content $installerManifest -Key "ManifestType") -Expected "installer" -Description "Installer manifest type mismatch."
Assert-Equal -Actual (Get-YamlValue -Content $localeManifest -Key "ManifestType") -Expected "defaultLocale" -Description "Locale manifest type mismatch."
Assert-Equal -Actual (Get-YamlValue -Content $localeManifest -Key "PackageLocale") -Expected "en-US" -Description "PackageLocale mismatch."

Assert-Equal -Actual (Get-YamlValue -Content $installerManifest -Key "InstallerType") -Expected "msi" -Description "InstallerType mismatch."
Assert-Equal -Actual (Get-YamlValue -Content $installerManifest -Key "Scope") -Expected "user" -Description "Scope mismatch."
Assert-Equal -Actual (Get-YamlValue -Content $installerManifest -Key "UpgradeBehavior") -Expected "install" -Description "UpgradeBehavior must preserve the MSI transactional major upgrade."
Assert-Equal -Actual (Get-YamlValue -Content $installerManifest -Key "MinimumOSVersion") -Expected $MinimumOSVersion -Description "MinimumOSVersion mismatch."

if ($installerManifest -notmatch "(?m)^Platform:\s*$\r?\n-\s*'?(Windows\.Desktop)'?\s*$") {
    throw "Installer manifest must declare Platform: Windows.Desktop."
}

if ($installerManifest -match "(?m)Architecture:\s*'?x86'?\s*$") {
    throw "Winget manifest must stay MSI-only for x64 and arm64; x86 MSI entries are not expected."
}

$repositoryParts = $Repository.Split("/", 2, [System.StringSplitOptions]::RemoveEmptyEntries)
if ($repositoryParts.Count -ne 2) {
    throw "Repository must be in 'owner/name' format. Actual value: $Repository"
}

$releaseBaseUrl = "https://github.com/$Repository/releases/download/v$Version"
$x64Block = Get-InstallerBlock -Content $installerManifest -Architecture "x64"
$arm64Block = Get-InstallerBlock -Content $installerManifest -Architecture "arm64"

foreach ($entry in @(
        @{ Architecture = "x64"; Block = $x64Block },
        @{ Architecture = "arm64"; Block = $arm64Block }
    )) {
    $architecture = $entry.Architecture
    $block = $entry.Block
    $expectedFileName = "AudioPilot-$Version-$architecture.msi"
    $expectedUrl = "$releaseBaseUrl/$expectedFileName"
    $installerPath = Join-Path $releaseRootPath $expectedFileName

    $url = Get-IndentedYamlValue -Content $block -Key "InstallerUrl"
    $hash = Get-IndentedYamlValue -Content $block -Key "InstallerSha256"
    $productCode = Get-IndentedYamlValue -Content $block -Key "ProductCode"
    $upgradeCode = Get-IndentedYamlValue -Content $block -Key "UpgradeCode"

    Assert-Equal -Actual $url -Expected $expectedUrl -Description "InstallerUrl mismatch for $architecture."
    Assert-Match -Value $hash -Pattern '^[A-F0-9]{64}$' -Description "InstallerSha256 mismatch for $architecture."
    Assert-Match -Value $productCode -Pattern '^\{[0-9A-Fa-f-]{36}\}$' -Description "ProductCode mismatch for $architecture."
    Assert-Match -Value $upgradeCode -Pattern '^\{[0-9A-Fa-f-]{36}\}$' -Description "UpgradeCode mismatch for $architecture."

    if (-not (Test-Path -LiteralPath $installerPath)) {
        throw "Expected MSI artifact for winget validation is missing: $installerPath"
    }

    $actualHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
    Assert-Equal -Actual $actualHash -Expected $hash -Description "InstallerSha256 does not match MSI artifact for $architecture."
    Assert-Equal -Actual (Get-AudioPilotMsiProperty -Path $installerPath -PropertyName "ProductCode") -Expected $productCode -Description "ProductCode does not match MSI artifact for $architecture."
    Assert-Equal -Actual (Get-AudioPilotMsiProperty -Path $installerPath -PropertyName "UpgradeCode") -Expected $upgradeCode -Description "UpgradeCode does not match MSI artifact for $architecture."
}

$wingetCommand = Get-Command winget -ErrorAction SilentlyContinue
$wingetCreateCommand = Get-Command wingetcreate -ErrorAction SilentlyContinue
if ($wingetCommand) {
    Write-Host "winget is available; external validation is not required by this repo-local check and was skipped."
}
elseif ($wingetCreateCommand) {
    Write-Host "wingetcreate is available; external validation is not required by this repo-local check and was skipped."
}
else {
    Write-Host "winget/wingetcreate not found; skipped optional external winget validation."
}

Write-Host "Winget manifest validation passed."
