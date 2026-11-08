[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [Parameter(Mandatory)][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$SignTool,
    [uri]$TimestampUrl,
    [switch]$TestIdentity
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
$receipt = [IO.Path]::GetFullPath($ReceiptPath)
if ($receipt.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $receipt)) { throw 'Use a new signing receipt outside the package payload.' }
$files = @(Get-ChildItem -LiteralPath $payload -File | Where-Object { $_.Name -cmatch '^AudioPilot(?:\.[A-Za-z0-9_-]+)*\.(exe|dll)$' } | Sort-Object Name | ForEach-Object FullName)
foreach ($name in @('AudioPilot.exe', 'AudioPilot.dll', 'AudioPilot.Cli.exe', 'AudioPilot.Cli.dll', 'AudioPilot.UpdateProtocol.dll')) {
    if ((Join-Path $payload $name) -cnotin $files) { throw "Missing first-party payload file: $name" }
}
$arguments = @{ CertificateThumbprint = $CertificateThumbprint; SignTool = $SignTool; TestIdentity = $TestIdentity }
if ($TimestampUrl) { $arguments.TimestampUrl = $TimestampUrl }
$signed = @(& (Join-Path $PSScriptRoot 'sign-update-artifacts.ps1') -Files $files @arguments)
$record = @{ schema = 1; stage = 'first-party-payload'; testOnly = [bool]$TestIdentity; files = @($signed | ForEach-Object { @{ name = [IO.Path]::GetFileName($_.Path); sha256 = $_.Sha256; publisher = $_.Publisher; thumbprint = $_.Thumbprint; timestamped = $_.Timestamped } }) }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($receipt)) | Out-Null
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receipt -Encoding utf8NoBOM
Write-Output @{ receipt = $receipt; signed = $signed.Count; testOnly = [bool]$TestIdentity; next = 'Regenerate the payload manifest, package these exact bytes, sign the MSI, then approve the packages before TUF publishing.' }
