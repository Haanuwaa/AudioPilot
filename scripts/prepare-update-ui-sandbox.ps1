[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineZip,
    [string]$CandidateZip,
    [Parameter(Mandatory)][string]$BaselineMsi,
    [string]$CandidateMsi,
    [Parameter(Mandatory)][string]$HelperDirectory,
    [Parameter(Mandatory)][string]$UiDirectory,
    [Parameter(Mandatory)][string]$OutputRoot,
    [switch]$HttpsFeed,
    [string]$StableFeedDirectory,
    [switch]$StagingFeed,
    [string]$StagingBootstrap,
    [string]$PublisherCertificate,
    [string]$SettingsFixture = (Join-Path $PSScriptRoot '../AudioPilot.Tests/Fixtures/Settings/1.0.0.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($StagingFeed -and $HttpsFeed) { throw 'Choose public staging or the loopback HTTPS fixture.' }
if ($StableFeedDirectory -and ($StagingFeed -or $HttpsFeed)) { throw 'Choose one feed validation mode.' }
if (-not $StagingFeed -and (-not $CandidateZip -or -not $CandidateMsi)) { throw 'Local modes require both candidate packages.' }
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) { throw 'Choose a new folder to preserve prior test evidence.' }
$files = @{
    'baseline.zip' = (Resolve-Path -LiteralPath $BaselineZip).Path
    'baseline.msi' = (Resolve-Path -LiteralPath $BaselineMsi).Path
    'settings.json' = (Resolve-Path -LiteralPath $SettingsFixture).Path
}
if ($StagingFeed) {
    $files['bootstrap.root.json'] = (Resolve-Path -LiteralPath $StagingBootstrap).Path
    $files['publisher.cer'] = (Resolve-Path -LiteralPath $PublisherCertificate).Path
} else {
    $files['candidate.zip'] = (Resolve-Path -LiteralPath $CandidateZip).Path
    $files['candidate.msi'] = (Resolve-Path -LiteralPath $CandidateMsi).Path
}
$directories = @{
    helper = (Resolve-Path -LiteralPath $HelperDirectory).Path
    ui = (Resolve-Path -LiteralPath $UiDirectory).Path
}
if ($StableFeedDirectory) { $directories['feed'] = (Resolve-Path -LiteralPath $StableFeedDirectory).Path }
foreach ($path in $files.Values) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Expected an input file: $path" }
}
foreach ($path in @($root) + @($files.Values) + @($directories.Values)) {
    for ($parent = $path; $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
        if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Redirected path: $parent" }
    }
}
foreach ($directory in $directories.Values) {
    if ($StableFeedDirectory -and $directory -ceq $directories['feed']) { continue }
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'coreclr.dll')) -or -not (Test-Path -LiteralPath (Join-Path $directory 'AudioPilot.TufBridge.exe'))) { throw 'Publish self-contained inputs with the local TUF bridge first.' }
    if (@(Get-ChildItem -LiteralPath $directory -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Redirected payload entries are not supported.' }
}
$inputRoot = Join-Path $root 'input'
$resultsRoot = Join-Path $root 'results'
New-Item -ItemType Directory -Path $inputRoot, $resultsRoot | Out-Null
foreach ($name in $files.Keys) { Copy-Item -LiteralPath $files[$name] -Destination (Join-Path $inputRoot $name) }
foreach ($name in $directories.Keys) { Copy-Item -LiteralPath $directories[$name] -Destination (Join-Path $inputRoot $name) -Recurse }
if ($HttpsFeed) { Set-Content -LiteralPath (Join-Path $inputRoot 'https-test.txt') -Value 'loopback-only' -Encoding ascii }
if ($StableFeedDirectory) { Set-Content -LiteralPath (Join-Path $inputRoot 'stable-test.txt') -Value 'stable-loopback-acceptance-only' -Encoding ascii }
if ($StagingFeed) { Set-Content -LiteralPath (Join-Path $inputRoot 'staging-test.txt') -Value 'explicit-public-prerelease' -Encoding ascii }
$runner = @'
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Run only in Windows Sandbox.' }
$result = @{status='running'; phase='copy'; startedUtc=[DateTime]::UtcNow.ToString('o')}
$local = 'C:\AudioPilotUiTest'
$output = 'C:\UpdaterResults'
$cleaned = @()
function Save-Result { $result | ConvertTo-Json | Set-Content "$output\result.json" }
function Invoke-Tool([string]$Name, [string]$Exe, [string[]]$Arguments) {
    $quoted = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    $process = Start-Process -FilePath $Exe -ArgumentList $quoted -PassThru -WindowStyle Hidden -RedirectStandardOutput "$output\$Name.log" -RedirectStandardError "$output\$Name-error.log"
    $null = $process.Handle
    $deadline = if (Test-Path -LiteralPath "$local\staging-test.txt") { 900000 } else { 600000 }
    if (-not $process.WaitForExit($deadline)) { $result.operationStillActive=$true; throw "$Name did not finish within its test deadline. The app was not terminated." }
    if ($process.ExitCode -ne 0) {
        $errorText = (Get-Content -LiteralPath "$output\$Name-error.log" -Tail 40) -join [Environment]::NewLine
        throw "$Name failed ($($process.ExitCode)): $errorText"
    }
}
Save-Result
try {
    if (Test-Path -LiteralPath $local) { throw 'Use a fresh Sandbox.' }
    New-Item -ItemType Directory -Path $local | Out-Null
    Copy-Item -Path C:\UpdaterInput\* -Destination $local -Recurse
    $https = Test-Path -LiteralPath "$local\https-test.txt"
    $staging = Test-Path -LiteralPath "$local\staging-test.txt"
    $stable = Test-Path -LiteralPath "$local\stable-test.txt"
    if ($staging) {
        if ((Get-FileHash -LiteralPath "$local\publisher.cer" -Algorithm SHA256).Hash -cne 'B42574DFCDEBBFFFE1BD56426979ED0C8676DB5B860BEC3120B3E9D268BB6902') { throw 'The temporary publisher differs from the approved staging package signer.' }
        & certutil.exe -f -addstore Root "$local\publisher.cer" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Sandbox publisher root import failed.' }
        & certutil.exe -user -f -addstore TrustedPublisher "$local\publisher.cer" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Sandbox publisher trust import failed.' }
        $result.publicLiveUpdates=$false; $result.source='public HTTPS staging, no local candidate files'; Save-Result
    }
    foreach ($kind in @('portable', 'msi')) {
        $result.phase = "$kind-prepare"; Save-Result
        $extension = if ($kind -eq 'portable') { 'zip' } else { 'msi' }
        $workspaceRoot = if ($kind -eq 'portable' -and ($staging -or $stable -or $https)) { "$local\$kind\app" } else { "$local\$kind" }
        if ($staging) {
            Invoke-Tool "$kind-prepare" "$local\helper\AudioPilot.Updater.exe" @('staging-prepare', "$local\baseline.$extension", "AudioPilot-1.0.1-win-x64.$extension", "$local\settings.json", "$local\bootstrap.root.json", $workspaceRoot)
        } elseif ($stable) {
            Invoke-Tool "$kind-prepare" "$local\helper\AudioPilot.Updater.exe" @('stable-prepare', "$local\baseline.$extension", "$local\candidate.$extension", "$local\settings.json", "$local\feed", $workspaceRoot)
        } elseif ($https) {
            Invoke-Tool "$kind-prepare" "$local\helper\AudioPilot.Updater.exe" @('feed-prepare', "$local\baseline.$extension", "$local\candidate.$extension", "$local\settings.json", $workspaceRoot)
        } else {
            Invoke-Tool "$kind-prepare" "$local\helper\AudioPilot.Updater.exe" @('job-ui-prepare', $kind, "$local\baseline.$extension", "$local\candidate.$extension", "$local\settings.json", $workspaceRoot, '7')
        }
        $result.phase = "$kind-ui"; Save-Result
        if ($staging) {
            Invoke-Tool "$kind-ui" "$local\ui\AudioPilot.Updater.exe" @('--staging-smoke', $workspaceRoot, "$output\$kind")
        } else {
            $uiMode = if ($stable) { '--stable-smoke' } elseif ($https) { '--feed-smoke' } else { '--smoke' }
            Invoke-Tool "$kind-ui" "$local\ui\AudioPilot.Updater.exe" @($uiMode, $workspaceRoot, "$local\candidate.$extension", "$output\$kind")
        }
        if ($https -or $staging -or $stable) {
            $result.phase="$kind-cleanup"; Save-Result
            Invoke-Tool "$kind-cleanup" "$local\helper\AudioPilot.Updater.exe" @('feed-cleanup', $workspaceRoot)
            $cleaned += $kind
        }
    }
    $result.status='passed'
}
catch { $result.status='failed'; $result.error=$_.ToString() }
finally {
    if (-not $result.ContainsKey('operationStillActive') -and ((Test-Path -LiteralPath "$local\https-test.txt") -or (Test-Path -LiteralPath "$local\staging-test.txt") -or (Test-Path -LiteralPath "$local\stable-test.txt"))) {
        foreach ($kind in @('portable','msi')) {
            if ($kind -notin $cleaned -and (Test-Path -LiteralPath $(if ($kind -eq 'portable') { "$local\$kind\app\installation.json" } else { "$local\$kind\installation.json" }))) {
                try { Invoke-Tool "$kind-failure-cleanup" "$local\helper\AudioPilot.Updater.exe" @('feed-cleanup', $(if ($kind -eq 'portable') { "$local\$kind\app" } else { "$local\$kind" })); $cleaned += $kind }
                catch { $result.status='failed'; $result.cleanupError=$_.ToString() }
            }
        }
    }
    if (-not $result.ContainsKey('operationStillActive') -and (Test-Path -LiteralPath 'HKCU:\Software\Haanuwaa\AudioPilot')) {
        $result.phase='uninstall'; Save-Result
        try {
            $cleanupMode=if ((Test-Path -LiteralPath "$local\https-test.txt") -or (Test-Path -LiteralPath "$local\staging-test.txt") -or (Test-Path -LiteralPath "$local\stable-test.txt")) { 'feed-cleanup' } else { 'job-ui-cleanup' }
            Invoke-Tool 'uninstall' "$local\helper\AudioPilot.Updater.exe" @($cleanupMode, "$local\msi")
        }
        catch { $result.status='failed'; $result.cleanupError=$_.ToString() }
    }
    foreach ($kind in @('portable','msi')) {
        $source = if ($kind -eq 'portable' -and ((Test-Path -LiteralPath "$local\https-test.txt") -or (Test-Path -LiteralPath "$local\staging-test.txt") -or (Test-Path -LiteralPath "$local\stable-test.txt"))) { "$local\$kind\app" } else { Join-Path $local $kind }
        if (Test-Path -LiteralPath $source) {
            foreach ($file in Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in @('.json','.log','.anchor') }) {
                New-Item -ItemType Directory -Path "$output\$kind" -Force | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination "$output\$kind"
            }
        }
    }
    $result.completedUtc=[DateTime]::UtcNow.ToString('o'); Save-Result
}
if ($result.status -ne 'passed') { exit 1 }
'@
Set-Content -LiteralPath (Join-Path $inputRoot 'run.ps1') -Value $runner -Encoding utf8
[xml]$configuration = '<Configuration><VGpu>Disable</VGpu><Networking>Disable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB><MappedFolders/><LogonCommand><Command/></LogonCommand></Configuration>'
if ($StagingFeed) { $configuration.SelectSingleNode('/Configuration/Networking').InnerText = 'Enable' }
foreach ($mapping in @(@{HostFolder=$inputRoot; SandboxFolder='C:\UpdaterInput'; ReadOnly='true'}, @{HostFolder=$resultsRoot; SandboxFolder='C:\UpdaterResults'; ReadOnly='false'})) {
    $folder = $configuration.CreateElement('MappedFolder')
    foreach ($name in @('HostFolder','SandboxFolder','ReadOnly')) {
        $element = $configuration.CreateElement($name); $element.InnerText = $mapping[$name]; [void]$folder.AppendChild($element)
    }
    [void]$configuration.SelectSingleNode('/Configuration/MappedFolders').AppendChild($folder)
}
$configuration.SelectSingleNode('/Configuration/LogonCommand/Command').InnerText = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\UpdaterInput\run.ps1'
$configuration.Save((Join-Path $root 'UpdaterUi.wsb'))
Write-Host "Prepared $root\UpdaterUi.wsb"
