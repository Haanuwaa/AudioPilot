param(
    [string]$AppPath = 'AudioPilot/bin/Release/net10.0-windows10.0.19041.0/AudioPilot.exe',
    [string]$CliPath = 'AudioPilot.CliHost/bin/Release/net10.0-windows10.0.19041.0/AudioPilot.Cli.exe',
    [ValidateRange(1, 100)][int]$Iterations = 3,
    [ValidateRange(1, 30)][int]$Cycles = 3,
    [ValidateRange(5, 120)][int]$TimeoutSeconds = 30,
    [string]$OutputDirectory = 'artifacts/benchmarks/application',
    [string]$BaselinePath,
    [switch]$AllowDesktopInteraction
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $AllowDesktopInteraction) { throw 'This scenario opens test windows. Pass -AllowDesktopInteraction on an interactive Windows desktop.' }
if (-not $IsWindows) { throw 'The application smoke scenario requires Windows.' }
$AppPath = (Resolve-Path -LiteralPath $AppPath).Path
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path
$appAssembly = [IO.Path]::ChangeExtension($AppPath, '.dll')
$cliAppAssembly = Join-Path ([IO.Path]::GetDirectoryName($CliPath)) 'AudioPilot.dll'
$appHash = (Get-FileHash -LiteralPath $appAssembly -Algorithm SHA256).Hash
if ($appHash -ne (Get-FileHash -LiteralPath $cliAppAssembly -Algorithm SHA256).Hash) { throw 'GUI and CLI must use the same AudioPilot assembly.' }
$baseline = $null
if ($BaselinePath) {
    $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
    if ($baseline.SchemaVersion -ne 1 -or -not $baseline.Passed -or $baseline.Scenario -ne 'tray-cli-show-refresh-hide-close' -or $baseline.Cycles -ne $Cycles) { throw 'Baseline scenario does not match.' }
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$runDirectory = Join-Path $OutputDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null

if (-not ('AudioPilotSmokeWindowProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AudioPilotSmokeWindowProbe {
    private delegate bool EnumProc(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder title, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static IntPtr Find(int process, bool visible) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, state) => {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != process) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "AudioPilot") return true;
            if (visible && (!IsWindowVisible(window) || IsIconic(window) || MonitorFromWindow(window, 0) == IntPtr.Zero)) return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }
    public static bool Close(int process) {
        IntPtr window = Find(process, false);
        return window != IntPtr.Zero && PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }
}
'@
}

function New-ScenarioStartInfo([string]$Executable, [string]$Session, [string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $info.Environment['AUDIOPILOT_DIAGNOSTIC_SESSION'] = $Session
    $info.Environment['AUDIOPILOT_DISABLE_CONSOLE_LOGGING'] = '1'
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    return $info
}

function Invoke-ScenarioCli([string]$Session, [string[]]$Arguments, [switch]$ExpectFailure) {
    $info = New-ScenarioStartInfo $CliPath $Session $Arguments
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $info
    $childStarted = $false
    try {
        $childStarted = $child.Start()
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit($TimeoutSeconds * 1000)) { throw "CLI request timed out: $($Arguments -join ' ')" }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        if ($ExpectFailure -and $child.ExitCode -eq 0) { throw "CLI unexpectedly accepted a prohibited operation." }
        if (-not $ExpectFailure -and $child.ExitCode -ne 0) { throw "CLI exit $($child.ExitCode): $output $errorOutput" }
        return $output
    }
    finally {
        if ($childStarted -and -not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
        $child.Dispose()
    }
}

function Wait-ScenarioState([Diagnostics.Process]$App, [scriptblock]$Condition, [string]$Description) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($App.HasExited) { throw "App exited ($($App.ExitCode)) while waiting for $Description." }
        if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw "Timed out waiting for $Description." }
        Start-Sleep -Milliseconds 25
    }
}

