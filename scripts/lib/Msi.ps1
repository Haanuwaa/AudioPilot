. (Join-Path $PSScriptRoot 'MsiPayload.ps1')

function Assert-AudioPilotMsiIdentity {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidateSet('x86', 'x64', 'arm64')][string]$Architecture)
    $actualVersion = Get-AudioPilotMsiProperty -Path $Path -PropertyName 'ProductVersion'
    if ($actualVersion -cne $Version) { throw "MSI ProductVersion '$actualVersion' does not match expected version '$Version': $Path" }
    $template = (Get-AudioPilotMsiSummaryProperty -Path $Path -PropertyId 7) -split ';', 2
    $expected = if ($Architecture -eq 'x86') { 'Intel' } else { $Architecture }
    if (-not $template[0].Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "MSI summary architecture '$($template[0])' does not match expected architecture '$Architecture': $Path"
    }
}

function Assert-AudioPilotMsiComponentBitness {
    param([Parameter(Mandatory)][string[]]$Paths)
    $bitness = [Collections.Generic.Dictionary[string, bool]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $Paths) {
        foreach ($component in @(Invoke-AudioPilotMsiQuery -Path $path -Query 'SELECT `ComponentId`, `Attributes` FROM `Component`' -Columns @('Guid', 'Attributes'))) {
            if ([string]::IsNullOrWhiteSpace($component.Guid)) { continue }
            $is64Bit = ([int]$component.Attributes -band 256) -ne 0
            if ($bitness.ContainsKey($component.Guid) -and $bitness[$component.Guid] -ne $is64Bit) {
                throw "MSI component GUID is shared across 32-bit and 64-bit packages: $($component.Guid)"
            }
            $bitness[$component.Guid] = $is64Bit
        }
    }
}

function Assert-AudioPilotMsiPayload {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$PublishArchive)
    $directories = @{}
    foreach ($row in @(Invoke-AudioPilotMsiQuery -Path $Path -Query 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' -Columns @('Directory', 'Parent', 'Name'))) { $directories[$row.Directory] = $row }
    $components = @{}
    foreach ($row in @(Invoke-AudioPilotMsiQuery -Path $Path -Query 'SELECT `Component`, `Directory_` FROM `Component`' -Columns @('Component', 'Directory'))) { $components[$row.Component] = $row.Directory }
    $files = @{}
    $fileIds = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($row in @(Invoke-AudioPilotMsiQuery -Path $Path -Query 'SELECT `File`, `Component_`, `FileName`, `FileSize` FROM `File`' -Columns @('Id', 'Component', 'Name', 'Size'))) {
        $relative = ($row.Name -split '\|')[-1]
        $directory = $components[$row.Component]
        $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        while ($directory -cne 'INSTALLFOLDER') {
            if (-not $directories.ContainsKey($directory) -or -not $visited.Add($directory)) { throw 'MSI payload file is outside INSTALLFOLDER or has a cyclic directory path.' }
            $folder = $directories[$directory]
            $name = (($folder.Name -split ':')[0] -split '\|')[-1]
            if ($name -ne '.') { $relative = "$name/$relative" }
            $directory = $folder.Parent
        }
        if ($files.ContainsKey($relative)) { throw "MSI contains duplicate payload path: $relative" }
        $files[$relative] = [long]$row.Size
        $fileIds.Add($row.Id, $relative)
    }
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ("AudioPilot-MsiPayload-" + [Guid]::NewGuid().ToString("N"))
    $archive = [IO.Compression.ZipFile]::OpenRead($PublishArchive)
    try {
        $entries = @($archive.Entries | Where-Object { $_.Name -ne '' })
        if ($entries.Count -ne $files.Count) { throw 'MSI payload file count differs from its self-contained release archive.' }
        $archivePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $extracted = Expand-AudioPilotMsiPayload -Path $Path -Destination $temporary -FileIds $fileIds -FileSizes $files
        foreach ($entry in $entries) {
            if (-not $entry.FullName.StartsWith('AudioPilot/', [StringComparison]::Ordinal)) { throw 'Unexpected portable archive root.' }
            $relative = $entry.FullName.Substring('AudioPilot/'.Length)
            if (-not $archivePaths.Add($relative)) { throw 'Portable archive contains a duplicate payload path.' }
            if (-not $files.ContainsKey($relative) -or $files[$relative] -ne $entry.Length) { throw "MSI payload differs from the self-contained release archive: $relative" }
            $stream = $entry.Open()
            try { $archiveHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if (-not $extracted.ContainsKey($relative) -or (Get-FileHash -LiteralPath $extracted[$relative] -Algorithm SHA256).Hash -cne $archiveHash) {
                throw "MSI payload content differs from the self-contained release archive: $relative"
            }
        }
    }
    finally {
        $archive.Dispose()
        if (Test-Path -LiteralPath $temporary) {
            $resolved = (Resolve-Path -LiteralPath $temporary).Path
            $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
            $cleanupItems = @(Get-Item -LiteralPath $resolved) + @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)
            if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
                @($cleanupItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
                throw 'Unsafe MSI payload cleanup path.'
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

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
