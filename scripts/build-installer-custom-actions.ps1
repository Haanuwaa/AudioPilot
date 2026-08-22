[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x86', 'x64', 'arm64')][string]$Platform,
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$RunPolicyTests
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path (Join-Path $PSScriptRoot '../AudioPilot.Installer/CustomActions.cpp')).Path
$destination = [IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $destination
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ build tools are required for MSI custom actions.' }
$installation = & $vswhere -latest -products '*' -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) { throw 'No Visual Studio installation was found.' }
Import-Module (Join-Path $installation 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
$hostArchitecture = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
Enter-VsDevShell -VsInstallPath $installation -SkipAutomaticLocation -DevCmdArguments "-arch=$Platform -host_arch=$hostArchitecture" | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $env:VCToolsInstallDir "lib/$Platform/libcpmt.lib"))) {
    throw "Install the Visual Studio MSVC C++ build tools and runtime libraries for $Platform before building this MSI."
}
& cl.exe /nologo /std:c++20 /O2 /W4 /WX /MT /EHsc /LD /DUNICODE /D_UNICODE $source "/Fo$directory/CustomActions.obj" /link "/OUT:$destination" /IMPLIB:"$directory/CustomActions.lib" /DYNAMICBASE /NXCOMPAT msi.lib advapi32.lib ole32.lib oleaut32.lib shell32.lib uuid.lib taskschd.lib
if ($LASTEXITCODE -ne 0) { throw "Native installer custom actions failed to build for $Platform." }
$exports = (& dumpbin.exe /nologo /exports $destination) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect native installer exports.' }
foreach ($entry in @('ValidateDataFolder', 'RecordDataFolderOwnership', 'CleanupOwnedStartup')) {
    if ($exports -notmatch "(?m)^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+$entry(?:\s|$)") {
        throw "MSI custom action entry point is missing or decorated: $entry"
    }
}
if ($RunPolicyTests) {
    if ($Platform -ne $hostArchitecture -and $Platform -ne 'x86') { throw 'Native policy tests require a compatible host architecture.' }
    $testSource = Join-Path (Split-Path -Parent $source) 'CustomActions.Tests.cpp'
    $testExecutable = Join-Path $directory 'CustomActions.Tests.exe'
    & cl.exe /nologo /std:c++20 /O2 /W4 /WX /MT /EHsc /DUNICODE /D_UNICODE $testSource "/Fo$directory/CustomActions.Tests.obj" "/Fe$testExecutable" /link /DYNAMICBASE /NXCOMPAT msi.lib advapi32.lib ole32.lib oleaut32.lib shell32.lib uuid.lib taskschd.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native installer policy tests failed to build.' }
    $fixture = Join-Path $directory "policy-fixture-$([Guid]::NewGuid().ToString('N'))"
    $tree = Join-Path $fixture 'tree'
    $target = Join-Path $fixture 'target'
    $junction = Join-Path $tree 'link'
    New-Item -ItemType Directory -Path $tree, $target -Force | Out-Null
    try {
        New-Item -ItemType Junction -Path $junction -Target $target | Out-Null
        & $testExecutable $junction
        if ($LASTEXITCODE -ne 0) { throw 'Native installer policy tests failed.' }
    }
    finally {
        if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
        [IO.Directory]::Delete($tree)
        [IO.Directory]::Delete($target)
        [IO.Directory]::Delete($fixture)
    }
}
