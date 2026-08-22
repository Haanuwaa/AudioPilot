[CmdletBinding()]
param(
    [string]$Project = "AudioPilot/AudioPilot.csproj",
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$ArtifactsRoot = "artifacts/benchmarks/readytorun",
    [ValidateRange(3, 50)]
    [int]$StartupIterations = 20,
    [ValidateRange(0, 10)]
    [int]$StartupWarmupIterations = 3,
    [ValidateRange(1, 60)]
    [int]$StartupTimeoutSeconds = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $Project)) {
    throw "Project not found: $Project"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifactsPath = Join-Path $repoRoot $ArtifactsRoot
$tempLockFile = Join-Path $artifactsPath "benchmark.packages.lock.json"
$lockFileSnapshots = @{}
$publishDirectories = [System.Collections.Generic.List[string]]::new()

if (-not ("AudioPilotBenchmarkWindowProbe" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class AudioPilotBenchmarkWindowProbe
{
    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);

    public static bool HasMainWindow(int targetProcessId)
    {
        bool found = false;
        EnumWindows((windowHandle, parameter) =>
        {
            GetWindowThreadProcessId(windowHandle, out uint processId);
            if (processId != (uint)targetProcessId)
            {
                return true;
            }

            var title = new StringBuilder(256);
            GetWindowText(windowHandle, title, title.Capacity);
            if (string.Equals(title.ToString(), "AudioPilot", StringComparison.Ordinal))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
}

function Measure-AudioPilotStartup {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true

    $process = $null
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $process = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw "Failed to start benchmark executable '$ExecutablePath'."
        }

        $timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
        while ($stopwatch.Elapsed -lt $timeout) {
            if ($process.HasExited) {
                throw "Benchmark process exited before creating its first window (exit code $($process.ExitCode))."
            }

            if ([AudioPilotBenchmarkWindowProbe]::HasMainWindow($process.Id)) {
                $stopwatch.Stop()
                return [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
            }

            Start-Sleep -Milliseconds 2
        }

        throw "Benchmark process did not create a window within $TimeoutSeconds seconds."
    }
    finally {
        $stopwatch.Stop()
        if ($null -ne $process) {
            if (-not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) {
                    throw "Benchmark process $($process.Id) did not exit after termination."
                }
            }

            $process.Dispose()
        }
    }
}

function Get-Percentile {
    param(
        [Parameter(Mandatory = $true)]
        [double[]]$SortedValues,
        [Parameter(Mandatory = $true)]
        [ValidateRange(0, 1)]
        [double]$Percentile
    )

    if ($SortedValues.Count -eq 1) {
        return $SortedValues[0]
    }

    $position = ($SortedValues.Count - 1) * $Percentile
    $lowerIndex = [Math]::Floor($position)
    $upperIndex = [Math]::Ceiling($position)
    if ($lowerIndex -eq $upperIndex) {
        return $SortedValues[$lowerIndex]
    }

    $fraction = $position - $lowerIndex
    return $SortedValues[$lowerIndex] + (($SortedValues[$upperIndex] - $SortedValues[$lowerIndex]) * $fraction)
}

function Get-StartupStatistics {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[double]]$Samples
    )

    [double[]]$sorted = [double[]]($Samples | Sort-Object)
    $mean = ($sorted | Measure-Object -Average).Average

    return [PSCustomObject]@{
        sampleCount = $sorted.Count
        medianMs = [Math]::Round((Get-Percentile -SortedValues $sorted -Percentile 0.5), 3)
        meanMs = [Math]::Round($mean, 3)
        p95Ms = [Math]::Round((Get-Percentile -SortedValues $sorted -Percentile 0.95), 3)
        minMs = [Math]::Round($sorted[0], 3)
        maxMs = [Math]::Round($sorted[-1], 3)
        samplesMs = $sorted
    }
}

function Assert-AudioPilotNotRunning {
    $runningAudioPilot = Get-Process -Name "AudioPilot" -ErrorAction SilentlyContinue
    if ($runningAudioPilot) {
        throw "AudioPilot is already running. Close it before measuring startup so the single-instance handoff does not invalidate results."
    }
}

Assert-AudioPilotNotRunning

Get-ChildItem -Path $repoRoot -Recurse -File -Filter "packages.lock.json" | ForEach-Object {
    $lockFileSnapshots[$_.FullName] = [System.IO.File]::ReadAllText($_.FullName)
}

try {
    if (Test-Path $artifactsPath) {
        Remove-Item -Path $artifactsPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $artifactsPath -Force | Out-Null

    & dotnet restore $Project -r $RuntimeIdentifier --force-evaluate --lock-file-path $tempLockFile --nologo -p:SelfContained=true -p:PublishReadyToRun=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for runtime '$RuntimeIdentifier'."
    }

    $variants = @(
        @{ Name = "r2r-off"; PublishReadyToRun = "false" },
        @{ Name = "r2r-on"; PublishReadyToRun = "true" }
    )

    $publishedVariants = [System.Collections.Generic.List[object]]::new()

    foreach ($variant in $variants) {
        $publishDir = Join-Path $artifactsPath $variant.Name
        $publishDirectories.Add($publishDir)

        $publishArgs = @(
            "publish",
            $Project,
            "-c",
            $Configuration,
            "-r",
            $RuntimeIdentifier,
            "--self-contained",
            "true",
            "--no-restore",
            "--nologo",
            "-p:PublishSingleFile=false",
            "-p:PublishReadyToRun=$($variant.PublishReadyToRun)",
            "-p:PublishDir=$publishDir"
        )

        & dotnet @publishArgs

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for variant '$($variant.Name)'."
        }

        $sizeBytes = (Get-ChildItem -Path $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
        $executablePath = Join-Path $publishDir "AudioPilot.exe"
        if (-not (Test-Path $executablePath)) {
            throw "Published benchmark executable not found: $executablePath"
        }

        $publishedVariants.Add([PSCustomObject]@{
            variant = $variant.Name
            publishReadyToRun = [bool]::Parse($variant.PublishReadyToRun)
            runtimeIdentifier = $RuntimeIdentifier
            sizeBytes = [int64]$sizeBytes
            publishDirectory = $publishDir
            executablePath = $executablePath
            startupSamples = [System.Collections.Generic.List[double]]::new()
        })
    }

    Assert-AudioPilotNotRunning

    foreach ($variant in $publishedVariants) {
        for ($warmup = 0; $warmup -lt $StartupWarmupIterations; $warmup++) {
            Measure-AudioPilotStartup -ExecutablePath $variant.executablePath -WorkingDirectory $variant.publishDirectory -TimeoutSeconds $StartupTimeoutSeconds | Out-Null
        }
    }

    for ($iteration = 0; $iteration -lt $StartupIterations; $iteration++) {
        $iterationVariants = if (($iteration % 2) -eq 0) {
            $publishedVariants.ToArray()
        }
        else {
            [object[]]($publishedVariants | Sort-Object variant -Descending)
        }

        foreach ($variant in $iterationVariants) {
            $elapsedMs = Measure-AudioPilotStartup -ExecutablePath $variant.executablePath -WorkingDirectory $variant.publishDirectory -TimeoutSeconds $StartupTimeoutSeconds
            $variant.startupSamples.Add($elapsedMs)
        }
    }

    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($variant in $publishedVariants) {
        $results.Add([PSCustomObject]@{
            variant = $variant.variant
            publishReadyToRun = $variant.publishReadyToRun
            runtimeIdentifier = $variant.runtimeIdentifier
            sizeBytes = $variant.sizeBytes
            startup = Get-StartupStatistics -Samples $variant.startupSamples
        })
    }

    $r2rOff = $results | Where-Object variant -eq "r2r-off"
    $r2rOn = $results | Where-Object variant -eq "r2r-on"
    $deltaBytes = [int64]($r2rOn.sizeBytes - $r2rOff.sizeBytes)
    $deltaPercent = if ($r2rOff.sizeBytes -gt 0) {
        [Math]::Round(($deltaBytes / $r2rOff.sizeBytes) * 100, 2)
    }
    else {
        0
    }
    $medianStartupImprovementMs = [Math]::Round($r2rOff.startup.medianMs - $r2rOn.startup.medianMs, 3)
    $medianStartupImprovementPercent = if ($r2rOff.startup.medianMs -gt 0) {
        [Math]::Round(($medianStartupImprovementMs / $r2rOff.startup.medianMs) * 100, 2)
    }
    else {
        0
    }

    $summary = [PSCustomObject]@{
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        project = $Project
        configuration = $Configuration
        runtimeIdentifier = $RuntimeIdentifier
        baseline = $r2rOff
        readyToRun = $r2rOn
        deltaBytes = $deltaBytes
        deltaPercent = $deltaPercent
        startupMethod = "repeated process launch to first top-level window; warmups excluded; variants interleaved"
        startupWarmupIterations = $StartupWarmupIterations
        medianStartupImprovementMs = $medianStartupImprovementMs
        medianStartupImprovementPercent = $medianStartupImprovementPercent
    }

    $summaryPath = Join-Path $artifactsPath "summary.json"
    $markdownPath = Join-Path $artifactsPath "summary.md"

    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath -Encoding UTF8

    $markdown = @(
        "# ReadyToRun Publish Benchmark",
        "",
        "- Runtime: $RuntimeIdentifier",
        "- Configuration: $Configuration",
        "- Startup method: repeated process launch to first top-level window; $StartupWarmupIterations warmups excluded; variants interleaved",
        "- Samples per variant: $StartupIterations",
        "- Baseline size (`PublishReadyToRun=false`): $($r2rOff.sizeBytes) bytes",
        "- ReadyToRun size (`PublishReadyToRun=true`): $($r2rOn.sizeBytes) bytes",
        "- Size delta: $deltaBytes bytes ($deltaPercent%)",
        "- Baseline startup: median $($r2rOff.startup.medianMs) ms; p95 $($r2rOff.startup.p95Ms) ms; range $($r2rOff.startup.minMs)-$($r2rOff.startup.maxMs) ms",
        "- ReadyToRun startup: median $($r2rOn.startup.medianMs) ms; p95 $($r2rOn.startup.p95Ms) ms; range $($r2rOn.startup.minMs)-$($r2rOn.startup.maxMs) ms",
        "- Median startup improvement (positive favors ReadyToRun): $medianStartupImprovementMs ms ($medianStartupImprovementPercent%)"
    ) -join [Environment]::NewLine

    $markdown | Set-Content -Path $markdownPath -Encoding UTF8
    Write-Host $markdown

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $markdown
    }

    foreach ($publishDirectory in $publishDirectories) {
        Remove-Item -Path $publishDirectory -Recurse -Force
    }
}
finally {
    foreach ($path in $lockFileSnapshots.Keys) {
        [System.IO.File]::WriteAllText($path, $lockFileSnapshots[$path])
    }

    Remove-Item -Path $tempLockFile -Force -ErrorAction SilentlyContinue
}
