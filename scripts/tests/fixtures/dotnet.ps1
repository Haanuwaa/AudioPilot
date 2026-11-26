$arguments = @($args)
$index = [Array]::IndexOf($arguments, '--results-directory')
if ($index -lt 0) { throw 'The test runner did not specify an output directory.' }
$directory = [string]$arguments[$index + 1]
New-Item -ItemType Directory -Force -Path $directory | Out-Null
@{
    Arguments = $arguments
    Logging = $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING
    AudioHardware = $env:AUDIOPILOT_RUN_AUDIO_HARDWARE
    VisualWpf = $env:AUDIOPILOT_RUN_VISUAL_WPF
    ShowWindows = $env:AUDIOPILOT_TEST_SHOW_WINDOWS
    HardwareSoak = $env:AUDIOPILOT_RUN_HARDWARE_SOAK
} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directory 'record.json')
if ($env:AUDIOPILOT_SCRIPT_TEST_GATE_ROOT -and $arguments -match 'FirstConcurrentProbe') {
    Set-Content -LiteralPath (Join-Path $env:AUDIOPILOT_SCRIPT_TEST_GATE_ROOT 'started') -Value $directory
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $env:AUDIOPILOT_SCRIPT_TEST_GATE_ROOT 'release'))) {
        if ($watch.Elapsed.TotalSeconds -gt 10) { throw 'Concurrent test fixture timed out.' }
        Start-Sleep -Milliseconds 20
    }
}
$category = Split-Path -Leaf (Split-Path -Parent $directory)
if ($env:AUDIOPILOT_SCRIPT_TEST_MIXED_STREAMS -eq '1') {
    Write-Warning 'script warning sentinel'
    [Console]::Error.WriteLine('native stderr sentinel')
}
if ($env:AUDIOPILOT_SCRIPT_TEST_FAIL_CATEGORY -eq $category) { exit 7 }
$trxIndex = [Array]::IndexOf($arguments, '--report-xunit-trx-filename')
if ($env:AUDIOPILOT_SCRIPT_TEST_TRX -ne 'missing') {
    $mode = $env:AUDIOPILOT_SCRIPT_TEST_TRX
    $executed = if ($mode -eq 'skipped') { 0 } else { 1 }
    $passed = if ($mode -in @('skipped','failed-counter','incomplete')) { 0 } else { 1 }
    $failed = if ($mode -eq 'failed-counter') { 1 } else { 0 }
    $outcome = if ($mode -eq 'failed-outcome') { 'Failed' } else { 'Completed' }
    $total = if ($mode -eq 'not-runnable') { 2 } else { 1 }
    $extra = if ($mode -eq 'aborted') { "aborted='1'" } elseif ($mode -eq 'not-runnable') { "notRunnable='1'" } else { '' }
    Set-Content -LiteralPath (Join-Path $directory $arguments[$trxIndex + 1]) -Value "<TestRun><ResultSummary outcome='$outcome'><Counters total='$total' executed='$executed' passed='$passed' failed='$failed' $extra/></ResultSummary></TestRun>"
}
exit 0
