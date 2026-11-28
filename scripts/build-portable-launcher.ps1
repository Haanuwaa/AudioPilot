[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x86', 'x64', 'arm64')][string]$Platform,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$RunTests
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$versionProps = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Version.props') -Raw
    $Version = ([string]($versionProps.Project.PropertyGroup.AudioPilotVersion | Select-Object -First 1)).Trim()
    if ($Version -cnotmatch '^\d+\.\d+\.\d+$') { throw "Invalid AudioPilotVersion in Version.props: '$Version'" }
}
$versionParts = $Version.Split('.')
$versionComma = "$($versionParts[0]),$($versionParts[1]),$($versionParts[2]),0"
$versionQuad = "$Version.0"

$source = (Resolve-Path (Join-Path $PSScriptRoot '../AudioPilot.Updater/PortableLauncher/PortableLauncher.cpp')).Path
$destination = [IO.Path]::GetFullPath($OutputDirectory)
for ($current = $destination; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Redirected portable launcher output.'
    }
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$objDir = Join-Path $destination ".portable-launcher-obj-$Platform"
New-Item -ItemType Directory -Force -Path $objDir | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ build tools are required for native portable launchers.' }
$installation = & $vswhere -latest -products '*' -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) { throw 'No Visual Studio installation was found.' }

$savedEnvironment = @{}
foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
    $savedEnvironment[[string]$entry.Key] = [string]$entry.Value
}

$guiExe = Join-Path $destination 'AudioPilot.exe'
$cliExe = Join-Path $destination 'AudioPilot.Cli.exe'

try {
    Import-Module (Join-Path $installation 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    $hostArchitecture = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    Enter-VsDevShell -VsInstallPath $installation -SkipAutomaticLocation -DevCmdArguments "-arch=$Platform -host_arch=$hostArchitecture" | Out-Null
    $vcToolsDir = [Environment]::GetEnvironmentVariable('VCToolsInstallDir', 'Process')
    if ([string]::IsNullOrWhiteSpace($vcToolsDir) -or -not (Test-Path -LiteralPath (Join-Path $vcToolsDir "lib/$Platform/libcpmt.lib"))) {
        throw "Install the Visual Studio MSVC C++ build tools and runtime libraries for $Platform before building portable launchers."
    }

    function Write-LauncherVersionResource([string]$RcPath, [string]$ResPath, [string]$Description, [string]$InternalName, [string]$OriginalFilename) {
        $rcContent = @"
#include <winver.h>
VS_VERSION_INFO VERSIONINFO
 FILEVERSION $versionComma
 PRODUCTVERSION $versionComma
 FILEFLAGSMASK VS_FFI_FILEFLAGSMASK
 FILEFLAGS 0x0L
 FILEOS VOS_NT_WINDOWS32
 FILETYPE VFT_APP
 FILESUBTYPE VFT2_UNKNOWN
BEGIN
    BLOCK "StringFileInfo"
    BEGIN
        BLOCK "040904b0"
        BEGIN
            VALUE "CompanyName", "Haanuwaa\0"
            VALUE "FileDescription", "$Description\0"
            VALUE "FileVersion", "$versionQuad\0"
            VALUE "InternalName", "$InternalName\0"
            VALUE "LegalCopyright", "Copyright (c) 2026 Haanuwaa\0"
            VALUE "OriginalFilename", "$OriginalFilename\0"
            VALUE "ProductName", "AudioPilot\0"
            VALUE "ProductVersion", "$Version\0"
        END
    END
    BLOCK "VarFileInfo"
    BEGIN
        VALUE "Translation", 0x0409, 1200
    END
END
"@
        [IO.File]::WriteAllText($RcPath, $rcContent, [Text.UTF8Encoding]::new($false))
        & rc.exe /nologo /c 65001 /fo $ResPath $RcPath
        if ($LASTEXITCODE -ne 0) { throw "Version resource compilation failed for $OriginalFilename ($Platform)." }
    }

    $guiRes = Join-Path $objDir 'PortableLauncher.gui.res'
    $cliRes = Join-Path $objDir 'PortableLauncher.cli.res'
    Write-LauncherVersionResource (Join-Path $objDir 'PortableLauncher.gui.rc') $guiRes 'AudioPilot portable launcher' 'AudioPilot' 'AudioPilot.exe'
    Write-LauncherVersionResource (Join-Path $objDir 'PortableLauncher.cli.rc') $cliRes 'AudioPilot portable CLI launcher' 'AudioPilot.Cli' 'AudioPilot.Cli.exe'

    & cl.exe /nologo /utf-8 /std:c++20 /O1 /Os /GL /W4 /WX /MT /EHsc /DUNICODE /D_UNICODE /DPACKAGED_LAUNCHER=1 $source $guiRes `
        "/Fo$objDir/PortableLauncher.gui.obj" "/Fe$guiExe" `
        /link /LTCG /OPT:REF /OPT:ICF /MANIFEST:NO /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT kernel32.lib advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw "Native portable GUI launcher failed to build for $Platform." }

    & cl.exe /nologo /utf-8 /std:c++20 /O1 /Os /GL /W4 /WX /MT /EHsc /DUNICODE /D_UNICODE /DPACKAGED_LAUNCHER=1 /DCLI_LAUNCHER=1 $source $cliRes `
        "/Fo$objDir/PortableLauncher.cli.obj" "/Fe$cliExe" `
        /link /LTCG /OPT:REF /OPT:ICF /MANIFEST:NO /SUBSYSTEM:CONSOLE /DYNAMICBASE /NXCOMPAT kernel32.lib
    if ($LASTEXITCODE -ne 0) { throw "Native portable CLI launcher failed to build for $Platform." }

    if ($RunTests) {
        if ($Platform -ne $hostArchitecture -and $Platform -ne 'x86') {
            throw 'Native portable launcher tests require a compatible host architecture.'
        }
        $testSource = Join-Path (Split-Path -Parent $source) 'PortableLauncher.Tests.cpp'
        $testExe = Join-Path $objDir 'PortableLauncher.Tests.exe'
        & cl.exe /nologo /utf-8 /std:c++20 /O2 /W4 /WX /MT /EHsc /DUNICODE /D_UNICODE $testSource `
            "/Fo$objDir/PortableLauncher.Tests.obj" "/Fe$testExe" `
            /link /MANIFEST:EMBED /MANIFESTUAC:"level='asInvoker' uiAccess='false'" /DYNAMICBASE /NXCOMPAT kernel32.lib user32.lib advapi32.lib shell32.lib ole32.lib
        if ($LASTEXITCODE -ne 0) { throw "Native portable launcher tests failed to build for $Platform." }

        $fixture = Join-Path $objDir "junction-fixture-$([Guid]::NewGuid().ToString('N'))"
        $target = Join-Path $fixture 'target'
        $junction = Join-Path $fixture 'link'
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        try {
            New-Item -ItemType Junction -Path $junction -Target $target | Out-Null
            & $testExe $junction
            if ($LASTEXITCODE -ne 0) { throw "Native portable launcher tests failed for $Platform." }
        }
        finally {
            if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
            if (Test-Path -LiteralPath $target) { [IO.Directory]::Delete($target) }
            if (Test-Path -LiteralPath $fixture) { [IO.Directory]::Delete($fixture) }
        }
    }
}
finally {
    foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
        $key = [string]$entry.Key
        if (-not $savedEnvironment.ContainsKey($key)) {
            [Environment]::SetEnvironmentVariable($key, $null, 'Process')
        }
    }
    foreach ($entry in $savedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    if (Test-Path -LiteralPath $objDir) { Remove-Item -LiteralPath $objDir -Recurse -Force }
}
