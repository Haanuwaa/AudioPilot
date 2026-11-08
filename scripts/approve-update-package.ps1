[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][ValidateSet('msi', 'portable')][string]$Kind,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-x86', 'win-arm64')][string]$Architecture,
    [Parameter(Mandatory)][ValidateSet('self-contained', 'framework-dependent')][string]$Runtime,
    [Parameter(Mandatory)][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$SignTool,
    [Parameter(Mandatory)][string]$OutputManifest,
    [string]$PayloadArchive,
    [uri]$TimestampUrl,
    [switch]$TestIdentity
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $Package).Path
$output = [IO.Path]::GetFullPath($OutputManifest)
if (Test-Path -LiteralPath $output) { throw 'Use a new approval manifest path.' }
if ([IO.Path]::GetExtension($path) -cne $(if ($Kind -eq 'msi') { '.msi' } else { '.zip' })) { throw 'Package extension does not match its deployment kind.' }
$sign = @{ CertificateThumbprint = $CertificateThumbprint; SignTool = $SignTool; VerifyOnly = $true; TestIdentity = $TestIdentity }
if ($TimestampUrl) { $sign.TimestampUrl = $TimestampUrl }
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
$publisherHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData))
$heldPackage = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $checks = @()
    if ($Kind -eq 'msi') {
        $checks += & (Join-Path $PSScriptRoot 'sign-update-artifacts.ps1') -Files $path @sign
        . (Join-Path $PSScriptRoot 'lib/Msi.ps1')
        Assert-AudioPilotMsiIdentity -Path $path -Version $Version -Architecture $Architecture.Substring(4)
        if ((Get-AudioPilotMsiProperty -Path $path -PropertyName ProductName) -cne 'AudioPilot') { throw 'Unexpected MSI product.' }
        if (-not $PayloadArchive) { throw 'MSI approval requires the exact separately approved portable ZIP.' }
        $payloadZip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PayloadArchive).Path)
        try { $flat = $null -ne $payloadZip.GetEntry('AudioPilot.exe') } finally { $payloadZip.Dispose() }
        Assert-AudioPilotMsiPayload -Path $path -PublishArchive (Resolve-Path -LiteralPath $PayloadArchive).Path -FlatArchive:$flat
    }
    else {
        Add-Type -AssemblyName System.IO.Compression
        $archive = [IO.Compression.ZipArchive]::new($heldPackage, [IO.Compression.ZipArchiveMode]::Read, $true)
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('AudioPilot-signature-check-' + [guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($temporary) | Out-Null
        try {
            if ($archive.Entries.Count -gt 2048) { throw 'Too many ZIP entries.' }
            $prefix = if ($null -ne $archive.GetEntry('AudioPilot/AudioPilot.exe')) { 'AudioPilot/' } else { '' }
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $archive.Entries) {
                if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'Mixed portable archive layouts.' }
                $relative = $entry.FullName.Substring($prefix.Length)
                if (-not $names.Add($relative) -or $relative -match '[\\:]|(^|/)\.\.?(/|$)|^/' -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) { throw 'Unsafe or duplicate ZIP entry.' }
                if ($relative -cnotmatch '^AudioPilot(?:\.[A-Za-z0-9_-]+)*\.(exe|dll)$') { continue }
                if ($entry.Length -le 0 -or $entry.Length -gt 64MB -or $checks.Count -ge 64) { throw 'Invalid first-party binary size or count.' }
                $destination = Join-Path $temporary $relative
                $input = $entry.Open(); $file = [IO.File]::Open($destination, [IO.FileMode]::CreateNew)
            try {
                $buffer = [byte[]]::new(65536); [long]$copied = 0
                while (($count = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $copied += $count
                    if ($copied -gt $entry.Length) { throw 'First-party ZIP entry expanded beyond its declared length.' }
                    $file.Write($buffer, 0, $count)
                }
                if ($copied -ne $entry.Length) { throw 'Truncated first-party ZIP entry.' }
            }
            finally { $file.Dispose(); $input.Dispose() }
                $checks += & (Join-Path $PSScriptRoot 'sign-update-artifacts.ps1') -Files $destination @sign
            }
            if (($Runtime -eq 'self-contained') -ne $names.Contains('coreclr.dll')) { throw 'ZIP runtime differs from its staging identity.' }
            foreach ($required in @('AudioPilot.exe', 'AudioPilot.dll', 'AudioPilot.Cli.exe', 'AudioPilot.Cli.dll', 'AudioPilot.UpdateProtocol.dll')) {
                if (-not $names.Contains($required) -or -not (Test-Path -LiteralPath (Join-Path $temporary $required))) { throw "Missing signed first-party file: $required" }
            }
            $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $temporary 'AudioPilot.exe')).FileVersion
            if ([version]$actualVersion -ne [version]"$Version.0") { throw 'ZIP executable version differs from its staging identity.' }
            $hostFile = [IO.File]::OpenRead((Join-Path $temporary 'AudioPilot.exe'))
            $reader = [Reflection.PortableExecutable.PEReader]::new($hostFile)
            try {
                $expectedMachine = @{ 'win-x64' = 'Amd64'; 'win-x86' = 'I386'; 'win-arm64' = 'Arm64' }[$Architecture]
                if ($reader.PEHeaders.CoffHeader.Machine.ToString() -cne $expectedMachine) { throw 'ZIP executable architecture differs from its staging identity.' }
            }
            finally { $reader.Dispose(); $hostFile.Dispose() }
        }
        finally {
            $archive.Dispose()
            $resolved = [IO.Path]::GetFullPath($temporary)
            if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe temporary signature directory.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    $identity = [ordered]@{
        product = 'AudioPilot'; channel = 'staging'; version = $Version; kind = $Kind; architecture = $Architecture
        runtime = $Runtime; minimumHelperVersion = '1.0.0'; windowsPublisherSha256 = $publisherHash
        windowsTrust = $(if ($TestIdentity) { 'self-signed-staging' } else { 'publicly-trusted' })
    }
    $heldPackage.Position = 0
    $packageHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($heldPackage))
    $approved = @{ schema = 1; channel = 'staging'; packages = @(@{ path = $path; name = [IO.Path]::GetFileName($path); sha256 = $packageHash; identity = $identity }) }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
    $approved | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding utf8NoBOM
    Write-Output @{ package = $path; checkedSignatures = $checks.Count; publisherSha256 = $publisherHash; testOnly = [bool]$TestIdentity; manifest = $output }
}
finally { $heldPackage.Dispose() }
