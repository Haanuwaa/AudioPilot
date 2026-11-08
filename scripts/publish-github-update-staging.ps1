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
try {
$release = Read-StagingRelease
if ($null -eq $release) {
    if ($MetadataOnly) { throw 'Metadata renewal requires an existing staging release with approved packages.' }
    $notes = Join-Path ([IO.Path]::GetTempPath()) ('AudioPilot-staging-notes-' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        @'
Engineering staging feed for Windows Sandbox tests and explicitly packaged opt-in updater pilots.

These packages use a temporary self-signed Windows identity. Public live updates remain disabled. This prerelease is separate from stable AudioPilot downloads and must not be treated as a production update channel.

Only public TUF metadata and previously approved MSI/portable package bytes are uploaded. Private signing keys are not included.
'@ | Set-Content -LiteralPath $notes -Encoding utf8NoBOM
        $commit = (& git rev-parse origin/main).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the existing main revision.' }
        if ($Channel -ceq 'stable') { @'
Signed stable AudioPilot update feed. The release is a prerelease container so it does not replace the main application download.

Only approved MSI/portable packages and public TUF metadata are hosted here. Installing an update requires the user's Update and restart approval. Self-signed Windows publishers are pinned by the bootstrap; this does not establish public Windows trust.
'@ | Set-Content -LiteralPath $notes -Encoding utf8NoBOM }
        $title = if ($Channel -ceq 'stable') { 'AudioPilot stable update feed' } else { 'Updater engineering feed (opt-in pilot)' }
        & gh release create $tag --repo $repo --target $commit --draft --prerelease --latest=false --title $title --notes-file $notes
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the separate staging draft.' }
    }
    finally { Remove-Item -LiteralPath $notes -Force }
    $release = Read-StagingRelease
    if ($null -eq $release) { throw 'The new staging draft could not be read.' }
}
if (-not $release.prerelease) { throw 'Refusing to use a stable release as a staging feed.' }
if ($MetadataOnly -and $release.draft) { throw 'Metadata renewal requires an already public staging feed.' }
if ($MetadataOnly) {
    foreach ($target in $verified.artifacts) {
        $assets = @($release.assets | Where-Object name -CEQ $target.name)
        if ($assets.Count -ne 1 -or $assets[0].size -ne $target.length -or $assets[0].digest -cne "sha256:$($target.sha256)") { throw 'An approved staging package is missing or differs. Renewal cannot repair or replace package bytes.' }
    }
}
$upload = Join-Path ([IO.Path]::GetTempPath()) ('AudioPilot-staging-upload-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($upload) | Out-Null
try {
    $files = @($verified.files | Sort-Object { $_ -ceq 'metadata/timestamp.json' }, { $_ })
    foreach ($relative in $files) {
        $name = [IO.Path]::GetFileName($relative)
        if ($relative -cnotmatch '^(bootstrap\.root\.json|metadata/[A-Za-z0-9._-]+|targets/[A-Za-z0-9._-]+)$' -or $relative.Contains('..')) { throw 'Unsafe staging export path.' }
        $path = Join-Path $source $relative
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $existing = @($release.assets | Where-Object name -CEQ $name)
        if ($existing.Count -gt 1) { throw 'Duplicate staging assets.' }
        if ($existing.Count -eq 1 -and $name -cne 'timestamp.json') {
            if (-not $existing[0].digest -or $existing[0].digest -cne "sha256:$hash") { throw "Immutable staging asset differs or has no server digest: $name" }
            continue
        }
        if ($existing.Count -eq 1 -and $name -ceq 'timestamp.json') {
            $previous = Join-Path $upload 'previous-timestamp.json'
            & gh release download $tag --repo $repo --pattern timestamp.json --output $previous
            if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the published timestamp checkpoint.' }
            $prior = Get-Content -LiteralPath $previous -Raw | ConvertFrom-Json
            if ($prior.signed.version -gt $verified.version -or ($prior.signed.version -eq $verified.version -and
                (Get-FileHash -LiteralPath $previous -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash)) { throw 'Refusing a staging timestamp rollback or collision.' }
            if ($prior.signed.version -eq $verified.version) { continue }
        }
        $copy = Join-Path $upload $name
        Copy-Item -LiteralPath $path -Destination $copy
        if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw 'Staging bytes changed while preparing upload.' }
        $arguments = @('release', 'upload', $tag, '--repo', $repo, $copy)
        if ($name -ceq 'timestamp.json') { $arguments += '--clobber' }
        & gh @arguments
        if ($LASTEXITCODE -ne 0) { throw "Staging upload failed: $name" }
    }
    & gh release edit $tag --repo $repo --draft=false --prerelease --latest=false
    if ($LASTEXITCODE -ne 0) { throw 'Staging publication failed.' }
    $published = (& gh api "repos/$repo/releases/tags/$tag") | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $published.draft -or -not $published.prerelease) { throw 'Staging release state could not be confirmed.' }
    foreach ($relative in $verified.files) {
        $name = [IO.Path]::GetFileName($relative)
        $asset = @($published.assets | Where-Object name -CEQ $name)
        $hash = (Get-FileHash -LiteralPath (Join-Path $source $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($asset.Count -ne 1 -or $asset[0].digest -cne "sha256:$hash") { throw "Published asset digest mismatch: $name" }
    }
    @{ schema = 1; channel = $Channel; publicLiveUpdates = ($Channel -ceq 'stable'); url = $published.html_url; bootstrapHash = $BootstrapHash; metadataVersion = $verified.version; assets = $verified.files.Count } |
        ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
    Write-Output $published.html_url
}
finally {
    $resolved = [IO.Path]::GetFullPath($upload)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe upload cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
}
finally {
    if ($leaseSha -and -not $ExistingLeaseSha) { Exit-StagingPublicationLease $leaseSha -Channel $Channel }
}
