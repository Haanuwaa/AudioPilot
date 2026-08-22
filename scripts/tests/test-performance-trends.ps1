Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot "../../artifacts/testresults/performance-report/$([guid]::NewGuid().ToString('N'))"
$current = Join-Path $root 'current'
$baseline = Join-Path $root 'baseline'
New-Item -ItemType Directory -Path $current, $baseline -Force | Out-Null
$names = @('picker-lifetime-Dark', 'picker-lifetime-Light', 'media-overlay-lifetime-Dark', 'media-overlay-lifetime-Light',
    'mixer-lifecycle', 'media-command-lifecycle', 'output-role-switches', 'input-role-switches')
foreach ($name in $names) {
    @{ MedianMs = 2; Samples = @(@{ Handles = 10; PrivateBytes = 1000; ManagedBytes = 500 }, @{ Handles = 13; PrivateBytes = 1400; ManagedBytes = 600 }) } |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $current "$name.json") -Encoding utf8
}
$reporter = Join-Path $PSScriptRoot '../report-performance-trends.ps1'
& $reporter -ReportDirectory $current -Architecture X64 -JobSummaryPath ''
$first = Get-Content (Join-Path $current 'summary.json') -Raw | ConvertFrom-Json
if ($first.Scenarios.Count -ne 8 -or @($first.Scenarios | Where-Object { $_.LateMinusEarlyHandles -ne 3 -or $_.LateMinusEarlyPrivateBytes -ne 400 -or $_.LateMinusEarlyManagedBytes -ne 100 }).Count -ne 0) { throw 'Incorrect growth calculations.' }
Copy-Item -LiteralPath (Join-Path $current 'summary.json') -Destination $baseline
& $reporter -ReportDirectory $current -Architecture X64 -BaselineDirectory $baseline -JobSummaryPath ''
$comparison = Get-Content (Join-Path $current 'summary.json') -Raw | ConvertFrom-Json
if (@($comparison.Scenarios | Where-Object MedianChangeMs -ne 0).Count -ne 0) { throw 'Incorrect baseline comparison.' }
& $reporter -ReportDirectory $current -Architecture Arm64 -BaselineDirectory $baseline -JobSummaryPath ''
$different = Get-Content (Join-Path $current 'summary.json') -Raw | ConvertFrom-Json
if (@($different.Scenarios | Where-Object { $null -ne $_.MedianChangeMs }).Count -ne 0) { throw 'Compared different architectures.' }
@{ Architecture = 'Arm64'; MedianMs = 2; Samples = @(@{ Handles = 10; PrivateBytes = 1000; ManagedBytes = 500 }) } |
    ConvertTo-Json -Depth 4 | Set-Content (Join-Path $current 'mixer-lifecycle.json') -Encoding utf8
$rejected = $false
try { & $reporter -ReportDirectory $current -Architecture X64 -JobSummaryPath '' }
catch { if ($_.Exception.Message -notlike '*Wrong process architecture*') { throw }; $rejected = $true }
if (-not $rejected) { throw 'Accepted a report from a different process architecture.' }
Write-Host 'Performance report calculations, baselines, and architecture isolation passed.'
