Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot '../validate-ci-test-gate.ps1'
$jobs = @('changes', 'test-unit', 'test-tooling', 'test-x86', 'test-arm64', 'workflow-lint', 'updater-tools', 'benchmark-smoke')
function New-Results([string]$Code = 'true', [string]$Updater = 'true', [string]$Benchmarks = 'true') {
    $value = @{}
    foreach ($job in $jobs) { $value[$job] = @{result='success'} }
    $value.changes.outputs = @{code=$Code;updater_tools=$Updater;benchmarks=$Benchmarks}
    return $value
}
function Assert-Rejected($Value, [string]$EventName = 'push') {
    $rejected = $false
    try { & $gate -ResultsJson ($Value | ConvertTo-Json -Depth 5) -EventName $EventName }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'CI aggregate accepted an incomplete or failed required job.' }
}
& $gate -ResultsJson ((New-Results) | ConvertTo-Json -Depth 5) -EventName push
foreach ($job in $jobs) {
    foreach ($result in @('failure', 'cancelled', 'skipped', 'missing')) {
        $fixture = New-Results
        if ($result -eq 'missing') { $fixture.Remove($job) }
        else { $fixture[$job].result = $result }
        Assert-Rejected $fixture
    }
}
foreach ($selection in @(
    @{code='false';updater_tools='false';benchmarks='false'},
    @{code='false';updater_tools='false';benchmarks='true'},
    @{code='true';updater_tools='false';benchmarks='false'}
)) {
    $fixture = New-Results $selection.code $selection.updater_tools $selection.benchmarks
    foreach ($job in @('test-unit','test-tooling','test-x86','test-arm64','workflow-lint')) {
        if ($selection.code -ceq 'false') { $fixture[$job].result='skipped' }
    }
    if ($selection.updater_tools -ceq 'false') { $fixture['updater-tools'].result='skipped' }
    if ($selection.benchmarks -ceq 'false') { $fixture['benchmark-smoke'].result='skipped' }
    & $gate -ResultsJson ($fixture | ConvertTo-Json -Depth 5) -EventName pull_request
    Assert-Rejected $fixture workflow_dispatch
    foreach ($job in @('updater-tools','benchmark-smoke')) {
        foreach ($result in @('failure','cancelled')) {
            $copy = $fixture | ConvertTo-Json -Depth 5 | ConvertFrom-Json -AsHashtable
            $copy[$job].result=$result
            Assert-Rejected $copy
        }
    }
}
foreach ($flag in @('code','updater_tools','benchmarks')) {
    $fixture=New-Results
    $fixture.changes.outputs.Remove($flag)
    Assert-Rejected $fixture
    $fixture.changes.outputs[$flag]='unknown'
    Assert-Rejected $fixture
}
Write-Host 'CI aggregation validates conditional gates, every failure, manual runs, and change-detection health.'
