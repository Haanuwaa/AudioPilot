param([Parameter(Mandatory)][string]$ResultsJson)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$results = $ResultsJson | ConvertFrom-Json -AsHashtable
foreach ($job in @('changes', 'test-unit', 'test-tooling', 'test-x86')) {
    if (-not $results.ContainsKey($job) -or $results[$job]['result'] -cne 'success') {
        throw "Required CI test dependency did not succeed: $job"
    }
}
Write-Host 'All CI test dependencies passed.'
