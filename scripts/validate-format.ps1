param(
    [ValidateSet("check", "fix")]
    [string]$Action = "check",
    [string]$SolutionPath = "AudioPilot.Format.slnf",
    [switch]$ChangedOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $SolutionPath)) {
    throw "Solution file not found: $SolutionPath"
}

$formatParameters = @($SolutionPath, "--severity", "info")
if ($Action -eq "check") {
    $formatParameters += "--verify-no-changes"
}

if ($ChangedOnly) {
    if ($env:GITHUB_EVENT_NAME -eq 'pull_request') {
        $baseRef = $env:GITHUB_BASE_REF
        if ([string]::IsNullOrWhiteSpace($baseRef)) {
            throw 'GITHUB_BASE_REF is required for pull_request events.'
        }
        & git fetch --no-tags origin $baseRef
        if ($LASTEXITCODE -ne 0) { throw 'Failed to fetch the pull-request base.' }
        $changed = @(& git -c core.quotepath=false diff --name-only --diff-filter=ACMRT "FETCH_HEAD...HEAD")
        if ($LASTEXITCODE -ne 0) { throw 'Failed to determine pull-request changes.' }
    }
    else {
        $changed = @(& git -c core.quotepath=false diff --name-only --diff-filter=ACMRT HEAD)
        if ($LASTEXITCODE -ne 0) { throw 'Failed to determine working-tree changes.' }
        $changed += @(& git -c core.quotepath=false ls-files --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) { throw 'Failed to determine untracked files.' }
    }

    $include = @($changed | Where-Object { $_.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Sort-Object -Unique)
    if ($include.Count -eq 0) {
        Write-Host 'No changed C# files; scoped formatting skipped.'
        exit 0
    }
    $formatParameters += '--include'
    $formatParameters += $include
}

& dotnet format @formatParameters
exit $LASTEXITCODE
