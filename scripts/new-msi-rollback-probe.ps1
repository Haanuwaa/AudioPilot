[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputMsiPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputMsiPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/Msi.ps1')

$sourcePath = (Resolve-Path -LiteralPath $InputMsiPath).Path
$destinationPath = [IO.Path]::GetFullPath($OutputMsiPath)
if ($sourcePath -eq $destinationPath -or (Test-Path -LiteralPath $destinationPath)) {
    throw 'The rollback probe must be a new test-only MSI copy; the source must remain unchanged.'
}

$sequence = @(Invoke-AudioPilotMsiQuery -Path $sourcePath -Query 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' -Columns @('Action', 'Sequence'))
$initialize = @($sequence | Where-Object Action -eq 'InstallInitialize')
$remove = @($sequence | Where-Object Action -eq 'RemoveExistingProducts')
$finalize = @($sequence | Where-Object Action -eq 'InstallFinalize')
if ($initialize.Count -ne 1 -or $remove.Count -ne 1 -or $finalize.Count -ne 1) {
    throw 'The MSI must have one InstallInitialize, RemoveExistingProducts, and InstallFinalize action.'
}
$failureSequence = [int]$remove[0].Sequence + 1
if ([int]$initialize[0].Sequence -ge [int]$remove[0].Sequence -or $failureSequence -ge [int]$finalize[0].Sequence) {
    throw 'RemoveExistingProducts must be inside the rollback transaction before constructing the probe.'
}
if (@($sequence | Where-Object { [int]$_.Sequence -eq $failureSequence }).Count -gt 0) {
    throw "The sequence immediately after RemoveExistingProducts is occupied: $failureSequence"
}

$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
try {
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($destinationPath, 1))
    # MSI type 19 reports an error. The action exists only in this disposable test copy.
    $statements = @(
        "INSERT INTO ``CustomAction`` (``Action``, ``Type``, ``Target``) VALUES ('AudioPilotRollbackProbe', 19, 'AudioPilot intentional upgrade rollback probe')",
        "INSERT INTO ``InstallExecuteSequence`` (``Action``, ``Condition``, ``Sequence``) VALUES ('AudioPilotRollbackProbe', 'WIX_UPGRADE_DETECTED', $failureSequence)"
    )
    foreach ($statement in $statements) {
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($statement))
        try {
            $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        }
        finally {
            $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
    }
    $database.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $database, $null) | Out-Null
}
finally {
    if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'The source MSI unexpectedly changed while constructing the rollback probe.'
}
Write-Host "Created test-only rollback probe: $destinationPath"
