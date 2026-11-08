Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot '../lib/Msi.ps1')
$root = Join-Path $repository "artifacts/testresults/winget/$([Guid]::NewGuid().ToString('N'))"
$releaseRoot = Join-Path $root 'release'
$manifestRoot = Join-Path $root 'winget'
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$generator = Join-Path $repository 'packaging/winget/generate-winget-manifest.ps1'
$validator = Join-Path $PSScriptRoot '../validate-winget-manifests.ps1'
$installer = New-Object -ComObject WindowsInstaller.Installer
$paths = @{}
try {
    foreach ($architecture in @('x86', 'x64', 'arm64')) {
        $path = [IO.Path]::GetFullPath((Join-Path $releaseRoot "AudioPilot-1.0.0-$architecture.msi"))
        $database = $installer.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$installer,@($path,3))
        try {
            $sql = @('CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)',
                "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('ProductVersion', '1.0.0')",
                "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('ProductCode', '{$([Guid]::NewGuid())}')",
                "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('UpgradeCode', '{00000000-1111-2222-3333-444444444444}')")
            foreach ($query in $sql) {
                $view = $database.GetType().InvokeMember('OpenView','InvokeMethod',$null,$database,@($query))
                try { $view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null) | Out-Null }
                finally { $view.GetType().InvokeMember('Close','InvokeMethod',$null,$view,$null) | Out-Null; [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
            }
            $summary = $database.GetType().InvokeMember('SummaryInformation','GetProperty',$null,$database,@(1))
            try {
                $template = if ($architecture -eq 'x86') { 'Intel' } else { $architecture }
                $summary.GetType().InvokeMember('Property','SetProperty',$null,$summary,@(7,"$template;1033")) | Out-Null
                $summary.GetType().InvokeMember('Persist','InvokeMethod',$null,$summary,$null) | Out-Null
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
            $database.GetType().InvokeMember('Commit','InvokeMethod',$null,$database,$null) | Out-Null
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        $paths[$architecture] = $path
    }
    $arguments = @{ Version = '1.0.0'; OutputRoot = $manifestRoot; X86InstallerPath = $paths.x86; X64InstallerPath = $paths.x64; Arm64InstallerPath = $paths.arm64 }
    & $generator @arguments
    & $validator -ManifestRoot $manifestRoot -ReleaseRoot $releaseRoot
    $generatedFiles = @(Get-ChildItem -LiteralPath $manifestRoot -Recurse -Filter '*.yaml')
    if ($generatedFiles.Count -ne 3) { throw 'Expected three winget manifests.' }
    foreach ($file in $generatedFiles) {
        if ([IO.File]::ReadAllText($file.FullName) -notmatch "(?m)^ManifestVersion: '1\.28\.0'\r?$") {
            throw "Generated manifest did not migrate to schema 1.28.0: $($file.Name)"
        }
    }
    foreach ($case in @('wrong-version', 'wrong-architecture')) {
        $bad = $arguments.Clone()
        if ($case -eq 'wrong-version') { $bad.Version = '9.9.9' } else { $bad.X86InstallerPath = $paths.x64 }
        $failed = $false
        try { & $generator @bad } catch { if ($_.Exception.Message -notmatch 'ProductVersion|summary architecture') { throw }; $failed = $true }
        if (-not $failed) { throw "Invalid winget generation accepted: $case" }
        Write-Host "Winget generator rejection passed: $case"
    }
    $manifest = Join-Path $manifestRoot 'h/Haanuwaa/AudioPilot/1.0.0/Haanuwaa.AudioPilot.installer.yaml'
    $text = [IO.File]::ReadAllText($manifest)
    $changed = $text.Replace("- Architecture: 'x64'", "- Architecture: 'x86'`n- Architecture: 'x64'")
    [IO.File]::WriteAllText($manifest,$changed,[Text.UTF8Encoding]::new($false))
    $failed = $false
    try { & $validator -ManifestRoot $manifestRoot -ReleaseRoot $releaseRoot } catch { if ($_.Exception.Message -notlike '*exactly*installers*') { throw }; $failed = $true }
    if (-not $failed) { throw 'Duplicate installer accepted.' }
    [IO.File]::WriteAllText($manifest,$text,[Text.UTF8Encoding]::new($false))
    $copy = Join-Path $root 'original-x86.msi'
    Copy-Item -LiteralPath $paths.x86 -Destination $copy
    Copy-Item -LiteralPath $paths.x64 -Destination $paths.x86 -Force
    $failed = $false
    try { & $validator -ManifestRoot $manifestRoot -ReleaseRoot $releaseRoot } catch { if ($_.Exception.Message -notlike '*summary architecture*') { throw }; $failed = $true }
    if (-not $failed) { throw 'Wrong MSI architecture accepted by standalone validator.' }
    Copy-Item -LiteralPath $copy -Destination $paths.x86 -Force
    $database = $installer.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$installer,@([string]$paths.x86,1))
    try {
        $view = $database.GetType().InvokeMember('OpenView','InvokeMethod',$null,$database,@("UPDATE ``Property`` SET ``Value``='9.9.9' WHERE ``Property``='ProductVersion'"))
        try { $view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null) | Out-Null }
        finally { $view.GetType().InvokeMember('Close','InvokeMethod',$null,$view,$null) | Out-Null; [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        $database.GetType().InvokeMember('Commit','InvokeMethod',$null,$database,$null) | Out-Null
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    $failed = $false
    try { & $validator -ManifestRoot $manifestRoot -ReleaseRoot $releaseRoot } catch { if ($_.Exception.Message -notlike '*ProductVersion*') { throw }; $failed = $true }
    if (-not $failed) { throw 'Wrong MSI version accepted by standalone validator.' }
    Write-Host 'Winget generation and standalone metadata checks passed.'
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    $parent = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/testresults/winget')) + '\'
    if (-not $root.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe winget fixture cleanup.' }
    Remove-Item -LiteralPath $root -Recurse -Force
}
