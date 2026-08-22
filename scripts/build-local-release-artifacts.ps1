[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version,
    [string]$Repository = "Haanuwaa/AudioPilot",
    [switch]$Clean,
    [switch]$IncludeDebugInstallers,
    [switch]$SuppressMsiValidation,
    [switch]$SkipPackage,
    [switch]$SkipIntegrityValidation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$installerProject = Join-Path $repoRoot "AudioPilot.Installer/AudioPilot.Installer.wixproj"

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Step,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host "=== $Step ==="
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path -LiteralPath $installerProject)) {
    throw "Installer project not found: $installerProject"
}

$effectiveVersion = $Version
if ([string]::IsNullOrWhiteSpace($effectiveVersion)) {
    [xml]$versionProps = Get-Content -LiteralPath (Join-Path $repoRoot "Version.props") -Raw
    $effectiveVersion = [string]($versionProps.Project.PropertyGroup.AudioPilotVersion | Select-Object -First 1)
}
if ($effectiveVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Release version must use numeric SemVer core format (for example, 1.2.3)."
}

& (Join-Path $PSScriptRoot "publish-release-profiles.ps1") -Configuration $Configuration -Version $effectiveVersion

Invoke-DotNet -Step "restore MSI installer project" -Arguments @(
    "restore",
    $installerProject,
    "--locked-mode",
    "--nologo"
)

$installerConfigurations = @($Configuration)
if ($IncludeDebugInstallers -and $Configuration -ne "Debug") {
    $installerConfigurations = @("Debug") + $installerConfigurations
}

foreach ($installerConfiguration in $installerConfigurations) {
    foreach ($platform in @("x64", "arm64")) {
        $arguments = @(
            "build",
            $installerProject,
            "-c",
            $installerConfiguration,
            "-p:Platform=$platform",
            "-p:AppVersion=$effectiveVersion",
            "-p:Version=$effectiveVersion",
            "-p:AssemblyVersion=$effectiveVersion.0",
            "-p:FileVersion=$effectiveVersion.0",
            "-p:InformationalVersion=$effectiveVersion",
            "--no-restore",
            "--nologo"
        )

        if ($SuppressMsiValidation) {
            $arguments += "-p:SuppressValidation=true"
        }

        Invoke-DotNet -Step "build MSI $platform $installerConfiguration" -Arguments $arguments
    }
}

if (-not $SkipPackage) {
    $packageArguments = @{
        Repository = $Repository
    }

    $packageArguments.Version = $effectiveVersion

    if ($Clean) {
        $packageArguments.Clean = $true
    }

    Write-Host "=== package release artifacts ==="
    & (Join-Path $PSScriptRoot "package-release.ps1") @packageArguments
}

if (-not $SkipIntegrityValidation) {
    Write-Host "=== validate release integrity ==="
    & (Join-Path $PSScriptRoot "validate-release-integrity.ps1") -ReleaseRoot "artifacts/release"
}

Write-Host ""
Write-Host "Local release artifact build completed."
