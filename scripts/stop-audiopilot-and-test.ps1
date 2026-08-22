param(
    [string]$Project = "AudioPilot.Tests/AudioPilot.Tests.csproj",
    [string[]]$DotnetTestArgs = @("--filter-not-trait", "Category=Integration", "Category=Stress", "Category=VisualWpf", "Category=HardwareSoak"),
    [switch]$ShowLogs,
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

$runningUi = Get-Process -Name "AudioPilot" -ErrorAction SilentlyContinue
if ($CheckOnly) {
    if ($runningUi) {
        Write-Host "AudioPilot is running."
        exit 1
    }

    Write-Host "AudioPilot is not running."
    exit 0
}

if ($runningUi) {
    $runningUi | Stop-Process -Force
    Wait-Process -Name "AudioPilot" -Timeout 5 -ErrorAction SilentlyContinue
}

if (-not $ShowLogs) {
    $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING = "1"
}

& dotnet test --project $Project @DotnetTestArgs --minimum-expected-tests 1
exit $LASTEXITCODE
