Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot '../validate-ci-test-gate.ps1'
$jobs = @('changes', 'test-unit', 'test-tooling', 'test-x86')
$success = @{}
foreach ($job in $jobs) { $success[$job] = @{ result = 'success' } }
& $gate -ResultsJson ($success | ConvertTo-Json)
foreach ($job in $jobs) {
    foreach ($result in @('failure', 'cancelled', 'skipped', 'missing')) {
        $fixture = $success.Clone()
        if ($result -eq 'missing') { $fixture.Remove($job) }
        else { $fixture[$job] = @{ result = $result } }
        $rejected = $false
        try { & $gate -ResultsJson ($fixture | ConvertTo-Json) }
        catch { if ($_.Exception.Message -notlike '*Required CI test dependency*') { throw }; $rejected = $true }
        if (-not $rejected) { throw "CI aggregate accepted $job : $result" }
    }
}
Write-Host 'CI aggregation rejects failed, cancelled, skipped, and missing dependencies.'
