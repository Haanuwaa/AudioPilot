[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$IncludeTools
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
if (-not (Test-Path -LiteralPath $artifactsRoot -PathType Container)) {
    Write-Host 'No artifacts directory found.'
    exit 0
}

$artifactsItem = Get-Item -LiteralPath $artifactsRoot -Force
if ($artifactsItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw "Refusing to clean redirected artifacts root: $artifactsRoot"
}

$entries = @(Get-ChildItem -LiteralPath $artifactsRoot -Force)
$removed = 0
$reclaimedBytes = [long]0

foreach ($entry in $entries) {
    if (-not $IncludeTools -and $entry.Name -ieq 'tools') {
        continue
    }

    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        Write-Warning "Skipping reparse point: $($entry.FullName)"
        continue
    }

    $linkedChild = if ($entry.PSIsContainer) {
        Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
            Select-Object -First 1
    }
    else { $null }

    if ($null -ne $linkedChild) {
        Write-Warning "Skipping directory containing reparse point: $($entry.FullName)"
        continue
    }

    $sizeBytes = if ($entry.PSIsContainer) {
        [long]((Get-ChildItem -LiteralPath $entry.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum)
    }
    else {
        [long]$entry.Length
    }

    if ($PSCmdlet.ShouldProcess($entry.FullName, 'Remove local artifact')) {
        Remove-Item -LiteralPath $entry.FullName -Recurse -Force
        $removed++
        $reclaimedBytes += $sizeBytes
    }
}

$reclaimedMiB = [Math]::Round($reclaimedBytes / 1MB, 1)
Write-Host "Removed $removed local artifact entry/entries ($reclaimedMiB MiB reclaimed; tools preserved: $(-not $IncludeTools))."
