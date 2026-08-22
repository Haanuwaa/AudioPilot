param(
    [string]$SolutionPath = "AudioPilot.Format.slnf"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $SolutionPath)) {
    throw "Solution file not found: $SolutionPath"
}

function Get-ChangedFiles {
    $eventName = $env:GITHUB_EVENT_NAME

    if ($eventName -eq "pull_request") {
        $baseRef = $env:GITHUB_BASE_REF
        if ([string]::IsNullOrWhiteSpace($baseRef)) {
            throw "GITHUB_BASE_REF is required for pull_request events."
        }

        git fetch --no-tags --prune --depth=1 origin $baseRef | Out-Null
        return git diff --name-only --diff-filter=ACMRT "origin/$baseRef...HEAD"
    }

    $commitCount = 0
    try {
        $commitCountOutput = git rev-list --count HEAD 2>$null
        [int]::TryParse($commitCountOutput, [ref]$commitCount) | Out-Null
    }
    catch {
        $commitCount = 0
    }

    if ($commitCount -lt 2) {
        Write-Host "No prior commit available for comparison; skipping changed-files style check."
        return @()
    }

    return git diff --name-only --diff-filter=ACMRT HEAD~1...HEAD
}

$changed = @(Get-ChangedFiles)
if ($changed.Count -eq 0) {
    Write-Host "No changed files detected for scoped style check."
    exit 0
}

$include = @()
foreach ($path in $changed) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        continue
    }

    if ($path.EndsWith(".cs", [System.StringComparison]::OrdinalIgnoreCase)) {
        $include += $path
    }
}

if ($include.Count -eq 0) {
    Write-Host "No changed C# files detected; scoped style check skipped."
    exit 0
}

Write-Host "Scoped style check for changed files:"
foreach ($path in $include) {
    Write-Host " - $path"
}

dotnet format $SolutionPath --verify-no-changes --severity info --include $include
