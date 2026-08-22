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

$testCategories = @('unit')
if ($IncludeIntegration) { $testCategories += 'integration' }
if ($IncludeStress) { $testCategories += 'stress' }
foreach ($category in $testCategories) {
    $testArgs = @('-Category', $category, '-NoBuild', '-NoRestore')
    if ($Coverage) { $testArgs += '-Coverage' }
    Invoke-Step -Label "Run tests ($category)" -ScriptPath 'scripts/run-tests.ps1' -ScriptArgs $testArgs
}

if ($Coverage) {
    Invoke-Step -Label 'Enforce unit coverage baseline' -ScriptPath 'scripts/validate-coverage.ps1'
}
Invoke-Step -Label 'Validate script behavior' -ScriptPath 'scripts/tests/test-runner.ps1'
Invoke-Step -Label 'Validate local validation scripts' -ScriptPath 'scripts/tests/test-validation.ps1'
Invoke-Step -Label 'Validate MSI inspection helpers' -ScriptPath 'scripts/tests/test-msi-helpers.ps1'
Invoke-Step -Label "Audit static test-hook isolation" -ScriptPath "scripts/validate-test-isolation.ps1"
Invoke-Step -Label "Validate line endings" -ScriptPath "scripts/validate-line-endings.ps1"
Invoke-Step -Label "Validate full solution formatting" -ScriptPath "scripts/validate-format.ps1" -ScriptArgs @("-Action", "check", "-SolutionPath", $FormatSolutionPath)
Invoke-Step -Label "Validate generated CLI docs blocks" -ScriptPath "scripts/update-cli-docs.ps1" -ScriptArgs @("-Check", "-NoBuild")
Invoke-Step -Label "Validate documentation links" -ScriptPath "scripts/validate-doc-links.ps1"
Invoke-Step -Label "Validate release gate policy" -ScriptPath "scripts/validate-release-gate-policy.ps1"
