[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][ValidateSet('win-x86', 'win-x64', 'win-arm64')][string]$RuntimeIdentifier
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$expected = [IO.Path]::GetFullPath((Join-Path $repository "artifacts/publish/$RuntimeIdentifier"))
$destination = [IO.Path]::GetFullPath($PublishDir).TrimEnd('\', '/')
if (-not $destination.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
    throw "MSI publish cleanup is restricted to '$expected'. Use UsePrebuiltPublish=true for a custom, validated payload directory."
}
for ($directory = [IO.DirectoryInfo]::new($destination); $null -ne $directory; $directory = $directory.Parent) {
    if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "MSI publish cleanup does not follow directory links: $($directory.FullName)"
    }
}
if ([IO.File]::Exists($destination)) { throw 'The MSI staging path is a file.' }
if ([IO.Directory]::Exists($destination)) {
    foreach ($item in Get-ChildItem -LiteralPath $destination -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "MSI publish cleanup does not remove redirected entries: $($item.FullName)"
        }
    }
    Remove-Item -LiteralPath $destination -Recurse -Force
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Write-Host "Prepared owned MSI staging folder: $destination"
