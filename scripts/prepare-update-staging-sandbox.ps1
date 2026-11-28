[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$HelperDirectory,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$SignTool,
    [Parameter(Mandatory)][string]$OutputRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'prepare-update-signing-sandbox.ps1') -BaselineDirectory $BaselineDirectory -CandidateDirectory $CandidateDirectory -HelperDirectory $HelperDirectory -SignTool $SignTool -OutputRoot $OutputRoot
$root = [IO.Path]::GetFullPath($OutputRoot)
$inputRoot = Join-Path $root 'input'
$scripts = Join-Path $inputRoot 'scripts'
[IO.Directory]::CreateDirectory($scripts) | Out-Null
foreach ($file in @('sign-update-artifacts.ps1', 'sign-update-payload.ps1', 'approve-update-package.ps1', 'publish-update-staging.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $scripts
}
[IO.Directory]::CreateDirectory((Join-Path $scripts 'lib')) | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lib/Msi.ps1'), (Join-Path $PSScriptRoot 'lib/MsiPayload.ps1') -Destination (Join-Path $scripts 'lib')
Copy-Item -LiteralPath $Publisher -Destination (Join-Path $inputRoot 'publisher.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tests/Invoke-UpdateStagingSandbox.ps1') -Destination (Join-Path $inputRoot 'stage.ps1')
$configurationPath = Join-Path $root 'Signing.wsb'
[xml]$configuration = Get-Content -LiteralPath $configurationPath -Raw
$pwshDirectory = [IO.Path]::GetDirectoryName((Get-Process -Id $PID).Path)
foreach ($mapping in @(
    @{ HostFolder = $pwshDirectory; SandboxFolder = 'C:\PowerShell'; ReadOnly = 'true' },
    @{ HostFolder = (Resolve-Path -LiteralPath $CandidateDirectory).Path; SandboxFolder = 'C:\CandidatePayload'; ReadOnly = 'true' }
)) {
    $folder = $configuration.CreateElement('MappedFolder')
    foreach ($name in @('HostFolder', 'SandboxFolder', 'ReadOnly')) { $element = $configuration.CreateElement($name); $element.InnerText = $mapping[$name]; [void]$folder.AppendChild($element) }
    [void]$configuration.SelectSingleNode('/Configuration/MappedFolders').AppendChild($folder)
}
$configuration.SelectSingleNode('/Configuration/LogonCommand/Command').InnerText = 'C:\PowerShell\pwsh.exe -NoProfile -ExecutionPolicy Bypass -File C:\SigningInput\stage.ps1'
$configuration.Save($configurationPath)
Write-Host 'The proof exports signed payload files, then waits for deployment/input/candidate.msi and candidate.ready. Build that MSI from those exact signed payload bytes. Only public feed files and reports are exported.'
