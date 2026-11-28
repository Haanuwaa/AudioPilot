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

function Get-ChangelogReleaseDate {
    param(
        [string]$Path,
        [string]$Version
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $escapedVersion = [regex]::Escape($Version)
    $headerPattern = "^##\s+\[$escapedVersion\]\s+-\s+(?<date>\d{4}-\d{2}-\d{2})\s*$"
    foreach ($line in (Get-Content -LiteralPath $Path)) {
        $headerMatch = [regex]::Match($line, $headerPattern)
        if ($headerMatch.Success) { return $headerMatch.Groups["date"].Value }
    }
    return $null
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

$releaseDate = Get-ChangelogReleaseDate -Path $ChangelogPath -Version $resolvedVersion
if ([string]::IsNullOrWhiteSpace($releaseDate)) {
    $releaseDate = (Get-Date).ToString("yyyy-MM-dd")
}

$fullChangelogUrl = "https://github.com/$Repository/commits/v$resolvedVersion"

$outLines = @()
$outLines += "# AudioPilot - $resolvedVersion"
$outLines += ""
$outLines += "> **Release Date:** $releaseDate"
$outLines += ""
$outLines += "AudioPilot is a free, open-source Windows app for audio controls and automation."
$outLines += ""
$outLines += "## Features"
$outLines += ""
$outLines += '- **Device switching** from the keyboard, tray, or command line. Use the Device Picker to select a saved or available'
$outLines += '  output or input, with reconnect support for compatible Bluetooth devices.'
$outLines += '- **Mixers and per-app routing** for playback and recording. Adjust volume and mute, choose devices per app,'
$outLines += '  and use Quick Mixer for live signal meters, app pins, and a Playing first sort on Output.'
$outLines += '- **Global shortcuts** for output, input, and foreground-app volume and mute. Includes Deafen, push-to-talk,'
$outLines += '  hold-to-mute, and media controls for supported players.'
$outLines += '- **Routines** that switch devices and apply volume or mute settings on demand or when apps, devices, Wi-Fi'
$outLines += '  networks, or schedules match.'
$outLines += '- **Audio tests and monitoring** for speakers, headphones, and microphones, with live mic levels and a choice of'
$outLines += '  monitoring output.'
$outLines += '- **Desktop controls** with tray and taskbar Jump List access, optional on-screen feedback, light and dark themes,'
$outLines += '  and live switching between English and Spanish.'
$outLines += '- **Settings and diagnostics** with optional auto-save, settings import and export, redacted support bundles,'
$outLines += '  and a scriptable CLI with JSON output.'
$outLines += '- **Integrated updates** with one **Update and restart** approval. AudioPilot stays open during download and'
$outLines += '  verification, then saves, installs, and restarts with recovery and microphone safeguards.'
$outLines += ""
$outLines += "[Setup and user guide](https://github.com/$Repository/blob/v$resolvedVersion/docs/USER_GUIDE.md) · [CLI reference](https://github.com/$Repository/blob/v$resolvedVersion/docs/CLI.md)"
$outLines += ""
$outLines += "## Downloads"
$outLines += ""
$downloadBaseUrl = "https://github.com/$Repository/releases/download/v$resolvedVersion"
$outLines += "- **Most Windows PCs:** [Download x64 MSI]($downloadBaseUrl/AudioPilot-$resolvedVersion-x64.msi)"
$outLines += "- **Windows on ARM:** [Download ARM64 MSI]($downloadBaseUrl/AudioPilot-$resolvedVersion-arm64.msi)"
$outLines += "- **32-bit Windows:** [Download x86 MSI]($downloadBaseUrl/AudioPilot-$resolvedVersion-x86.msi)"
$outLines += '- All three installers include the runtime and install for your Windows account.'
$outLines += '- **Smaller portable download:** [x64 ZIP][framework-x64] | [ARM64 ZIP][framework-arm64] | [x86 ZIP][framework-x86]'
$outLines += '  Requires the matching **.NET 11 Desktop Runtime** already installed. These ZIPs use the shared runtime.'
$outLines += '- **Portable copy:** choose a `SelfContained` ZIP for your architecture from the assets below.'
$outLines += '  Extract the archive, open its `AudioPilot` folder and run `AudioPilot.exe`. Keep `app` and `data` beside the launchers.'
$outLines += '- **Minimum compatible OS**: Windows 10 version 2004 (build 19041) or later.'
$outLines += '- **CPU**: .NET 11 baseline; x86/x64 requires SSE4.2 and POPCNT.'
$outLines += '- **Official OS support**: Windows 11 and currently supported Windows 10 Enterprise/LTSC editions that meet the'
$outLines += '  minimum build, subject to the Microsoft and .NET 11 support lifecycles.'
$outLines += ""
$outLines += "[framework-x64]: $downloadBaseUrl/AudioPilot-$resolvedVersion-FrameworkDependent-win-x64.zip"
$outLines += "[framework-arm64]: $downloadBaseUrl/AudioPilot-$resolvedVersion-FrameworkDependent-win-arm64.zip"
$outLines += "[framework-x86]: $downloadBaseUrl/AudioPilot-$resolvedVersion-FrameworkDependent-win-x86.zip"
$outLines += ""

$outLines += "**Windows download warnings:** AudioPilot has no publicly trusted signing identity. Windows may show an unknown-publisher"
$outLines += "or SmartScreen warning. After checking the source, use **Properties > Unblock** if shown, before extracting ZIPs."
$outLines += "[Windows download guidance](https://github.com/$Repository/blob/v$resolvedVersion/docs/USER_GUIDE.md#unsigned-downloads-and-windows-warnings)"
$outLines += ""
$outLines += "### Choosing A Package"
$outLines += ""
$outLines += "- **MSI**: per-user Windows installer with the runtime included, Start Menu and desktop shortcuts, and an optional"
$outLines += "  CLI PATH entry."
$outLines += "- **Framework-Dependent ZIP**: smaller download; requires the .NET 11 Desktop Runtime on Windows."
$outLines += "- **Self-Contained ZIP**: larger download; includes the .NET runtime and runs without a separate .NET install."
$outLines += ""
$outLines += "## SHA256 Checksums"
$outLines += ""
$outLines += 'See `SHA256SUMS.txt` in the release assets for the full checksum list covering published MSI, ZIP, SBOM, provenance,'
$outLines += 'and release manifest assets.'
$outLines += ""
$outLines += "## Supply Chain Metadata"
$outLines += ""
$outLines += '- `release-manifest.json`: machine-readable release artifact inventory.'
$outLines += ('- `AudioPilot-{0}-sbom.spdx.json`: SPDX 3.0.1 JSON-LD SBOM for shipped artifacts, NuGet lock-file dependencies, and Go updater modules.' -f $resolvedVersion)
$outLines += ('- `AudioPilot-{0}-provenance.json`: source commit and build provenance for the packaged release.' -f $resolvedVersion)
$outLines += "- GitHub artifact attestations accompany releases built in GitHub Actions."
$outLines += ""
$outLines += "## Changelog"
$outLines += ""
$changelogRef = "v$resolvedVersion"
if ($resolvedVersion -eq '1.0.0') {
    $outLines += "Initial release of AudioPilot."
    $outLines += ""
    $changelogRef = 'main'
}
$outLines += "[View CHANGELOG.md](https://github.com/$Repository/blob/$changelogRef/docs/CHANGELOG.md)"
$outLines += "[Commits in v$resolvedVersion]($fullChangelogUrl)"

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$outLines | Set-Content -Path $OutputPath -Encoding UTF8
Write-Host "Release notes written to: $OutputPath"
