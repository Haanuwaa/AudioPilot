[CmdletBinding()]
param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$TargetProcessId,

    [ValidateNotNullOrEmpty()]
    [string]$ProcessName = "AudioPilot",

    [ValidateRange(1, 86400)]
    [int]$DurationSeconds = 300,

    [ValidateRange(0.1, 3600)]
    [double]$SampleIntervalSeconds = 5,

    [ValidateNotNullOrEmpty()]
    [string]$Phase = "tray-idle",

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-TargetProcess {
    if ($TargetProcessId -gt 0) {
        return Get-Process -Id $TargetProcessId -ErrorAction Stop
    }

    $matches = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) {
        throw "No running process named '$ProcessName' was found. Start AudioPilot or pass -TargetProcessId."
    }

    if ($matches.Count -gt 1) {
        $matchingIds = ($matches.Id | Sort-Object) -join ", "
        foreach ($match in $matches) { $match.Dispose() }
        throw "More than one '$ProcessName' process is running (PIDs: $matchingIds). Pass -TargetProcessId to select one."
    }

    return $matches[0]
}

function Resolve-OutputPath {
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        return [System.IO.Path]::GetFullPath($OutputPath)
    }

    $diagnosticsDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "AudioPilotDiagnostics"
    $fileName = "AudioPilot-profile-{0:yyyyMMdd-HHmmss}.csv" -f [DateTimeOffset]::Now
    return Join-Path $diagnosticsDirectory $fileName
}

$target = Resolve-TargetProcess
try {
    $targetId = $target.Id
    $targetName = $target.ProcessName
    $destinationPath = Resolve-OutputPath
    $destinationDirectory = Split-Path -Parent $destinationPath
    if ([string]::IsNullOrWhiteSpace($destinationDirectory)) {
        throw "The output path must include a valid parent directory."
    }

    [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null

    $logicalProcessorCount = [Math]::Max(1, [Environment]::ProcessorCount)
    $samples = [System.Collections.Generic.List[object]]::new()
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $previousElapsed = $timer.Elapsed
    $previousProcessorTime = $target.TotalProcessorTime
    $initialProcessorTime = $previousProcessorTime
    $completed = $true

    Write-Host "Profiling $targetName (PID $targetId) for $DurationSeconds seconds during '$Phase'..."

    while ($timer.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $remainingSeconds = $DurationSeconds - $timer.Elapsed.TotalSeconds
        $delaySeconds = [Math]::Min($SampleIntervalSeconds, $remainingSeconds)
        if ($delaySeconds -gt 0) {
            Start-Sleep -Milliseconds ([Math]::Max(1, [int][Math]::Round($delaySeconds * 1000)))
        }

        try {
            $target.Refresh()
            if ($target.HasExited) {
                $completed = $false
                break
            }
            $current = $target
            $processorTime = $current.TotalProcessorTime
            $currentElapsed = $timer.Elapsed
            $elapsedDelta = ($currentElapsed - $previousElapsed).TotalSeconds
            $processorDelta = ($processorTime - $previousProcessorTime).TotalSeconds
            $normalizedCpuPercent = if ($elapsedDelta -gt 0) {
                100 * $processorDelta / $elapsedDelta / $logicalProcessorCount
            }
            else {
                0
            }

            $samples.Add([pscustomobject]@{
                TimestampUtc = [DateTimeOffset]::UtcNow.ToString("O")
                Phase = $Phase
                ProcessId = $targetId
                ElapsedSeconds = [Math]::Round($currentElapsed.TotalSeconds, 3)
                CpuPercent = [Math]::Round([Math]::Max(0.0, $normalizedCpuPercent), 4)
                WorkingSetMiB = [Math]::Round($current.WorkingSet64 / 1MB, 3)
                PrivateMemoryMiB = [Math]::Round($current.PrivateMemorySize64 / 1MB, 3)
                HandleCount = $current.HandleCount
                ThreadCount = $current.Threads.Count
            })

            $previousElapsed = $currentElapsed
            $previousProcessorTime = $processorTime
        }
        catch {
            if (-not $target.HasExited) { throw }
            $completed = $false
            break
        }
    }

    $timer.Stop()
    if (-not $completed) {
        Write-Warning "Process $targetId exited; preserving the samples collected before shutdown."
    }
    if ($samples.Count -eq 0) {
        throw "No runtime samples were collected."
    }

    $temporaryPath = "$destinationPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $samples | Export-Csv -LiteralPath $temporaryPath -NoTypeInformation -Encoding utf8
        [System.IO.File]::Move($temporaryPath, $destinationPath, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }

    $cpu = $samples | Measure-Object -Property CpuPercent -Maximum
    $workingSet = $samples | Measure-Object -Property WorkingSetMiB -Minimum -Maximum
    $privateMemory = $samples | Measure-Object -Property PrivateMemoryMiB -Minimum -Maximum
    $first = $samples[0]
    $last = $samples[$samples.Count - 1]

    $summary = [pscustomobject]@{
        Process = "$targetName ($targetId)"
        Phase = $Phase
        Completed = $completed
        DurationSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
        Samples = $samples.Count
        AverageCpuPercent = [Math]::Round(100 * ($previousProcessorTime - $initialProcessorTime).TotalSeconds / $previousElapsed.TotalSeconds / $logicalProcessorCount, 4)
        PeakCpuPercent = [Math]::Round($cpu.Maximum, 4)
        WorkingSetRangeMiB = "{0:N3} - {1:N3}" -f $workingSet.Minimum, $workingSet.Maximum
        WorkingSetDeltaMiB = [Math]::Round($last.WorkingSetMiB - $first.WorkingSetMiB, 3)
        PrivateMemoryRangeMiB = "{0:N3} - {1:N3}" -f $privateMemory.Minimum, $privateMemory.Maximum
        PrivateMemoryDeltaMiB = [Math]::Round($last.PrivateMemoryMiB - $first.PrivateMemoryMiB, 3)
        HandleDelta = $last.HandleCount - $first.HandleCount
        ThreadDelta = $last.ThreadCount - $first.ThreadCount
        OutputPath = $destinationPath
    }

    $summary | Format-List | Out-Host

}
finally {
    $target.Dispose()
}
