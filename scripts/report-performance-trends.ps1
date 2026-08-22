param(
    [Parameter(Mandatory)][string]$ReportDirectory,
    [Parameter(Mandatory)][ValidateSet('X64', 'Arm64')][string]$Architecture,
    [string]$BaselineDirectory,
    [string]$JobSummaryPath = $env:GITHUB_STEP_SUMMARY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem -LiteralPath $ReportDirectory -Filter '*.json' | Where-Object Name -ne 'summary.json' | Sort-Object Name)
if ($reports.Count -ne 8) { throw "Expected eight scenario reports, found $($reports.Count)." }
$expected = @('picker-lifetime-Dark', 'picker-lifetime-Light', 'media-overlay-lifetime-Dark', 'media-overlay-lifetime-Light',
    'mixer-lifecycle', 'media-command-lifecycle', 'output-role-switches', 'input-role-switches')
if (@(Compare-Object $expected @($reports.BaseName) -CaseSensitive).Count -ne 0) { throw 'The performance scenario set is incomplete or unexpected.' }
$baseline = $null
if ($BaselineDirectory -and (Test-Path -LiteralPath (Join-Path $BaselineDirectory 'summary.json'))) {
    $candidate = Get-Content -LiteralPath (Join-Path $BaselineDirectory 'summary.json') -Raw | ConvertFrom-Json
    if ($candidate.SchemaVersion -eq 1 -and $candidate.Architecture -ceq $Architecture) { $baseline = $candidate }
}
function Get-Median($Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    return ($sorted[[int][Math]::Floor(($sorted.Count - 1) / 2)] + $sorted[[int][Math]::Floor($sorted.Count / 2)]) / 2
}
$rows = foreach ($file in $reports) {
    $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if ($report.PSObject.Properties['Architecture'] -and $report.Architecture -cne $Architecture) { throw "Wrong process architecture in $($file.Name)." }
    $samples = @($report.Samples)
    if ($samples.Count -eq 0) { throw "No resource samples in $($file.Name)." }
    $latencyName = @('MedianMs', 'MedianOpenMs', 'MedianUpdateMs' | Where-Object { $report.PSObject.Properties[$_] })[0]
    $latency = $report.$latencyName
    $window = [Math]::Max(1, [int][Math]::Floor($samples.Count / 3))
    $early = @($samples | Select-Object -First $window)
    $late = @($samples | Select-Object -Last $window)
    $handleGrowth = if ($samples.Count -ge 2) { (Get-Median $late.Handles) - (Get-Median $early.Handles) } else { $null }
    $privateGrowth = if ($samples.Count -ge 2) { (Get-Median $late.PrivateBytes) - (Get-Median $early.PrivateBytes) } else { $null }
    $managedGrowth = if ($samples.Count -ge 2) { (Get-Median $late.ManagedBytes) - (Get-Median $early.ManagedBytes) } else { $null }
    $previous = @(if ($null -ne $baseline) { $baseline.Scenarios | Where-Object Scenario -ceq $file.BaseName })
    [ordered]@{
        Scenario = $file.BaseName; ResourceSamples = $samples.Count; MedianMs = $latency
        MedianChangeMs = if ($previous.Count -eq 1) { $latency - $previous[0].MedianMs } else { $null }
        LateMinusEarlyHandles = $handleGrowth; LateMinusEarlyPrivateBytes = $privateGrowth; LateMinusEarlyManagedBytes = $managedGrowth
    }
}
$summary = [ordered]@{ SchemaVersion = 1; Architecture = $Architecture; CapturedUtc = [DateTimeOffset]::UtcNow.ToString('O');
    TimingThresholdsEnforced = $false; Scenarios = @($rows) }
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'summary.json') -Encoding utf8
$lines = @("## AudioPilot performance trends ($Architecture)", '',
    'Reports use controlled inputs. Switching writes are simulated; real drivers and Bluetooth latency are not measured.',
    'Early/late resource medians describe growth across each run. Timing and growth trends do not enforce new thresholds.', '',
    '| Scenario | Median ms | Change ms | Handle growth | Private growth bytes | Managed growth bytes |', '| --- | ---: | ---: | ---: | ---: | ---: |')
foreach ($row in $rows) {
    $values = @($row.MedianMs, $row.MedianChangeMs, $row.LateMinusEarlyHandles, $row.LateMinusEarlyPrivateBytes, $row.LateMinusEarlyManagedBytes) | ForEach-Object {
        if ($null -eq $_) { 'n/a' } else { [Math]::Round($_, 3).ToString([Globalization.CultureInfo]::InvariantCulture) }
    }
    $lines += "| $($row.Scenario) | $($values -join ' | ') |"
}
$lines | Set-Content -LiteralPath (Join-Path $ReportDirectory 'summary.md') -Encoding utf8
if ($JobSummaryPath) { $lines | Add-Content -LiteralPath $JobSummaryPath -Encoding utf8 }
Write-Host "Generated $Architecture trends for $($rows.Count) scenarios."
