[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$BootstrapHash,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$ReportPath,
    [ValidateSet('staging','stable')][string]$Channel = 'staging',
    [switch]$MetadataOnly,
    [ValidatePattern('^[a-f0-9]{40}$')][string]$ExistingLeaseSha
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = 'Haanuwaa/AudioPilot'
$tag = "updater-$Channel-feed"
function Read-StagingRelease {
    $json = & gh api "repos/$script:repo/releases?per_page=100" --paginate --jq ".[] | select(.tag_name == `"$script:tag`")"
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect staging releases.' }
    if (-not $json) { return $null }
    return ($json | ConvertFrom-Json)
}
$source = (Resolve-Path -LiteralPath $FeedDirectory).Path
$request = @{ operation = 'check'; channel = $Channel; repository = $source; bootstrapHash = $BootstrapHash; metadataOnly = [bool]$MetadataOnly } | ConvertTo-Json -Compress
$verified = ($request | & $Publisher) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $verified.channel -cne $Channel) { throw 'A verified channel-bound update feed is required.' }
. (Join-Path $PSScriptRoot 'lib/StagingPublicationLease.ps1')
$leaseSha = if ($ExistingLeaseSha) { Assert-StagingPublicationLease $ExistingLeaseSha -Channel $Channel; $ExistingLeaseSha } else { Enter-StagingPublicationLease -Channel $Channel }
$notes = Join-Path ([IO.Path]::GetTempPath()) ('AudioPilot-feed-notes-' + [guid]::NewGuid().ToString('N') + '.txt')
try {
$title = if ($Channel -ceq 'stable') { 'Updater data - Stable' } else { 'Updater data - Testing' }
$description = if ($Channel -ceq 'stable') {
    @'
**Application downloads: [Get the latest AudioPilot release](https://github.com/Haanuwaa/AudioPilot/releases/latest).**

This entry stores signed metadata and approved packages for AudioPilot's stable updater. AudioPilot uses these files after you approve **Update and restart**. For a manual installation, use the application download link above.

The Pre-release badge keeps this infrastructure entry out of Latest; the application release has its own stable status. Only public metadata and approved package files are hosted here. Private signing keys are never uploaded. Windows publishers are self-signed and pinned by AudioPilot; this does not establish public Windows trust.
'@
} else {
    @'
**Application downloads: [Get the latest AudioPilot release](https://github.com/Haanuwaa/AudioPilot/releases/latest).**

Development and isolated Windows Sandbox testing only. This entry stores the separate engineering updater feed; normal AudioPilot installations use the stable channel.

Engineering packages can use temporary self-signed Windows identities and belong in isolated test environments. Their availability does not control stable updates. Only public metadata and approved test packages are hosted here. Private signing keys are never uploaded.
'@
}
$description | Set-Content -LiteralPath $notes -Encoding utf8NoBOM
$release = Read-StagingRelease
if ($null -eq $release) {
    if ($MetadataOnly) { throw 'Metadata renewal requires an existing staging release with approved packages.' }
    $commit = (& gh api "repos/$repo/git/ref/heads/main" --jq '.object.sha').Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[a-f0-9]{40}$') { throw 'Cannot identify the destination main revision.' }
    $creation = @{tag_name=$tag;target_commitish=$commit;name=$title;body=$description;draft=$true;prerelease=$true;make_latest='false'} | ConvertTo-Json -Compress
    $release = ($creation | & gh api --method POST "repos/$repo/releases" --input -) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $null -eq $release) { throw 'Could not create the separate feed draft.' }
}
if ($release.tag_name -cne $tag) { throw 'The feed release tag differs from its selected channel.' }
if (-not $release.prerelease) { throw 'Feed storage must remain a prerelease container.' }
if ($MetadataOnly -and $release.draft) { throw 'Metadata renewal requires an already public staging feed.' }
if ($MetadataOnly) {
    foreach ($target in $verified.artifacts) {
        $assets = @($release.assets | Where-Object name -CEQ $target.name)
        if ($assets.Count -ne 1 -or $assets[0].size -ne $target.length -or $assets[0].digest -cne "sha256:$($target.sha256)") { throw 'An approved staging package is missing or differs. Renewal cannot repair or replace package bytes.' }
    }
}
$releaseId = [long]$release.id
if ($releaseId -le 0) { throw 'Invalid feed release identity.' }
if (@($release.assets | Where-Object { $_.name -cmatch '^([1-9][0-9]*)\.root\.json$' -and [long]$Matches[1] -gt $verified.rootVersion }).Count) { throw 'The published trust root advanced; refusing an older signed export.' }
$upload = Join-Path ([IO.Path]::GetTempPath()) ('AudioPilot-feed-upload-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($upload) | Out-Null
$heldSources = [Collections.Generic.List[IDisposable]]::new()
try {
    $records = @($verified.files | ForEach-Object {
        $relative = [string]$_
        if ($relative -cnotmatch '^(bootstrap\.root\.json|metadata/[A-Za-z0-9._-]+|targets/[A-Za-z0-9._-]+)$' -or $relative.Contains('..')) { throw 'Unsafe feed export path.' }
        $path = Join-Path $source $relative
        $heldSources.Add([IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        @{name=[IO.Path]::GetFileName($relative);path=$path;length=(Get-Item -LiteralPath $path).Length;hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $confirmed = ($request | & $Publisher) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $confirmed.channel -cne $Channel -or $confirmed.version -ne $verified.version -or
        (@($confirmed.files | Sort-Object) -join '|') -cne (@($verified.files | Sort-Object) -join '|')) { throw 'The signed feed changed before publication.' }
    $verified = $confirmed
    if (@($records | Where-Object name -CEQ 'timestamp.json').Count -ne 1) { throw 'Expected one verified feed timestamp.' }
    function Read-CurrentRelease {
        $value = (& gh api "repos/$repo/releases/$releaseId") | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $null -eq $value -or [long]$value.id -ne $script:releaseId -or $value.tag_name -cne $tag -or -not $value.prerelease) { throw 'The feed release identity changed.' }
        if (@($value.assets | Where-Object { $_.name -cmatch '^([1-9][0-9]*)\.root\.json$' -and [long]$Matches[1] -gt $verified.rootVersion }).Count) { throw 'The published trust root advanced; refusing an older signed export.' }
        return $value
    }
    function Find-Asset($Value, [string]$Name) {
        $matching = @($Value.assets | Where-Object name -CEQ $Name)
        if ($matching.Count -gt 1) { throw "Duplicate feed asset: $Name" }
        if ($matching.Count) { return $matching[0] }
        return $null
    }
    function Test-AssetBytes($Asset, [string]$Hash, [long]$Length) {
        return $null -ne $Asset -and $Asset.state -ceq 'uploaded' -and $Asset.size -eq $Length -and
            $Asset.PSObject.Properties.Name -ccontains 'digest' -and $Asset.digest -ceq "sha256:$Hash"
    }
    function Test-EmptyStarter($Asset) {
        return $null -ne $Asset -and $Asset.state -ceq 'starter' -and $Asset.size -eq 0 -and
            ($Asset.PSObject.Properties.Name -cnotcontains 'digest' -or -not $Asset.digest)
    }
    function Remove-EmptyStarter($Asset) {
        if (-not (Test-EmptyStarter $Asset) -or [long]$Asset.id -le 0) { throw 'Refusing to remove a nonempty or completed feed asset.' }
        Assert-StagingPublicationLease $leaseSha -Channel $Channel
        gh api --method DELETE "repos/$repo/releases/assets/$($Asset.id)" --silent
        if ($LASTEXITCODE -ne 0) { throw 'Could not remove the empty failed-upload placeholder.' }
    }
    function Upload-Immutable($Record, [string]$Name) {
        $copy = Join-Path $upload $Name
        Copy-Item -LiteralPath $Record.path -Destination $copy -Force
        if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Record.hash) { throw 'Feed bytes changed while preparing upload.' }
        for ($attempt=1; $attempt -le 3; $attempt++) {
            Assert-StagingPublicationLease $leaseSha -Channel $Channel
            $asset = Find-Asset (Read-CurrentRelease) $Name
            if (Test-AssetBytes $asset $Record.hash $Record.length) { return $asset }
            if ($null -ne $asset) {
                if (Test-EmptyStarter $asset) { Remove-EmptyStarter $asset }
                else { throw "Immutable feed asset differs: $Name" }
            }
            gh release upload $tag --repo $repo $copy
            # A lost upload acknowledgement can still leave complete, correct bytes on the server.
            $asset = Find-Asset (Read-CurrentRelease) $Name
            if (Test-AssetBytes $asset $Record.hash $Record.length) { return $asset }
            if ($null -ne $asset -and $asset.state -cne 'starter') { throw "Uploaded feed asset digest mismatch: $Name" }
            if ($attempt -lt 3) { Start-Sleep -Milliseconds (100 * $attempt) }
        }
        throw "Feed upload failed after three attempts: $Name"
    }
    function Assert-ImmutableAssets($Value) {
        foreach ($record in $records | Where-Object name -CNE 'timestamp.json') {
            if (-not (Test-AssetBytes (Find-Asset $Value $record.name) $record.hash $record.length)) { throw "Feed prerequisite is missing or differs: $($record.name)" }
        }
        foreach ($target in $verified.artifacts) {
            if (-not (Test-AssetBytes (Find-Asset $Value $target.name) $target.sha256 $target.length)) { throw 'An approved package is missing or differs; the timestamp was not advanced.' }
        }
    }
    $stamp = @($records | Where-Object name -CEQ 'timestamp.json')[0]
    $pendingName = "timestamp-$($stamp.hash).pending"
    $backupPrefix = "timestamp-$($stamp.hash)-"
    function Read-Timestamp($Value) {
        $asset = Find-Asset $Value 'timestamp.json'
        if ($null -eq $asset -or (Test-EmptyStarter $asset)) {
            if (@($Value.assets | Where-Object { $_.name -cmatch '^([1-9][0-9]*)\.snapshot\.json$' -and [long]$Matches[1] -gt $verified.version }).Count) { throw 'A newer snapshot exists without a readable timestamp; inspect recovery before replaying this export.' }
            if ($null -ne $asset) { Remove-EmptyStarter $asset }
            return $null
        }
        if ($asset.state -cne 'uploaded' -or $asset.size -le 0 -or $asset.size -gt 16384 -or $asset.digest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'Invalid published timestamp asset.' }
        if (Test-AssetBytes $asset $stamp.hash $stamp.length) { return @{asset=$asset;version=$verified.version} }
        $previous = Join-Path $upload ('previous-' + [guid]::NewGuid().ToString('N'))
        gh release download $tag --repo $repo --pattern timestamp.json --output $previous
        if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $previous).Length -ne $asset.size -or
            (Get-FileHash -LiteralPath $previous -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.digest.Substring(7)) { throw 'Could not verify the previous timestamp bytes.' }
        $prior = Get-Content -LiteralPath $previous -Raw | ConvertFrom-Json
        if ($prior.signed.version -isnot [long] -and $prior.signed.version -isnot [int] -or $prior.signed.version -lt 1 -or
            $prior.signed.version -gt $verified.version -or $prior.signed.version -eq $verified.version) { throw 'Refusing a timestamp rollback or same-version collision.' }
        return @{asset=$asset;version=$prior.signed.version}
    }
    function Rename-Asset($Asset, [string]$Name) {
        Assert-StagingPublicationLease $leaseSha -Channel $Channel
        gh api --method PATCH "repos/$repo/releases/assets/$($Asset.id)" -f "name=$Name" --silent
    }
    function Restore-PreviousTimestamp {
        $value = Read-CurrentRelease
        if ($null -ne (Find-Asset $value 'timestamp.json')) { return }
        if (@($value.assets | Where-Object { $_.name -cmatch '^([1-9][0-9]*)\.snapshot\.json$' -and [long]$Matches[1] -gt $verified.version }).Count) { throw 'A newer snapshot exists; refusing to restore an older timestamp.' }
        $backups = @($value.assets | Where-Object { $_.name -cmatch ('^' + [regex]::Escape($backupPrefix) + '[a-f0-9]{64}\.previous$') })
        if ($backups.Count -gt 1) { throw 'Ambiguous timestamp recovery assets; inspect publication before retrying.' }
        if ($backups.Count -eq 1) {
            $backup = $backups[0]
            $hash = $backup.name.Substring($backupPrefix.Length, 64)
            if ($backup.state -cne 'uploaded' -or $backup.size -le 0 -or $backup.size -gt 16384 -or $backup.digest -cne "sha256:$hash") { throw 'Invalid timestamp recovery asset.' }
            Rename-Asset $backup 'timestamp.json'
            $restored = Find-Asset (Read-CurrentRelease) 'timestamp.json'
            if ($null -eq $restored -or $restored.id -ne $backup.id -or $restored.digest -cne $backup.digest) { throw 'Timestamp restoration could not be confirmed.' }
        }
    }
    foreach ($record in $records | Where-Object name -CNE 'timestamp.json') { $null = Upload-Immutable $record $record.name }
    Assert-ImmutableAssets (Read-CurrentRelease)
    $current = Read-Timestamp (Read-CurrentRelease)
    if ($null -eq $current -or -not (Test-AssetBytes $current.asset $stamp.hash $stamp.length)) {
        $null = Upload-Immutable $stamp $pendingName
        for ($attempt=1; $attempt -le 3; $attempt++) {
            try {
                $value = Read-CurrentRelease
                Assert-ImmutableAssets $value
                $current = Read-Timestamp $value
                if ($null -ne $current -and (Test-AssetBytes $current.asset $stamp.hash $stamp.length)) { break }
                if ($null -ne $current) {
                    $backupName = $backupPrefix + $current.asset.digest.Substring(7) + '.previous'
                    if ($null -ne (Find-Asset $value $backupName)) { throw 'Timestamp recovery name is already occupied.' }
                    Rename-Asset $current.asset $backupName
                    if ($null -ne (Find-Asset (Read-CurrentRelease) 'timestamp.json')) { throw 'The previous timestamp was not moved; publication was not committed.' }
                }
                $pending = Find-Asset (Read-CurrentRelease) $pendingName
                if (-not (Test-AssetBytes $pending $stamp.hash $stamp.length)) { throw 'The prepared timestamp changed.' }
                Rename-Asset $pending 'timestamp.json'
                if (-not (Test-AssetBytes (Find-Asset (Read-CurrentRelease) 'timestamp.json') $stamp.hash $stamp.length)) { throw 'The timestamp commit could not be confirmed.' }
                break
            }
            catch {
                $failure = $_
                try {
                    if (Test-AssetBytes (Find-Asset (Read-CurrentRelease) 'timestamp.json') $stamp.hash $stamp.length) { break }
                    Restore-PreviousTimestamp
                } catch { Write-Warning "Timestamp recovery needs attention: $($_.Exception.Message)" }
                if ($attempt -eq 3) { throw $failure }
                Start-Sleep -Milliseconds (100 * $attempt)
            }
        }
    }
    gh release edit $tag --repo $repo --draft=false --prerelease --latest=false --title $title --notes-file $notes
    if ($LASTEXITCODE -ne 0) { throw 'Feed publication failed.' }
    $published = Read-CurrentRelease
    if ($published.draft) { throw 'Feed publication was not confirmed.' }
    if ($published.name -cne $title -or $published.body.Replace("`r`n", "`n").TrimEnd() -cne $description.Replace("`r`n", "`n").TrimEnd()) {
        throw 'Feed title and download guidance were not confirmed.'
    }
    Assert-ImmutableAssets $published
    if (-not (Test-AssetBytes (Find-Asset $published 'timestamp.json') $stamp.hash $stamp.length)) { throw 'Published timestamp digest mismatch.' }
    foreach ($asset in @($published.assets | Where-Object { $_.name -ceq $pendingName -or $_.name -cmatch ('^' + [regex]::Escape($backupPrefix) + '[a-f0-9]{64}\.previous$') })) {
        Assert-StagingPublicationLease $leaseSha -Channel $Channel
        gh api --method DELETE "repos/$repo/releases/assets/$($asset.id)" --silent
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Publication succeeded, but a temporary timestamp asset needs cleanup.' }
    }
    @{ schema=1;channel=$Channel;publicLiveUpdates=($Channel -ceq 'stable');url=$published.html_url;bootstrapHash=$BootstrapHash;metadataVersion=$verified.version;assets=$verified.files.Count } |
        ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
    Write-Output $published.html_url
}
finally {
    foreach ($heldSource in $heldSources) { $heldSource.Dispose() }
    $resolved = [IO.Path]::GetFullPath($upload)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe upload cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
}
finally {
    Remove-Item -LiteralPath $notes -Force -ErrorAction SilentlyContinue
    if ($leaseSha -and -not $ExistingLeaseSha) { Exit-StagingPublicationLease $leaseSha -Channel $Channel }
}
