[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallMsiPath,

    [string]$UpgradeFromMsiPath,

    [string]$ProductName = "AudioPilot",

    [string]$ManufacturerName = "Haanuwaa",

    [string]$ExpectedVersion,

    [string]$InstallRoot,

    [string]$DataRoot,

    [string]$ResultsRoot,

    [ValidateSet("0", "1")]
    [string]$DesktopShortcut = "1",

    [ValidateSet("0", "1")]
    [string]$StartMenuShortcut = "1",

    [ValidateSet("0", "1")]
    [string]$AddCliToPath = "0",

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$LogName = "default",

    [switch]$CleanUninstall,

    [switch]$VerifyUpgradeRollback
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:msiSmokeMutex = $null
$script:msiSmokeMutexAcquired = $false

function Release-MsiSmokeMutex {
    if ($script:msiSmokeMutexAcquired -and $null -ne $script:msiSmokeMutex) {
        try {
            $script:msiSmokeMutex.ReleaseMutex()
        }
        catch {
        }
    }

    if ($null -ne $script:msiSmokeMutex) {
        $script:msiSmokeMutex.Dispose()
    }

    $script:msiSmokeMutex = $null
    $script:msiSmokeMutexAcquired = $false
}

trap {
    Release-MsiSmokeMutex
    throw $_
}

function Acquire-MsiSmokeMutex {
    $script:msiSmokeMutex = [Threading.Mutex]::new($false, "Local\AudioPilot.MsiSmoke")
    $script:msiSmokeMutexAcquired = $script:msiSmokeMutex.WaitOne([TimeSpan]::FromMinutes(10))
    if (-not $script:msiSmokeMutexAcquired) {
        throw "Timed out waiting for another AudioPilot MSI smoke test to finish."
    }
}

function Invoke-MsiInstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MsiPath,

        [Parameter(Mandatory = $true)]
        [string]$LogPath,

        [hashtable]$Properties = @{}
    )

    $arguments = @(
        "/i", $MsiPath,
        "/qn",
        "/norestart",
        "/l*v", $LogPath
    )

    foreach ($key in ($Properties.Keys | Sort-Object)) {
        $arguments += "$key=$($Properties[$key])"
    }

    $process = Start-Process -FilePath "msiexec.exe" -ArgumentList (ConvertTo-ProcessArgumentLine $arguments) -Wait -PassThru -NoNewWindow

    if ($process.ExitCode -ne 0) {
        throw "MSI install failed for '$MsiPath' with exit code $($process.ExitCode). Log: $LogPath"
    }
}

function Invoke-MsiInstallExpectFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MsiPath,

        [Parameter(Mandatory = $true)]
        [string]$LogPath,

        [Nullable[int]]$ExpectedExitCode
    )

    $arguments = @("/i", $MsiPath, "/qn", "/norestart", "/l*v", $LogPath)
    $process = Start-Process -FilePath "msiexec.exe" -ArgumentList (ConvertTo-ProcessArgumentLine $arguments) -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -eq 0) {
        throw "Expected MSI install to be rejected, but it succeeded: '$MsiPath'. Log: $LogPath"
    }
    if ($null -ne $ExpectedExitCode -and $process.ExitCode -ne $ExpectedExitCode) {
        throw "Expected MSI exit code $ExpectedExitCode, got $($process.ExitCode). Log: $LogPath"
    }
}

function Invoke-MsiUninstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProductCode,

        [Parameter(Mandatory = $true)]
        [string]$LogPath,

        [hashtable]$Properties = @{}
    )

    $arguments = @(
        "/x", $ProductCode,
        "/qn",
        "/norestart",
        "/l*v", $LogPath
    )

    foreach ($key in ($Properties.Keys | Sort-Object)) {
        $arguments += "$key=$($Properties[$key])"
    }

    $process = Start-Process -FilePath "msiexec.exe" -ArgumentList (ConvertTo-ProcessArgumentLine $arguments) -Wait -PassThru -NoNewWindow

    if ($process.ExitCode -ne 0) {
        throw "MSI uninstall failed for product code '$ProductCode' with exit code $($process.ExitCode). Log: $LogPath"
    }
}

