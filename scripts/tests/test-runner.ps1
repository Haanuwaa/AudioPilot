Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runner = Join-Path $repositoryRoot 'scripts/run-tests.ps1'
$project = Join-Path $repositoryRoot 'AudioPilot.Tests/AudioPilot.Tests.csproj'
$runRoot = Join-Path $repositoryRoot "artifacts/testresults/script-tests/$([Guid]::NewGuid().ToString('N'))"
$toolsRoot = Join-Path $runRoot 'tools'
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures/dotnet.ps1') -Destination $toolsRoot

function Get-TestRunDirectory([string]$Root) {
    $run = Get-Content -LiteralPath (Join-Path $Root 'latest.json') -Raw | ConvertFrom-Json
    if ($run.RunId -notmatch '^[a-f0-9]{32}$') { throw 'Invalid test run identity.' }
    return Join-Path $Root $run.RunId
}
function Get-TestRecord([string]$Root) {
    return Get-Content -LiteralPath (Join-Path (Get-TestRunDirectory $Root) 'record.json') -Raw
}

$savedEnvironment = @{}
$interactionVariables = @('AUDIOPILOT_RUN_AUDIO_HARDWARE', 'AUDIOPILOT_RUN_VISUAL_WPF', 'AUDIOPILOT_TEST_SHOW_WINDOWS', 'AUDIOPILOT_RUN_HARDWARE_SOAK')
$soakVariables = @('AUDIOPILOT_HARDWARE_SOAK_MINUTES', 'AUDIOPILOT_TEST_OUTPUT_DEVICE_A', 'AUDIOPILOT_TEST_OUTPUT_DEVICE_B', 'AUDIOPILOT_TEST_INPUT_DEVICE_A', 'AUDIOPILOT_TEST_INPUT_DEVICE_B')
foreach ($name in @('PATH', 'AUDIOPILOT_TEST_ALLOW_RUNNING_UI', 'AUDIOPILOT_DISABLE_CONSOLE_LOGGING', 'AUDIOPILOT_SCRIPT_TEST_FAIL_CATEGORY', 'AUDIOPILOT_SCRIPT_TEST_MIXED_STREAMS', 'AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE', 'AUDIOPILOT_SCRIPT_TEST_GATE_ROOT') + $interactionVariables + $soakVariables) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
Push-Location $runRoot
try {
    $env:PATH = $toolsRoot + [IO.Path]::PathSeparator + $env:PATH
    $env:AUDIOPILOT_TEST_ALLOW_RUNNING_UI = '1'
    $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = 'original'
    foreach ($name in $interactionVariables) { [Environment]::SetEnvironmentVariable($name, '1') }
    Remove-Item Env:AUDIOPILOT_SCRIPT_TEST_FAIL_CATEGORY -ErrorAction SilentlyContinue
    Remove-Item Env:AUDIOPILOT_SCRIPT_TEST_MIXED_STREAMS -ErrorAction SilentlyContinue
    Remove-Item Env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE -ErrorAction SilentlyContinue
    if ((Get-Command dotnet).Source -ne (Join-Path $toolsRoot 'dotnet.ps1')) { throw 'The fake dotnet was not selected.' }
    $extra = @('--long-running', '120', '--diagnostic-output-directory', 'directory with spaces; $(literal)', '--crashdump', '--crashdump-type', 'Full')
    & $runner -Category full -Project $project -Coverage -ShowLogs -DotnetTestArgs $extra
    if ($LASTEXITCODE -ne 0) { throw 'Full invocation failed.' }
    foreach ($category in @('unit', 'integration', 'stress')) {
        $record = Get-TestRecord "artifacts/testresults/coverage/$category" | ConvertFrom-Json
        foreach ($argument in $extra) {
            if ($argument -cnotin $record.Arguments) { throw "Argument was not preserved: $argument" }
        }
        if (@($record.Arguments | Where-Object { $_ -eq '--crashdump' }).Count -ne 1 -or
            @($record.Arguments | Where-Object { $_ -eq '--crashdump-type' }).Count -ne 1 -or
            'Mini' -in $record.Arguments) { throw 'Explicit crash-dump arguments were duplicated or overridden.' }
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
    $record = Get-TestRecord 'artifacts/testresults/unit' | ConvertFrom-Json
    $dumpTypeIndex = [Array]::IndexOf($record.Arguments, '--crashdump-type')
    if ('--crashdump' -notin $record.Arguments -or $dumpTypeIndex -lt 0 -or $record.Arguments[$dumpTypeIndex + 1] -ne 'Mini') {
        throw 'Default test runs must collect a native mini dump if the process crashes.'
    }
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

    foreach ($name in $soakVariables | Where-Object { $_ -ne 'AUDIOPILOT_HARDWARE_SOAK_MINUTES' }) {
        [Environment]::SetEnvironmentVariable($name, $name)
    }
    foreach ($scenario in @(@{ Minutes = ''; Timeout = '100m' }, @{ Minutes = '120'; Timeout = '370m' })) {
        $env:AUDIOPILOT_HARDWARE_SOAK_MINUTES = $scenario.Minutes
        & $runner -Category hardware-soak -AllowHardwareAudio -Project $project -NoBuild
        if ($LASTEXITCODE -ne 0) { throw 'Hardware soak invocation failed.' }
        $record = Get-TestRecord 'artifacts/testresults/hardware-soak' | ConvertFrom-Json
        $timeoutIndex = [Array]::IndexOf($record.Arguments, '--timeout')
        if ($timeoutIndex -lt 0 -or $record.Arguments[$timeoutIndex + 1] -ne $scenario.Timeout) {
            throw 'Hardware soak timeout does not cover all sequential soaks plus cleanup.'
        }
    }
    & $runner -Category hardware-soak -AllowHardwareAudio -Project $project -NoBuild -DotnetTestArgs @('--timeout', '400m')
    $record = Get-TestRecord 'artifacts/testresults/hardware-soak' | ConvertFrom-Json
    if (@($record.Arguments | Where-Object { $_ -eq '--timeout' }).Count -ne 1 -or '400m' -notin $record.Arguments) {
        throw 'Explicit soak timeout was overridden.'
    }

    & $runner -Category unit -Project $project -NoBuild -DotnetTestArgs @('--timeout=2m', '--long-running=90')
    $record = Get-TestRecord 'artifacts/testresults/unit' | ConvertFrom-Json
    if ('--timeout=2m' -notin $record.Arguments -or '--timeout' -in $record.Arguments -or
        '--long-running=90' -notin $record.Arguments -or '--long-running' -in $record.Arguments) {
        throw 'Equals-form timing options were duplicated or overridden.'
    }

    & $runner -Category visual -AllowDesktopInteraction -Project $project -NoBuild
    if ($LASTEXITCODE -ne 0) { throw 'Explicit visual invocation failed.' }
    $record = Get-TestRecord 'artifacts/testresults/visual' | ConvertFrom-Json
    if ($record.ShowWindows -ne '1' -or $record.VisualWpf -ne '1' -or $record.AudioHardware -or $record.HardwareSoak) {
        throw 'Visual opt-in did not stay separate from audio permission.'
    }

    $completeVisualReport = Get-TestRecord 'artifacts/testresults/visual'
    $completeUnitReport = Get-TestRecord 'artifacts/testresults/unit'
    & $runner -Category visual -AllowDesktopInteraction -TestClass TestWindowFactoryTests -TestMethod VisibleFixture_UsesAppPaletteControlsAndNativeTitleBarWithoutChangingApplicationTheme -Project $project -NoBuild
    if ($LASTEXITCODE -ne 0) { throw 'Focused visual invocation failed.' }
    $record = Get-TestRecord 'artifacts/testresults/focused/visual' | ConvertFrom-Json
    $queryIndex = [Array]::IndexOf($record.Arguments, '--filter-query')
    if ($record.Arguments[$queryIndex + 1] -cne '/*/*/TestWindowFactoryTests/VisibleFixture_UsesAppPaletteControlsAndNativeTitleBarWithoutChangingApplicationTheme/[Category=VisualWpf]') {
        throw 'Focused visual invocation lost its class, method, or category filter.'
    }
    & $runner -Category unit -TestClass SettingsValidationServiceTests -Project $project -NoBuild
    if ($LASTEXITCODE -ne 0) { throw 'Focused unit invocation failed.' }
    $record = Get-TestRecord 'artifacts/testresults/focused/unit' | ConvertFrom-Json
    $queryIndex = [Array]::IndexOf($record.Arguments, '--filter-query')
    if ($record.Arguments[$queryIndex + 1] -cne '/*/*/SettingsValidationServiceTests/*/[(Category!=Integration)&(Category!=Stress)&(Category!=VisualWpf)&(Category!=HardwareSoak)]' -or $record.ShowWindows) {
        throw 'Focused unit invocation weakened category isolation.'
    }
    if ((Get-TestRecord 'artifacts/testresults/visual') -cne $completeVisualReport -or
        (Get-TestRecord 'artifacts/testresults/unit') -cne $completeUnitReport) {
        throw 'Focused checks overwrote complete-suite reports.'
    }
    $rejected = $false
    try { & $runner -Category full -TestClass TestWindowFactoryTests -Project $project -NoBuild }
    catch { $rejected = $_.Exception.Message -match 'specific category' }
    if (-not $rejected) { throw 'A focused full run must be rejected instead of failing midway through unrelated empty categories.' }

    & $runner -Category full -AllowHardwareAudio -Project $project -NoBuild
    if ($LASTEXITCODE -ne 0) { throw 'Explicit hardware invocation failed.' }
    foreach ($category in @('unit', 'integration', 'stress')) {
        $record = Get-TestRecord "artifacts/testresults/$category" | ConvertFrom-Json
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
    $record = Get-TestRecord 'artifacts/testresults/integration' | ConvertFrom-Json
    $policyIndex = [Array]::IndexOf($record.Arguments, '--zero-tests-policy')
    if ($policyIndex -lt 0 -or $record.Arguments[$policyIndex + 1] -ne 'strict') { throw 'Required hardware allowed all tests to skip.' }
    Remove-Item Env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE

    Remove-Item Env:AUDIOPILOT_TEST_ALLOW_RUNNING_UI
    function Get-Process { param([string]$Name) if ($Name -eq 'AudioPilot') { [pscustomobject]@{ Id = 1 } } }
    try {
        & $runner -Category pure -Project $project -NoBuild
        if ($LASTEXITCODE -ne 0) { throw 'Pure tests were blocked by the running-app guard.' }
        $record = Get-TestRecord 'artifacts/testresults/pure' | ConvertFrom-Json
        $query = $record.Arguments[[Array]::IndexOf($record.Arguments, '--filter-query') + 1]
        if ($query -notlike '*Execution=Pure*' -or $record.AudioHardware -or $record.VisualWpf -or $record.ShowWindows) {
            throw 'Pure tests lost their explicit discovery or interaction restrictions.'
        }
        $blocked = $false
        try { & $runner -Category unit -Project $project -NoBuild }
        catch { $blocked = $_.Exception.Message -like '*UI is running*' }
        if (-not $blocked) { throw 'The complete unit suite bypassed its running-app guard.' }
    }
    finally { Remove-Item Function:Get-Process; $env:AUDIOPILOT_TEST_ALLOW_RUNNING_UI = '1' }

    $gateRoot = Join-Path $runRoot 'concurrent'
    New-Item -ItemType Directory -Path $gateRoot -Force | Out-Null
    $env:AUDIOPILOT_SCRIPT_TEST_GATE_ROOT = $gateRoot
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $runRoot
    foreach ($argument in @('-NoProfile', '-File', $runner, '-Category', 'unit', '-Project', $project, '-NoBuild', '-TestClass', 'FirstConcurrentProbe')) { $info.ArgumentList.Add($argument) }
    $first = [Diagnostics.Process]::Start($info)
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath (Join-Path $gateRoot 'started'))) {
            if ($watch.Elapsed.TotalSeconds -gt 5) { throw 'First concurrent test run did not start.' }
            Start-Sleep -Milliseconds 20
        }
        $firstDirectory = (Get-Content -LiteralPath (Join-Path $gateRoot 'started') -Raw).Trim()
        & $runner -Category unit -Project $project -NoBuild -TestClass SecondConcurrentProbe
        $secondDirectory = Get-TestRunDirectory 'artifacts/testresults/focused/unit'
        if ($firstDirectory -eq $secondDirectory -or -not (Test-Path -LiteralPath (Join-Path $firstDirectory 'record.json'))) {
            throw 'A concurrent focused run replaced the active invocation.'
        }
    }
    finally {
        Set-Content -LiteralPath (Join-Path $gateRoot 'release') -Value release
        if (-not $first.WaitForExit(8000)) { $first.Kill(); $first.WaitForExit(); throw 'Concurrent test helper failed to stop.' }
        if ($first.ExitCode -ne 0) { throw 'First concurrent test run failed.' }
        $first.Dispose()
        Remove-Item Env:AUDIOPILOT_SCRIPT_TEST_GATE_ROOT
    }
    if (-not (Test-Path -LiteralPath (Join-Path $secondDirectory 'record.json'))) { throw 'The first run overwrote the second run results.' }

    foreach ($withCoverage in @($false, $true)) {
        $resultsRoot = if ($withCoverage) { 'artifacts/testresults/coverage/unit' } else { 'artifacts/testresults/unit' }
        & $runner -Category unit -Project $project -NoBuild -Coverage:$withCoverage
        $firstDirectory = Get-TestRunDirectory $resultsRoot
        Set-Content -LiteralPath (Join-Path $firstDirectory 'previous-crash.dmp') -Value 'dump sentinel'
        & $runner -Category unit -Project $project -NoBuild -Coverage:$withCoverage
        $secondDirectory = Get-TestRunDirectory $resultsRoot
        if ($firstDirectory -eq $secondDirectory -or
            (Get-Content -LiteralPath (Join-Path $firstDirectory 'previous-crash.dmp') -Raw).Trim() -ne 'dump sentinel') {
            throw 'A later run replaced an earlier invocation or its crash evidence.'
        }
    }

    $resultsRoot = 'artifacts/testresults/unit'
    $activeDirectory = Join-Path $resultsRoot ([Guid]::NewGuid().ToString('N'))
    $foreignDirectory = Join-Path $resultsRoot ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $activeDirectory, $foreignDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $foreignDirectory 'run.json') -Value '{'
    $seeded = @(foreach ($index in 1..20) {
        $id = [Guid]::NewGuid().ToString('N')
        $directory = Join-Path $resultsRoot $id
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        @{ Owner = 'AudioPilot.TestRunner'; RunId = $id; ExitCode = 0; CompletedUtc = [DateTimeOffset]::UtcNow.AddDays(-1).AddSeconds(-$index).ToString('O') } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'run.json')
        $directory
    })
    Set-Content -LiteralPath (Join-Path $seeded[-1] 'keep.dmp') -Value 'dump sentinel'
    & $runner -Category unit -Project $project -NoBuild
    if ((Test-Path -LiteralPath $seeded[-2]) -or -not (Test-Path -LiteralPath $seeded[-1]) -or
        -not (Test-Path -LiteralPath $activeDirectory) -or -not (Test-Path -LiteralPath $foreignDirectory)) {
        throw 'Result retention removed active/foreign/crash evidence or failed to bound successful reports.'
    }
    Get-TestRecord $resultsRoot | Out-Null

    $coverageRoot = Join-Path $runRoot 'coverage-selection'
    $id = [Guid]::NewGuid().ToString('N')
    $successfulCoverage = Join-Path $coverageRoot $id
    New-Item -ItemType Directory -Path $successfulCoverage -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $successfulCoverage 'result.cobertura.xml') -Value '<coverage><packages><package><classes><class filename="source.cs"><lines><line number="1" hits="1"/><line number="2" hits="0"/></lines></class></classes></package></packages></coverage>'
    $policy = Join-Path $runRoot 'coverage-policy.json'
    @{ minimumCoveragePercent = 40; ratchetStepPercent = 1; nextTargetPercent = 80 } | ConvertTo-Json | Set-Content -LiteralPath $policy
    foreach ($scenario in @(@{ExitCode=0;Focused=$false;RunId=$id;Category='unit'}, @{ExitCode=7;Focused=$false;RunId=$id;Category='unit'}, @{ExitCode=0;Focused=$true;RunId=$id;Category='unit'}, @{ExitCode=0;Focused=$false;RunId='../outside';Category='unit'}, @{ExitCode=0;Focused=$false;RunId=$id;Category='pure'})) {
        $scenario | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $coverageRoot 'latest.json')
        $rejected = $false
        try { & (Join-Path $repositoryRoot 'scripts/validate-coverage.ps1') -CoverageRoot $coverageRoot -PolicyPath $policy }
        catch { $rejected = $true }
        $shouldReject = $scenario.ExitCode -ne 0 -or $scenario.Focused -or $scenario.RunId -ne $id -or $scenario.Category -ne 'unit'
        if ($rejected -ne $shouldReject) { throw 'Coverage selected a failed/focused/unsafe run or rejected the completed full run.' }
        $scenario | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $successfulCoverage 'run.json')
        $rejected = $false
        try { & (Join-Path $repositoryRoot 'scripts/validate-coverage.ps1') -CoverageRoot $successfulCoverage -PolicyPath $policy }
        catch { $rejected = $true }
        if ($rejected -ne $shouldReject) { throw 'Selecting a run directory directly bypassed coverage validation.' }
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

    $env:AUDIOPILOT_SCRIPT_TEST_MIXED_STREAMS = '1'
    $mixedOutput = & $runner -Category full -Project $project -NoBuild 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $mixedOutput -notmatch 'script warning sentinel' -or
        $mixedOutput -notmatch 'native stderr sentinel' -or $mixedOutput -match 'Cannot process the XML|#< CLIXML') {
        throw "Child stream output was not preserved as text: $mixedOutput"
    }
    Remove-Item Env:AUDIOPILOT_SCRIPT_TEST_MIXED_STREAMS

    $failureRoot = Join-Path $runRoot 'failure'
    New-Item -ItemType Directory -Path $failureRoot | Out-Null
    Set-Location $failureRoot
    $env:AUDIOPILOT_SCRIPT_TEST_FAIL_CATEGORY = 'integration'
    & $runner -Category full -Project $project -Coverage
    if ($LASTEXITCODE -ne 7 -or (Test-Path 'artifacts/testresults/coverage/stress')) { throw 'Child failure did not stop subsequent categories.' }
    Write-Host 'Test runner checks passed: interaction opt-ins, environment isolation, argument preservation, category retention, logging, diagnostic retention, filter rejection, mixed-stream output, and child failure propagation.'
}
finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
}
exit 0