function Read-ScenarioLog([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return '' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Get-Statistics([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2) { $sorted[$middle] } else { ($sorted[$middle - 1] + $sorted[$middle]) / 2 }
    return [ordered]@{ Count = $sorted.Count; Median = $median; Min = $sorted[0]; Max = $sorted[-1] }
}

$samples = [Collections.Generic.List[object]]::new()
$failure = $null
try {
    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        $session = [guid]::NewGuid().ToString('N')
        $dataRoot = Join-Path ([IO.Path]::GetTempPath()) "AudioPilot.Diagnostics/$session"
        New-Item -ItemType Directory -Path $dataRoot | Out-Null
        $logPath = Join-Path $dataRoot 'AudioPilot.log'
        @{
            RunAtStartup = $false
            DeviceSwitching = @{ Output = @{ HotkeysEnabled = $false; CycleDevices = @(@{ Id = 'diagnostic-missing-endpoint'; Name = 'Smoke fixture' }) } }
            Hotkeys = @{ App = @{ ToggleAppVisibility = '' } }
            Miscellaneous = @{ LogLevel = 'Trace'; RedactLogContent = $true; SuppressDeviceStartupWarnings = $true; PlayAppSounds = $false }
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dataRoot 'settings.json') -Encoding utf8
        $app = [Diagnostics.Process]::new()
        $app.StartInfo = New-ScenarioStartInfo $AppPath $session @('-startup')
        $started = $false
        try {
            $startup = [Diagnostics.Stopwatch]::StartNew()
            $started = $app.Start()
            Wait-ScenarioState $app { (Read-ScenarioLog $logPath).Contains('tray-runtime-ready | registered=true') } 'tray registration'
            if ([AudioPilotSmokeWindowProbe]::Find($app.Id, $true) -ne [IntPtr]::Zero) { throw 'Tray startup unexpectedly showed the main window.' }
            $trayReadyMs = $startup.Elapsed.TotalMilliseconds
            $before = (Invoke-ScenarioCli $session @('diagnostics', 'status', '--json') | ConvertFrom-Json).Data.Process
            if ($before.ProcessId -ne $app.Id) { throw 'Diagnostics were not served by the isolated GUI process.' }
            $readyMs = $startup.Elapsed.TotalMilliseconds
            if ([AudioPilotSmokeWindowProbe]::Find($app.Id, $false) -ne [IntPtr]::Zero) { throw 'Configured tray startup created a main window before Show.' }
            $blocked = Invoke-ScenarioCli $session @('startup', 'enable', '--json') -ExpectFailure
            if ($blocked -notmatch 'failed') { throw 'Startup registration guard did not report failure.' }
            $startupState = Invoke-ScenarioCli $session @('startup', 'status', '--json') | ConvertFrom-Json
            if ($startupState.Data.StartupEnabled) { throw 'Diagnostic instance enabled startup registration.' }
            $null = Invoke-ScenarioCli $session @('config', 'set', 'auto-save-enabled', 'true', '--json')
            $config = Invoke-ScenarioCli $session @('config', 'get', 'auto-save-enabled', '--json') | ConvertFrom-Json
            if ($config.Data.Value -ne 'true') { throw 'GUI-backed configuration round trip failed.' }
            $null = Invoke-ScenarioCli $session @('devices', 'list', 'output', '--json', '--redact')
            for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
                $timer = [Diagnostics.Stopwatch]::StartNew()
                $null = Invoke-ScenarioCli $session @('show')
                Wait-ScenarioState $app { [AudioPilotSmokeWindowProbe]::Find($app.Id, $true) -ne [IntPtr]::Zero } 'visible main window'
                $showMs = $timer.Elapsed.TotalMilliseconds
                $timer.Restart()
                $null = Invoke-ScenarioCli $session @('refresh', '--json')
                $refreshMs = $timer.Elapsed.TotalMilliseconds
                $after = (Invoke-ScenarioCli $session @('diagnostics', 'status', '--json') | ConvertFrom-Json).Data.Process
                if ($after.ProcessId -ne $app.Id) { throw 'CLI unexpectedly fell back to headless execution.' }
                $timer.Restart()
                $null = Invoke-ScenarioCli $session @('hide')
                Wait-ScenarioState $app { [AudioPilotSmokeWindowProbe]::Find($app.Id, $true) -eq [IntPtr]::Zero } 'hidden main window'
                $firstShowMs = if ($cycle -eq 1) { $showMs } else { $null }
                $restoreMs = if ($cycle -gt 1) { $showMs } else { $null }
                $samples.Add([ordered]@{
                    Iteration = $iteration; Cycle = $cycle; TrayReadyMs = $trayReadyMs; CliReadyMs = $readyMs
                    FirstShowMs = $firstShowMs; RestoreMs = $restoreMs; RefreshRequestMs = $refreshMs; HideMs = $timer.Elapsed.TotalMilliseconds
                    AllocatedBytesSinceTray = $after.AllocatedBytes - $before.AllocatedBytes
                    ManagedBytes = $after.ManagedBytes; PrivateBytes = $after.PrivateBytes
                    Handles = $after.Handles; HandlesSinceTray = $after.Handles - $before.Handles
                    Gen2CollectionsSinceTray = $after.Gen2Collections - $before.Gen2Collections; ShutdownMs = $null
                })
            }
            $shutdown = [Diagnostics.Stopwatch]::StartNew()
            if (-not [AudioPilotSmokeWindowProbe]::Close($app.Id)) { throw 'Could not request normal window-close shutdown.' }
            if (-not $app.WaitForExit($TimeoutSeconds * 1000)) { throw 'Graceful shutdown timed out.' }
            if ($app.ExitCode -ne 0) { throw "App exited with code $($app.ExitCode)." }
            $samples[$samples.Count - 1].ShutdownMs = $shutdown.Elapsed.TotalMilliseconds
            $log = Read-ScenarioLog $logPath
            if ($log -notmatch 'mixer-refresh-complete') { throw 'No completed mixer refresh was observed.' }
            $unexpectedErrors = @($log -split '\r?\n' | Where-Object { $_ -match '\[Error\]' -and $_ -notmatch '\[AppViewModel\] startup-cli-update-failed \| enabled=True error=InvalidOperationException' })
            if ($unexpectedErrors.Count -gt 0 -or $log -notmatch 'runtime-shutdown-complete \|[^\r\n]*allStepsDrained=True') { throw 'The app logged an unexpected error or incomplete shutdown; inspect the retained run log.' }
            Write-Host "Iteration $iteration/$Iterations passed: tray, CLI, config, show/refresh/hide, graceful shutdown."
        }
        finally {
            if ($started -and -not $app.HasExited) { $app.Kill($true); $app.WaitForExit() }
            $app.Dispose()
            if (Test-Path -LiteralPath $logPath) { Copy-Item -LiteralPath $logPath -Destination (Join-Path $runDirectory "iteration-$iteration.log") }
            $resolvedDataRoot = [IO.Path]::GetFullPath($dataRoot)
            $expectedDataRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "AudioPilot.Diagnostics/$session"))
            if ($resolvedDataRoot -ne $expectedDataRoot -or [IO.Path]::GetFileName($resolvedDataRoot) -ne $session) { throw 'Unsafe diagnostic cleanup path.' }
            Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
        }
    }
}
catch { $failure = $_.Exception.Message }

