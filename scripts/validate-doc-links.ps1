$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$markdownFiles = @()
$markdownFiles += Get-ChildItem -Path (Join-Path $repoRoot 'docs') -Recurse -File -Filter '*.md'
$readmePath = Join-Path $repoRoot 'README.md'
if (Test-Path $readmePath) {
    $markdownFiles += Get-Item $readmePath
}

$linkRegex = [regex]'\[[^\]]+\]\((?!https?://|mailto:|#)([^)]+)\)'
$missing = New-Object System.Collections.Generic.List[object]

foreach ($file in $markdownFiles) {
    $content = Get-Content -Path $file.FullName -Raw

    foreach ($match in $linkRegex.Matches($content)) {
        $rawTarget = $match.Groups[1].Value.Trim()
        if ([string]::IsNullOrWhiteSpace($rawTarget)) {
            continue
        }

        if ($rawTarget.StartsWith('<') -and $rawTarget.EndsWith('>')) {
            $rawTarget = $rawTarget.Trim('<', '>')
        }

        $targetNoAnchor = $rawTarget.Split('#')[0]
        if ([string]::IsNullOrWhiteSpace($targetNoAnchor)) {
            continue
        }

        $targetPathRelative = Join-Path -Path $file.DirectoryName -ChildPath $targetNoAnchor
        $targetPathFromRoot = Join-Path -Path $repoRoot -ChildPath $targetNoAnchor

        $exists = (Test-Path -LiteralPath $targetPathRelative) -or (Test-Path -LiteralPath $targetPathFromRoot)
        if (-not $exists) {
            $missing.Add([pscustomobject]@{
                File = $file.FullName
                Link = $rawTarget
                ResolvedPath = "$targetPathRelative | $targetPathFromRoot"
            })
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

Write-Host 'Markdown links validated successfully.' -ForegroundColor Green
