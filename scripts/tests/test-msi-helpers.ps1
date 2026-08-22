Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../lib/Msi.ps1')

$testRoot = Join-Path $PSScriptRoot "../../artifacts/testresults/msi-helpers/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$fixturePath = [IO.Path]::GetFullPath((Join-Path $testRoot 'fixture.msi'))
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
$summary = $null
try {
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($fixturePath, 3))
    foreach ($sql in @(
        'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)',
        "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('Probe', 'expected')"
    )) {
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($sql))
        try {
            $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        }
        finally {
            $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
    }
    $summary = $database.GetType().InvokeMember('SummaryInformation', 'GetProperty', $null, $database, @(1))
    $summary.GetType().InvokeMember('Property', 'SetProperty', $null, $summary, @(7, 'x64;1033')) | Out-Null
    $summary.GetType().InvokeMember('Persist', 'InvokeMethod', $null, $summary, $null) | Out-Null
    $database.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $database, $null) | Out-Null
}
finally {
    if ($null -ne $summary) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
    if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}

$cases = @(
    @{ Name = 'property'; Run = {
        if ((Get-AudioPilotMsiProperty -Path $casePath -PropertyName Probe) -cne 'expected') { throw 'Property value changed.' }
    } },
    @{ Name = 'empty-query'; Run = {
        if ($null -ne (Get-AudioPilotMsiProperty -Path $casePath -PropertyName Missing -AllowMissing)) { throw 'Missing property was not empty.' }
    } },
    @{ Name = 'missing-table'; Run = {
        if (@(Invoke-AudioPilotMsiQuery -Path $casePath -Query 'SELECT `Value` FROM `MissingTable`' -Columns @('Value')).Count -ne 0) { throw 'Missing table was not empty.' }
    } },
    @{ Name = 'summary'; Run = {
        if ((Get-AudioPilotMsiSummaryProperty -Path $casePath -PropertyId 7) -cne 'x64;1033') { throw 'Summary value changed.' }
    } }
)
$failures = [Collections.Generic.List[string]]::new()
foreach ($case in $cases) {
    $casePath = [IO.Path]::GetFullPath((Join-Path $testRoot "$($case.Name).msi"))
    Copy-Item -LiteralPath $fixturePath -Destination $casePath
    try {
        & $case.Run
        # The caller must be able to replace the MSI immediately, without forcing a GC.
        $exclusive = [IO.File]::Open($casePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $exclusive.Dispose()
        Write-Host "MSI helper check passed: $($case.Name)"
    }
    catch {
        $failures.Add("$($case.Name): $($_.Exception.Message)")
    }
}
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Host "MSI helper checks passed ($($cases.Count) cases; no installation performed)."
