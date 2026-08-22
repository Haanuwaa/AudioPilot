Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runner = Join-Path $repositoryRoot 'scripts/run-tests.ps1'
$project = Join-Path $repositoryRoot 'AudioPilot.Tests/AudioPilot.Tests.csproj'
$runRoot = Join-Path $repositoryRoot "artifacts/testresults/script-tests/$([Guid]::NewGuid().ToString('N'))"
$toolsRoot = Join-Path $runRoot 'tools'
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures/dotnet.ps1') -Destination $toolsRoot

$savedEnvironment = @{}
foreach ($name in @('PATH', 'AUDIOPILOT_TEST_ALLOW_RUNNING_UI', 'AUDIOPILOT_DISABLE_CONSOLE_LOGGING', 'AUDIOPILOT_SCRIPT_TEST_FAIL_CATEGORY')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
Push-Location $runRoot
try {
    $env:PATH = $toolsRoot + [IO.Path]::PathSeparator + $env:PATH
    $env:AUDIOPILOT_TEST_ALLOW_RUNNING_UI = '1'
    $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = 'original'
    Remove-Item Env:AUDIOPILOT_SCRIPT_TEST_FAIL_CATEGORY -ErrorAction SilentlyContinue
    if ((Get-Command dotnet).Source -ne (Join-Path $toolsRoot 'dotnet.ps1')) { throw 'The fake dotnet was not selected.' }
    $extra = @('--long-running', '120', '--diagnostic-output-directory', 'directory with spaces; $(literal)', '--crashdump')
    & $runner -Category full -Project $project -Coverage -ShowLogs -DotnetTestArgs $extra
    if ($LASTEXITCODE -ne 0) { throw 'Full invocation failed.' }
    foreach ($category in @('unit', 'integration', 'stress')) {
        $record = Get-Content -LiteralPath "artifacts/testresults/coverage/$category/record.json" -Raw | ConvertFrom-Json
        foreach ($argument in $extra) {
            if ($argument -cnotin $record.Arguments) { throw "Argument was not preserved: $argument" }
        }
        if ($record.Logging) { throw 'ShowLogs did not clear inherited suppression.' }
        if ($category -ne 'unit' -and '--no-build' -notin $record.Arguments) { throw 'A later category rebuilt the solution.' }
    }
    & $runner -Category unit -Project $project -NoBuild
    if ($LASTEXITCODE -ne 0 -or $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING -ne 'original') { throw 'The caller logging environment was not restored.' }

    foreach ($withCoverage in @($false, $true)) {
        $resultsDirectory = if ($withCoverage) { 'artifacts/testresults/coverage/unit' } else { 'artifacts/testresults/unit' }
        Set-Content -LiteralPath (Join-Path $resultsDirectory 'previous-crash.dmp') -Value 'dump sentinel'
        Set-Content -LiteralPath (Join-Path $resultsDirectory 'previous-crash.sequence.log') -Value 'sequence sentinel'
        & $runner -Category unit -Project $project -NoBuild -Coverage:$withCoverage
        if ($LASTEXITCODE -ne 0 -or (Test-Path (Join-Path $resultsDirectory 'previous-crash.dmp'))) { throw 'New run did not get a fresh results directory.' }
    }
    $archives = @(Get-ChildItem -LiteralPath 'artifacts/testresults/diagnostics' -Directory)
    if ($archives.Count -ne 2) { throw 'Prior crash evidence was not archived for both result layouts.' }
    foreach ($archive in $archives) {
        if ((Get-Content -LiteralPath (Join-Path $archive.FullName 'previous-crash.dmp') -Raw).Trim() -ne 'dump sentinel' -or
            (Get-Content -LiteralPath (Join-Path $archive.FullName 'previous-crash.sequence.log') -Raw).Trim() -ne 'sequence sentinel') {
            throw 'Archived diagnostic evidence changed.'
        }
    }

    foreach ($filter in @('--filter-class', '--filter-not-class', '--filter', '--filter-query', '--filter-class=Example')) {
        $rejected = $false
        try { & $runner -Category unit -Project $project -DotnetTestArgs @($filter, 'Example') }
        catch {
            if ($_.Exception.Message -notlike '*extra filter arguments are not supported*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "Unsafe extra filter was accepted: $filter" }
    }

    $failureRoot = Join-Path $runRoot 'failure'
    New-Item -ItemType Directory -Path $failureRoot | Out-Null
    Set-Location $failureRoot
    $env:AUDIOPILOT_SCRIPT_TEST_FAIL_CATEGORY = 'integration'
    & $runner -Category full -Project $project -Coverage
    if ($LASTEXITCODE -ne 7 -or (Test-Path 'artifacts/testresults/coverage/stress')) { throw 'Child failure did not stop subsequent categories.' }
    Write-Host 'Test runner checks passed: argument preservation, category retention, logging, diagnostic retention, filter rejection, and child failure propagation.'
}
finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
}
exit 0
