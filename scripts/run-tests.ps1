param(
    [ValidateSet("unit", "pure", "integration", "visual", "stress", "hardware-soak", "full")]
    [string]$Category = "unit",
    [string]$Project = "AudioPilot.Tests/AudioPilot.Tests.csproj",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [switch]$NoRestore,
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string]$TestClass,
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string]$TestMethod,
    [string[]]$DotnetTestArgs = @(),
    [switch]$Coverage,
    [switch]$StopRunningUi,
    [switch]$AllowDesktopInteraction,
    [switch]$AllowHardwareAudio,
    [switch]$ShowLogs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Category -eq 'visual' -and -not $AllowDesktopInteraction) {
    throw 'Visual tests show windows, popups, and tray menus and can take focus. Run on a disposable desktop or explicitly add -AllowDesktopInteraction.'
}
if ($Category -eq 'hardware-soak' -and -not $AllowHardwareAudio) {
    throw 'Hardware soak changes real audio devices and can play or record audio. Explicitly add -AllowHardwareAudio on a dedicated test system.'
}

# This script owns the category query. xUnit treats separate queries as OR and throws
# an unhandled exception when query filters are combined with simple/VSTest filters.
$extraFilters = @($DotnetTestArgs | Where-Object { $_ -match '^--filter(?:$|[-=])' })
if ($extraFilters.Count -gt 0) {
    throw "run-tests.ps1 supplies the category filter; extra filter arguments are not supported ($($extraFilters -join ', ')). Use -TestClass and -TestMethod for focused runs."
}
if ($Category -eq 'full' -and ($TestClass -or $TestMethod)) {
    throw 'Choose a specific category when using -TestClass or -TestMethod; full runs every suite.'
}

if (-not (Test-Path $Project)) {
    throw "Test project not found: $Project"
}

if ($Category -eq "full") {
    $fullCategories = @("unit", "integration", "stress")
    for ($categoryIndex = 0; $categoryIndex -lt $fullCategories.Count; $categoryIndex++) {
        $childCategory = $fullCategories[$categoryIndex]
        Write-Host "==> Isolated full-suite category: $childCategory"
        $childParameters = @{
            Category = $childCategory
            Project = $Project
            Configuration = $Configuration
            NoBuild = [bool]($NoBuild -or $categoryIndex -gt 0)
            NoRestore = [bool]($NoRestore -or $categoryIndex -gt 0)
            Coverage = [bool]$Coverage
            StopRunningUi = [bool]$StopRunningUi
            AllowHardwareAudio = [bool]$AllowHardwareAudio
            ShowLogs = [bool]$ShowLogs
            DotnetTestArgs = @($DotnetTestArgs)
        }

        # -File cannot preserve an array parameter across the native process boundary.
        # Transfer the parameter values as data, then splat them inside the child process.
        $parameterPayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($childParameters | ConvertTo-Json -Compress)))
        $childCommand = '$parameters = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(''{0}'')) | ConvertFrom-Json -AsHashtable; & ''{1}'' @parameters; exit $LASTEXITCODE' -f $parameterPayload, $PSCommandPath.Replace("'", "''")
        $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCommand))
        & pwsh -NoProfile -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand $encodedCommand
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    exit 0
}

