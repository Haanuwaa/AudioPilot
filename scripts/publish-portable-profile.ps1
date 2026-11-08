[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-x86', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][ValidateSet('true', 'false')][string]$SelfContained,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [string]$BuildDirectory,
    [switch]$AllProfiles
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/PublisherVerification.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$mode = if ($SelfContained -eq 'true') { 'SelfContained' } else { 'FrameworkDependent' }
$profile = "$mode-$RuntimeIdentifier"
$destinationPath = [IO.Path]::GetFullPath($Destination, $repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ([IO.Path]::GetFileName($destinationPath) -ne 'AudioPilot') {
    throw 'Choose a dedicated AudioPilot folder.'
}
function Assert-DirectPath([string]$Path) {
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Publishing does not follow directory links: $current"
        }
    }
}
function Move-ProfileDirectory([string]$Source, [string]$Destination) {
    for ($attempt = 0; ; $attempt++) {
        try {
            [IO.Directory]::Move($Source, $Destination)
            return
        }
        catch {
            $code = $_.Exception.GetBaseException().HResult -band 0xffff
            if ($attempt -ge 4 -or $code -notin @(5, 32, 33) -or
                -not (Test-Path -LiteralPath $Source -PathType Container) -or (Test-Path -LiteralPath $Destination)) { throw }
            Start-Sleep -Milliseconds (100 -shl $attempt)
        }
    }
}
function Assert-Destination {
    Assert-DirectPath $destinationPath
    if (-not (Test-Path -LiteralPath $destinationPath)) { return }
    $unexpected = @(Get-ChildItem -LiteralPath $destinationPath -Force | Where-Object Name -notin @('AudioPilot.exe', 'AudioPilot.Cli.exe', 'app', 'data', 'ABOUT.txt', 'licenses'))
    if ($unexpected.Count) { throw 'The destination contains unrelated files. Choose a fresh AudioPilot folder.' }
    if (Test-Path -LiteralPath (Join-Path $destinationPath 'licenses')) {
        Assert-DirectPath (Join-Path $destinationPath 'licenses')
        $unexpectedNotices = @(Get-ChildItem -LiteralPath (Join-Path $destinationPath 'licenses') -Force | Where-Object { $_.PSIsContainer -or $_.Name -cnotin @('LICENSE','THIRD-PARTY-NOTICES.txt','UPDATER-NOTICES.txt') })
        if ($unexpectedNotices.Count) { throw 'The licenses folder contains unrelated files. Choose a fresh AudioPilot folder.' }
    }
    if (Test-Path -LiteralPath (Join-Path $destinationPath 'app')) {
        $layout = Get-Content -LiteralPath (Join-Path $destinationPath 'app/portable-layout.json') -Raw | ConvertFrom-Json
        $state = Get-Content -LiteralPath (Join-Path $destinationPath 'app/deployment.json') -Raw | ConvertFrom-Json
        if ($layout.Schema -ne 1 -or $layout.Managed -ne $true -or $state.Schema -ne 1 -or $state.PendingHealth) {
            throw 'Finish or recover the pending update before publishing.'
        }
    }
    foreach ($process in Get-Process) {
        try {
            try { $path = $process.Path } catch { continue }
            if ($path -and $path.StartsWith($destinationPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Exit this portable AudioPilot copy before publishing.'
            }
        }
        finally { $process.Dispose() }
    }
}
Assert-Destination
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $repo ('artifacts/portable-publish/' + [guid]::NewGuid().ToString('N')) }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory, $repo)
if (-not $BuildDirectory.StartsWith((Join-Path $repo 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep portable build intermediates under repository artifacts.'
}
Assert-DirectPath $BuildDirectory
if (-not (Test-Path -LiteralPath $BuildDirectory)) {
    $policyDirectory = "$BuildDirectory-policy"
    [IO.Directory]::CreateDirectory($policyDirectory) | Out-Null
    $policy = Get-Content -LiteralPath (Join-Path $repo 'packaging/updater/stable/update-policy.json') -Raw | ConvertFrom-Json
    $certificates = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.HasPrivateKey -and [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($_.RawData)) -ceq $policy.PublisherSha256
    })
    if ($certificates.Count -ne 1) { throw 'Install the existing pinned publisher signing identity in CurrentUser\My before publishing this profile.' }
    $signTool = @(Get-ChildItem 'C:/Program Files (x86)/Windows Kits/10/bin/*/x64/signtool.exe' | Sort-Object FullName -Descending | Select-Object -First 1)
    if ($signTool.Count -ne 1) { throw 'Install the Windows SDK signing tools before publishing this profile.' }
    Copy-Item -LiteralPath (Join-Path $repo 'packaging/updater/stable/update-policy.json'), (Join-Path $repo 'packaging/updater/stable/update-publisher.cer') -Destination $policyDirectory
    @{ certificateThumbprint = $certificates[0].Thumbprint } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $policyDirectory 'identity-receipt.json') -Encoding utf8NoBOM
    $runtimes = if ($AllProfiles) { @('win-x64', 'win-x86', 'win-arm64') } else { @($RuntimeIdentifier) }
    $modes = if ($AllProfiles) { @('SelfContained', 'FrameworkDependent') } else { @($mode) }
    & (Join-Path $PSScriptRoot 'build-updater-packages.ps1') -Bootstrap -Version $Version -PolicyDirectory $policyDirectory `
        -OutputDirectory $BuildDirectory -SignTool $signTool[0].FullName -Runtimes $runtimes -DeploymentModes $modes -SkipInstallers -DirectoryOnly
}
$build = Get-Content -LiteralPath (Join-Path $BuildDirectory 'update-build.json') -Raw | ConvertFrom-Json
$record = @($build.packages | Where-Object { $_.profile -ceq $profile -and $_.version -ceq $Version })
if ($record.Count -ne 1) { throw 'The signed build does not contain the selected profile and version.' }
$source = Join-Path $BuildDirectory "portable-initial-$profile/AudioPilot"
Assert-DirectPath $source
foreach ($file in Get-ChildItem -LiteralPath $source -Force -Recurse) { Assert-DirectPath $file.FullName }
$publicPolicy = Get-Content -LiteralPath (Join-Path $repo 'packaging/updater/stable/update-policy.json') -Raw | ConvertFrom-Json
$state = Get-Content -LiteralPath (Join-Path $source 'app/deployment.json') -Raw | ConvertFrom-Json
if ($state.Schema -ne 1 -or $state.PendingHealth -or $state.Current -cnotmatch '^\d+\.\d+\.\d+_[a-f0-9]{32}$') { throw 'Invalid initial portable deployment.' }
$payload = Join-Path $source "app/versions/$($state.Current)"
& (Join-Path $PSScriptRoot 'validate-publish-payload.ps1') -PublishDir $payload -RuntimeIdentifier $RuntimeIdentifier -Version $Version -DeploymentMode $mode -UpdateBootstrap -FromArchive
$signedFiles = @('AudioPilot.exe', 'AudioPilot.Cli.exe', 'AudioPilot.dll', 'AudioPilot.Cli.dll', 'AudioPilot.Updater.exe', 'AudioPilot.Updater.dll', 'AudioPilot.Updater.Core.dll', 'AudioPilot.UpdateProtocol.dll', 'AudioPilot.TufBridge.exe') | ForEach-Object { Join-Path $payload $_ }
$signedFiles += @((Join-Path $source 'AudioPilot.exe'), (Join-Path $source 'AudioPilot.Cli.exe'))
$verifierDirectory = Join-Path $BuildDirectory 'publisher-verifier'
& dotnet build (Join-Path $repo 'AudioPilot.Updater/Ui/AudioPilot.Updater.Ui.csproj') -c Release --nologo `
    -p:RestoreLockedMode=true -p:BuildIsolationKey=PortableProfileVerifier -p:DebugSymbols=false -p:DebugType=None -o $verifierDirectory
