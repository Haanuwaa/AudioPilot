Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
$excludedSegment = '[\\/](?:\.git|artifacts|bin|obj|node_modules)[\\/]'
$markdownFiles = @(Get-ChildItem -Path $repoRoot -Recurse -File -Filter '*.md' |
    Where-Object { $_.FullName -notmatch $excludedSegment } |
    Sort-Object FullName)

function Get-MarkdownHeadingAnchors {
    param([string]$FilePath)

    $anchors = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $counts = @{}
    $inCodeFence = $false
    foreach ($line in [IO.File]::ReadLines($FilePath)) {
        if ($line -match '^\s*(`{3,}|~{3,})') {
            $inCodeFence = -not $inCodeFence
            continue
        }
        if ($inCodeFence) { continue }
        if ($line -match '^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$') {
            $heading = $matches[1]
            # Strip inline markdown formatting/links while keeping visible text
            $heading = [regex]::Replace($heading, '\[([^\]]+)\]\([^)]+\)', '$1')
            $heading = $heading.Replace('`', '').Replace('*', '').Replace('_', '').ToLowerInvariant().Trim()
            $slug = [regex]::Replace($heading, '[^a-z0-9\-\s]', '')
            $slug = [regex]::Replace($slug, '\s', '-')
            if ([string]::IsNullOrWhiteSpace($slug)) { continue }
            $key = $slug
            if ($counts.ContainsKey($slug)) {
                $counts[$slug]++
                $key = "$slug-$($counts[$slug])"
            }
            else {
                $counts[$slug] = 0
            }
            [void]$anchors.Add($key)
        }
    }
    return $anchors
}

$anchorCache = @{}
function Test-MarkdownAnchor {
    param([string]$FilePath, [string]$Anchor)

    $resolved = [IO.Path]::GetFullPath($FilePath)
    if (-not $anchorCache.ContainsKey($resolved)) {
        $anchorCache[$resolved] = Get-MarkdownHeadingAnchors -FilePath $resolved
    }
    return $anchorCache[$resolved].Contains($Anchor)
}

$linkRegex = [regex]'\[[^\]]+\]\((?!https?://|mailto:)([^)]+)\)'
$missing = New-Object System.Collections.Generic.List[object]

foreach ($file in $markdownFiles) {
    $rawContent = Get-Content -Path $file.FullName -Raw
    # Ignore links inside fenced code blocks
    $content = [regex]::Replace($rawContent, '(?ms)^\s*```.*?^\s*```', '')

    foreach ($match in $linkRegex.Matches($content)) {
        $rawTarget = $match.Groups[1].Value.Trim()
        if ([string]::IsNullOrWhiteSpace($rawTarget)) {
            continue
        }

        if ($rawTarget.StartsWith('<') -and $rawTarget.EndsWith('>')) {
            $rawTarget = $rawTarget.Trim('<', '>')
        }

        $parts = $rawTarget.Split('#', 2)
        $targetNoAnchor = $parts[0]
        $anchor = if ($parts.Length -eq 2) { $parts[1] } else { '' }

        $resolvedTarget = $null
        if ([string]::IsNullOrWhiteSpace($targetNoAnchor)) {
            $resolvedTarget = $file.FullName
        }
        else {
            $candidate = if ($targetNoAnchor.StartsWith('/')) {
                Join-Path -Path $repoRoot -ChildPath $targetNoAnchor.TrimStart('/')
            }
            else {
                Join-Path -Path $file.DirectoryName -ChildPath $targetNoAnchor
            }

            if (Test-Path -LiteralPath $candidate) {
                $resolvedTarget = (Resolve-Path -LiteralPath $candidate).Path
            }
            else {
                $missing.Add([pscustomobject]@{
                    File = $file.FullName
                    Link = $rawTarget
                    ResolvedPath = $candidate
                })
                continue
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($anchor) -and
            $resolvedTarget.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $resolvedTarget -PathType Leaf)) {
            if (-not (Test-MarkdownAnchor -FilePath $resolvedTarget -Anchor $anchor)) {
                $missing.Add([pscustomobject]@{
                    File = $file.FullName
                    Link = $rawTarget
                    ResolvedPath = "$resolvedTarget (missing anchor #$anchor)"
                })
            }
        }
    }
}

if ($missing.Count -gt 0) {
    Write-Host 'Missing markdown link targets found:' -ForegroundColor Red
    foreach ($entry in $missing) {
        Write-Host "- File: $($entry.File) | Link: $($entry.Link) | Resolved: $($entry.ResolvedPath)"
    }
    exit 1
}

Write-Host "Markdown links validated successfully ($($markdownFiles.Count) files)." -ForegroundColor Green
