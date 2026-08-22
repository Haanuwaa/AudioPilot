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

if (-not (Test-Path $Project)) {
    throw "Test project not found: $Project"
}

if ($Category -eq "full") {
    $fullCategories = @("unit", "integration", "stress")
    for ($categoryIndex = 0; $categoryIndex -lt $fullCategories.Count; $categoryIndex++) {
        $childCategory = $fullCategories[$categoryIndex]
        Write-Host "==> Isolated full-suite category: $childCategory"
        $childArguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $PSCommandPath,
            "-Category", $childCategory,
            "-Project", $Project,
            "-Configuration", $Configuration
        )

        if ($NoBuild -or $categoryIndex -gt 0) { $childArguments += "-NoBuild" }
        if ($NoRestore -or $categoryIndex -gt 0) { $childArguments += "-NoRestore" }
        if ($Coverage) { $childArguments += "-Coverage" }
        if ($StopRunningUi) { $childArguments += "-StopRunningUi" }
        if ($ShowLogs) { $childArguments += "-ShowLogs" }
        if (@($DotnetTestArgs).Count -gt 0) {
            $childArguments += "-DotnetTestArgs"
            $childArguments += $DotnetTestArgs
        }

        & pwsh @childArguments
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

    $disableLogging = if ($ShowLogs) { $null } else { "1" }

    switch ($SelectedCategory) {
        "unit" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            Remove-Item Env:AUDIOPILOT_RUN_INTEGRATION -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_HARDWARE_SOAK -ErrorAction SilentlyContinue
        }
        "integration" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            $env:AUDIOPILOT_RUN_INTEGRATION = "1"
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_HARDWARE_SOAK -ErrorAction SilentlyContinue
        }
        "visual" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            $env:AUDIOPILOT_RUN_INTEGRATION = "1"
            $env:AUDIOPILOT_RUN_VISUAL_WPF = "1"
            $env:AUDIOPILOT_TEST_SHOW_WINDOWS = "1"
            Remove-Item Env:AUDIOPILOT_RUN_STRESS -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_HARDWARE_SOAK -ErrorAction SilentlyContinue
        }
        "stress" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            Remove-Item Env:AUDIOPILOT_RUN_INTEGRATION -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            $env:AUDIOPILOT_RUN_STRESS = "1"
            Remove-Item Env:AUDIOPILOT_RUN_HARDWARE_SOAK -ErrorAction SilentlyContinue
        }
        "hardware-soak" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            $env:AUDIOPILOT_RUN_INTEGRATION = "1"
            $env:AUDIOPILOT_RUN_STRESS = "1"
            $env:AUDIOPILOT_RUN_HARDWARE_SOAK = "1"
            $env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE = "1"
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
        }
        "full" {
            if ($disableLogging) { $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = $disableLogging }
            $env:AUDIOPILOT_RUN_INTEGRATION = "1"
            Remove-Item Env:AUDIOPILOT_RUN_VISUAL_WPF -ErrorAction SilentlyContinue
            Remove-Item Env:AUDIOPILOT_TEST_SHOW_WINDOWS -ErrorAction SilentlyContinue
            $env:AUDIOPILOT_RUN_STRESS = "1"
            Remove-Item Env:AUDIOPILOT_RUN_HARDWARE_SOAK -ErrorAction SilentlyContinue
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
    $dotnetArgs += "--minimum-expected-tests"
    $dotnetArgs += "1"
    $dotnetArgs += "--zero-tests-policy"
    $dotnetArgs += "strict"
    $dotnetArgs += "--results-directory"
    $dotnetArgs += $ResultsDirectory
    $dotnetArgs += "--report-xunit-trx"
    $dotnetArgs += "--report-xunit-trx-filename"
    $dotnetArgs += "AudioPilot.Tests-$SelectedCategory.trx"
    if (-not ($DotnetTestArgs -contains "--timeout")) {
        $timeout = switch ($SelectedCategory) {
            "visual" { "10m" }
            "integration" { "25m" }
            "stress" { "25m" }
            "hardware-soak" { "90m" }
            "full" { "45m" }
            default { "20m" }
        }
        $dotnetArgs += "--timeout"
        $dotnetArgs += $timeout
    }

    if (-not ($DotnetTestArgs -contains "--long-running")) {
        $dotnetArgs += "--long-running"
        $dotnetArgs += "60"
    }

    if (@($TestFilterArguments).Count -gt 0) {
        $dotnetArgs += $TestFilterArguments
    }

    if ($Coverage) {
        $dotnetArgs += "--coverage"
        $dotnetArgs += "--coverage-output-format"
        $dotnetArgs += "cobertura"
        $dotnetArgs += "--coverage-output"
        $dotnetArgs += "AudioPilot.Tests-$SelectedCategory-coverage.cobertura.xml"
        $dotnetArgs += "--coverage-settings"
        $dotnetArgs += ".github/quality/coverage.settings.xml"
    }

    return $dotnetArgs
}

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

    $resultsDirectory = if ($Coverage) { "artifacts/testresults/coverage" } else { "artifacts/testresults/$Category" }
    if (Test-Path -LiteralPath $resultsDirectory) {
        Remove-Item -LiteralPath $resultsDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
    $dotnetArgs = New-DotnetTestArguments -ResultsDirectory $resultsDirectory -SelectedCategory $Category -TestFilterArguments $testFilterArguments

    & dotnet test @dotnetArgs
    exit $LASTEXITCODE
}
finally {
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
