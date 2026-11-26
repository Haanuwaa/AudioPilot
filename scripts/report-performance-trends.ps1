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
    $candidate = Get-Content -LiteralPath (Join-Path $BaselineDirectory 'summary.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($candidate.SchemaVersion -eq 2 -and $candidate.Architecture -ceq $Architecture) { $baseline = $candidate }
}
function Get-Median($Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    return ($sorted[[int][Math]::Floor(($sorted.Count - 1) / 2)] + $sorted[[int][Math]::Floor($sorted.Count / 2)]) / 2
}
function Test-NonnegativeNumber($Value) {
    return ($Value -is [long] -or $Value -is [int] -or $Value -is [double]) -and
        [double]::IsFinite($Value) -and $Value -ge 0
}

$rows = foreach ($file in $reports) {
    $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
    if ($report.SchemaVersion -ne 2 -or $report.Scenario -cne $file.BaseName) { throw "Invalid report schema or scenario in $($file.Name)." }
    if ($report.Architecture -cne $Architecture) { throw "Wrong process architecture in $($file.Name)." }
    if ($report.Runtime -isnot [string] -or [string]::IsNullOrWhiteSpace($report.Runtime) -or
        $report.WorkloadVersion -isnot [long] -or $report.WorkloadVersion -lt 1 -or
        $report.Iterations -isnot [long] -or $report.Iterations -lt 1 -or
        $report.WarmupIterations -isnot [long] -or $report.WarmupIterations -lt 0 -or $report.WarmupIterations -ge $report.Iterations -or
        $report.SampleInterval -isnot [long] -or $report.SampleInterval -lt 1) { throw "Invalid workload metadata in $($file.Name)." }
    $samples = $report.Samples
    if ($samples -isnot [array] -or $samples.Count -eq 0) { throw "No resource samples in $($file.Name)." }
    $sampleCycles = @(foreach ($sample in $samples) {
        if ($sample.Cycle -isnot [long] -or $sample.Cycle -lt 1 -or
            -not (Test-NonnegativeNumber $sample.Handles) -or -not (Test-NonnegativeNumber $sample.PrivateBytes) -or
            -not (Test-NonnegativeNumber $sample.ManagedBytes)) { throw "Invalid resource sample in $($file.Name)." }
        $sample.Cycle
    })
    for ($index = 1; $index -lt $sampleCycles.Count; $index++) {
        if ($sampleCycles[$index] -le $sampleCycles[$index - 1]) { throw "Resource samples are out of order in $($file.Name)." }
    }
    $latencyName = switch -Wildcard ($file.BaseName) {
        'picker-lifetime-*' { 'MedianOpenMs' }
        'media-overlay-lifetime-*' { 'MedianUpdateMs' }
        default { 'MedianMs' }
    }
    $latency = $report[$latencyName]
    if (-not (Test-NonnegativeNumber $latency)) { throw "Invalid median latency in $($file.Name)." }
    $window = [Math]::Max(1, [int][Math]::Floor($samples.Count / 3))
    $early = @($samples | Select-Object -First $window)
    $late = @($samples | Select-Object -Last $window)
    $handleGrowth = if ($samples.Count -ge 2) { (Get-Median $late.Handles) - (Get-Median $early.Handles) } else { $null }
    $privateGrowth = if ($samples.Count -ge 2) { (Get-Median $late.PrivateBytes) - (Get-Median $early.PrivateBytes) } else { $null }
    $managedGrowth = if ($samples.Count -ge 2) { (Get-Median $late.ManagedBytes) - (Get-Median $early.ManagedBytes) } else { $null }
    $previous = @(if ($null -ne $baseline) {
        $baseline.Scenarios | Where-Object {
            $_.Scenario -ceq $report.Scenario -and $_.WorkloadVersion -eq $report.WorkloadVersion -and
            $_.Runtime -ceq $report.Runtime -and $_.Iterations -eq $report.Iterations -and
            $_.WarmupIterations -eq $report.WarmupIterations -and $_.SampleInterval -eq $report.SampleInterval -and
            ($_.SampleCycles -join ',') -ceq ($sampleCycles -join ',') -and (Test-NonnegativeNumber $_.MedianMs)
        }
    })
    [ordered]@{
        Scenario = $file.BaseName; WorkloadVersion = $report.WorkloadVersion; Runtime = $report.Runtime
        Iterations = $report.Iterations; WarmupIterations = $report.WarmupIterations; SampleInterval = $report.SampleInterval
        SampleCycles = $sampleCycles; ResourceSamples = $samples.Count; MedianMs = $latency
        MedianChangeMs = if ($previous.Count -eq 1) { $latency - $previous[0].MedianMs } else { $null }
        LateMinusEarlyHandles = $handleGrowth; LateMinusEarlyPrivateBytes = $privateGrowth; LateMinusEarlyManagedBytes = $managedGrowth
    }
}
$summary = [ordered]@{ SchemaVersion = 2; Architecture = $Architecture; CapturedUtc = [DateTimeOffset]::UtcNow.ToString('O');
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
