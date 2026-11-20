function Read-AudioPilotArchiveJson {
    param($Entry, [string[]]$Properties)
    if ($null -eq $Entry -or $Entry.Length -gt 8192) { throw 'Missing or oversized portable state.' }
    $stream = $Entry.Open()
    try { $document = [Text.Json.JsonDocument]::Parse($stream) } finally { $stream.Dispose() }
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if ($property.Name -cnotin $Properties -or -not $seen.Add($property.Name)) { throw 'Ambiguous portable state.' }
        }
        if ($seen.Count -ne $Properties.Count) { throw 'Incomplete portable state.' }
        return ($document.RootElement.GetRawText() | ConvertFrom-Json)
    } finally { $document.Dispose() }
}

function Get-AudioPilotReleaseArchiveLayout {
    param([Parameter(Mandatory)]$Archive)
    if ($Archive.Entries.Count -eq 0 -or $Archive.Entries.Count -gt 4096) { throw 'Invalid release archive entry count.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total = 0L
    foreach ($entry in $Archive.Entries) {
        $name = $entry.FullName
        $total += $entry.Length
        if ($total -gt 1GB -or $name.Contains('\') -or
            @($name.TrimEnd('/') -split '/' | Where-Object { $_ -in @('', '.', '..') -or $_.Contains(':') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -or
            -not $names.Add($name) -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000 -or ($entry.ExternalAttributes -band 1024)) {
            throw 'Unsafe or duplicate release archive entry.'
        }
    }
    $root = 'AudioPilot/'
    $workspace = $root + 'app/'
    if (@($Archive.Entries | Where-Object { -not $_.FullName.StartsWith($root, [StringComparison]::Ordinal) }).Count) { throw 'Unexpected portable archive root.' }
    if ($null -eq $Archive.GetEntry($workspace + '.updater')) {
        if (@($Archive.Entries | Where-Object { $_.FullName.StartsWith($workspace, [StringComparison]::Ordinal) }).Count) { throw 'Missing portable workspace marker.' }
        return @{ Prefix=$root; RootPrefix=$root; Managed=$false }
    }
    $layout = Read-AudioPilotArchiveJson $Archive.GetEntry($workspace + 'portable-layout.json') @('Schema','DiagnosticId','Managed')
    $state = Read-AudioPilotArchiveJson $Archive.GetEntry($workspace + 'deployment.json') @('Schema','Current','Previous','PendingHealth')
    $id = [Guid]::Empty
    if ($layout.Schema -ne 1 -or $layout.Managed -isnot [bool] -or -not $layout.Managed -or
        -not [Guid]::TryParseExact($layout.DiagnosticId,'N',[ref]$id) -or $id -eq [Guid]::Empty -or
        $state.Schema -ne 1 -or $state.Current -cnotmatch '^\d+\.\d+\.\d+_[a-f0-9]{32}$' -or
        $null -ne $state.Previous -or $state.PendingHealth -isnot [bool] -or $state.PendingHealth) { throw 'Invalid initial portable layout.' }
    $prefix = $workspace + "versions/$($state.Current)/"
    $owner = Read-AudioPilotArchiveJson $Archive.GetEntry($prefix+'.updater-owned.json') @('Schema','Kind','Identity')
    if ($owner.Schema -ne 1 -or $owner.Kind -cne 'generation' -or $owner.Identity -cne $state.Current) { throw 'Invalid portable generation owner.' }
    foreach ($entry in $Archive.Entries) {
        if ($entry.FullName.StartsWith($prefix,[StringComparison]::Ordinal)) { continue }
        if ($entry.FullName -cin @($root,$workspace,($workspace+'versions/'),($root+'data/'),($root+'licenses/'))) {
            if ($entry.Length -ne 0) { throw 'Nonempty initial portable directory entry.' }
            continue
        }
        if ($entry.FullName -cnotin @(($workspace+'.updater'),($workspace+'portable-layout.json'),($workspace+'deployment.json'),($workspace+'.documents-generation'),($root+'AudioPilot.exe'),($root+'AudioPilot.Cli.exe'),($root+'ABOUT.txt'),($root+'licenses/LICENSE'),($root+'licenses/THIRD-PARTY-NOTICES.txt'),($root+'licenses/UPDATER-NOTICES.txt'))) { throw 'Unexpected initial portable entry.' }
    }
    foreach ($required in @('AudioPilot.exe','AudioPilot.Cli.exe')) { if ($null -eq $Archive.GetEntry($root + $required)) { throw 'Missing portable launcher.' } }
    $receipt = $Archive.GetEntry($workspace + '.documents-generation')
    if ($null -ne $receipt) {
        if ($receipt.Length -gt 100) { throw 'Invalid portable document receipt.' }
        $reader = [IO.StreamReader]::new($receipt.Open())
        try { if ($reader.ReadToEnd() -cne $state.Current) { throw 'Stale portable document receipt.' } } finally { $reader.Dispose() }
    }
    foreach ($document in @('ABOUT.txt','licenses/LICENSE','licenses/THIRD-PARTY-NOTICES.txt','licenses/UPDATER-NOTICES.txt')) {
        $outer = $Archive.GetEntry($root + $document)
        $inner = $Archive.GetEntry($prefix + $document)
        if ($null -eq $outer -or $null -eq $inner -or $outer.Length -le 0 -or $outer.Length -gt 4MB -or $outer.Length -ne $inner.Length) { throw 'Missing or invalid portable document.' }
        $left = $outer.Open(); $right = $inner.Open()
        try {
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($left)) -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($right))) { throw 'Portable root document differs from its generation.' }
        } finally { $left.Dispose(); $right.Dispose() }
    }
    $marker = $Archive.GetEntry($workspace + '.updater')
    if ($marker.Length -gt 64) { throw 'Invalid portable marker.' }
    $reader = [IO.StreamReader]::new($marker.Open())
    try { if ($reader.ReadToEnd() -cne 'AudioPilot updater workspace 1') { throw 'Invalid portable marker.' } } finally { $reader.Dispose() }
    return @{ Prefix=$prefix; RootPrefix=$root; WorkspacePrefix=$workspace; Managed=$true; Generation=$state.Current }
}

function Read-AudioPilotEmbeddedUpdatePolicy {
    param([Parameter(Mandatory)][string]$Protocol)
    $inputFile = [IO.File]::OpenRead($Protocol)
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($inputFile)
        try {
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $resources = @($metadata.ManifestResources | ForEach-Object { $metadata.GetManifestResource($_) } | Where-Object { $metadata.GetString($_.Name) -ceq 'AudioPilot.UpdateProtocol.PilotPolicy.json' })
            if ($resources.Count -ne 1 -or -not $resources[0].Implementation.IsNil) { throw 'Missing embedded updater policy.' }
            $reader = $pe.GetSectionData($pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress).GetReader()
            $reader.Offset = [int]$resources[0].Offset
            $length = $reader.ReadInt32()
            if ($length -lt 1 -or $length -gt 98304) { throw 'Invalid embedded updater policy length.' }
            return ([Text.Encoding]::UTF8.GetString($reader.ReadBytes($length)) | ConvertFrom-Json)
        } finally { $pe.Dispose() }
    } finally { $inputFile.Dispose() }
}