function ConvertTo-ProcessArgumentLine {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    return ($Arguments | ForEach-Object {
        if ($_ -match '^(?<name>[A-Z][A-Z0-9_]*)=(?<value>.*)$') {
            $propertyName = $Matches.name
            $propertyValue = $Matches.value
            if ($propertyValue -match '[\s"]') {
                return $propertyName + '="' + $propertyValue.Replace('"', '""') + '"'
            }
        }

        if ($_ -notmatch '[\s"]') {
            return $_
        }

        '"' + $_.Replace('"', '""') + '"'
    }) -join ' '
}

function Test-UserPathContains {
    param([Parameter(Mandatory = $true)][string]$ExpectedPath)

    $pathValue = [string](Get-ItemPropertyValue -Path "HKCU:\Environment" -Name "Path" -ErrorAction SilentlyContinue)
    $normalizedExpected = $ExpectedPath.TrimEnd('\')
    return @($pathValue -split ';' | ForEach-Object { $_.Trim().TrimEnd('\') }) -contains $normalizedExpected
}

function Get-UninstallEntries {
    param([string]$DisplayName)

    $roots = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    $entries = foreach ($root in $roots) {
        Get-ItemProperty -Path $root -ErrorAction SilentlyContinue |
            Where-Object {
                $displayNameProperty = $_.PSObject.Properties["DisplayName"]
                $displayNameProperty -and $displayNameProperty.Value -eq $DisplayName
            }
    }

    return @($entries)
}

function Assert-PathExists {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Expected path was not created: $Path"
    }
}

function Assert-PathMissing {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        throw "Expected path to be removed: $Path"
    }
}

function Assert-RegistryValueMissing {
    param(
        [string]$Path,
        [string]$Name
    )

    $deadline = [Environment]::TickCount64 + 5000
    do {
        if (-not (Test-Path -LiteralPath $Path)) {
            return
        }

        $item = Get-ItemProperty -Path $Path -ErrorAction SilentlyContinue
        if ($null -eq $item -or -not $item.PSObject.Properties[$Name]) {
            return
        }

        Start-Sleep -Milliseconds 50
    } while ([Environment]::TickCount64 -lt $deadline)

    throw "Expected registry value to be removed: $Path :: $Name"
}

function Test-RegistryValueExists {
    param(
        [string]$Path,
        [string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    $item = Get-ItemProperty -Path $Path -ErrorAction SilentlyContinue
    return $null -ne $item -and $null -ne $item.PSObject.Properties[$Name]
}

function Assert-UninstallEntryDword {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Entry,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [int]$ExpectedValue
    )

    $property = $Entry.PSObject.Properties[$Name]
    if (-not $property) {
        throw "Expected uninstall entry to include '$Name'."
    }

    if ([int]$property.Value -ne $ExpectedValue) {
        throw "Expected uninstall entry '$Name' to be $ExpectedValue, found '$($property.Value)'."
    }
}

function Get-VersionFromInstallerName {
    param([string]$Path)

    $fileName = [IO.Path]::GetFileNameWithoutExtension($Path)
    if ($fileName -match '^.+-(?<version>\d+\.\d+\.\d+)-(?<arch>x64|arm64)$') {
        return $Matches.version
    }

    return $null
}

$resolvedInstallMsiPath = (Resolve-Path -LiteralPath $InstallMsiPath).Path
$resolvedUpgradeMsiPath = if ([string]::IsNullOrWhiteSpace($UpgradeFromMsiPath)) {
    $null
}
else {
    (Resolve-Path -LiteralPath $UpgradeFromMsiPath).Path
}

if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $ExpectedVersion = Get-VersionFromInstallerName -Path $resolvedInstallMsiPath
}

$logBase = if ([string]::IsNullOrWhiteSpace($ResultsRoot)) { Join-Path $PSScriptRoot '..\artifacts\msi-smoke' } else { $ResultsRoot }
$logRoot = [IO.Path]::GetFullPath((Join-Path $logBase $LogName))
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
Acquire-MsiSmokeMutex

$currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User?.Value
if ([string]::IsNullOrWhiteSpace($currentUserSid)) {
    Release-MsiSmokeMutex
    throw "Unable to resolve the current Windows user SID for MSI startup-registration validation."
}

# Use the explicit user hive because CI and sandboxed test hosts can remap HKCU for the
# PowerShell process while the Windows Installer service continues to use the real hive.
$runRegistryPath = "Registry::HKEY_USERS\$currentUserSid\Software\Microsoft\Windows\CurrentVersion\Run"
$startupProbeCreated = $false
$serviceRegistryIsRemapped = $env:CODEX_CI -eq "1"
$preexistingEntries = @(Get-UninstallEntries -DisplayName $ProductName)
if ($preexistingEntries.Count -gt 0) {
    Release-MsiSmokeMutex
    throw "Refusing to run the MSI smoke test while $ProductName is already installed for this user. Uninstall the existing copy or use a disposable test account."
}
if (Test-RegistryValueExists -Path $runRegistryPath -Name $ProductName) {
    Release-MsiSmokeMutex
    throw "Refusing to run the MSI smoke test while an existing $ProductName Run-at-startup value is present. Disable Run at Startup or use a disposable test account."
}

try {

$installRoot = if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    [IO.Path]::GetFullPath((Join-Path $logRoot "install-root"))
}
else {
    [IO.Path]::GetFullPath($InstallRoot)
}
$dataRoot = if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    [IO.Path]::GetFullPath((Join-Path $logRoot "data-root"))
}
else {
    [IO.Path]::GetFullPath($DataRoot)
}
$exePath = Join-Path $installRoot "$ProductName.exe"
$startMenuFolder = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$ProductName"
$startMenuShortcutPath = Join-Path $startMenuFolder "$ProductName.lnk"
$uninstallShortcutPath = Join-Path $startMenuFolder "Change or uninstall $ProductName.lnk"
$cleanUninstallShortcutPath = Join-Path $startMenuFolder "Uninstall $ProductName and delete settings.lnk"
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "$ProductName.lnk"
$userDataPath = $dataRoot
$userDataSentinelPath = Join-Path $userDataPath "smoke-user-data.txt"
$manufacturerRegistryPath = "HKCU:\Software\$ManufacturerName\$ProductName"
$installProperties = @{
    INSTALLFOLDER = $installRoot
    AUDIOPILOT_DATA_FOLDER = $userDataPath
    INSTALLDESKTOPSHORTCUT = $DesktopShortcut
    INSTALLSTARTMENUSHORTCUT = $StartMenuShortcut
    ADD_CLI_TO_PATH = $AddCliToPath
}
$isUpgradeSmoke = $null -ne $resolvedUpgradeMsiPath
if ($VerifyUpgradeRollback -and -not $isUpgradeSmoke) {
    throw 'Upgrade rollback verification requires -UpgradeFromMsiPath.'
}

if ($isUpgradeSmoke) {
    $upgradeInstallLog = Join-Path $logRoot "install-upgrade-baseline.log"
    Invoke-MsiInstall -MsiPath $resolvedUpgradeMsiPath -LogPath $upgradeInstallLog -Properties $installProperties

    $baselineEntries = @(Get-UninstallEntries -DisplayName $ProductName)
    if ($baselineEntries.Count -ne 1) {
        throw "Expected one uninstall entry after baseline install, found $($baselineEntries.Count)."
    }

    New-Item -ItemType Directory -Path $userDataPath -Force | Out-Null
    Set-Content -Path $userDataSentinelPath -Value "preserve-upgrade" -Encoding UTF8

    if ($VerifyUpgradeRollback) {
        $baselineProductCode = [string]$baselineEntries[0].PSChildName
        $baselineVersion = [string]$baselineEntries[0].DisplayVersion
        $baselineHash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash
        $baselineCliHash = (Get-FileHash -LiteralPath (Join-Path $installRoot 'AudioPilot.Cli.exe') -Algorithm SHA256).Hash
        $probePath = Join-Path $logRoot "rollback-probe-$([Guid]::NewGuid().ToString('N')).msi"
        & (Join-Path $PSScriptRoot 'new-msi-rollback-probe.ps1') -InputMsiPath $resolvedInstallMsiPath -OutputMsiPath $probePath
        $rollbackLogPath = Join-Path $logRoot 'upgrade-rollback.log'
        Invoke-MsiInstallExpectFailure -MsiPath $probePath -LogPath $rollbackLogPath -ExpectedExitCode 1603

        $rollbackLog = Get-Content -LiteralPath $rollbackLogPath -Raw
        if ($rollbackLog -notmatch '(?m)Action ended .*RemoveExistingProducts\. Return value 1\.' -or $rollbackLog -notmatch 'AudioPilot intentional upgrade rollback probe') {
            throw 'The upgrade did not reach the intended failure after successful old-product removal.'
        }
        $restoredEntries = @(Get-UninstallEntries -DisplayName $ProductName)
        if ($restoredEntries.Count -ne 1 -or $restoredEntries[0].PSChildName -ne $baselineProductCode -or $restoredEntries[0].DisplayVersion -ne $baselineVersion) {
            throw 'Upgrade rollback did not restore the original product registration and version.'
        }
        if ((Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash -ne $baselineHash -or
            (Get-FileHash -LiteralPath (Join-Path $installRoot 'AudioPilot.Cli.exe') -Algorithm SHA256).Hash -ne $baselineCliHash) {
            throw 'Upgrade rollback did not restore the original application and CLI binaries.'
        }
        if ((Get-Content -LiteralPath $userDataSentinelPath -Raw).Trim() -ne 'preserve-upgrade') {
            throw 'Upgrade rollback changed the user-data sentinel.'
        }
        Write-Host 'Failed-upgrade rollback restored product registration, binaries, and user data.'
    }
}

$installLogPath = Join-Path $logRoot "install-current.log"
$currentInstallProperties = if ($isUpgradeSmoke) { @{} } else { $installProperties }
Invoke-MsiInstall -MsiPath $resolvedInstallMsiPath -LogPath $installLogPath -Properties $currentInstallProperties

$entries = @(Get-UninstallEntries -DisplayName $ProductName)
if ($entries.Count -ne 1) {
    throw "Expected one uninstall entry after current install, found $($entries.Count)."
}

$entry = $entries[0]
Assert-UninstallEntryDword -Entry $entry -Name "NoRepair" -ExpectedValue 1
$displayVersionProperty = $entry.PSObject.Properties["DisplayVersion"]
$installedDisplayVersion = if ($displayVersionProperty) { [string]$displayVersionProperty.Value } else { $null }

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $installedDisplayVersion -ne $ExpectedVersion) {
    throw "Installed DisplayVersion '$installedDisplayVersion' did not match expected version '$ExpectedVersion'."
}

Assert-PathExists -Path $exePath
if ($StartMenuShortcut -eq "1") {
    Assert-PathExists -Path $startMenuShortcutPath
    Assert-PathExists -Path $uninstallShortcutPath
    Assert-PathExists -Path $cleanUninstallShortcutPath
}
else {
    Assert-PathMissing -Path $startMenuShortcutPath
    Assert-PathMissing -Path $uninstallShortcutPath
    Assert-PathMissing -Path $cleanUninstallShortcutPath
}

if ($DesktopShortcut -eq "1") {
    Assert-PathExists -Path $desktopShortcutPath
}
else {
    Assert-PathMissing -Path $desktopShortcutPath
}

if ((Test-UserPathContains -ExpectedPath $installRoot) -ne ($AddCliToPath -eq "1")) {
    throw "CLI PATH option did not match ADD_CLI_TO_PATH=$AddCliToPath after installation."
}

if ($isUpgradeSmoke) {
    Assert-PathExists -Path $userDataSentinelPath
    $downgradeLogPath = Join-Path $logRoot 'downgrade-rejected.log'
    Invoke-MsiInstallExpectFailure -MsiPath $resolvedUpgradeMsiPath -LogPath $downgradeLogPath -ExpectedExitCode 1603
    $downgradeLog = Get-Content -LiteralPath $downgradeLogPath -Raw
    if (-not $downgradeLog.Contains("A newer version of $ProductName is already installed.", [StringComparison]::Ordinal)) {
        throw 'The downgrade failed without the expected newer-version rejection message.'
    }
    Assert-PathExists -Path $exePath
}
else {
    New-Item -ItemType Directory -Path $userDataPath -Force | Out-Null
    Set-Content -Path $userDataSentinelPath -Value "preserve-uninstall" -Encoding UTF8
}
New-Item -Path $runRegistryPath -Force | Out-Null
Set-ItemProperty -Path $runRegistryPath -Name $ProductName -Value $exePath -Type String
$startupProbeCreated = $true

Assert-PathExists -Path $userDataSentinelPath
Assert-PathExists -Path $manufacturerRegistryPath

$productCode = $entry.PSChildName
if ([string]::IsNullOrWhiteSpace($productCode)) {
    throw "Unable to determine installed product code from uninstall entry."
}

# Exercise repair against an actually missing installed file, then recheck options.
$cliPath = Join-Path $installRoot 'AudioPilot.Cli.exe'
$cliHash = (Get-FileHash -LiteralPath $cliPath -Algorithm SHA256).Hash
Remove-Item -LiteralPath $cliPath -Force
$repairArguments = @('/fomus', $productCode, '/qn', '/norestart', '/l*v', (Join-Path $logRoot 'repair-current.log'))
$repairProcess = Start-Process -FilePath 'msiexec.exe' -ArgumentList (ConvertTo-ProcessArgumentLine $repairArguments) -Wait -PassThru -WindowStyle Hidden
if ($repairProcess.ExitCode -ne 0 -or (Get-FileHash -LiteralPath $cliPath -Algorithm SHA256).Hash -ne $cliHash) {
    throw 'MSI repair did not restore the missing CLI binary.'
}
if ((Test-Path -LiteralPath $desktopShortcutPath) -ne ($DesktopShortcut -eq '1') -or
    (Test-Path -LiteralPath $startMenuShortcutPath) -ne ($StartMenuShortcut -eq '1') -or
    (Test-UserPathContains -ExpectedPath $installRoot) -ne ($AddCliToPath -eq '1')) {
    throw 'MSI repair changed the installed shortcut or CLI PATH options.'
}
if (-not (Test-Path -LiteralPath $userDataSentinelPath)) {
    throw 'MSI repair removed saved user data.'
}

$uninstallLogPath = Join-Path $logRoot "uninstall-current.log"
$uninstallProperties = @{}
if ($CleanUninstall) {
    $uninstallProperties["AUDIOPILOT_CLEAN_UNINSTALL"] = "1"
}

Invoke-MsiUninstall -ProductCode $productCode -LogPath $uninstallLogPath -Properties $uninstallProperties

$remainingEntries = @(Get-UninstallEntries -DisplayName $ProductName)
if ($remainingEntries.Count -ne 0) {
    throw "Expected uninstall entry to be removed after uninstall, found $($remainingEntries.Count)."
}

Assert-PathMissing -Path $exePath
Assert-PathMissing -Path $startMenuShortcutPath
Assert-PathMissing -Path $uninstallShortcutPath
Assert-PathMissing -Path $desktopShortcutPath
Assert-PathMissing -Path $cleanUninstallShortcutPath
if (Test-UserPathContains -ExpectedPath $installRoot) {
    throw "MSI uninstall left the AudioPilot install directory in the current-user PATH."
}
if ($CleanUninstall) {
    Assert-PathMissing -Path $userDataSentinelPath
}
else {
    Assert-PathExists -Path $userDataSentinelPath
}
if ($serviceRegistryIsRemapped -and (Test-RegistryValueExists -Path $runRegistryPath -Name $ProductName)) {
    Write-Warning "Skipping the service-visible startup cleanup assertion because this Codex test host remaps registry writes away from Windows Installer."
}
else {
    Assert-RegistryValueMissing -Path $runRegistryPath -Name $ProductName
}

if (Test-Path -LiteralPath $manufacturerRegistryPath) {
    throw "Expected current-user AudioPilot registry key to be removed: $manufacturerRegistryPath"
}

if (Test-Path -LiteralPath $userDataSentinelPath) {
    Remove-Item -LiteralPath $userDataSentinelPath -Force
}
if ((Test-Path -LiteralPath $userDataPath -PathType Container) -and -not (Get-ChildItem -LiteralPath $userDataPath -Force)) {
    Remove-Item -LiteralPath $userDataPath -Force
}

Write-Host "MSI smoke test passed."
}
finally {
    try {
        if ($startupProbeCreated) {
            Remove-ItemProperty -LiteralPath $runRegistryPath -Name $ProductName -ErrorAction SilentlyContinue
        }

        $cleanupEntries = @(Get-UninstallEntries -DisplayName $ProductName)
        foreach ($cleanupEntry in $cleanupEntries) {
            $cleanupProductCode = [string]$cleanupEntry.PSChildName
            if (-not [string]::IsNullOrWhiteSpace($cleanupProductCode)) {
                Invoke-MsiUninstall `
                    -ProductCode $cleanupProductCode `
                    -LogPath (Join-Path $logRoot "cleanup-after-smoke-failure.log")
            }
        }
    }
    catch {
        Write-Warning "Failed to clean up the MSI smoke installation automatically: $($_.Exception.Message)"
    }
    finally {
        Release-MsiSmokeMutex
    }
}