function Set-TestCategoryEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SelectedCategory,
        [switch]$ShowLogs
    )

    if ($ShowLogs) {
        Remove-Item Env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = '1'
    }

    Remove-Item Env:AUDIOPILOT_RUN_AUDIO_HARDWARE -ErrorAction SilentlyContinue
    if ($AllowHardwareAudio -and $SelectedCategory -in @('integration', 'hardware-soak')) {
        $env:AUDIOPILOT_RUN_AUDIO_HARDWARE = '1'
    }

    switch ($SelectedCategory) {
        { $_ -in @("unit", "pure") } {
            Remove-Item Env:AUDIOPILOT_RUN_INTEGRATION -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_HARDWARE_SOAK -ErrorAction SilentlyContinue
        }
        "integration" {
            $env:AUDIOPILOT_RUN_INTEGRATION = "1"
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_HARDWARE_SOAK -ErrorAction SilentlyContinue
        }
        "visual" {
            $env:AUDIOPILOT_RUN_INTEGRATION = "1"
            $env:AUDIOPILOT_RUN_VISUAL_WPF = "1"
            $env:AUDIOPILOT_TEST_SHOW_WINDOWS = "1"
            Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_HARDWARE_SOAK -ErrorAction SilentlyContinue
        }
        "stress" {
            Remove-Item Env:AUDIOPILOT_RUN_INTEGRATION -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            $env:AUDIOPILOT_RUN_STRESS = "1"
            Remove-Item Env:AUDIOPILOT_RUN_HARDWARE_SOAK -ErrorAction SilentlyContinue
        }
        "hardware-soak" {
            $env:AUDIOPILOT_RUN_INTEGRATION = "1"
            $env:AUDIOPILOT_RUN_STRESS = "1"
            $env:AUDIOPILOT_RUN_HARDWARE_SOAK = "1"
            $env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE = "1"
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
        }
    }
}

function New-DotnetTestArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResultsDirectory,
        [Parameter(Mandatory = $true)]
        [string]$SelectedCategory,
        [string[]]$TestFilterArguments
    )

    $dotnetArgs = @("--project", $Project, "--configuration", $Configuration)
    if ($NoBuild) {
        $dotnetArgs += "--no-build"
    }
    if ($NoRestore) {
        $dotnetArgs += "--no-restore"
    }
    $dotnetArgs += $DotnetTestArgs
    if ('--crashdump' -notin $DotnetTestArgs) {
        $dotnetArgs += '--crashdump'
    }
    if (-not ($DotnetTestArgs -match '^--crashdump-type(?:=|$)')) {
        $dotnetArgs += @('--crashdump-type', 'Mini')
    }
    $requireHardware = $AllowHardwareAudio -and $env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE -in @('1', 'true')
    $zeroTestsPolicy = if ($SelectedCategory -eq 'integration' -and -not $requireHardware) { 'allow-skipped' } else { 'strict' }
    $dotnetArgs += @(
        '--minimum-expected-tests', '1', '--zero-tests-policy', $zeroTestsPolicy,
        '--results-directory', $ResultsDirectory,
        '--report-xunit-trx', '--report-xunit-trx-filename', "AudioPilot.Tests-$SelectedCategory.trx"
    )
    if (-not ($DotnetTestArgs -match '^--timeout(?:=|$)')) {
        $timeout = switch ($SelectedCategory) {
            "visual" { "10m" }
            "integration" { "25m" }
            "stress" { "25m" }
            "hardware-soak" {
                $minutes = 30
                $configuredMinutes = $env:AUDIOPILOT_HARDWARE_SOAK_MINUTES
                if (-not [string]::IsNullOrWhiteSpace($configuredMinutes) -and
                    (-not [int]::TryParse($configuredMinutes, [ref]$minutes) -or $minutes -lt 1 -or $minutes -gt 120)) {
                    throw 'AUDIOPILOT_HARDWARE_SOAK_MINUTES must be an integer from 1 through 120.'
                }
                "$($minutes * 3 + 10)m"
            }
            default { "20m" }
        }
        $dotnetArgs += @('--timeout', $timeout)
    }

    if (-not ($DotnetTestArgs -match '^--long-running(?:=|$)')) {
        $dotnetArgs += @('--long-running', '60')
    }

    if (@($TestFilterArguments).Count -gt 0) {
        $dotnetArgs += $TestFilterArguments
    }

    if ($Coverage) {
        $dotnetArgs += @(
            '--coverage', '--coverage-output-format', 'cobertura',
            '--coverage-output', "AudioPilot.Tests-$SelectedCategory-coverage.cobertura.xml",
            '--coverage-settings', '.github/quality/coverage.settings.xml'
        )
    }

    return $dotnetArgs
}

