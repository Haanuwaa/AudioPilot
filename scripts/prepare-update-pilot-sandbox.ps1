[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SignTool,
    [string]$PowerShellDirectory = $PSHOME
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if(-not (Test-Path -LiteralPath (Join-Path $PowerShellDirectory 'pwsh.exe') -PathType Leaf)){throw 'PowerShell 7 is required in the Sandbox mapping.'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build=(Resolve-Path -LiteralPath $BuildDirectory).Path
$output=[IO.Path]::GetFullPath($OutputDirectory,$repo)
if(-not $output.StartsWith($repo+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Choose a fresh Sandbox evidence folder under the checkout.'}
$description=Get-Content -LiteralPath (Join-Path $build 'pilot-build.json') -Raw | ConvertFrom-Json
if($description.schema -ne 1 -or $description.publicLiveUpdates -ne $false -or $description.packages.Count -ne 2){throw 'Invalid pilot build receipt.'}
$baseline=$description.packages[0];$candidate=$description.packages[1]
foreach($path in @($baseline.zip,$baseline.msi,$candidate.zip,$candidate.msi,$description.portableInitial)) {
    $resolved=[IO.Path]::GetFullPath($path)
    if(-not $resolved.StartsWith($build+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($resolved) -cnotin @('.zip','.msi')){throw 'A pilot package path escapes the build directory or has the wrong type.'}
}
$inputRoot=Join-Path $output 'input';$results=Join-Path $output 'results'
$files=@{
    'baseline.zip'=$baseline.zip;'baseline.msi'=$baseline.msi
    'candidate.zip'=$candidate.zip;'candidate.msi'=$candidate.msi
    'portable-initial.zip'=$description.portableInitial
    'pilot-publisher.cer'=(Join-Path $build 'pilot-publisher.cer')
    'settings.json'=(Join-Path $repo 'AudioPilot.Tests/Fixtures/Settings/1.0.0.json')
    'signtool.exe'=(Resolve-Path -LiteralPath $SignTool).Path
    'run.ps1'=(Join-Path $PSScriptRoot 'tests/Invoke-UpdatePilotSandbox.ps1')
    'scripts/approve-update-package.ps1'=(Join-Path $PSScriptRoot 'approve-update-package.ps1')
    'scripts/sign-update-artifacts.ps1'=(Join-Path $PSScriptRoot 'sign-update-artifacts.ps1')
    'scripts/lib/Msi.ps1'=(Join-Path $PSScriptRoot 'lib/Msi.ps1')
    'scripts/lib/MsiPayload.ps1'=(Join-Path $PSScriptRoot 'lib/MsiPayload.ps1')
}
foreach($path in @($output,$build,$PowerShellDirectory)+@($files.Values)) {
    for($current=[IO.Path]::GetFullPath($path);$current;$current=[IO.Path]::GetDirectoryName($current)) {
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Redirected Sandbox input or output.'}
    }
}
[IO.Directory]::CreateDirectory((Join-Path $inputRoot 'scripts/lib')) | Out-Null
[IO.Directory]::CreateDirectory($results) | Out-Null
foreach($name in $files.Keys){Copy-Item -LiteralPath $files[$name] -Destination (Join-Path $inputRoot $name)}
@{schema=1;baselineVersion=$baseline.version;candidateVersion=$candidate.version;publisherSha256=$description.publisherSha256;publicLiveUpdates=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputRoot 'probe.json') -Encoding utf8NoBOM
[xml]$configuration='<Configuration><VGpu>Disable</VGpu><Networking>Enable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>6144</MemoryInMB><MappedFolders/><LogonCommand><Command/></LogonCommand></Configuration>'
foreach($mapping in @(@{HostFolder=$inputRoot;SandboxFolder='C:\PilotInput';ReadOnly='true'},@{HostFolder=(Resolve-Path -LiteralPath $PowerShellDirectory).Path;SandboxFolder='C:\PilotPowerShell';ReadOnly='true'},@{HostFolder=$results;SandboxFolder='C:\PilotResults';ReadOnly='false'})) {
    $folder=$configuration.CreateElement('MappedFolder')
    foreach($name in @('HostFolder','SandboxFolder','ReadOnly')){$element=$configuration.CreateElement($name);$element.InnerText=$mapping[$name];[void]$folder.AppendChild($element)}
    [void]$configuration.SelectSingleNode('/Configuration/MappedFolders').AppendChild($folder)
}
$configuration.SelectSingleNode('/Configuration/LogonCommand/Command').InnerText='cmd.exe /c C:\PilotPowerShell\pwsh.exe -NoProfile -ExecutionPolicy Bypass -File C:\PilotInput\run.ps1 > C:\PilotResults\run.log 2>&1'
$configuration.Save((Join-Path $output 'UpdaterPilot.wsb'))
Write-Output (Join-Path $output 'UpdaterPilot.wsb')
