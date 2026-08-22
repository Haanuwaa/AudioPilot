function Invoke-AudioPilotMsiQuery {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Query,

        [Parameter(Mandatory = $true)]
        [string[]]$Columns
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    try {
        $database = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @($Path, 0))
        try {
            $view = $database.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $database, @($Query))
        }
        catch {
            return @()
        }

        $rows = [Collections.Generic.List[object]]::new()
        $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null

        while ($true) {
            $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
            if ($null -eq $record) {
                break
            }

            try {
                $row = [ordered]@{}
                for ($index = 0; $index -lt $Columns.Count; $index++) {
                    $row[$Columns[$index]] = $record.StringData($index + 1)
                }
                $rows.Add([pscustomobject]$row)
            }
            finally {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
            }
        }
        return $rows.ToArray()
    }
    finally {
        if ($null -ne $view) {
            try {
                $view.GetType().InvokeMember("Close", "InvokeMethod", $null, $view, $null) | Out-Null
            }
            catch {
            }
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
        if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

function Get-AudioPilotMsiProperty {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$PropertyName,

        [switch]$AllowMissing
    )

    if ($PropertyName -notmatch '^[A-Za-z0-9_]+$') {
        throw "MSI property name contains unsupported characters: $PropertyName"
    }

    $rows = @(Invoke-AudioPilotMsiQuery `
            -Path $Path `
            -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$PropertyName'" `
            -Columns @("Value"))

    if ($rows.Count -eq 0) {
        if ($AllowMissing) {
            return $null
        }

        throw "MSI property '$PropertyName' was not found in '$Path'."
    }

    return [string]$rows[0].Value
}

function Get-AudioPilotMsiSummaryProperty {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 19)]
        [int]$PropertyId
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $summary = $null
    try {
        $database = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @($Path, 0))
        $summary = $database.GetType().InvokeMember("SummaryInformation", "GetProperty", $null, $database, @(0))
        return [string]$summary.GetType().InvokeMember("Property", "GetProperty", $null, $summary, @($PropertyId))
    }
    finally {
        if ($null -ne $summary) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
        if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

function Test-AudioPilotMsiQueryHasRows {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Query
    )

    $rows = @(Invoke-AudioPilotMsiQuery -Path $Path -Query $Query -Columns @("Value"))
    return $rows.Count -gt 0
}

function Get-AudioPilotMsiWildcardRemoveFileRows {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return @(Invoke-AudioPilotMsiQuery `
            -Path $Path `
            -Query "SELECT ``FileKey``, ``DirProperty`` FROM ``RemoveFile`` WHERE ``FileName``='*'" `
            -Columns @("FileKey", "DirProperty"))
}
