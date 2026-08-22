param(
    [ValidateSet("check", "fix")]
    [string]$Action = "check",
    [string]$SolutionPath = "AudioPilot.Format.slnf"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $SolutionPath)) {
    throw "Solution file not found: $SolutionPath"
}

$formatParameters = @($SolutionPath, "--severity", "info")
if ($Action -eq "check") {
    $formatParameters += "--verify-no-changes"
}

& dotnet format @formatParameters
exit $LASTEXITCODE
