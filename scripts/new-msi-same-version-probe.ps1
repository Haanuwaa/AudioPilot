[CmdletBinding()]
param([Parameter(Mandatory)][string]$InputMsiPath, [Parameter(Mandatory)][string]$OutputMsiPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $InputMsiPath).Path
$destination = [IO.Path]::GetFullPath($OutputMsiPath)
if (Test-Path -LiteralPath $destination) { throw 'The same-version probe must be a new test-only MSI copy.' }
Copy-Item -LiteralPath $source -Destination $destination
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
$summary = $null
try {
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($destination, 1))
    $code = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
    $query = "UPDATE ``Property`` SET ``Value``='$code' WHERE ``Property``='ProductCode'"
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($query))
    try { $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null }
    finally {
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
    $summary = $database.GetType().InvokeMember('SummaryInformation', 'GetProperty', $null, $database, @(1))
    $summary.GetType().InvokeMember('Property', 'SetProperty', $null, $summary, @(9, [Guid]::NewGuid().ToString('B').ToUpperInvariant())) | Out-Null
    $summary.GetType().InvokeMember('Persist', 'InvokeMethod', $null, $summary, $null) | Out-Null
    $database.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $database, $null) | Out-Null
}
finally {
    if ($null -ne $summary) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
    if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
Write-Host "Created test-only same-version product: $destination"
