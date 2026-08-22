[CmdletBinding()]
param(
    [string]$CliHostProject = "AudioPilot.CliHost/AudioPilot.CliHost.csproj",
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [switch]$Strict
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $CliHostProject)) {
    throw "CLI host project not found: $CliHostProject"
}

$deviceTargets = @(
    @{ Name = "AUDIOPILOT_TEST_OUTPUT_DEVICE_A"; Flow = "output" },
    @{ Name = "AUDIOPILOT_TEST_OUTPUT_DEVICE_B"; Flow = "output" },
    @{ Name = "AUDIOPILOT_TEST_INPUT_DEVICE_A"; Flow = "input" },
    @{ Name = "AUDIOPILOT_TEST_INPUT_DEVICE_B"; Flow = "input" }
)

function Test-ConfiguredDeviceId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Flow,
        [Parameter(Mandatory = $true)]
        [string]$DeviceId
    )

    $dotnetArgs = @(
        "run",
        "--project",
        $CliHostProject,
        "--configuration",
        $Configuration
    )

    if ($NoBuild) {
        $dotnetArgs += "--no-build"
    }

    $dotnetArgs += @(
        "--",
        "devices",
        "get",
        $Flow,
        $DeviceId,
        "--json"
    )

    & dotnet @dotnetArgs 1>$null 2>$null
    return ($LASTEXITCODE -eq 0)
}

$missing = New-Object System.Collections.Generic.List[string]
$configured = New-Object System.Collections.Generic.List[object]

foreach ($target in $deviceTargets) {
    $value = [Environment]::GetEnvironmentVariable($target.Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        $missing.Add($target.Name)
        continue
    }

    $configured.Add([PSCustomObject]@{
        Name = $target.Name
        Flow = $target.Flow
        DeviceId = $value.Trim()
    })
}

if ($missing.Count -gt 0) {
    $message = "Missing release hardware environment variables: $($missing -join ', ')."
    if ($Strict) {
        throw $message
    }

    Write-Warning "$message Skipping hardware validation."
    exit 0
}

$outputA = ($configured | Where-Object Name -eq "AUDIOPILOT_TEST_OUTPUT_DEVICE_A").DeviceId
$outputB = ($configured | Where-Object Name -eq "AUDIOPILOT_TEST_OUTPUT_DEVICE_B").DeviceId
$inputA = ($configured | Where-Object Name -eq "AUDIOPILOT_TEST_INPUT_DEVICE_A").DeviceId
$inputB = ($configured | Where-Object Name -eq "AUDIOPILOT_TEST_INPUT_DEVICE_B").DeviceId

$pairErrors = New-Object System.Collections.Generic.List[string]
if ($outputA -eq $outputB) {
    $pairErrors.Add("AUDIOPILOT_TEST_OUTPUT_DEVICE_A and AUDIOPILOT_TEST_OUTPUT_DEVICE_B must reference different output endpoint IDs.")
}
if ($inputA -eq $inputB) {
    $pairErrors.Add("AUDIOPILOT_TEST_INPUT_DEVICE_A and AUDIOPILOT_TEST_INPUT_DEVICE_B must reference different input endpoint IDs.")
}

if ($pairErrors.Count -gt 0) {
    $message = $pairErrors -join ' '
    if ($Strict) {
        throw $message
    }

    Write-Warning $message
    exit 1
}

$invalid = New-Object System.Collections.Generic.List[string]

foreach ($entry in $configured) {
    if (-not (Test-ConfiguredDeviceId -Flow $entry.Flow -DeviceId $entry.DeviceId)) {
        $invalid.Add("$($entry.Name) ($($entry.Flow)): '$($entry.DeviceId)'")
    }
}

if ($invalid.Count -gt 0) {
    $message = "Configured release hardware IDs were not found on this machine: $($invalid -join ', '). Refresh the AUDIOPILOT_TEST_* secrets with exact Core Audio endpoint IDs from this runner."
    if ($Strict) {
        throw $message
    }

    Write-Warning $message
    exit 1
}

Write-Host "Release hardware validation succeeded for $($configured.Count) configured endpoint IDs."
