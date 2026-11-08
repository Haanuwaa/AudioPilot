Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot "../../artifacts/testresults/performance-report/$([guid]::NewGuid().ToString('N'))"
$current = Join-Path $root 'current'
$baseline = Join-Path $root 'baseline'
New-Item -ItemType Directory -Path $current, $baseline -Force | Out-Null
$names = @('picker-lifetime-Dark', 'picker-lifetime-Light', 'media-overlay-lifetime-Dark', 'media-overlay-lifetime-Light',
    'mixer-lifecycle', 'media-command-lifecycle', 'output-role-switches', 'input-role-switches')
foreach ($name in $names) {
    $latencyName = switch -Wildcard ($name) {
        'picker-lifetime-*' { 'MedianOpenMs' }
        'media-overlay-lifetime-*' { 'MedianUpdateMs' }
        default { 'MedianMs' }
    }
    @{ SchemaVersion = 2; Scenario = $name; Architecture = 'X64'; Runtime = '.NET test'; WorkloadVersion = 1
        Iterations = 20; WarmupIterations = 5; SampleInterval = 10; $latencyName = 2
        Samples = @(@{ Cycle = 10; Handles = 10; PrivateBytes = 1000; ManagedBytes = 500 },
            @{ Cycle = 20; Handles = 13; PrivateBytes = 1400; ManagedBytes = 600 }) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $current "$name.json") -Encoding utf8
}
$reporter = Join-Path $PSScriptRoot '../report-performance-trends.ps1'
& $reporter -ReportDirectory $current -Architecture X64 -JobSummaryPath ''
$first = Get-Content (Join-Path $current 'summary.json') -Raw | ConvertFrom-Json
if ($first.Scenarios.Count -ne 8 -or @($first.Scenarios | Where-Object { $_.LateMinusEarlyHandles -ne 3 -or $_.LateMinusEarlyPrivateBytes -ne 400 -or $_.LateMinusEarlyManagedBytes -ne 100 }).Count -ne 0) { throw 'Incorrect growth calculations.' }
Copy-Item -LiteralPath (Join-Path $current 'summary.json') -Destination $baseline
& $reporter -ReportDirectory $current -Architecture X64 -BaselineDirectory $baseline -JobSummaryPath ''
$comparison = Get-Content (Join-Path $current 'summary.json') -Raw | ConvertFrom-Json
if (@($comparison.Scenarios | Where-Object MedianChangeMs -ne 0).Count -ne 0) { throw 'Incorrect baseline comparison.' }
$mixerPath = Join-Path $current 'mixer-lifecycle.json'
$original = Get-Content -LiteralPath $mixerPath -Raw
foreach ($mutation in @(
    @{ Key = 'Runtime'; Value = '.NET different' },
    @{ Key = 'WarmupIterations'; Value = 4 },
    @{ Key = 'SampleInterval'; Value = 5 },
    @{ Key = 'WorkloadVersion'; Value = 2 }
)) {
    $report = $original | ConvertFrom-Json -AsHashtable
    $report[$mutation.Key] = $mutation.Value
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $mixerPath
    & $reporter -ReportDirectory $current -Architecture X64 -BaselineDirectory $baseline -JobSummaryPath ''
    $different = Get-Content -LiteralPath (Join-Path $current 'summary.json') -Raw | ConvertFrom-Json
    if (@($different.Scenarios | Where-Object { $null -eq $_.MedianChangeMs }).Scenario -cne 'mixer-lifecycle') {
        throw "Compared incompatible $($mutation.Key), or suppressed other matching scenarios."
    }
}

foreach ($mutation in @(
    @{ Key = 'Architecture'; Value = 'Arm64'; Error = 'Wrong process architecture' },
    @{ Key = 'Architecture'; Value = $null; Error = 'Wrong process architecture' },
    @{ Key = 'SchemaVersion'; Value = 99; Error = 'Invalid report schema or scenario' },
    @{ Key = 'Scenario'; Value = 'other-workload'; Error = 'Invalid report schema or scenario' }
)) {
    $report = $original | ConvertFrom-Json -AsHashtable
    $report[$mutation.Key] = $mutation.Value
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $mixerPath
    $rejected = $false
    try { & $reporter -ReportDirectory $current -Architecture X64 -JobSummaryPath '' }
    catch { if ($_.Exception.Message -notlike "*$($mutation.Error)*") { throw }; $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid performance metadata: $($mutation.Key)" }
}
Write-Host 'Performance report calculations, baseline compatibility, and identity validation passed.'
