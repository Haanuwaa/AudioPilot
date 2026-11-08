param(
    [Parameter(Mandatory)][string]$ResultsJson,
    [Parameter(Mandatory)][ValidateSet('push', 'pull_request', 'workflow_dispatch')][string]$EventName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$results = $ResultsJson | ConvertFrom-Json -AsHashtable
if (-not $results.ContainsKey('changes') -or $results.changes.result -cne 'success') {
    throw 'Required CI test dependency did not succeed: changes'
}
$requirements = @{
    'test-unit' = 'code'; 'test-tooling' = 'code'; 'test-x86' = 'code'; 'test-arm64' = 'code'; 'workflow-lint' = 'code'
    'updater-tools' = 'updater_tools'; 'benchmark-smoke' = 'benchmarks'
}
foreach ($flag in @('code', 'updater_tools', 'benchmarks')) {
    if (-not $results.changes.ContainsKey('outputs') -or -not $results.changes.outputs.ContainsKey($flag) -or
        $results.changes.outputs[$flag] -cnotin @('true', 'false')) { throw "Invalid CI change detection output: $flag" }
}
foreach ($job in $requirements.Keys) {
    $required = $EventName -ceq 'workflow_dispatch' -or $results.changes.outputs[$requirements[$job]] -ceq 'true'
    if (-not $results.ContainsKey($job) -or
        ($results[$job].result -cne 'success' -and ($required -or $results[$job].result -cne 'skipped'))) {
        throw "Required CI test dependency did not succeed: $job"
    }
}
Write-Host 'All CI test dependencies passed.'
