Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
# BenchmarkDotNet skips dot-prefixed directories when looking for projects.
$root = Join-Path $repo "artifacts/testresults/.benchmark-cleanup/$([guid]::NewGuid().ToString('N'))"
foreach ($case in @('success','restore-failure','build-failure','verify-failure','run-failure','linked-content','shared-cli','shared-cli-build-failure')) {
    $fixture = Join-Path $root $case
    New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts'), (Join-Path $fixture 'AudioPilot.Benchmarks'),
        (Join-Path $fixture 'AudioPilot'), (Join-Path $fixture 'AudioPilot.Updater/Protocol') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'scripts/benchmark.ps1') -Destination (Join-Path $fixture 'scripts')
    Set-Content -LiteralPath (Join-Path $fixture 'AudioPilot.Benchmarks/AudioPilot.Benchmarks.csproj') -Value '<Project><ItemGroup><ProjectReference Include="../AudioPilot/AudioPilot.csproj"/></ItemGroup></Project>'
    Set-Content -LiteralPath (Join-Path $fixture 'AudioPilot/AudioPilot.csproj') -Value '<Project><ItemGroup><ProjectReference Include="../AudioPilot.Updater/Protocol/AudioPilot.UpdateProtocol.csproj"/></ItemGroup></Project>'
    Set-Content -LiteralPath (Join-Path $fixture 'AudioPilot.Updater/Protocol/AudioPilot.UpdateProtocol.csproj') -Value '<Project/>'
    $sharedCli = $case -like 'shared-cli*'
    if ($sharedCli) {
        New-Item -ItemType Directory -Path (Join-Path $fixture 'AudioPilot.CliHost') | Out-Null
        Set-Content -LiteralPath (Join-Path $fixture 'AudioPilot.CliHost/AudioPilot.CliHost.csproj') -Value '<Project><ItemGroup><ProjectReference Include="../AudioPilot/AudioPilot.csproj"/></ItemGroup></Project>'
    }
    $owned = [Collections.Generic.List[string]]::new()
    $buildKeys = [Collections.Generic.List[string]]::new()
    $buildOrder = [Collections.Generic.List[string]]::new()
    $unrelated = Join-Path $fixture 'unrelated'
    New-Item -ItemType Directory -Path $unrelated -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $unrelated 'keep') -Value 'untouched'
    function dotnet {
        if ($args[0] -in @('restore', 'build')) {
            $projectName = [IO.Path]::GetFileNameWithoutExtension($args[1])
            $key = @($args | Where-Object {$_ -clike '-p:BuildIsolationKey=*'})[0].Substring('-p:BuildIsolationKey='.Length)
            $buildKeys.Add($key)
            $buildOrder.Add("$($args[0]) $projectName")
        }
        if ($args[0] -ceq 'restore') {
            foreach ($project in @($projectName,'AudioPilot','AudioPilot.Updater/Protocol')) {
                foreach ($kind in @('bin','obj')) {
                    $directory = Join-Path $fixture "$project/$kind/isolated/$key"
                    if ($owned.Contains($directory)) { continue }
                    New-Item -ItemType Directory -Path $directory -Force | Out-Null
                    Set-Content -LiteralPath (Join-Path $directory 'scratch') -Value 'owned'
                    $owned.Add($directory)
                    $other = Join-Path $fixture "$project/$kind/isolated/another-run"
                    New-Item -ItemType Directory -Path $other -Force | Out-Null
                    Set-Content -LiteralPath (Join-Path $other 'keep') -Value 'untouched'
                }
            }
            if ($case -ceq 'linked-content') { New-Item -ItemType Junction -Path (Join-Path $owned[0] 'linked') -Target $unrelated | Out-Null }
        }
        if ($args[0] -ceq 'build' -and $projectName -ceq 'AudioPilot.CliHost' -and $case -ceq 'shared-cli') {
            $cliBin = Join-Path $fixture "AudioPilot.CliHost/bin/isolated/$key/Release/net11.0-windows10.0.19041.0"
            New-Item -ItemType Directory -Path $cliBin | Out-Null
            Set-Content -LiteralPath (Join-Path $cliBin 'AudioPilot.Cli.exe') -Value 'fixture'
        }
        if ($args[1] -ceq 'startup') {
            $cliExe = Join-Path $fixture "AudioPilot.CliHost/bin/isolated/$($buildKeys[0])/Release/net11.0-windows10.0.19041.0/AudioPilot.Cli.exe"
            if ([IO.Path]::GetFullPath($args[2]) -cne [IO.Path]::GetFullPath($cliExe) -or -not (Test-Path -LiteralPath $args[2])) {
                throw 'Startup used a CLI outside the shared build.'
            }
        }
        if ($args[1] -ceq 'verify') { Set-Content -LiteralPath (Join-Path $args[2] 'report.json') -Value 'evidence' }
        $global:LASTEXITCODE = if (($case -ceq 'restore-failure' -and $args[0] -ceq 'restore') -or
            ($case -in @('build-failure','shared-cli-build-failure') -and $args[0] -ceq 'build' -and $projectName -ceq 'AudioPilot.Benchmarks') -or
            ($case -ceq 'verify-failure' -and $args[1] -ceq 'verify') -or
            ($case -ceq 'run-failure' -and $args[1] -ceq 'transport')) { 7 } else { 0 }
    }
    $failed = $false
    try {
        if ($sharedCli) { & (Join-Path $fixture 'scripts/benchmark.ps1') -Mode All -BuildCliFixture }
        else { & (Join-Path $fixture 'scripts/benchmark.ps1') -Mode Transport -TransportIterations 20 -TransportWarmupIterations 0 }
    }
    catch {
        if ($_.Exception.Message -notlike '*failed*') { throw }
        $failed = $true
    }
    if ($failed -ne ($case -like '*-failure')) { throw "Unexpected benchmark outcome: $case" }
    if ($case -ceq 'shared-cli' -and (
        ($buildOrder -join '|') -cne 'restore AudioPilot.CliHost|build AudioPilot.CliHost|restore AudioPilot.Benchmarks|build AudioPilot.Benchmarks' -or
        @($buildKeys | Sort-Object -Unique).Count -ne 1)) { throw 'The benchmark and CLI fixture did not share their build.' }
    $remaining = @($owned | Where-Object { Test-Path -LiteralPath $_ })
    if ($remaining.Count -ne $(if ($case -ceq 'linked-content') { 1 } else { 0 })) { throw "Benchmark scratch was not reclaimed correctly: $case" }
    if ((Get-Content -LiteralPath (Join-Path $unrelated 'keep')) -cne 'untouched' -or
        @(Get-ChildItem -LiteralPath $fixture -Recurse -File -Filter keep).Count -lt 7) { throw 'Benchmark cleanup touched unrelated data.' }
    if ($case -in @('success','run-failure','linked-content','shared-cli') -and
        @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/benchmarks') -Recurse -File -Filter report.json).Count -ne 1) { throw 'Benchmark cleanup lost its report.' }
    if ($case -ceq 'linked-content') { Remove-Item -LiteralPath (Join-Path $owned[0] 'linked') -Force }
}
Write-Host 'Benchmark cleanup checks passed: complete reference graph, failures, reports, unrelated runs, and directory links.'
