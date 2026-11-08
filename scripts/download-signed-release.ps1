[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ChecksumsSha256,
    [string]$Repository='Haanuwaa/AudioPilot',
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedCommit,
    [string]$OutputDirectory='artifacts/signed-release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'lib/GitHubRelease.ps1')
$release = Assert-AudioPilotReleaseMutable -Repository $Repository -Version $Version -RequireDraft -ExpectedCommit $ExpectedCommit
foreach ($asset in $release.assets) {
    $maximum = if ($asset.name -match '\.(zip|msi)$') { 1GB } else { 16MB }
    if ($asset.size -le 0 -or $asset.size -gt $maximum -or ($asset.name -ceq 'SHA256SUMS.txt' -and $asset.size -gt 65536)) { throw 'Signed draft asset exceeds its bound.' }
}
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory,$repoRoot)
if (-not $output.StartsWith((Join-Path $repoRoot 'artifacts')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) { throw 'Use a new directory inside artifacts for signed release inputs.' }
for ($current=$output; $current; $current=[IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected release download directory.' }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
& gh release download "v$Version" --repo $Repository --pattern SHA256SUMS.txt --dir $output
if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath (Join-Path $output 'SHA256SUMS.txt')).Hash -ine $ChecksumsSha256) { throw 'The draft checksum file differs from the explicitly approved digest.' }
$expected=@('release-manifest.json',"AudioPilot-$Version-provenance.json","AudioPilot-$Version-sbom.spdx.json")
foreach ($arch in @('x86','x64','arm64')) {
    $expected += "AudioPilot-$Version-$arch.msi"
    foreach ($mode in @('SelfContained','FrameworkDependent')) { $expected += "AudioPilot-$Version-$mode-win-$arch.zip" }
}
foreach ($name in $expected) {
    & gh release download "v$Version" --repo $Repository --pattern $name --dir $output
    if ($LASTEXITCODE -ne 0) { throw "Signed draft is incomplete: $name" }
}
Assert-AudioPilotRemoteReleaseHashes -Release $release -Directory $output
& (Join-Path $PSScriptRoot '../packaging/winget/generate-winget-manifest.ps1') -Version $Version -RepositoryOwner $Repository.Split('/')[0] -RepositoryName $Repository.Split('/')[1] `
    -OutputRoot (Join-Path $output 'winget') -X86InstallerPath (Join-Path $output "AudioPilot-$Version-x86.msi") -X64InstallerPath (Join-Path $output "AudioPilot-$Version-x64.msi") -Arm64InstallerPath (Join-Path $output "AudioPilot-$Version-arm64.msi")
Write-Output 'Downloaded the explicitly approved signed draft; full payload, signature, provenance, SBOM and installer validation is still required.'
