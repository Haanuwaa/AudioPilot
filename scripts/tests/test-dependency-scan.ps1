Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$reportRoot = Join-Path $repoRoot "artifacts/testresults/dependency-scan/$([guid]::NewGuid().ToString('N'))"
$scriptPath = Join-Path $PSScriptRoot '../scan-dependencies.ps1'
$state = @{ Fixture = 'clean'; Calls = 0 }
function dotnet {
    $state.Calls++
    if ($args -notcontains '--no-restore' -or $args -notcontains '--output-version' -or $args -notcontains '1') { throw 'Missing stable query options.' }
    $projects = if ($args[1] -like '*.sln') {
        @('AudioPilot/AudioPilot.csproj', 'AudioPilot.Tests/AudioPilot.Tests.csproj', 'AudioPilot.CliHost/AudioPilot.CliHost.csproj', 'AudioPilot.Installer/AudioPilot.Installer.wixproj', 'AudioPilot.UpdateProtocol/AudioPilot.UpdateProtocol.csproj')
    }
    else { @('AudioPilot.Benchmarks/AudioPilot.Benchmarks.csproj') }
    $report = @{ version = 1; projects = @($projects | ForEach-Object { @{ path = Join-Path $repoRoot $_ } }) }
    $global:LASTEXITCODE = 0
    switch ($state.Fixture) {
        'query-failure' { $global:LASTEXITCODE = 1 }
        'invalid-json' { return 'invalid json' }
        'version' { $report.version = 99 }
        'missing-project' { $report.projects = @() }
        'duplicate-project' { $report.projects += $report.projects[0] }
        'wrong-project' { $report.projects[0].path = Join-Path $repoRoot 'Unknown.csproj' }
        'problem' { $report.problems = @(@{ error = 'Package source unavailable.' }) }
        'project-problem' { $report.projects[0].problems = @(@{ warning = 'Incomplete package information.' }) }
        'vulnerable' { $report.projects[0].frameworks = @(@{ framework = 'test'; topLevelPackages = @(@{
            id = 'Example'; resolvedVersion = '1.0'; vulnerabilities = @(@{ severity = 'High'; advisoryurl = 'https://example.test/advisory' }) }) }) }
        'outdated' { $report.projects[0].frameworks = @(@{ framework = 'test'; topLevelPackages = @(@{
            id = 'Example'; requestedVersion = '1.0'; resolvedVersion = '1.0'; latestVersion = '2.0' }) }) }
    }
    $report | ConvertTo-Json -Depth 10
}
foreach ($mode in @('Vulnerabilities', 'Outdated')) {
    $state.Fixture = 'clean'
    $state.Calls = 0
    & $scriptPath -Mode $mode -ReportDirectory $reportRoot -JobSummaryPath ''
    if ($state.Calls -ne 2) { throw 'Did not scan the solution and benchmarks.' }
    foreach ($case in @('query-failure', 'invalid-json', 'version', 'missing-project', 'duplicate-project', 'wrong-project', 'problem', 'project-problem')) {
        $state.Fixture = $case
        $rejected = $false
        try { & $scriptPath -Mode $mode -ReportDirectory $reportRoot -JobSummaryPath '' }
        catch {
            $expected = switch ($case) {
                'query-failure' { '*query failed*' }
                'invalid-json' { '*JSON*' }
                'version' { '*Unsupported NuGet JSON report*' }
                'missing-project' { '*Incomplete NuGet report*' }
                'duplicate-project' { '*Unexpected or duplicate NuGet report project*' }
                'wrong-project' { '*Unexpected or duplicate NuGet report project*' }
                default { '*collection problems*' }
            }
            if ($_.Exception.Message -notlike $expected) { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "Accepted invalid $mode report: $case" }
    }
}
$state.Fixture = 'vulnerable'
$rejected = $false
try { & $scriptPath -Mode Vulnerabilities -ReportDirectory $reportRoot -JobSummaryPath '' }
catch { if ($_.Exception.Message -notlike '*Dependency vulnerability scan failed*') { throw }; $rejected = $true }
if (-not $rejected) { throw 'Accepted vulnerable packages.' }
$state.Fixture = 'outdated'
& $scriptPath -Mode Outdated -ReportDirectory $reportRoot -JobSummaryPath ''
Write-Host 'Dependency reporting validates both graphs, fails closed on query problems, and keeps updates advisory.'
