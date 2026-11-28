$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:UPDATE_STAGING_HEARTBEAT_URL -or $env:UPDATE_STABLE_HEARTBEAT_URL) { throw 'Run this fixture without a real heartbeat URL.' }
$reporter = Join-Path $PSScriptRoot '../report-update-staging-health.ps1'
$privateUrl = 'https://hc-ping.com/01234567-89ab-4cde-8fab-0123456789ab'
$requests = [Collections.Generic.List[object]]::new()
$commands = [Collections.Generic.List[object]]::new()
function gh {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $global:LASTEXITCODE = 0
    $commands.Add($Arguments)
    if ($Arguments[0] -ne 'issue') { throw 'Unexpected GitHub boundary.' }
    if ($Arguments[1] -eq 'list') {
        if ($case.existing) { @(@{number=42;title="[Updater $channel] Feed maintenance needs attention"}) | ConvertTo-Json -AsArray }
        else { '[]' }
    }
    elseif ($Arguments[1] -notin @('create', 'close')) { throw 'Unexpected issue operation.' }
}
function Invoke-WebRequest {
    [CmdletBinding()]
    param([uri]$Uri, [string]$Method, [int]$MaximumRedirection, [int]$TimeoutSec)
    $requests.Add(@{ uri = $Uri.AbsoluteUri; method = $Method; redirects = $MaximumRedirection; timeout = $TimeoutSec })
    if ($case.networkFailure) { throw "Injected transport failure at $Uri" }
    [pscustomobject]@{ StatusCode = 200 }
}
$cases = @(
    @{ name = 'not configured'; recovered = $true; url = ''; calls = 0; existing = $false; close = 0; create = 0; fails = $false; networkFailure = $false },
    @{ name = 'healthy feed'; recovered = $true; url = $privateUrl; calls = 1; existing = $true; close = 1; create = 0; fails = $false; networkFailure = $false },
    @{ name = 'failed feed'; recovered = $false; url = $privateUrl; calls = 0; existing = $false; close = 0; create = 1; fails = $false; networkFailure = $false },
    @{ name = 'failed monitor'; recovered = $true; url = $privateUrl; calls = 1; existing = $true; close = 0; create = 0; fails = $true; networkFailure = $true },
    @{ name = 'healthy stable feed'; channel = 'stable'; recovered = $true; url = $privateUrl; calls = 1; existing = $true; close = 1; create = 0; fails = $false; networkFailure = $false },
    @{ name = 'failed stable feed'; channel = 'stable'; recovered = $false; url = $privateUrl; calls = 0; existing = $false; close = 0; create = 1; fails = $false; networkFailure = $false }
)
foreach ($url in @($privateUrl.Replace('https:', 'http:'), $privateUrl.Replace('hc-ping.com', 'example.com'), ($privateUrl + '?secret=private'), ($privateUrl + '/start'))) {
    $cases += @{ name = 'invalid endpoint'; recovered = $true; url = $url; calls = 0; existing = $false; close = 0; create = 1; fails = $true; networkFailure = $false }
}
try {
    foreach ($case in $cases) {
        $requests.Clear(); $commands.Clear()
        $channel = if ($case.ContainsKey('channel')) { $case.channel } else { 'staging' }
        $variable = "UPDATE_$($channel.ToUpperInvariant())_HEARTBEAT_URL"
        [Environment]::SetEnvironmentVariable($variable, $case.url, 'Process')
        $failure = $null
        try { & $reporter -Channel $channel -Recovered:($case.recovered) -RunUrl 'https://github.com/Haanuwaa/AudioPilot/actions/runs/1' }
        catch { $failure = $_.Exception.Message }
        if (($null -ne $failure) -ne $case.fails) { throw "Unexpected outcome: $($case.name)" }
        if ($requests.Count -ne $case.calls) { throw "Unexpected heartbeat count: $($case.name)" }
        if (@($commands | Where-Object { $_[1] -eq 'close' }).Count -ne $case.close -or @($commands | Where-Object { $_[1] -eq 'create' }).Count -ne $case.create) { throw "Incorrect alert handling: $($case.name)" }
        if ($failure -and ($failure.Contains('hc-ping.com') -or $failure.Contains('01234567-89ab'))) { throw 'Private monitor URL leaked.' }
        if ($env:UPDATE_STAGING_HEARTBEAT_URL -or $env:UPDATE_STABLE_HEARTBEAT_URL) { throw 'Heartbeat URL remained in the child environment.' }
        foreach ($request in $requests) {
            if ($request.uri -cne $privateUrl -or $request.method -cne 'Get' -or $request.redirects -ne 0 -or $request.timeout -ne 15) { throw 'Unsafe heartbeat request options.' }
        }
    }
    @{ passed = $true; cases = $cases.Count; realNetworkRequests = 0; privateUrlRedacted = $true } | ConvertTo-Json
}
finally { Remove-Item Env:UPDATE_STAGING_HEARTBEAT_URL,Env:UPDATE_STABLE_HEARTBEAT_URL -ErrorAction SilentlyContinue }
