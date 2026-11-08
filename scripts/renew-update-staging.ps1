[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$BootstrapHash,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('staging','stable')][string]$Channel = 'staging',
    [ValidateRange(1, 32)][long]$MinimumRootVersion = 1,
    [ValidateRange(0, 23)][int]$RenewBeforeHours = 0,
    [switch]$CheckOnly,
    [switch]$Publish
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$keyVariable = "UPDATE_$($Channel.ToUpperInvariant())_RENEWAL_KEYS"
$rawKeys = [Environment]::GetEnvironmentVariable($keyVariable, 'Process')
[Environment]::SetEnvironmentVariable($keyVariable, $null, 'Process')
$tag = "updater-$Channel-feed"
if ($CheckOnly) { $rawKeys = $null }
if ($CheckOnly -and $Publish) { throw 'Read-only health checks cannot publish.' }
$root = [IO.Path]::GetFullPath($OutputDirectory)
for ($current = $root; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected renewal path.' }
}
if (Test-Path -LiteralPath $root) { throw 'Choose a fresh renewal directory; never reset an existing checkpoint.' }
[IO.Directory]::CreateDirectory((Join-Path $root 'metadata')) | Out-Null
$tool = (Resolve-Path -LiteralPath $Publisher).Path
$publicationLease = $null
try {
if ($Publish) {
    . (Join-Path $PSScriptRoot 'lib/StagingPublicationLease.ps1')
    $publicationLease = Enter-StagingPublicationLease -Channel $Channel
}
$release = (& gh api "repos/Haanuwaa/AudioPilot/releases/tags/$tag") | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $release.draft -or -not $release.prerelease) { throw 'Cannot read the public staging prerelease.' }
$assets = @($release.assets | Where-Object { $_.name -clike '*.json' })
if ($assets.Count -lt 4 -or $assets.Count -gt 10000) { throw 'Unexpected staging metadata count; inspect feed retention.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($asset in $assets) {
    if (-not $seen.Add($asset.name) -or $asset.name -cnotmatch '^(bootstrap\.root\.json|timestamp\.json|[1-9][0-9]{0,5}\.(root|targets|snapshot)\.json)$' -or
        $asset.size -le 0 -or $asset.size -gt 131072 -or $asset.digest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'Unexpected staging metadata asset.' }
}
function Read-MetadataVersion($Value) {
    if (($Value -isnot [long] -and $Value -isnot [int]) -or $Value -lt 1 -or $Value -gt 1000000) { throw 'Invalid metadata reference version.' }
    return [long]$Value
}
function Get-StagingMetadata([string]$Name) {
    $matching = @($assets | Where-Object name -CEQ $Name)
    if ($matching.Count -ne 1) { throw 'A referenced metadata asset is missing.' }
    $asset = $matching[0]
    $relative = if ($Name -ceq 'bootstrap.root.json') { $Name } else { 'metadata/' + $Name }
    $path = Join-Path $root $relative
    & gh release download $tag --repo Haanuwaa/AudioPilot --pattern $asset.name --output $path
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.digest.Substring(7)) { throw 'Staging metadata download failed its digest check.' }
    return $path
}
$null = Get-StagingMetadata 'bootstrap.root.json'
$roots = @($assets | Where-Object name -CMatch '^[1-9][0-9]*\.root\.json$')
if ($roots.Count -gt 32) { throw 'Root-chain bound exceeded.' }
foreach ($asset in $roots) { $null = Get-StagingMetadata $asset.name }
$stampPath = Get-StagingMetadata 'timestamp.json'
$stamp = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json -AsHashtable
$stampVersion = Read-MetadataVersion $stamp.signed.version
$snapshotVersion = Read-MetadataVersion $stamp.signed.meta['snapshot.json'].version
$snapshotPath = Get-StagingMetadata "$snapshotVersion.snapshot.json"
$snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json -AsHashtable
$targetsVersion = Read-MetadataVersion $snapshot.signed.meta['targets.json'].version
$null = Get-StagingMetadata "$targetsVersion.targets.json"
# Read references only to select bounded filenames; go-tuf verifies all signatures and hashes before publication.
# Keep signed pre-commit versions so interrupted uploads do not cause a version collision on retry.
$orphans = @($assets | Where-Object { $_.name -cmatch '^([1-9][0-9]*)\.(snapshot|targets)\.json$' -and [long]$Matches[1] -gt $stampVersion })
if ($orphans.Count -gt 16) { throw 'Too many interrupted metadata versions; inspect publication history.' }
foreach ($asset in $orphans) { $null = Get-StagingMetadata $asset.name }
function Invoke-RenewalPublisher([hashtable]$Request) {
    $Request.channel = $Channel
    $start = [Diagnostics.ProcessStartInfo]::new($tool)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write(($Request | ConvertTo-Json -Depth 8 -Compress))
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(120000)) { $process.Kill($true); $process.WaitForExit(); throw 'Metadata renewal timed out.' }
        if ($process.ExitCode -ne 0) { throw ('Metadata verification or renewal failed: ' + $errors.GetAwaiter().GetResult()) }
        $result = $output.GetAwaiter().GetResult()
        if ($result.Length -gt 65536) { throw 'Oversized metadata result.' }
        return ($result | ConvertFrom-Json -AsHashtable)
    }
    finally { $process.Dispose() }
}
if (-not $CheckOnly) {
    $keys = $null
    $request = $null
    try {
        if (-not $rawKeys -or $rawKeys.Length -gt 1024) { throw 'Configure only snapshot and timestamp keys in the protected renewal environment.' }
        try { $keys = $rawKeys | ConvertFrom-Json -AsHashtable }
        catch { throw 'Invalid renewal key document.' }
        $rawKeys = $null
        if ($keys -isnot [hashtable] -or $keys.Count -ne 2 -or -not $keys.ContainsKey('snapshot') -or -not $keys.ContainsKey('timestamp')) { throw 'Renewal rejects root and targets signing material.' }
        $request = @{ operation = 'renew'; repository = $root; bootstrapHash = $BootstrapHash; keys = $keys; minimumRootVersion = $MinimumRootVersion; renewBeforeHours = $RenewBeforeHours }
        $renewal = Invoke-RenewalPublisher $request
    }
    finally {
        $rawKeys = $null
        if ($request) { $request.Remove('keys') }
        if ($keys -is [hashtable]) { $keys.Clear() }
    }
}
$verified = Invoke-RenewalPublisher @{ operation = 'check'; repository = $root; bootstrapHash = $BootstrapHash; metadataOnly = $true; minimumRootVersion = $MinimumRootVersion }
foreach ($target in $verified.artifacts) {
    $matching = @($release.assets | Where-Object name -CEQ $target.name)
    if ($matching.Count -ne 1 -or $matching[0].size -ne $target.length -or $matching[0].digest -cne "sha256:$($target.sha256)") { throw 'An approved staging package is missing or differs.' }
}
$now = [DateTimeOffset]::UtcNow
$warnings = @()
foreach ($role in @('root', 'targets', 'snapshot', 'timestamp')) {
    $value = $verified.expires[$role]
    $expiry = if ($value -is [DateTime]) { [DateTimeOffset]$value } else { [DateTimeOffset]::Parse($value, [Globalization.CultureInfo]::InvariantCulture) }
    $margin = switch ($role) { 'root' { 14 * 24 }; 'targets' { 7 * 24 }; 'snapshot' { 24 }; 'timestamp' { 12 } }
    if ($expiry -le $now.AddHours($margin)) { $warnings += "$role metadata needs attention before $($expiry.ToString('u'))." }
}
if ($Publish -and -not $renewal.unchanged) {
    & (Join-Path $PSScriptRoot 'publish-github-update-staging.ps1') -Channel $Channel -FeedDirectory $root -BootstrapHash $BootstrapHash -Publisher $tool -MetadataOnly -ReportPath (Join-Path $root 'publication.json') -ExistingLeaseSha $publicationLease
}
$action = if ($CheckOnly) { 'checked' } elseif ($renewal.unchanged) { 'unchanged' } else { 'renewed' }
$report = @{ schema = 1; channel = $Channel; publicLiveUpdates = ($Channel -ceq 'stable'); action = $action; metadataVersion = $verified.version; rootVersion = $verified.rootVersion; expires = $verified.expires; warnings = $warnings; checkedUtc = $now.ToString('O') }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'health.json') -Encoding utf8NoBOM
$report
if ($warnings.Count) { throw ($warnings -join ' ') }

}
finally {
    if ($publicationLease) { Exit-StagingPublicationLease $publicationLease -Channel $Channel }
}
