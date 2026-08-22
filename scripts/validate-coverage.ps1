[CmdletBinding()]
param(
    [string]$CoverageRoot = "artifacts/testresults/coverage/unit",
    [string]$PolicyPath = ".github/quality/coverage-policy.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PolicyPath -PathType Leaf)) {
    throw "Coverage policy file not found: $PolicyPath"
}

if (-not (Test-Path -LiteralPath $CoverageRoot -PathType Container)) {
    throw "Coverage results directory not found: $CoverageRoot"
}

$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
[double]$minimumCoverage = [double]$policy.minimumCoveragePercent
[double]$ratchetStep = [double]$policy.ratchetStepPercent
[double]$nextTarget = [double]$policy.nextTargetPercent

if ($minimumCoverage -le 0) {
    throw "Coverage policy minimumCoveragePercent must be > 0."
}

if ($ratchetStep -le 0) {
    throw "Coverage policy ratchetStepPercent must be > 0."
}

if ($nextTarget -lt $minimumCoverage) {
    throw "Coverage policy invalid: nextTargetPercent ($nextTarget) is below minimumCoveragePercent ($minimumCoverage)."
}

$coverageFiles = @(Get-ChildItem -LiteralPath $CoverageRoot -Recurse -Filter "*.cobertura.xml")
if ($coverageFiles.Count -ne 1) {
    throw "Expected exactly one Cobertura report in '$CoverageRoot', but found $($coverageFiles.Count). Run scripts/run-tests.ps1 with -Coverage to create a clean report directory."
}

$lineHits = @{}
[long]$excludedGeneratedLines = 0
[xml]$coverage = Get-Content -LiteralPath $coverageFiles[0].FullName -Raw
foreach ($class in $coverage.coverage.packages.package.classes.class) {
    $sourcePath = [string]$class.filename
    $classLines = @($class.lines.line)

    if ($sourcePath -match '[\\/]obj[\\/]' -or $sourcePath -match '\.g(?:\.i)?\.cs$') {
        $excludedGeneratedLines += $classLines.Count
        continue
    }

    foreach ($line in $classLines) {
        $lineKey = "$sourcePath`:$($line.number)"
        $hits = [int]$line.hits
        if (-not $lineHits.ContainsKey($lineKey) -or $hits -gt $lineHits[$lineKey]) {
            $lineHits[$lineKey] = $hits
        }
    }
}

if ($lineHits.Count -eq 0) {
    throw "Coverage report did not contain valid production source lines."
}

[long]$coveredLines = @($lineHits.Values | Where-Object { $_ -gt 0 }).Count
[double]$coveragePercent = [Math]::Round(($coveredLines / $lineHits.Count) * 100, 2)

Write-Host "Computed unique production line coverage: $coveragePercent% ($coveredLines/$($lineHits.Count); minimum: $minimumCoverage%; next target: $nextTarget%; excluded generated lines: $excludedGeneratedLines)"

if ($coveragePercent -lt $minimumCoverage) {
    throw "Coverage baseline failed: $coveragePercent% is below $minimumCoverage%."
}

if ($coveragePercent -ge $nextTarget) {
    throw "Coverage ratchet threshold reached: $coveragePercent% >= $nextTarget%. Update $PolicyPath in this change."
}
