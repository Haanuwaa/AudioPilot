[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BootstrapRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$CandidateVersion = '1.0.2',
    [string]$IdentityDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AudioPilot.UpdateSigning/pilot-windows')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert-DirectPath([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected pilot policy path.' }
    }
}
$root = (Resolve-Path -LiteralPath $BootstrapRoot).Path
Assert-DirectPath $root
if ((Get-Item -LiteralPath $root).Length -gt 65536 -or (Get-FileHash -LiteralPath $root -Algorithm SHA256).Hash -cne '85D114D65F2367E8A587765BDF56580E08E9F4D0D644559C73B991209453F61F') { throw 'The independent staging bootstrap pin does not match.' }
$identityRoot = [IO.Path]::GetFullPath($IdentityDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
Assert-DirectPath $identityRoot; Assert-DirectPath $output
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh pilot policy output directory.' }
$receipt = Join-Path $identityRoot 'identity.json'
if (Test-Path -LiteralPath $receipt) {
    $saved = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($saved.schema -ne 1 -or $saved.thumbprint -cnotmatch '^[A-F0-9]{40}$') { throw 'Invalid existing pilot signing identity.' }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$($saved.thumbprint)"
} else {
    [IO.Directory]::CreateDirectory($identityRoot) | Out-Null
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=AudioPilot updater pilot' -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter ([DateTime]::Now.AddYears(1))
    @{schema=1;thumbprint=$certificate.Thumbprint;createdUtc=[DateTimeOffset]::UtcNow.ToString('O');store='CurrentUser/My';publicLiveUpdates=$false} | ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding utf8NoBOM
}
if (-not $certificate.HasPrivateKey -or $certificate.Subject -cne 'CN=AudioPilot updater pilot' -or $certificate.NotAfter.ToUniversalTime() -lt [DateTime]::UtcNow.AddDays(30)) { throw 'The pilot signing identity needs operator attention; do not silently replace a pinned key.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$publisher = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData))
@{Schema=1;CandidateVersion=$CandidateVersion;BootstrapRoot=[Convert]::ToBase64String([IO.File]::ReadAllBytes($root));PublisherSha256=$publisher} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'pilot-policy.json') -Encoding utf8NoBOM
[IO.File]::WriteAllBytes((Join-Path $output 'pilot-publisher.cer'),$certificate.RawData)
@{schema=1;certificateThumbprint=$certificate.Thumbprint;publisherSha256=$publisher;notAfterUtc=$certificate.NotAfter.ToUniversalTime().ToString('O');policy=Join-Path $output 'pilot-policy.json';privateKey='Windows CurrentUser certificate key store; not exported';trustedRootStoreChanged=$false;publicLiveUpdates=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'identity-receipt.json') -Encoding utf8NoBOM
Get-Content -LiteralPath (Join-Path $output 'identity-receipt.json')
