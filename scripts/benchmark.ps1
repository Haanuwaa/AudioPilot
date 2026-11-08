[CmdletBinding()]
param(
    [ValidateSet('Codec', 'Startup', 'Transport', 'Settings', 'Both', 'All', 'Mixer', 'Export', 'App')][string]$Mode = 'Both',
    [string]$CliPath = 'AudioPilot.CliHost/bin/Release/net11.0-windows10.0.19041.0/AudioPilot.Cli.exe',
    [ValidateRange(3, 200)][int]$StartupIterations = 30,
    [ValidateRange(0, 20)][int]$StartupWarmupIterations = 3,
    [ValidateRange(20, 100000)][int]$TransportIterations = 500,
    [ValidateRange(0, 1000)][int]$TransportWarmupIterations = 20,
    [Alias('DryCodec')][switch]$Dry,
    [switch]$Short,
    [string]$ResultPathFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'AudioPilot benchmarks require Windows.' }
if ($Dry -and $Short) { throw 'Choose Dry or Short, not both.' }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($Mode -in @('Startup', 'Both', 'All')) {
    $CliPath = (Resolve-Path -LiteralPath ([IO.Path]::GetFullPath($CliPath, $repoRoot))).Path
}
$project = Join-Path $repoRoot 'AudioPilot.Benchmarks/AudioPilot.Benchmarks.csproj'
$runId = [Guid]::NewGuid().ToString('N')
$output = Join-Path $repoRoot "artifacts/benchmarks/run-$runId"
$buildKey = "Benchmark-$runId"
New-Item -ItemType Directory -Path $output -Force | Out-Null

$projectDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$seenProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$pendingProjects = [Collections.Generic.Queue[string]]::new()
$pendingProjects.Enqueue($project)
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
    & dotnet restore $project "-p:BuildIsolationKey=$buildKey" --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'AudioPilot benchmark restore failed.' }
    & dotnet build $project -c Release "-p:BuildIsolationKey=$buildKey" --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'AudioPilot benchmark build failed.' }

    $executable = Join-Path (Split-Path -Parent $project) "bin/isolated/$buildKey/Release/net11.0-windows10.0.19041.0/AudioPilot.Benchmarks.dll"
    if ($Mode -in @('Mixer', 'Export', 'App')) { & dotnet $executable verify-app }
    else { & dotnet $executable verify $output }
    if ($LASTEXITCODE -ne 0) { throw 'AudioPilot benchmark fixture verification failed.' }
    if ($Mode -in @('Codec', 'Settings', 'Both', 'All', 'Mixer', 'Export', 'App')) {
        $name = switch ($Mode) { 'All' { 'comparisons' }; 'Both' { 'codec' }; default { $Mode.ToLowerInvariant() } }
        $arguments = @($executable, $name, '--artifacts', (Join-Path $output $name), '--filter', '*')
        if ($Dry) { $arguments += @('--job', 'Dry') }
        elseif ($Short) { $arguments += @('--job', 'Short') }
        $previousReportDirectory = $env:AUDIOPILOT_BENCHMARK_REPORT_DIR
        try {
            $env:AUDIOPILOT_BENCHMARK_REPORT_DIR = Join-Path $output 'resources'
            & dotnet @arguments
            if ($LASTEXITCODE -ne 0) { throw "AudioPilot $name benchmark failed." }
        }
        finally { $env:AUDIOPILOT_BENCHMARK_REPORT_DIR = $previousReportDirectory }
    }
    if ($Mode -in @('Startup', 'Both', 'All')) {
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
