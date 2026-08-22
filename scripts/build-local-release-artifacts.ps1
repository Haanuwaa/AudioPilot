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
$buildStarted = [Diagnostics.Stopwatch]::StartNew()
$stepTimings = [Collections.Generic.List[object]]::new()

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Step,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host "=== $Step ==="
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
    $stepTimings.Add([pscustomobject]@{ Step = $Step; Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 2) })
    Write-Host ("Completed {0} in {1:N1}s" -f $Step, $watch.Elapsed.TotalSeconds)
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

$publishWatch = [Diagnostics.Stopwatch]::StartNew()
& (Join-Path $PSScriptRoot "publish-release-profiles.ps1") -Configuration $Configuration -Version $effectiveVersion
$stepTimings.Add([pscustomobject]@{ Step = 'publish portable profiles'; Seconds = [math]::Round($publishWatch.Elapsed.TotalSeconds, 2) })

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

        if ($installerConfiguration -eq 'Release' -and $Configuration -eq 'Release') {
            $arguments += '-p:UsePrebuiltPublish=true'
            $arguments += "-p:PublishDirForMsi=$(Join-Path $repoRoot "artifacts/publish/SelfContained/win-$platform")"
        }

        Invoke-DotNet -Step "build MSI $platform $installerConfiguration" -Arguments $arguments
    }
}

if (-not $SkipPackage) {
    $packageWatch = [Diagnostics.Stopwatch]::StartNew()
    $packageArguments = @{
        Repository = $Repository
    }

    $packageArguments.Version = $effectiveVersion

    if ($Clean) {
        $packageArguments.Clean = $true
    }

    Write-Host "=== package release artifacts ==="
    & (Join-Path $PSScriptRoot "package-release.ps1") @packageArguments
    $stepTimings.Add([pscustomobject]@{ Step = 'package release artifacts'; Seconds = [math]::Round($packageWatch.Elapsed.TotalSeconds, 2) })
}

if (-not $SkipIntegrityValidation) {
    $integrityWatch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "=== validate release integrity ==="
    & (Join-Path $PSScriptRoot "validate-release-integrity.ps1") -ReleaseRoot "artifacts/release"
    $stepTimings.Add([pscustomobject]@{ Step = 'validate release integrity'; Seconds = [math]::Round($integrityWatch.Elapsed.TotalSeconds, 2) })
}

Write-Host ""
Write-Host "Local release artifact build completed."
$stepTimings | Format-Table -AutoSize
Write-Host ("Total elapsed: {0:N1}s" -f $buildStarted.Elapsed.TotalSeconds)
$stepTimings | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts/local-build-timings.json') -Encoding utf8
