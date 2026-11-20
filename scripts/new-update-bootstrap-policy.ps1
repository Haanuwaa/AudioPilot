[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BootstrapRoot,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$BootstrapSha256,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{40}$')][string]$PublisherThumbprint,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$EnablePublicUpdates
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory, $repo)
if (-not $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep bootstrap policy outputs inside the repository.' }
foreach ($path in @($output, [IO.Path]::GetFullPath($BootstrapRoot, $repo))) {
    for ($current=$path; $current; $current=[IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected bootstrap policy path.' }
    }
}
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh policy output directory.' }
$root = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($BootstrapRoot, $repo))
if ($root.Length -gt 65536 -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($root)) -cne $BootstrapSha256) { throw 'Bootstrap root differs from its independently supplied pin.' }
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$PublisherThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.Subject -cne $certificate.Issuer -or $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow -or
    $certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or '1.3.6.1.5.5.7.3.3' -notin @($certificate.EnhancedKeyUsageList.ObjectId)) { throw 'Select a current self-signed code-signing identity with its private key.' }
$publisherHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData))
[IO.Directory]::CreateDirectory($output) | Out-Null
[ordered]@{
    Schema=1; CandidateVersion=$null; BootstrapRoot=[Convert]::ToBase64String($root); PublisherSha256=$publisherHash
    Channel='stable'; BootstrapSha256=$BootstrapSha256; PublicUpdatesEnabled=[bool]$EnablePublicUpdates
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'update-policy.json') -Encoding utf8NoBOM
[IO.File]::WriteAllBytes((Join-Path $output 'update-publisher.cer'), $certificate.RawData)
@{schema=1;certificateThumbprint=$certificate.Thumbprint;publisherSha256=$publisherHash;publicLiveUpdates=[bool]$EnablePublicUpdates} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'identity-receipt.json') -Encoding utf8NoBOM
Write-Host 'Created a pinned stable bootstrap policy and public certificate. No private key was exported.'
