[CmdletBinding()]
param(
    [ValidateSet('Codec', 'Startup', 'Transport', 'Settings', 'Both', 'All', 'Mixer', 'Export', 'Dsp', 'App')][string]$Mode = 'Both',
    [string]$CliPath = 'AudioPilot.CliHost/bin/Release/net11.0-windows10.0.19041.0/AudioPilot.Cli.exe',
    [ValidateRange(3, 200)][int]$StartupIterations = 30,
    [ValidateRange(0, 20)][int]$StartupWarmupIterations = 3,
    [ValidateRange(20, 100000)][int]$TransportIterations = 500,
    [ValidateRange(0, 1000)][int]$TransportWarmupIterations = 20,
    [switch]$Dry,
    [switch]$Short,
    [string]$ResultPathFile,
    [switch]$BuildCliFixture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'AudioPilot benchmarks require Windows.' }
if ($Dry -and $Short) { throw 'Choose Dry or Short, not both.' }
$usesCli = $Mode -in @('Startup', 'Both', 'All')
if ($BuildCliFixture -and (-not $usesCli -or $PSBoundParameters.ContainsKey('CliPath'))) {
    throw 'Use -BuildCliFixture with Startup, Both, or All and omit -CliPath.'
}
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($usesCli -and -not $BuildCliFixture) {
    $CliPath = (Resolve-Path -LiteralPath ([IO.Path]::GetFullPath($CliPath, $repoRoot))).Path
}
$benchmarkRoot = Join-Path $repoRoot 'AudioPilot.Benchmarks'
$project = Join-Path $benchmarkRoot 'AudioPilot.Benchmarks.csproj'
$buildProjects = if ($BuildCliFixture) { @((Join-Path $repoRoot 'AudioPilot.CliHost/AudioPilot.CliHost.csproj'), $project) } else { @($project) }
$runId = [Guid]::NewGuid().ToString('N')
$output = Join-Path $repoRoot "artifacts/benchmarks/run-$runId"
$buildKey = "Benchmark-$runId"
$isolatedOutput = "bin/isolated/$buildKey/Release/net11.0-windows10.0.19041.0"
New-Item -ItemType Directory -Path $output -Force | Out-Null

$projectDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$seenProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$pendingProjects = [Collections.Generic.Queue[string]]::new()
foreach ($buildProject in $buildProjects) { $pendingProjects.Enqueue($buildProject) }
while ($pendingProjects.Count) {
    $path = [IO.Path]::GetFullPath($pendingProjects.Dequeue())
    if (-not $path.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Benchmark project references must stay inside the repository.' }
    if (-not $seenProjects.Add($path)) { continue }
    $directory = [IO.Path]::GetDirectoryName($path)
    [void]$projectDirectories.Add($directory)
    [xml]$definition = Get-Content -LiteralPath $path -Raw
    foreach ($reference in $definition.SelectNodes('//ProjectReference[@Include]')) {
        $pendingProjects.Enqueue([IO.Path]::GetFullPath($reference.GetAttribute('Include'), $directory))
    }
}
try {
    foreach ($buildProject in $buildProjects) {
        $projectName = [IO.Path]::GetFileNameWithoutExtension($buildProject)
        & dotnet restore $buildProject "-p:BuildIsolationKey=$buildKey" --locked-mode --nologo
        if ($LASTEXITCODE -ne 0) { throw "$projectName restore failed." }
        & dotnet build $buildProject -c Release "-p:BuildIsolationKey=$buildKey" --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw "$projectName build failed." }
    }

    if ($BuildCliFixture) {
        $CliPath = (Resolve-Path -LiteralPath (Join-Path $repoRoot "AudioPilot.CliHost/$isolatedOutput/AudioPilot.Cli.exe")).Path
    }
    $executable = Join-Path $benchmarkRoot "$isolatedOutput/AudioPilot.Benchmarks.dll"
    if ($Mode -notin @('Mixer', 'Export', 'Dsp', 'App')) {
        & dotnet $executable verify $output
        if ($LASTEXITCODE -ne 0) { throw 'AudioPilot benchmark fixture verification failed.' }
    }
    if ($Mode -in @('All', 'Mixer', 'Export', 'Dsp', 'App')) {
        & dotnet $executable verify-app
        if ($LASTEXITCODE -ne 0) { throw 'AudioPilot application benchmark fixture verification failed.' }
    }
    if ($Mode -in @('Codec', 'Settings', 'Both', 'All', 'Mixer', 'Export', 'Dsp', 'App')) {
        $suites = if ($Mode -eq 'All' -and $Dry) { @('comparisons', 'app') }
            else { @(switch ($Mode) { 'All' { 'comparisons' }; 'Both' { 'codec' }; default { $Mode.ToLowerInvariant() } }) }
        $previousReportDirectory = $env:AUDIOPILOT_BENCHMARK_REPORT_DIR
        # Resolve projects within the benchmark solution so ignored worktrees and old fixtures cannot shadow the project.
        Push-Location -LiteralPath $benchmarkRoot
        try {
            $env:AUDIOPILOT_BENCHMARK_REPORT_DIR = Join-Path $output 'resources'
            foreach ($name in $suites) {
                $arguments = @($executable, $name, '--artifacts', (Join-Path $output $name), '--filter', '*')
                if ($Dry) { $arguments += @('--job', 'Dry') }
                elseif ($Short) { $arguments += @('--job', 'Short') }
                & dotnet @arguments
                if ($LASTEXITCODE -ne 0) { throw "AudioPilot $name benchmark failed." }
            }
        }
        finally {
            Pop-Location
            $env:AUDIOPILOT_BENCHMARK_REPORT_DIR = $previousReportDirectory
        }
    }
    if ($usesCli) {
        & dotnet $executable startup $CliPath $output $StartupIterations $StartupWarmupIterations
        if ($LASTEXITCODE -ne 0) { throw 'IPC startup benchmark failed.' }
    }
    if ($Mode -in @('Transport', 'All')) {
        & dotnet $executable transport $output $TransportIterations $TransportWarmupIterations
        if ($LASTEXITCODE -ne 0) { throw 'Warmed IPC benchmark failed.' }
    }
}
finally {
    foreach ($projectDirectory in $projectDirectories) {
        foreach ($kind in @('bin', 'obj')) {
            try {
                $parent = [IO.Path]::GetFullPath((Join-Path $projectDirectory "$kind/isolated"))
                $buildDirectory = Join-Path $parent $buildKey
                if (-not (Test-Path -LiteralPath $buildDirectory)) { continue }
                $item = Get-Item -LiteralPath $buildDirectory
                if ($item.Parent.FullName -ne $parent -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw "Unexpected benchmark build cleanup target: $buildDirectory"
                }
                for ($ancestor = $item.Parent; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
                    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                        throw "Benchmark cleanup does not follow directory links: $($ancestor.FullName)"
                    }
                }
                if (@(Get-ChildItem -LiteralPath $item.FullName -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
                    throw 'Benchmark cleanup does not follow linked contents.'
                }
                Remove-Item -LiteralPath $item.FullName -Recurse -Force
            }
            catch { Write-Warning "Benchmark scratch cleanup skipped: $($_.Exception.Message)" }
        }
    }
}
if ($ResultPathFile) { Set-Content -LiteralPath $ResultPathFile -Value $output -Encoding utf8 }
Write-Host "AudioPilot benchmark reports: $output"
