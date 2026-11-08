[CmdletBinding()]
param(
    [switch]$Recovered,
    [ValidateSet('staging','stable')][string]$Channel = 'staging',
    [ValidatePattern('^https://github.com/Haanuwaa/AudioPilot/actions/runs/[0-9]+$')][string]$RunUrl
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$heartbeatVariable = "UPDATE_$($Channel.ToUpperInvariant())_HEARTBEAT_URL"
$heartbeat = [Environment]::GetEnvironmentVariable($heartbeatVariable, 'Process')
[Environment]::SetEnvironmentVariable($heartbeatVariable, $null, 'Process')
$heartbeatFailed = $false
if ($Recovered -and $heartbeat) {
    try {
        $uri = $null
        $check = [guid]::Empty
        if (-not [Uri]::TryCreate($heartbeat, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -cne 'https' -or $uri.Host -cne 'hc-ping.com' -or $uri.Port -ne 443 -or
            $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
            -not [guid]::TryParseExact($uri.AbsolutePath.TrimStart('/'), 'D', [ref]$check) -or $check -eq [guid]::Empty -or
            $uri.AbsolutePath -cne ('/' + $check.ToString('D'))) { throw 'Invalid independent heartbeat endpoint.' }
        $null = Invoke-WebRequest -Uri $uri -Method Get -MaximumRedirection 0 -TimeoutSec 15 -ErrorAction Stop
    }
    catch { $heartbeatFailed = $true; $Recovered = $false }
}
$title = "[Updater $Channel] Feed maintenance needs attention"
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
    & gh issue create --repo Haanuwaa/AudioPilot --title $title --body "The $Channel feed renewal, its expiry check or independent heartbeat failed. Inspect $RunUrl. Do not reset trust history or publish new package approvals to silence this alert. Installed applications remain usable; expired metadata must block update installation."
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the staging maintenance alert.' }
}
if ($heartbeatFailed) { throw 'The independent staging heartbeat failed. Feed renewal results remain in the preceding step. The private monitor URL is omitted.' }
