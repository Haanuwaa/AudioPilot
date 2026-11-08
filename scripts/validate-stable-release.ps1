[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseRoot,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/StableRelease.ps1')
$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../packaging/updater/stable/update-policy.json') -Raw | ConvertFrom-Json
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh stable release verification directory.' }
& (Join-Path $PSScriptRoot 'renew-update-staging.ps1') -Channel stable -Publisher $Publisher -BootstrapHash $policy.BootstrapSha256 -OutputDirectory $output -CheckOnly | Out-Null
$timestamp = Get-Content -LiteralPath (Join-Path $output 'metadata/timestamp.json') -Raw | ConvertFrom-Json -AsHashtable
$snapshot = Get-Content -LiteralPath (Join-Path $output "metadata/$($timestamp.signed.meta['snapshot.json'].version).snapshot.json") -Raw | ConvertFrom-Json -AsHashtable
$catalog = Get-Content -LiteralPath (Join-Path $output "metadata/$($snapshot.signed.meta['targets.json'].version).targets.json") -Raw | ConvertFrom-Json -AsHashtable
$version = (Get-Content -LiteralPath (Join-Path $ReleaseRoot 'release-manifest.json') -Raw | ConvertFrom-Json).version
$selected = @($catalog.signed.targets.GetEnumerator() | Where-Object { $_.Value.custom.version -ceq $version })
if ($selected.Count -ne 9) { throw "Stable feed must contain all nine variants for release $version." }
$paths = @{}
$arguments = @('release', 'download', 'updater-stable-feed', '--repo', 'Haanuwaa/AudioPilot', '--dir', (Join-Path $output 'packages'))
foreach ($target in $selected) {
    $name = "$($target.Value.hashes.sha256).$($target.Key)"
    if ($name -cnotmatch '^[a-f0-9]{64}\.[A-Za-z0-9][A-Za-z0-9._-]{0,159}$') { throw 'Unsafe approved stable target name.' }
    $arguments += @('--pattern', $name)
    $paths[$target.Key] = Join-Path $output "packages/$name"
}
& gh @arguments
if ($LASTEXITCODE -ne 0) { throw 'Could not download the nine approved stable packages.' }
$count = Assert-AudioPilotStableReleasePackages -ReleaseRoot $ReleaseRoot -Targets $catalog.signed.targets -PackagePaths $paths -PublisherSha256 $policy.PublisherSha256
@{schema=1;channel='stable';version=$version;packages=$count;metadataVersion=$timestamp.signed.version;bootstrapHash=$policy.BootstrapSha256;checkedUtc=[DateTimeOffset]::UtcNow.ToString('O')} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release-verification.json') -Encoding utf8NoBOM
Write-Output "All $count stable packages match release $version, including every portable payload file."
