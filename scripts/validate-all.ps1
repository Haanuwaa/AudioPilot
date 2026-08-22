param(
    [string]$SolutionPath = "AudioPilot.sln",
    [string]$FormatSolutionPath = "AudioPilot.Format.slnf",
    [switch]$IncludeIntegration,
    [switch]$IncludeStress,
    [switch]$Coverage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Step {
    param(
        [string]$Label,
        [string]$ScriptPath,
        [string[]]$ScriptArgs = @()
    )

    Write-Host "==> $Label"
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @ScriptArgs
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Invoke-Step -Label "Build solution" -ScriptPath "scripts/build.ps1" -ScriptArgs @("-SolutionPath", $SolutionPath)

$testCategory = if ($IncludeIntegration -and $IncludeStress) {
    "full"
}
elseif ($IncludeIntegration) {
    "integration"
}
elseif ($IncludeStress) {
    "stress"
}
else {
    "unit"
}

$testArgs = @("-Category", $testCategory)
if ($Coverage) {
    $testArgs += "-Coverage"
}

Invoke-Step -Label "Run tests ($testCategory)" -ScriptPath "scripts/run-tests.ps1" -ScriptArgs $testArgs
Invoke-Step -Label "Audit static test-hook isolation" -ScriptPath "scripts/validate-test-isolation.ps1"
Invoke-Step -Label "Validate line endings" -ScriptPath "scripts/validate-line-endings.ps1"
Invoke-Step -Label "Validate full solution formatting" -ScriptPath "scripts/validate-format.ps1" -ScriptArgs @("-Action", "check", "-SolutionPath", $FormatSolutionPath)
Invoke-Step -Label "Validate changed-file formatting" -ScriptPath "scripts/check-format-changed-files.ps1" -ScriptArgs @("-SolutionPath", $FormatSolutionPath)
Invoke-Step -Label "Validate generated CLI docs blocks" -ScriptPath "scripts/update-cli-docs.ps1" -ScriptArgs @("-Check")
Invoke-Step -Label "Validate documentation links" -ScriptPath "scripts/validate-doc-links.ps1"
Invoke-Step -Label "Validate release gate policy" -ScriptPath "scripts/validate-release-gate-policy.ps1"
