[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallMsiPath,
    [Parameter(Mandatory = $true)]
    [string]$UpgradeFromMsiPath,
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/Msi.ps1')

$currentMsi = (Resolve-Path -LiteralPath $InstallMsiPath).Path
$baselineMsi = (Resolve-Path -LiteralPath $UpgradeFromMsiPath).Path
$currentVersion = Get-AudioPilotMsiProperty -Path $currentMsi -PropertyName ProductVersion
$baselineVersion = Get-AudioPilotMsiProperty -Path $baselineMsi -PropertyName ProductVersion
if ([version]$baselineVersion -ge [version]$currentVersion) {
    throw 'The baseline MSI version must be older than the current MSI.'
}
if ((Get-AudioPilotMsiProperty -Path $currentMsi -PropertyName UpgradeCode) -ne
    (Get-AudioPilotMsiProperty -Path $baselineMsi -PropertyName UpgradeCode)) {
    throw 'The two MSI packages must belong to the same upgrade family.'
}
if ((Get-AudioPilotMsiSummaryProperty -Path $currentMsi -PropertyId 7) -notlike 'x64;*' -or
    (Get-AudioPilotMsiSummaryProperty -Path $baselineMsi -PropertyId 7) -notlike 'x64;*') {
    throw 'This Windows Sandbox validation requires x64 MSI packages.'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot "../artifacts/msi-sandbox/$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))"
}
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) {
    throw "Choose a new output directory so previous sandbox evidence is preserved: $root"
}
$inputRoot = Join-Path $root 'input'
$resultsRoot = Join-Path $root 'results'
New-Item -ItemType Directory -Path $inputRoot, $resultsRoot, (Join-Path $inputRoot 'scripts/lib') -Force | Out-Null
Copy-Item -LiteralPath $currentMsi -Destination (Join-Path $inputRoot 'current.msi')
Copy-Item -LiteralPath $baselineMsi -Destination (Join-Path $inputRoot 'baseline.msi')
foreach ($name in @('test-msi-smoke.ps1', 'test-msi-option-matrix.ps1', 'new-msi-rollback-probe.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $inputRoot "scripts/$name")
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lib/Msi.ps1') -Destination (Join-Path $inputRoot 'scripts/lib/Msi.ps1')
@{
    currentVersion = $currentVersion
    baselineVersion = $baselineVersion
    currentSha256 = (Get-FileHash -LiteralPath $currentMsi -Algorithm SHA256).Hash
    baselineSha256 = (Get-FileHash -LiteralPath $baselineMsi -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputRoot 'manifest.json') -Encoding utf8

# The runner is copied into the read-only input mapping. Only the dedicated results
# directory is writable from the guest, and no network access is needed for these MSIs.
$runner = @'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    throw 'This runner must execute inside Windows Sandbox, never on the host.'
}
Set-Location C:\AudioPilotResults
Start-Transcript -Path C:\AudioPilotResults\validation.log
$result = @{ status = 'running'; machine = $env:COMPUTERNAME; startedUtc = [DateTime]::UtcNow.ToString('o') }
$result | ConvertTo-Json | Set-Content C:\AudioPilotResults\result.json
try {
    $manifest = Get-Content C:\AudioPilotInput\manifest.json -Raw | ConvertFrom-Json
    if ((Get-FileHash C:\AudioPilotInput\current.msi).Hash -ne $manifest.currentSha256 -or
        (Get-FileHash C:\AudioPilotInput\baseline.msi).Hash -ne $manifest.baselineSha256) {
        throw 'Sandbox MSI input hashes do not match the prepared manifest.'
    }
    & C:\AudioPilotInput\scripts\test-msi-option-matrix.ps1 -InstallMsiPath C:\AudioPilotInput\current.msi -ExpectedVersion $manifest.currentVersion -TestRoot C:\AudioPilotValidation\options -ResultsRoot C:\AudioPilotResults\msi-smoke
    & C:\AudioPilotInput\scripts\test-msi-smoke.ps1 -InstallMsiPath C:\AudioPilotInput\current.msi -UpgradeFromMsiPath C:\AudioPilotInput\baseline.msi -ExpectedVersion $manifest.currentVersion -InstallRoot 'C:\AudioPilotValidation\upgrade install' -DataRoot 'C:\AudioPilotValidation\upgrade data' -DesktopShortcut 0 -StartMenuShortcut 0 -AddCliToPath 1 -VerifyUpgradeRollback -LogName sandbox-upgrade -ResultsRoot C:\AudioPilotResults\msi-smoke
    $result.status = 'passed'
}
catch {
    $result.status = 'failed'
    $result.error = $_.ToString()
    Write-Host $_ -ForegroundColor Red
}
finally {
    $result.completedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json | Set-Content C:\AudioPilotResults\result.json
    Stop-Transcript
}
'@
Set-Content -LiteralPath (Join-Path $inputRoot 'run-validation.ps1') -Value $runner -Encoding utf8

$powershellRoot = Split-Path -Parent (Get-Command pwsh -ErrorAction Stop).Source
[xml]$configuration = '<Configuration><VGpu>Disable</VGpu><Networking>Disable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><PrinterRedirection>Disable</PrinterRedirection><ClipboardRedirection>Disable</ClipboardRedirection><MemoryInMB>4096</MemoryInMB><MappedFolders/><LogonCommand><Command/></LogonCommand></Configuration>'
foreach ($mapping in @(
    @{ hostPath = $inputRoot; guestPath = 'C:\AudioPilotInput'; readOnly = 'true' },
    @{ hostPath = $powershellRoot; guestPath = 'C:\AudioPilotPowerShell'; readOnly = 'true' },
    @{ hostPath = $resultsRoot; guestPath = 'C:\AudioPilotResults'; readOnly = 'false' }
)) {
    $folder = $configuration.CreateElement('MappedFolder')
    foreach ($pair in @(@('HostFolder', $mapping.hostPath), @('SandboxFolder', $mapping.guestPath), @('ReadOnly', $mapping.readOnly))) {
        $element = $configuration.CreateElement($pair[0])
        $element.InnerText = $pair[1]
        [void]$folder.AppendChild($element)
    }
    [void]$configuration.SelectSingleNode('/Configuration/MappedFolders').AppendChild($folder)
}
$configuration.SelectSingleNode('/Configuration/LogonCommand/Command').InnerText = 'C:\AudioPilotPowerShell\pwsh.exe -NoProfile -ExecutionPolicy Bypass -File C:\AudioPilotInput\run-validation.ps1'
$configurationPath = Join-Path $root 'AudioPilot-validation.wsb'
$configuration.Save($configurationPath)
Write-Host "Prepared Windows Sandbox configuration: $configurationPath"
Write-Host "Validation results will appear in: $resultsRoot"
