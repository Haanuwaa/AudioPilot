param(
    [ValidateSet("unit", "integration", "visual", "stress", "hardware-soak", "full")]
    [string]$Category = "unit",
    [string]$Project = "AudioPilot.Tests/AudioPilot.Tests.csproj",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [switch]$NoRestore,
    [string[]]$DotnetTestArgs = @(),
    [switch]$Coverage,
    [switch]$StopRunningUi,
    [switch]$ShowLogs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# This script owns the category query. xUnit treats separate queries as OR and throws
# an unhandled exception when query filters are combined with simple/VSTest filters.
$extraFilters = @($DotnetTestArgs | Where-Object { $_ -match '^--filter(?:$|[-=])' })
if ($extraFilters.Count -gt 0) {
    throw "run-tests.ps1 supplies the category filter; extra filter arguments are not supported ($($extraFilters -join ', ')). For a focused run, invoke dotnet test with one combined --filter-query instead."
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
            ShowLogs = [bool]$ShowLogs
            DotnetTestArgs = @($DotnetTestArgs)
        }

        # -File cannot preserve an array parameter across the native process boundary.
        # Transfer the parameter values as data, then splat them inside the child process.
        $parameterPayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($childParameters | ConvertTo-Json -Compress)))
        $childCommand = '$parameters = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(''{0}'')) | ConvertFrom-Json -AsHashtable; & ''{1}'' @parameters; exit $LASTEXITCODE' -f $parameterPayload, $PSCommandPath.Replace("'", "''")
        $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCommand))
        & pwsh -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedCommand
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

    switch ($SelectedCategory) {
        "unit" {
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
    $dotnetArgs += @(
        '--minimum-expected-tests', '1', '--zero-tests-policy', 'strict',
        '--results-directory', $ResultsDirectory,
        '--report-xunit-trx', '--report-xunit-trx-filename', "AudioPilot.Tests-$SelectedCategory.trx"
    )
    if (-not ($DotnetTestArgs -contains "--timeout")) {
        $timeout = switch ($SelectedCategory) {
            "visual" { "10m" }
            "integration" { "25m" }
            "stress" { "25m" }
            "hardware-soak" { "90m" }
            default { "20m" }
        }
        $dotnetArgs += @('--timeout', $timeout)
    }

    if (-not ($DotnetTestArgs -contains "--long-running")) {
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
        "integration" { @("--filter-query", "/[Category=Integration]") }
        "visual" { @("--filter-query", "/[Category=VisualWpf]") }
        "stress" { @("--filter-query", "/[Category=Stress]") }
        "hardware-soak" { @("--filter-query", "/[Category=HardwareSoak]") }
        default { @() }
    }

    if (-not $env:AUDIOPILOT_TEST_ALLOW_RUNNING_UI) {
        $runningUi = Get-Process -Name "AudioPilot" -ErrorAction SilentlyContinue
        if ($runningUi -and $StopRunningUi) {
            $runningUi | Stop-Process -Force
            Wait-Process -Name "AudioPilot" -Timeout 5 -ErrorAction SilentlyContinue
        }
        elseif ($runningUi) {
            throw "AudioPilot UI is running. Close it before testing, or rerun with -StopRunningUi to stop it explicitly."
        }
    }

    $resultsDirectory = if ($Coverage) { "artifacts/testresults/coverage/$Category" } else { "artifacts/testresults/$Category" }
    $resultsRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath('artifacts/testresults')
    $resultsDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($resultsDirectory)
    $resultsPrefix = $resultsRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $resultsDirectory.StartsWith($resultsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Results directory is outside the intended test-results root: $resultsDirectory"
    }
    if (Test-Path -LiteralPath $resultsDirectory) {
        if ((Get-Item -LiteralPath $resultsDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to replace a linked test-results directory: $resultsDirectory"
        }
        $priorDiagnostics = Get-ChildItem -LiteralPath $resultsDirectory -File -Recurse |
            Where-Object { $_.Extension -eq '.dmp' -or $_.Name -like '*.sequence.log' } | Select-Object -First 1
        if ($priorDiagnostics) {
            $archiveDirectory = [IO.Path]::GetFullPath((Join-Path $resultsRoot "diagnostics/$Category-$([Guid]::NewGuid().ToString('N'))"))
            if (-not $archiveDirectory.StartsWith($resultsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Diagnostic archive is outside the intended test-results root: $archiveDirectory"
            }
            New-Item -ItemType Directory -Path (Split-Path -Parent $archiveDirectory) -Force | Out-Null
            Move-Item -LiteralPath $resultsDirectory -Destination $archiveDirectory
            Write-Host "Preserved previous crash/hang diagnostics: $archiveDirectory"
        }
        else {
            Remove-Item -LiteralPath $resultsDirectory -Recurse -Force
        }
    }
    New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
    $dotnetArgs = New-DotnetTestArguments -ResultsDirectory $resultsDirectory -SelectedCategory $Category -TestFilterArguments $testFilterArguments

    & dotnet test @dotnetArgs
    exit $LASTEXITCODE
}
finally {
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