$originalDisableConsoleLogging = $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING
$originalIntegration = $env:AUDIOPILOT_RUN_INTEGRATION
$originalAudioHardware = $env:AUDIOPILOT_RUN_AUDIO_HARDWARE
$originalVisualWpf = $env:AUDIOPILOT_RUN_VISUAL_WPF
$originalShowWindows = $env:AUDIOPILOT_TEST_SHOW_WINDOWS
$originalStress = $env:AUDIOPILOT_RUN_STRESS
$originalHardwareSoak = $env:AUDIOPILOT_RUN_HARDWARE_SOAK
$originalRequireIntegrationHardware = $env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE

try {
    Set-TestCategoryEnvironment -SelectedCategory $Category -ShowLogs:$ShowLogs

    if ($Category -eq "hardware-soak") {
        $requiredHardwareVariables = @(
            "AUDIOPILOT_TEST_OUTPUT_DEVICE_A",
            "AUDIOPILOT_TEST_OUTPUT_DEVICE_B",
            "AUDIOPILOT_TEST_INPUT_DEVICE_A",
            "AUDIOPILOT_TEST_INPUT_DEVICE_B"
        )
        $missingHardwareVariables = @($requiredHardwareVariables | Where-Object {
            [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
        })
        if ($missingHardwareVariables.Count -gt 0) {
            throw "Hardware soak requires configured real endpoints. Missing environment variables: $($missingHardwareVariables -join ', ')"
        }
    }

    $testFilterArguments = switch ($Category) {
        "unit" { @("--filter-query", "/[(Category!=Integration)&(Category!=Stress)&(Category!=VisualWpf)&(Category!=HardwareSoak)]") }
        "pure" { @("--filter-query", "/[(Execution=Pure)&(Category!=Integration)&(Category!=Stress)&(Category!=VisualWpf)&(Category!=HardwareSoak)]") }
        "integration" { @("--filter-query", "/[(Category=Integration)&(Category!=VisualWpf)&(Category!=HardwareSoak)]") }
        "visual" { @("--filter-query", "/[Category=VisualWpf]") }
        "stress" { @("--filter-query", "/[(Category=Stress)&(Category!=HardwareSoak)]") }
        "hardware-soak" { @("--filter-query", "/[Category=HardwareSoak]") }
        default { @() }
    }
    if ($TestClass -or $TestMethod) {
        $classPattern = if ($TestClass) { $TestClass } else { '*' }
        $methodPattern = if ($TestMethod) { $TestMethod } else { '*' }
        $testFilterArguments[1] = "/*/*/$classPattern/$methodPattern" + $testFilterArguments[1]
    }

    if ($Category -ne "pure" -and -not $env:AUDIOPILOT_TEST_ALLOW_RUNNING_UI) {
        $runningUi = Get-Process -Name "AudioPilot" -ErrorAction SilentlyContinue
        if ($runningUi -and $StopRunningUi) {
            $runningUi | Stop-Process -Force
            Wait-Process -Name "AudioPilot" -Timeout 5 -ErrorAction SilentlyContinue
        }
        elseif ($runningUi) {
            throw "AudioPilot UI is running. Close it before testing, or rerun with -StopRunningUi to stop it explicitly."
        }
    }

    $resultScope = if ($TestClass -or $TestMethod) { "focused/$Category" } else { $Category }
    $resultsBase = if ($Coverage) { "artifacts/testresults/coverage/$resultScope" } else { "artifacts/testresults/$resultScope" }
    $resultsRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath('artifacts/testresults')
    $resultsBase = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($resultsBase)
    $resultsPrefix = $resultsRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $resultsBase.StartsWith($resultsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Results directory is outside the intended test-results root: $resultsBase"
    }
    for ($directory = [IO.DirectoryInfo]::new($resultsBase); $null -ne $directory -and
        ($directory.FullName.StartsWith($resultsPrefix, [StringComparison]::OrdinalIgnoreCase) -or $directory.FullName -eq $resultsRoot); $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to write through a linked test-results directory: $($directory.FullName)"
        }
    }
    $runId = [Guid]::NewGuid().ToString('N')
    $resultsDirectory = Join-Path $resultsBase $runId
    New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
    Write-Host "Test results: $resultsDirectory"
    $dotnetArgs = New-DotnetTestArguments -ResultsDirectory $resultsDirectory -SelectedCategory $Category -TestFilterArguments $testFilterArguments
    $testExitCode = 1
    try {
        & dotnet test @dotnetArgs
        $testExitCode = $LASTEXITCODE
    }
    finally {
        $metadata = @{ Owner = 'AudioPilot.TestRunner'; RunId = $runId; Category = $Category; Focused = [bool]($TestClass -or $TestMethod); ExitCode = $testExitCode; CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json
        [IO.File]::WriteAllText((Join-Path $resultsDirectory 'run.json'), $metadata)
        $temporaryPath = Join-Path $resultsBase "$runId.latest.tmp"
        try {
            [IO.File]::WriteAllText($temporaryPath, $metadata)
            [IO.File]::Move($temporaryPath, (Join-Path $resultsBase 'latest.json'), $true)
        }
        finally { if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force } }
    }
    try {
        $completedRuns = @(foreach ($directory in Get-ChildItem -LiteralPath $resultsBase -Directory) {
            if ($directory.Name -notmatch '^[a-f0-9]{32}$' -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
            $recordPath = Join-Path $directory.FullName 'run.json'
            if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) { continue }
            try {
                $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
                if ($record.PSObject.Properties['Owner'] -and $record.Owner -eq 'AudioPilot.TestRunner' -and $record.RunId -eq $directory.Name -and $record.ExitCode -eq 0) {
                    [pscustomobject]@{ Directory = $directory; Completed = [DateTimeOffset]::Parse($record.CompletedUtc) }
                }
            }
            catch { continue }
        })
        foreach ($expired in @($completedRuns | Sort-Object Completed -Descending | Select-Object -Skip 10)) {
            $directory = $expired.Directory
            if ($directory.Name -eq $runId -or $directory.Parent.FullName -ne $resultsBase) { continue }
            $evidence = Get-ChildItem -LiteralPath $directory.FullName -Recurse -Force | Where-Object {
                ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Extension -eq '.dmp' -or $_.Name -like '*.sequence.log'
            } | Select-Object -First 1
            if (-not $evidence) { Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
    catch { Write-Verbose "Test result retention skipped: $($_.Exception.Message)" }
    exit $testExitCode
}
finally {
    if ($null -eq $originalAudioHardware) {
        Remove-Item Env:AUDIOPILOT_RUN_AUDIO_HARDWARE -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_RUN_AUDIO_HARDWARE = $originalAudioHardware
    }

    if ($null -eq $originalDisableConsoleLogging) {
        Remove-Item Env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $originalDisableConsoleLogging
    }

    if ($null -eq $originalIntegration) {
        Remove-Item Env:AUDIOPILOT_RUN_INTEGRATION -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_RUN_INTEGRATION = $originalIntegration
    }

    if ($null -eq $originalVisualWpf) {
        Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_RUN_VISUAL_WPF = $originalVisualWpf
    }

    if ($null -eq $originalShowWindows) {
        Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_TEST_SHOW_WINDOWS = $originalShowWindows
    }

    if ($null -eq $originalStress) {
        Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_RUN_STRESS = $originalStress
    }

    if ($null -eq $originalHardwareSoak) {
        Remove-Item Env:AUDIOPILOT_RUN_HARDWARE_SOAK -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_RUN_HARDWARE_SOAK = $originalHardwareSoak
    }

    if ($null -eq $originalRequireIntegrationHardware) {
        Remove-Item Env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE -ErrorAction SilentlyContinue
    }
    else {
        $env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE = $originalRequireIntegrationHardware
    }
}
