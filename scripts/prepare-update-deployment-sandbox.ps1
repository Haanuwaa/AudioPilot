[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$BaselineMsi,
    [Parameter(Mandatory)][string]$CandidateMsi,
    [Parameter(Mandatory)][string]$HelperDirectory,
    [Parameter(Mandatory)][string]$OutputRoot,
    [ValidateRange(0, 7)][int[]]$OptionMasks = @(0, 7),
    [switch]$SignInExistingSandbox,
    [switch]$UseJobController,
    [string]$SettingsFixture = (Join-Path $PSScriptRoot '../AudioPilot.Tests/Fixtures/Settings/1.0.0.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$baseline = (Resolve-Path -LiteralPath $BaselineDirectory).Path
$candidate = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$helper = (Resolve-Path -LiteralPath $HelperDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $helper 'AudioPilot.TufBridge.exe') -PathType Leaf)) { throw 'Build and copy the maintained TUF bridge beside the updater helper first.' }
$baselinePackage = (Resolve-Path -LiteralPath $BaselineMsi).Path
$candidatePackage = (Resolve-Path -LiteralPath $CandidateMsi).Path
$fixture = (Resolve-Path -LiteralPath $SettingsFixture).Path
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) { throw 'Choose a new output folder to preserve evidence.' }
if ($OptionMasks.Count -eq 0 -or @($OptionMasks | Select-Object -Unique).Count -ne $OptionMasks.Count) { throw 'Choose unique installer option masks.' }
foreach ($path in @($baseline, $candidate, $helper, $baselinePackage, $candidatePackage, $fixture, $root)) {
    for ($current = $path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Redirected path: $current" }
    }
}
foreach ($directory in @($baseline, $candidate, $helper)) {
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'coreclr.dll'))) { throw 'Publish all inputs self-contained first.' }
    if (@(Get-ChildItem -LiteralPath $directory -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Redirected payload entries are not supported.' }
}
foreach ($item in @(@{Directory=$baseline; Version='1.0.0'}, @{Directory=$candidate; Version='1.0.1'})) {
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $item.Directory 'AudioPilot.dll')).ProductVersion.Split('+')[0] -ne $item.Version) { throw 'Unexpected fixture version.' }
}
$inputRoot = Join-Path $root 'input'
$resultsRoot = Join-Path $root 'results'
New-Item -ItemType Directory -Path $inputRoot, $resultsRoot -Force | Out-Null
foreach ($item in @(@{Directory=$baseline; Name='baseline.zip'}, @{Directory=$candidate; Name='candidate.zip'})) {
    $archive = [IO.Compression.ZipFile]::Open((Join-Path $inputRoot $item.Name), [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $item.Directory -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($item.Directory, $file.FullName).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Fastest)
        }
    }
    finally { $archive.Dispose() }
}
Copy-Item -LiteralPath $baselinePackage -Destination (Join-Path $inputRoot 'baseline.msi')
Copy-Item -LiteralPath $candidatePackage -Destination (Join-Path $inputRoot 'candidate.msi')
Copy-Item -LiteralPath $helper -Destination (Join-Path $inputRoot 'helper') -Recurse
Copy-Item -LiteralPath $fixture -Destination (Join-Path $inputRoot 'settings-fixture.json')
Set-Content -LiteralPath (Join-Path $inputRoot 'masks.txt') -Value ($OptionMasks -join ',') -Encoding ascii
if ($UseJobController) { Set-Content -LiteralPath (Join-Path $inputRoot 'job-controller.txt') -Value 'local-packages-only' -Encoding ascii }
if ($SignInExistingSandbox) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sign-update-artifacts.ps1') -Destination $inputRoot
    Set-Content -LiteralPath (Join-Path $inputRoot 'sandbox-signing.txt') -Value 'temporary-test-identity-only' -Encoding ascii
}
$runner = @'
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Run this only inside Windows Sandbox.' }
$result = @{status='running'; phase='copy'; startedUtc=[DateTime]::UtcNow.ToString('o')}
$result | ConvertTo-Json | Set-Content C:\DeploymentResults\result.json
try {
    $local = 'C:\AudioPilotDeployment'
    if (Test-Path -LiteralPath $local) { throw 'Choose a fresh Sandbox.' }
    New-Item -ItemType Directory -Path $local | Out-Null
    Copy-Item -Path C:\DeploymentInput\* -Destination $local -Recurse
    if (Test-Path -LiteralPath "$local\sandbox-signing.txt") {
        $result.phase='authenticode'
        $result | ConvertTo-Json | Set-Content C:\DeploymentResults\result.json
        $sign=@{ CertificateThumbprint=(Get-Content C:\AudioPilotSigning\thumbprint.txt).Trim(); SignTool='C:\AudioPilotSigning\tools\signtool.exe'; TestIdentity=$true }
        $signatures=@(& "$local\sign-update-artifacts.ps1" -Files @("$local\baseline.msi", "$local\candidate.msi") @sign)
        $firstParty=@('AudioPilot.exe','AudioPilot.dll','AudioPilot.Cli.exe','AudioPilot.Cli.dll','AudioPilot.UpdateProtocol.dll')
        foreach ($name in @('baseline','candidate')) {
            $check=Join-Path $local "verify-$name"
            Expand-Archive -LiteralPath "$local\$name.zip" -DestinationPath $check
            $files=@($firstParty | ForEach-Object { Join-Path $check $_ })
            $signatures += & "$local\sign-update-artifacts.ps1" -Files $files -VerifyOnly @sign
        }
        $helperFiles=@('AudioPilot.Updater.exe','AudioPilot.Updater.dll','AudioPilot.UpdateProtocol.dll','AudioPilot.TufBridge.exe' | ForEach-Object { Join-Path "$local\helper" $_ })
        $signatures += & "$local\sign-update-artifacts.ps1" -Files $helperFiles -VerifyOnly @sign
        $signatures | ConvertTo-Json -Depth 4 | Set-Content C:\DeploymentResults\signatures.json
    }
    function Invoke-Probe([string]$Name, [string[]]$Arguments) {
        $quoted = @($Arguments | ForEach-Object { '"' + $_ + '"' })
        $process = Start-Process -FilePath "$local\helper\AudioPilot.Updater.exe" -ArgumentList $quoted -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput "C:\DeploymentResults\$Name.log" -RedirectStandardError "C:\DeploymentResults\$Name-error.log"
        if ($process.ExitCode -ne 0) { throw "$Name deployment failed ($($process.ExitCode)): $([IO.File]::ReadAllText("C:\DeploymentResults\$Name-error.log"))" }
    }
    $result.phase='portable'
    $result | ConvertTo-Json | Set-Content C:\DeploymentResults\result.json
    $jobs=Test-Path -LiteralPath "$local\job-controller.txt"
    $portableCommand=if ($jobs) { 'job-portable' } else { 'deployment-portable' }
    Invoke-Probe 'portable' @($portableCommand, "$local\baseline.zip", "$local\candidate.zip", "$local\settings-fixture.json", "$local\portable")
    $result.phase='msi'
    $result | ConvertTo-Json | Set-Content C:\DeploymentResults\result.json
    if ($jobs) {
        foreach ($mask in (Get-Content "$local\masks.txt").Trim().Split(',')) {
            Invoke-Probe "msi-$mask" @('job-msi', "$local\baseline.msi", "$local\candidate.msi", "$local\settings-fixture.json", "$local\msi\option-$mask", $mask)
        }
    } else {
        Invoke-Probe 'msi' @('deployment-msi', "$local\baseline.msi", "$local\candidate.msi", "$local\settings-fixture.json", "$local\msi", (Get-Content "$local\masks.txt").Trim())
        $result.phase='msi-startup-failure'
        $result | ConvertTo-Json | Set-Content C:\DeploymentResults\result.json
        Invoke-Probe 'msi-startup-failure' @('deployment-msi-failed-start', "$local\baseline.msi", "$local\candidate.msi", "$local\settings-fixture.json", "$local\msi-startup-failure")
    }
    $result.status='passed'
}
catch { $result.status='failed'; $result.error=$_.ToString() }
finally {
    foreach ($tree in @('portable','msi','msi-startup-failure')) {
        $source = Join-Path C:\AudioPilotDeployment $tree
        if (Test-Path $source) {
            $destination = Join-Path C:\DeploymentResults $tree
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object { $_.FullName -notmatch '\\versions\\' -and $_.Extension -in @('.json','.log','.journal','.anchor') }) {
                $relative = $file.FullName.Substring($source.Length + 1)
                $target = Join-Path $destination $relative
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $target
            }
        }
    }
    $result.completedUtc=[DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json | Set-Content C:\DeploymentResults\result.json
}
if ($result.status -ne 'passed') { exit 1 }
'@
Set-Content -LiteralPath (Join-Path $inputRoot 'run.ps1') -Value $runner -Encoding utf8
[xml]$configuration = '<Configuration><VGpu>Disable</VGpu><Networking>Disable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB><MappedFolders/><LogonCommand><Command/></LogonCommand></Configuration>'
foreach ($mapping in @(
    @{HostFolder=$inputRoot; SandboxFolder='C:\DeploymentInput'; ReadOnly='true'},
    @{HostFolder=$resultsRoot; SandboxFolder='C:\DeploymentResults'; ReadOnly='false'}
)) {
    $folder = $configuration.CreateElement('MappedFolder')
    foreach ($name in @('HostFolder','SandboxFolder','ReadOnly')) {
        $element = $configuration.CreateElement($name)
        $element.InnerText = $mapping[$name]
        [void]$folder.AppendChild($element)
    }
    [void]$configuration.SelectSingleNode('/Configuration/MappedFolders').AppendChild($folder)
}
$configuration.SelectSingleNode('/Configuration/LogonCommand/Command').InnerText = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\DeploymentInput\run.ps1'
$configuration.Save((Join-Path $root 'Deployment.wsb'))
Write-Host "Prepared $root\Deployment.wsb"
