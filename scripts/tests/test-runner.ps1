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
$interactionVariables = @('AUDIOPILOT_RUN_AUDIO_HARDWARE', 'AUDIOPILOT_RUN_VISUAL_WPF', 'AUDIOPILOT_TEST_SHOW_WINDOWS', 'AUDIOPILOT_RUN_HARDWARE_SOAK')
foreach ($name in @('PATH', 'AUDIOPILOT_TEST_ALLOW_RUNNING_UI', 'AUDIOPILOT_DISABLE_CONSOLE_LOGGING', 'AUDIOPILOT_SCRIPT_TEST_FAIL_CATEGORY', 'AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE') + $interactionVariables) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
Push-Location $runRoot
try {
    $env:PATH = $toolsRoot + [IO.Path]::PathSeparator + $env:PATH
    $env:AUDIOPILOT_TEST_ALLOW_RUNNING_UI = '1'
    $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = 'original'
    foreach ($name in $interactionVariables) { [Environment]::SetEnvironmentVariable($name, '1') }
    Remove-Item Env:AUDIOPILOT_SCRIPT_TEST_FAIL_CATEGORY -ErrorAction SilentlyContinue
    Remove-Item Env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE -ErrorAction SilentlyContinue
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
        if ($record.AudioHardware -or $record.VisualWpf -or $record.ShowWindows -or $record.HardwareSoak) {
            throw "Default $category run inherited permission to affect the desktop or audio."
        }
        $policyIndex = [Array]::IndexOf($record.Arguments, '--zero-tests-policy')
        $expectedPolicy = if ($category -eq 'integration') { 'allow-skipped' } else { 'strict' }
        if ($policyIndex -lt 0 -or $record.Arguments[$policyIndex + 1] -ne $expectedPolicy) {
            throw "Default $category run did not preserve its expected zero-test policy."
        }
        if ($category -ne 'unit' -and '--no-build' -notin $record.Arguments) { throw 'A later category rebuilt the solution.' }
    }
    & $runner -Category unit -Project $project -NoBuild
    if ($LASTEXITCODE -ne 0 -or $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING -ne 'original') { throw 'The caller logging environment was not restored.' }
    foreach ($name in $interactionVariables) {
        if ([Environment]::GetEnvironmentVariable($name) -ne '1') { throw "Caller environment was not restored: $name" }
    }

    foreach ($category in @('visual', 'hardware-soak')) {
        $rejected = $false
        try { & $runner -Category $category -Project $project -NoBuild }
        catch {
            if ($_.Exception.Message -notmatch '-Allow(DesktopInteraction|HardwareAudio)') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "$category accepted inherited environment without explicit permission." }
        if (Test-Path "artifacts/testresults/$category") { throw "$category launched tests before checking permission." }
    }

    & $runner -Category visual -AllowDesktopInteraction -Project $project -NoBuild
    if ($LASTEXITCODE -ne 0) { throw 'Explicit visual invocation failed.' }
    $record = Get-Content 'artifacts/testresults/visual/record.json' -Raw | ConvertFrom-Json
    if ($record.ShowWindows -ne '1' -or $record.VisualWpf -ne '1' -or $record.AudioHardware -or $record.HardwareSoak) {
        throw 'Visual opt-in did not stay separate from audio permission.'
    }

    & $runner -Category full -AllowHardwareAudio -Project $project -NoBuild
    if ($LASTEXITCODE -ne 0) { throw 'Explicit hardware invocation failed.' }
    foreach ($category in @('unit', 'integration', 'stress')) {
        $record = Get-Content "artifacts/testresults/$category/record.json" -Raw | ConvertFrom-Json
        $expectedAudioPermission = if ($category -eq 'integration') { '1' } else { '' }
        if ([string]$record.AudioHardware -ne $expectedAudioPermission -or $record.ShowWindows -or $record.VisualWpf -or $record.HardwareSoak) {
            throw "Hardware opt-in leaked across categories: $category"
        }
        $policyIndex = [Array]::IndexOf($record.Arguments, '--zero-tests-policy')
        $expectedPolicy = if ($category -eq 'integration') { 'allow-skipped' } else { 'strict' }
        if ($policyIndex -lt 0 -or $record.Arguments[$policyIndex + 1] -ne $expectedPolicy) {
            throw "Optional hardware invocation did not preserve the zero-test policy for $category."
        }
    }

    $env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE = '1'
    & $runner -Category integration -AllowHardwareAudio -Project $project -NoBuild
    if ($LASTEXITCODE -ne 0) { throw 'Required hardware invocation failed.' }
    $record = Get-Content 'artifacts/testresults/integration/record.json' -Raw | ConvertFrom-Json
    $policyIndex = [Array]::IndexOf($record.Arguments, '--zero-tests-policy')
    if ($policyIndex -lt 0 -or $record.Arguments[$policyIndex + 1] -ne 'strict') { throw 'Required hardware allowed all tests to skip.' }
    Remove-Item Env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE

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
    Write-Host 'Test runner checks passed: interaction opt-ins, environment isolation, argument preservation, category retention, logging, diagnostic retention, filter rejection, and child failure propagation.'
}
finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
}
exit 0
