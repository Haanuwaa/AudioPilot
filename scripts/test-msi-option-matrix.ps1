[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallMsiPath,

    [string]$ExpectedVersion,

    [string]$TestRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedMsiPath = (Resolve-Path -LiteralPath $InstallMsiPath).Path
$effectiveTestRoot = if ([string]::IsNullOrWhiteSpace($TestRoot)) {
    Join-Path $PSScriptRoot "..\artifacts\msi-option-matrix"
}
else {
    $TestRoot
}
$effectiveTestRoot = [IO.Path]::GetFullPath($effectiveTestRoot)
New-Item -ItemType Directory -Path $effectiveTestRoot -Force | Out-Null

$cases = foreach ($desktop in @("0", "1")) {
    foreach ($startMenu in @("0", "1")) {
        foreach ($cliPath in @("0", "1")) {
            [pscustomobject]@{
                Desktop = $desktop
                StartMenu = $startMenu
                CliPath = $cliPath
            }
        }
    }
}

foreach ($case in $cases) {
    $caseName = "desktop-$($case.Desktop)-start-$($case.StartMenu)-path-$($case.CliPath)"
    $caseRoot = Join-Path $effectiveTestRoot $caseName
    Write-Host "=== MSI option matrix: $caseName ==="

    $arguments = @{
        InstallMsiPath = $resolvedMsiPath
        InstallRoot = Join-Path $caseRoot "install root with spaces"
        DataRoot = Join-Path $caseRoot "data root with spaces"
        DesktopShortcut = $case.Desktop
        StartMenuShortcut = $case.StartMenu
        AddCliToPath = $case.CliPath
        LogName = "option-matrix-$caseName"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
        $arguments.ExpectedVersion = $ExpectedVersion
    }

    # Exercise destructive cleanup for one case and preservation semantics for all others.
    if ($case.Desktop -eq "1" -and $case.StartMenu -eq "1" -and $case.CliPath -eq "1") {
        $arguments.CleanUninstall = $true
    }

    & (Join-Path $PSScriptRoot "test-msi-smoke.ps1") @arguments
}

Write-Host "MSI option matrix passed ($($cases.Count) combinations)."
