[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$BootstrapHash,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$WebsiteDirectory,
    [string]$PreviousTimestamp
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$website = (Resolve-Path -LiteralPath $WebsiteDirectory).Path
$destination = Join-Path $website 'updates/staging'
if (Test-Path -LiteralPath $destination) { throw 'Use a fresh website output. Never merge feed generations.' }
for ($current = $destination; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected feed output.' }
}
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
if ((Get-Item -LiteralPath $archivePath).Length -gt 900MB) { throw 'Staging feed archive exceeded its bound.' }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('AudioPilot-public-feed-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($zip.Entries.Count -gt 4096) { throw 'Too many staging feed entries.' }
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$total = 0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if ($name -cnotmatch '^(bootstrap\.root\.json|metadata/(timestamp\.json|[1-9][0-9]{0,6}\.(root|targets|snapshot)\.json)|targets/[a-f0-9]{64}\.[A-Za-z0-9][A-Za-z0-9._-]{0,159})$' -or
                $name.Contains('..') -or -not $names.Add($name) -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) { throw 'Unexpected, linked or duplicate public feed entry.' }
            $limit = if ($name.StartsWith('targets/', [StringComparison]::Ordinal)) { 1GB } else { 128KB }
            $total += $entry.Length
            if ($entry.Length -le 0 -or $entry.Length -gt $limit -or $total -gt 900MB) { throw 'Public feed entry exceeded its length bound.' }
            $path = Join-Path $temporary $name
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            $input = $entry.Open(); $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
            try {
                $buffer = [byte[]]::new(65536); [long]$copied = 0
                while (($count = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $copied += $count
                    if ($copied -gt $entry.Length) { throw 'Feed entry expanded beyond its declared length.' }
                    $output.Write($buffer, 0, $count)
                }
                if ($copied -ne $entry.Length) { throw 'Truncated feed entry.' }
            }
            finally { $output.Dispose(); $input.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    $request = @{ operation = 'check'; repository = $temporary; bootstrapHash = $BootstrapHash } | ConvertTo-Json -Compress
    $response = $request | & $Publisher
    if ($LASTEXITCODE -ne 0) { throw 'Maintained TUF verification rejected the public staging feed.' }
    $verified = $response | ConvertFrom-Json
    if ($verified.channel -cne 'staging' -or $verified.publicLiveUpdates -ne $false -or $verified.bootstrapHash -cne $BootstrapHash.ToUpperInvariant()) { throw 'Unexpected staging feed identity.' }
    $timestamp = Join-Path $temporary 'metadata/timestamp.json'
    if ($PreviousTimestamp) {
        if ((Get-Item -LiteralPath $PreviousTimestamp).Length -gt 16KB) { throw 'Oversized published timestamp checkpoint.' }
        $previous = Get-Content -LiteralPath $PreviousTimestamp -Raw | ConvertFrom-Json
        if ($verified.version -lt $previous.signed.version -or ($verified.version -eq $previous.signed.version -and
            (Get-FileHash -LiteralPath $timestamp -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $PreviousTimestamp -Algorithm SHA256).Hash)) { throw 'Refusing to deploy an older or conflicting staging feed.' }
    }
    $exported = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($relative in $verified.files) { [void]$exported.Add($relative) }
    if (-not $exported.SetEquals($names)) { throw 'Archive contains files outside the verified public export.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.Directory]::Move($temporary, $destination)
    Write-Output "Attached verified staging feed metadata $($verified.version). Public app updates remain disabled."
}
finally {
    if ([IO.Directory]::Exists($temporary)) {
        $resolved = [IO.Path]::GetFullPath($temporary)
        if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe feed cleanup directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
