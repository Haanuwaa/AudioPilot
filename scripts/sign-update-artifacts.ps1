[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Files,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$SignTool,
    [uri]$TimestampUrl,
    [switch]$TestIdentity,
    [switch]$VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Files.Count -eq 0) { throw 'Select explicit first-party binaries or MSI files.' }
if ($TestIdentity -and $env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Temporary signing identities are allowed only inside Windows Sandbox.' }
if (-not $TestIdentity -and (-not $TimestampUrl -or $TimestampUrl.Scheme -ne 'https' -or $TimestampUrl.UserInfo -or $TimestampUrl.Fragment)) {
    throw 'Production signing requires an explicit HTTPS RFC 3161 timestamp service.'
}
if ($TestIdentity -and $TimestampUrl) { throw 'The offline test identity must not contact a timestamp service.' }
$tool = (Resolve-Path -LiteralPath $SignTool).Path
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
$codeSigning = $false
foreach ($extension in $certificate.Extensions) {
    if ($extension -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
        foreach ($usage in $extension.EnhancedKeyUsages) {
            if ($usage.Value -eq '1.3.6.1.5.5.7.3.3') { $codeSigning = $true }
        }
    }
}
if (-not $codeSigning) { throw 'A code-signing certificate is required.' }
if (-not $TestIdentity -and ($certificate.Subject -eq $certificate.Issuer -or $certificate.Subject -like '*AudioPilot temporary*')) { throw 'A temporary or self-signed identity cannot sign production updates.' }
if (-not $VerifyOnly -and (-not $certificate.HasPrivateKey -or $certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow)) {
    throw 'The signing identity has no usable private key or is outside its validity period.'
}
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $Files) {
    $path = (Resolve-Path -LiteralPath $file).Path
    if ([IO.Path]::GetExtension($path) -notin @('.exe', '.dll', '.msi') -or -not $paths.Add($path)) { throw 'Select unique EXE, DLL or MSI files.' }
    for ($current = $path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Redirected signing input: $current" }
    }
}
foreach ($path in $paths) {
    if (-not $VerifyOnly) {
        $arguments = @('sign', '/q', '/s', 'My', '/sha1', $CertificateThumbprint, '/fd', 'SHA256')
        if (-not $TestIdentity) { $arguments += @('/tr', $TimestampUrl.AbsoluteUri, '/td', 'SHA256') }
        & $tool @arguments $path | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $path" }
    }
    & $tool verify /q /pa /all $path | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Windows signature verification failed: $path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
        throw "Unexpected signature or publisher: $path"
    }
    if (-not $TestIdentity -and $null -eq $signature.TimeStamperCertificate) { throw "A trusted timestamp is required: $path" }
    [pscustomobject]@{
        Path = $path; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        Publisher = $signature.SignerCertificate.Subject; Thumbprint = $signature.SignerCertificate.Thumbprint
        Timestamped = $null -ne $signature.TimeStamperCertificate; TestOnly = [bool]$TestIdentity
    }
}
