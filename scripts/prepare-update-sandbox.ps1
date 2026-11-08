[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineMsi,
    [Parameter(Mandatory)][string]$CandidateMsi,
    [Parameter(Mandatory)][string]$HelperDirectory,
    [Parameter(Mandatory)][string]$OutputRoot,
    [switch]$EnableNetworking,
    [ValidateRange(0, 7)][int[]]$OptionMasks = @(0, 1, 2, 3, 4, 5, 6, 7)
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($OptionMasks.Count -eq 0 -or @($OptionMasks | Select-Object -Unique).Count -ne $OptionMasks.Count) { throw 'Choose one or more distinct MSI option masks.' }
. (Join-Path $PSScriptRoot 'lib/Msi.ps1')
$baseline = (Resolve-Path -LiteralPath $BaselineMsi).Path
$candidate = (Resolve-Path -LiteralPath $CandidateMsi).Path
$helper = (Resolve-Path -LiteralPath $HelperDirectory).Path
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) { throw 'Choose a new Sandbox output directory to preserve previous evidence.' }
Assert-AudioPilotMsiIdentity -Path $baseline -Version 1.0.0 -Architecture x64
Assert-AudioPilotMsiIdentity -Path $candidate -Version 1.0.1 -Architecture x64
foreach ($package in @($baseline, $candidate)) {
    if ((Get-AudioPilotMsiProperty -Path $package -PropertyName UpgradeCode) -ne '{8D18A00C-7CC8-487B-A462-7D3252139D72}' -or
        (Get-AudioPilotMsiProperty -Path $package -PropertyName ProductName) -cne 'AudioPilot') {
        throw 'Use the AudioPilot installer fixtures, not another MSI product.'
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $helper 'coreclr.dll'))) { throw 'Publish the updater helper self-contained first.' }
foreach ($path in @($baseline, $candidate, $helper, $root)) {
    for ($current = $path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Redirected updater input or output: $current"
        }
    }
}
if (@(Get-ChildItem -LiteralPath $helper -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
    throw 'The published helper must not contain redirected files or directories.'
}
$inputRoot = Join-Path $root 'input'
$resultsRoot = Join-Path $root 'results'
New-Item -ItemType Directory -Path $inputRoot, $resultsRoot -Force | Out-Null
Copy-Item -LiteralPath $baseline -Destination (Join-Path $inputRoot 'baseline.msi')
Copy-Item -LiteralPath $candidate -Destination (Join-Path $inputRoot 'candidate.msi')
Copy-Item -LiteralPath $helper -Destination (Join-Path $inputRoot 'helper') -Recurse
$runner = @'
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Run only inside Windows Sandbox.' }
$result = @{ status = 'running'; startedUtc = [DateTime]::UtcNow.ToString('o') }
$result | ConvertTo-Json | Set-Content C:\UpdateResults\result.json
try {
    $local = 'C:\AudioPilotUpdater'
    if (Test-Path -LiteralPath $local) { throw 'Choose a fresh Sandbox.' }
    New-Item -ItemType Directory -Path $local | Out-Null
    Copy-Item C:\UpdateInput\baseline.msi, C:\UpdateInput\candidate.msi -Destination $local
    Copy-Item -LiteralPath C:\UpdateInput\helper -Destination "$local\helper" -Recurse
    & "$local\helper\AudioPilot.Updater.exe" msi-smoke "$local\baseline.msi" "$local\candidate.msi" "$local\deployment" '__OPTION_MASKS__' *> C:\UpdateResults\msi-console.log
    if ($LASTEXITCODE -ne 0) { throw "MSI updater failed: $LASTEXITCODE" }
    $result.status = 'passed'
}
catch { $result.status = 'failed'; $result.error = $_.ToString() }
finally {
    if (Test-Path -LiteralPath C:\AudioPilotUpdater\deployment) {
        foreach ($file in Get-ChildItem C:\AudioPilotUpdater\deployment -File -Recurse | Where-Object Extension -in '.log', '.json') {
            $relative = $file.FullName.Substring('C:\AudioPilotUpdater\deployment\'.Length)
            $destination = Join-Path C:\UpdateResults\evidence $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    $result.completedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json | Set-Content C:\UpdateResults\result.json
}
'@
Set-Content -LiteralPath (Join-Path $inputRoot 'run.ps1') -Value $runner.Replace('__OPTION_MASKS__', ($OptionMasks -join ',')) -Encoding utf8
[xml]$configuration = '<Configuration><VGpu>Disable</VGpu><Networking>Disable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><PrinterRedirection>Disable</PrinterRedirection><ClipboardRedirection>Disable</ClipboardRedirection><MemoryInMB>4096</MemoryInMB><MappedFolders/><LogonCommand><Command/></LogonCommand></Configuration>'
if ($EnableNetworking) { $configuration.SelectSingleNode('/Configuration/Networking').InnerText = 'Enable' }
foreach ($mapping in @(
    @{ HostFolder = $inputRoot; SandboxFolder = 'C:\UpdateInput'; ReadOnly = 'true' },
    @{ HostFolder = $resultsRoot; SandboxFolder = 'C:\UpdateResults'; ReadOnly = 'false' }
)) {
    $folder = $configuration.CreateElement('MappedFolder')
    foreach ($name in @('HostFolder', 'SandboxFolder', 'ReadOnly')) {
        $element = $configuration.CreateElement($name)
        $element.InnerText = $mapping[$name]
        [void]$folder.AppendChild($element)
    }
    [void]$configuration.SelectSingleNode('/Configuration/MappedFolders').AppendChild($folder)
}
$configuration.SelectSingleNode('/Configuration/LogonCommand/Command').InnerText = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\UpdateInput\run.ps1'
$path = Join-Path $root 'Update-updater.wsb'
$configuration.Save($path)
Write-Host "Prepared $path"
Write-Host "Results: $resultsRoot"
