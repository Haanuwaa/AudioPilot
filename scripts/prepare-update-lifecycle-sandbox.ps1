[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$HelperDirectory,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$SettingsFixture = (Join-Path $PSScriptRoot '../AudioPilot.Tests/Fixtures/Settings/1.0.0.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
$helper = (Resolve-Path -LiteralPath $HelperDirectory).Path
$fixture = (Resolve-Path -LiteralPath $SettingsFixture).Path
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) { throw 'Choose a new output folder to preserve existing evidence.' }
foreach ($directory in @($app, $helper)) {
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'coreclr.dll'))) { throw 'Publish both inputs self-contained first.' }
    if (@(Get-ChildItem -LiteralPath $directory -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw 'Redirected input files are not allowed.' }
}
foreach ($path in @($app, $helper, $fixture, $root)) {
    for ($current = $path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Redirected path: $current" }
    }
}
$inputRoot = Join-Path $root 'input'
$resultsRoot = Join-Path $root 'results'
New-Item -ItemType Directory -Path $inputRoot, $resultsRoot -Force | Out-Null
Copy-Item -LiteralPath $app -Destination (Join-Path $inputRoot 'app') -Recurse
Copy-Item -LiteralPath $helper -Destination (Join-Path $inputRoot 'helper') -Recurse
Copy-Item -LiteralPath $fixture -Destination (Join-Path $inputRoot 'settings-fixture.json')
$runner = @'
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Run this only inside Windows Sandbox.' }
$result = @{ status='running'; startedUtc=[DateTime]::UtcNow.ToString('o') }
$result | ConvertTo-Json | Set-Content C:\LifecycleResults\result.json
try {
    $local = 'C:\AudioPilotLifecycle'
    if (Test-Path -LiteralPath $local) { throw 'Choose a fresh Sandbox.' }
    New-Item -ItemType Directory -Path $local | Out-Null
    Copy-Item -LiteralPath C:\LifecycleInput\app, C:\LifecycleInput\helper -Destination $local -Recurse
    Copy-Item -LiteralPath C:\LifecycleInput\settings-fixture.json -Destination $local
    & "$local\helper\AudioPilot.Updater.exe" real-app-smoke "$local\app\AudioPilot.exe" "$local\settings-fixture.json" "$local\evidence" *> C:\LifecycleResults\console.log
    if ($LASTEXITCODE -ne 0) { throw "Real-app lifecycle smoke failed: $LASTEXITCODE" }
    $result.status = 'passed'
}
catch { $result.status='failed'; $result.error=$_.ToString() }
finally {
    if (Test-Path C:\AudioPilotLifecycle\evidence) { Copy-Item C:\AudioPilotLifecycle\evidence C:\LifecycleResults\evidence -Recurse }
    $result.completedUtc=[DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json | Set-Content C:\LifecycleResults\result.json
}
'@
Set-Content -LiteralPath (Join-Path $inputRoot 'run.ps1') -Value $runner -Encoding utf8
[xml]$configuration = '<Configuration><VGpu>Disable</VGpu><Networking>Disable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB><MappedFolders/><LogonCommand><Command/></LogonCommand></Configuration>'
foreach ($mapping in @(
    @{ HostFolder=$inputRoot; SandboxFolder='C:\LifecycleInput'; ReadOnly='true' },
    @{ HostFolder=$resultsRoot; SandboxFolder='C:\LifecycleResults'; ReadOnly='false' }
)) {
    $folder = $configuration.CreateElement('MappedFolder')
    foreach ($name in @('HostFolder','SandboxFolder','ReadOnly')) {
        $element = $configuration.CreateElement($name)
        $element.InnerText = $mapping[$name]
        [void]$folder.AppendChild($element)
    }
    [void]$configuration.SelectSingleNode('/Configuration/MappedFolders').AppendChild($folder)
}
$configuration.SelectSingleNode('/Configuration/LogonCommand/Command').InnerText = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\LifecycleInput\run.ps1'
$configuration.Save((Join-Path $root 'Lifecycle.wsb'))
Write-Host "Prepared $root\Lifecycle.wsb"
