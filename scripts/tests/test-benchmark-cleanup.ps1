Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = Join-Path $repo "artifacts/testresults/benchmark-cleanup/$([guid]::NewGuid().ToString('N'))"
foreach ($case in @('success','restore-failure','build-failure','verify-failure','run-failure','linked-content')) {
    $fixture = Join-Path $root $case
    New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts'), (Join-Path $fixture 'AudioPilot.Benchmarks'),
        (Join-Path $fixture 'AudioPilot'), (Join-Path $fixture 'AudioPilot.Updater/Protocol') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'scripts/benchmark.ps1') -Destination (Join-Path $fixture 'scripts')
    Set-Content -LiteralPath (Join-Path $fixture 'AudioPilot.Benchmarks/AudioPilot.Benchmarks.csproj') -Value '<Project><ItemGroup><ProjectReference Include="../AudioPilot/AudioPilot.csproj"/></ItemGroup></Project>'
    Set-Content -LiteralPath (Join-Path $fixture 'AudioPilot/AudioPilot.csproj') -Value '<Project><ItemGroup><ProjectReference Include="../AudioPilot.Updater/Protocol/AudioPilot.UpdateProtocol.csproj"/></ItemGroup></Project>'
    Set-Content -LiteralPath (Join-Path $fixture 'AudioPilot.Updater/Protocol/AudioPilot.UpdateProtocol.csproj') -Value '<Project/>'
    $owned = [Collections.Generic.List[string]]::new()
    $unrelated = Join-Path $fixture 'unrelated'
    New-Item -ItemType Directory -Path $unrelated -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $unrelated 'keep') -Value 'untouched'
    function dotnet {
        if ($args[0] -ceq 'restore') {
            $key = @($args | Where-Object {$_ -clike '-p:BuildIsolationKey=*'})[0].Substring('-p:BuildIsolationKey='.Length)
            foreach ($project in @('AudioPilot.Benchmarks','AudioPilot','AudioPilot.Updater/Protocol')) {
                foreach ($kind in @('bin','obj')) {
                    $directory = Join-Path $fixture "$project/$kind/isolated/$key"
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
        if ($args[1] -ceq 'verify') { Set-Content -LiteralPath (Join-Path $args[2] 'report.json') -Value 'evidence' }
        $global:LASTEXITCODE = if (($case -ceq 'restore-failure' -and $args[0] -ceq 'restore') -or
            ($case -ceq 'build-failure' -and $args[0] -ceq 'build') -or ($case -ceq 'verify-failure' -and $args[1] -ceq 'verify') -or
            ($case -ceq 'run-failure' -and $args[1] -ceq 'transport')) { 7 } else { 0 }
    }
    $failed = $false
    try { & (Join-Path $fixture 'scripts/benchmark.ps1') -Mode Transport -TransportIterations 20 -TransportWarmupIterations 0 }
    catch {
        if ($_.Exception.Message -notlike '*failed*') { throw }
        $failed = $true
    }
    if ($failed -ne ($case -like '*-failure')) { throw "Unexpected benchmark outcome: $case" }
    $remaining = @($owned | Where-Object { Test-Path -LiteralPath $_ })
    if ($remaining.Count -ne $(if ($case -ceq 'linked-content') { 1 } else { 0 })) { throw "Benchmark scratch was not reclaimed correctly: $case" }
    if ((Get-Content -LiteralPath (Join-Path $unrelated 'keep')) -cne 'untouched' -or
        @(Get-ChildItem -LiteralPath $fixture -Recurse -File -Filter keep).Count -lt 7) { throw 'Benchmark cleanup touched unrelated data.' }
    if ($case -in @('success','run-failure','linked-content') -and
        @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/benchmarks') -Recurse -File -Filter report.json).Count -ne 1) { throw 'Benchmark cleanup lost its report.' }
    if ($case -ceq 'linked-content') { Remove-Item -LiteralPath (Join-Path $owned[0] 'linked') -Force }
}
Write-Host 'Benchmark cleanup checks passed: complete reference graph, failures, reports, unrelated runs, and directory links.'