if ($LASTEXITCODE -ne 0) { throw 'Building the local publisher verifier failed.' }
Invoke-AudioPilotPublisherVerification -Verifier (Join-Path $verifierDirectory 'AudioPilot.Updater.exe') -PublisherSha256 $publicPolicy.PublisherSha256 -Files $signedFiles
$parent = [IO.Path]::GetDirectoryName($destinationPath)
[IO.Directory]::CreateDirectory($parent) | Out-Null
$staged = Join-Path $parent ('.AudioPilot-publish-' + [guid]::NewGuid().ToString('N'))
$previous = Join-Path $parent ('.AudioPilot-previous-' + [guid]::NewGuid().ToString('N'))
$replaced = $false
$dataMoved = $false
$preserveStaged = $false
try {
    Copy-Item -LiteralPath $source -Destination $staged -Recurse
    [IO.Directory]::CreateDirectory((Join-Path $staged 'licenses')) | Out-Null
    foreach ($document in @('ABOUT.txt','licenses/LICENSE','licenses/THIRD-PARTY-NOTICES.txt','licenses/UPDATER-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $payload $document) -Destination (Join-Path $staged $document)
    }
    [IO.File]::WriteAllText((Join-Path $staged 'app/.documents-generation'),$state.Current,[Text.UTF8Encoding]::new($false))
    Assert-Destination
    if (Test-Path -LiteralPath $destinationPath) {
        foreach ($file in Get-ChildItem -LiteralPath $destinationPath -Force -Recurse) { Assert-DirectPath $file.FullName }
        Move-ProfileDirectory $destinationPath $previous
        $replaced = $true
        $oldData = Join-Path $previous 'data'
        if (Test-Path -LiteralPath $oldData) {
            [IO.Directory]::Delete((Join-Path $staged 'data'))
            Move-ProfileDirectory $oldData (Join-Path $staged 'data')
            $dataMoved = $true
        }
    }
    Move-ProfileDirectory $staged $destinationPath
}
catch {
    try {
        if ($replaced) {
            if ($dataMoved) {
                Move-ProfileDirectory (Join-Path $staged 'data') (Join-Path $previous 'data')
            }
            Move-ProfileDirectory $previous $destinationPath
        }
    }
    catch {
        $preserveStaged = $true
        Write-Warning "Publish recovery is incomplete. Preserved the staged folder '$staged' and previous folder '$previous' for recovery."
    }
    throw
}
finally {
    if (-not $preserveStaged -and (Test-Path -LiteralPath $staged)) { Remove-Item -LiteralPath $staged -Recurse -Force }
}
if ($replaced) {
    try { Remove-Item -LiteralPath $previous -Recurse -Force }
    catch { Write-Warning "Published successfully; the previous owned build could not be removed: $previous" }
}
Write-Output "Published updater-enabled $profile to $destinationPath (data preserved)."