$metrics = [ordered]@{}
foreach ($metric in @('TrayReadyMs', 'CliReadyMs', 'FirstShowMs', 'RestoreMs', 'RefreshRequestMs', 'HideMs', 'AllocatedBytesSinceTray', 'ManagedBytes', 'PrivateBytes', 'Handles', 'HandlesSinceTray', 'ShutdownMs')) {
    $rows = if ($metric -in @('TrayReadyMs', 'CliReadyMs')) { @($samples | Where-Object { $_.Cycle -eq 1 }) } else { @($samples) }
    $values = @($rows | ForEach-Object { if ($null -ne $_[$metric]) { [double]$_[$metric] } })
    $metrics[$metric] = Get-Statistics $values
}
$mixerTimes = @(Get-ChildItem -LiteralPath $runDirectory -Filter '*.log' | ForEach-Object {
    foreach ($match in [regex]::Matches((Read-ScenarioLog $_.FullName), 'mixer-refresh-complete \| totalMs=([\d.,]+)')) {
        [double]::Parse($match.Groups[1].Value.Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture)
    }
})
$metrics['MixerRefreshMs'] = Get-Statistics $mixerTimes
$report = [ordered]@{
    SchemaVersion = 1; Scenario = 'tray-cli-show-refresh-hide-close'; CreatedUtc = [DateTime]::UtcNow.ToString('o')
    Passed = $null -eq $failure; Failure = $failure; Iterations = $Iterations; Cycles = $Cycles
    AppSha256 = $appHash
    OS = [Environment]::OSVersion.VersionString; Architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    ProcessorCount = [Environment]::ProcessorCount; Metrics = $metrics; Samples = @($samples)
    Notes = 'Trace logging enabled. CLI timings include client startup and IPC. Mixer timings come from completed refresh logs. Allocations are process-wide approximations; no forced GC. Fresh processes, warm OS caches. Compare matching hardware, displays, audio sessions and deployment.'
}
$comparison = [ordered]@{}
if ($null -ne $baseline) {
    foreach ($key in $metrics.Keys) {
        $baselineMetric = $baseline.Metrics.PSObject.Properties[$key]
        if ($null -ne $metrics[$key] -and $null -ne $baselineMetric -and $null -ne $baselineMetric.Value) {
            $comparison[$key] = $metrics[$key].Median - $baselineMetric.Value.Median
        }
    }
}
$report['MedianChangesFromBaseline'] = $comparison
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDirectory 'report.json') -Encoding utf8
$lines = @('# Application smoke and performance report', '', "Passed: $($report.Passed)", '', $report.Notes, '', '| Metric | Samples | Median | Min | Max |', '| --- | ---: | ---: | ---: | ---: |')
foreach ($key in $metrics.Keys) {
    $stats = $metrics[$key]
    if ($null -ne $stats) { $lines += "| $key | $($stats.Count) | $([Math]::Round($stats.Median, 2)) | $([Math]::Round($stats.Min, 2)) | $([Math]::Round($stats.Max, 2)) |" }
}
if ($comparison.Count -gt 0) {
    $lines += @('', '| Metric | Median change from baseline |', '| --- | ---: |')
    foreach ($key in $comparison.Keys) { $lines += "| $key | $([Math]::Round($comparison[$key], 2)) |" }
}
if ($failure) { $lines += @('', "Failure: $failure") }
$lines | Set-Content -LiteralPath (Join-Path $runDirectory 'report.md') -Encoding utf8
Write-Host "Report: $runDirectory"
if ($failure) { throw $failure }
