[CmdletBinding()]
param(
    [switch]$Recovered,
    [ValidatePattern('^https://github.com/Haanuwaa/AudioPilot/actions/runs/[0-9]+$')][string]$RunUrl
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$title = '[Updater staging] Feed maintenance needs attention'
$issues = @(& gh issue list --repo Haanuwaa/AudioPilot --state open --author 'github-actions[bot]' --search ('in:title "' + $title + '"') --json number,title | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect staging maintenance alerts.' }
$matching = @($issues | Where-Object title -CEQ $title)
if ($Recovered) {
    foreach ($issue in $matching) {
        & gh issue close $issue.number --repo Haanuwaa/AudioPilot
        if ($LASTEXITCODE -ne 0) { throw 'Cannot close the resolved staging alert.' }
    }
}
elseif ($matching.Count -eq 0) {
    if (-not $RunUrl) { throw 'A failed maintenance run URL is required.' }
    & gh issue create --repo Haanuwaa/AudioPilot --title $title --body "Staging metadata renewal or its expiry check failed. Inspect $RunUrl. Public live updates remain disabled. Do not reset trust history or publish new package approvals to silence this alert."
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the staging maintenance alert.' }
}
