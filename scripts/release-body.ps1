[CmdletBinding()]
param(
    [string]$ChecksumsPath = "artifacts/release/SHA256SUMS.txt",
    [string]$ManifestPath = "artifacts/release/release-manifest.json",
    [string]$Version,
    [string]$Repository = "Haanuwaa/AudioPilot",
    [string]$ChangelogPath = "docs/CHANGELOG.md",
    [string]$OutputPath = "artifacts/release-notes.md",
    [switch]$ChecksumTable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-ChangelogReleaseSection {
    param(
        [string]$Path,
        [string]$Version
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $escapedVersion = [regex]::Escape($Version)
    $headerPattern = "^##\s+\[$escapedVersion\]\s+-\s+(?<date>\d{4}-\d{2}-\d{2})\s*$"
    $nextHeaderPattern = "^##\s+\[.+\]"
    $currentSubheading = $null
    $capturing = $false
    $releaseDate = $null
    $entries = @()

    foreach ($line in (Get-Content -LiteralPath $Path)) {
        if (-not $capturing) {
            $headerMatch = [regex]::Match($line, $headerPattern)
            if ($headerMatch.Success) {
                $capturing = $true
                $releaseDate = $headerMatch.Groups["date"].Value
            }

            continue
        }

        if ([regex]::IsMatch($line, $nextHeaderPattern)) {
            break
        }

        $subheadingMatch = [regex]::Match($line, "^###\s+(?<title>.+?)\s*$")
        if ($subheadingMatch.Success) {
            $currentSubheading = $subheadingMatch.Groups["title"].Value.Trim()
            continue
        }

        $bulletMatch = [regex]::Match($line, "^- (?<text>.+)$")
        if ($bulletMatch.Success) {
            $entries += [pscustomobject]@{
                Section = $currentSubheading
                Text = $bulletMatch.Groups["text"].Value.Trim()
            }
        }
    }

    if (-not $capturing) {
        return $null
    }

    return [pscustomobject]@{
        Date = $releaseDate
        Entries = $entries
    }
}

if (-not (Test-Path -LiteralPath $ChecksumsPath)) {
    throw "Checksums file not found: $ChecksumsPath"
}

function Get-ChecksumRows {
    param([string]$Path)

    $lines = Get-Content -LiteralPath $Path | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if ($lines.Count -eq 0) {
        throw "No checksum entries found in: $Path"
    }

    $rows = foreach ($line in $lines) {
        if ($line -match '^(?<hash>[a-fA-F0-9]{64})\s+\*(?<file>.+)$') {
            [pscustomobject]@{
                File = $matches.file.Trim()
                Hash = $matches.hash.ToLowerInvariant()
            }
        }
    }

    if ($null -eq $rows -or $rows.Count -eq 0) {
        throw "Could not parse checksum lines from: $Path"
    }

    return @($rows)
}

if ($ChecksumTable) {
    Write-Output "| Artifact | SHA256 |"
    Write-Output "|---|---|"
    foreach ($row in (Get-ChecksumRows -Path $ChecksumsPath)) {
        Write-Output "| $($row.File) | $($row.Hash) |"
    }

    exit 0
}

if (Test-Path -LiteralPath $ChecksumsPath -PathType Container) {
    throw "Checksums path points to a directory, expected a file: $ChecksumsPath"
}

$resolvedVersion = $Version
if ([string]::IsNullOrWhiteSpace($resolvedVersion)) {
    if (Test-Path -LiteralPath $ManifestPath) {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace($manifest.version)) {
            $resolvedVersion = $manifest.version
        }
    }
}

if ([string]::IsNullOrWhiteSpace($resolvedVersion)) {
    throw "Version not provided and could not be inferred from manifest: $ManifestPath"
}

$manifest = $null
if (Test-Path -LiteralPath $ManifestPath) {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
}

$changelogRelease = Get-ChangelogReleaseSection -Path $ChangelogPath -Version $resolvedVersion
$releaseDate = $null
if ($null -ne $changelogRelease) {
    $releaseDate = $changelogRelease.Date
}

if ([string]::IsNullOrWhiteSpace($releaseDate)) {
    $releaseDate = (Get-Date).ToString("yyyy-MM-dd")
}

$highlightLines = @()
if ($null -ne $changelogRelease) {
    foreach ($entry in $changelogRelease.Entries) {
        $prefix = if ([string]::IsNullOrWhiteSpace($entry.Section)) { "" } else { "$($entry.Section): " }
        $highlightLines += "- $prefix$($entry.Text)"
    }
}

if ($highlightLines.Count -eq 0) {
    $highlightLines += "- Add release highlights here before publishing."
    $highlightLines += "- Keep this section short and user-facing."
}

$projectChangelogUrl = "https://github.com/$Repository/blob/main/docs/CHANGELOG.md"
$fullChangelogUrl = "https://github.com/$Repository/commits/v$resolvedVersion"

$outLines = @()
$outLines += "# Release v$resolvedVersion"
$outLines += ""
$outLines += "> **Release Date:** $releaseDate"
$outLines += ""
$outLines += "## Highlights"
$outLines += ""
foreach ($highlightLine in $highlightLines) {
    $outLines += $highlightLine
}
$outLines += ""
$outLines += "## Installers"
$outLines += ""
$outLines += ('- **Recommended for most users**: `AudioPilot-{0}-SelfContained-win-x64.zip`' -f $resolvedVersion)
$outLines += ('- **Recommended for Windows on ARM**: `AudioPilot-{0}-SelfContained-win-arm64.zip`' -f $resolvedVersion)
$outLines += ('- **MSI installers**: `AudioPilot-{0}-x64.msi` and `AudioPilot-{0}-arm64.msi` remain available as an alternate install path.' -f $resolvedVersion)
$outLines += '- **Target framework**: `net10.0-windows10.0.19041.0`'
$outLines += '- **Minimum compatible OS**: Windows 10 version 2004 (build 19041) or later.'
$outLines += '- **Official OS support**: Windows 11 and currently supported Windows 10 Enterprise/LTSC editions that meet the minimum build, subject to the Microsoft and .NET 10 support lifecycles.'
$outLines += ""

$outLines += "## Build Types"
$outLines += ""
$outLines += "- **MSI**: per-user Windows installer with Start Menu and desktop shortcuts."
$outLines += "- **Framework-Dependent ZIP**: smaller download; requires the .NET 10 Desktop Runtime on Windows."
$outLines += "- **Self-Contained ZIP**: larger download; includes the .NET runtime and runs without a separate .NET install."
$outLines += ""
$outLines += "## SHA256 Checksums"
$outLines += ""
$outLines += 'See `SHA256SUMS.txt` in the release assets for the full checksum list covering published MSI, ZIP, SBOM, provenance, and release manifest assets.'
$outLines += ""
$outLines += "## Supply Chain Metadata"
$outLines += ""
$outLines += '- `release-manifest.json`: machine-readable release artifact inventory.'
$outLines += ('- `AudioPilot-{0}-sbom.spdx.json`: SPDX 2.3 SBOM for shipped artifacts and NuGet lock-file dependencies.' -f $resolvedVersion)
$outLines += ('- `AudioPilot-{0}-provenance.json`: local build provenance metadata for the packaged release.' -f $resolvedVersion)
$outLines += "- GitHub artifact attestations are generated by the release workflow for the checksum subjects when the release is built in GitHub Actions."
$outLines += ""
$outLines += "## Project Changelog"
$outLines += ""
$outLines += "[View CHANGELOG.md]($projectChangelogUrl)"
$outLines += ""
$outLines += "## Full Changelog"
$outLines += ""
$outLines += "[Commits in v$resolvedVersion]($fullChangelogUrl)"

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$outLines | Set-Content -Path $OutputPath -Encoding UTF8
Write-Host "Release notes written to: $OutputPath"
