[CmdletBinding()]
param(
    [ValidateSet('Vulnerabilities', 'Outdated')][string]$Mode = 'Vulnerabilities',
    [string]$ReportDirectory = 'artifacts/dependencies',
    [string]$JobSummaryPath = $env:GITHUB_STEP_SUMMARY
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$reportRoot = [IO.Path]::GetFullPath($ReportDirectory, $repoRoot)
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$findings = [Collections.Generic.List[object]]::new()
$targets = @(
    @{ Name = 'solution'; Path = 'AudioPilot.sln'; Projects = @(
        'AudioPilot/AudioPilot.csproj', 'AudioPilot.Tests/AudioPilot.Tests.csproj',
        'AudioPilot.CliHost/AudioPilot.CliHost.csproj', 'AudioPilot.Installer/AudioPilot.Installer.wixproj',
        'AudioPilot.UpdateProtocol/AudioPilot.UpdateProtocol.csproj') },
    @{ Name = 'benchmarks'; Path = 'AudioPilot.Benchmarks/AudioPilot.Benchmarks.csproj'; Projects = @(
        'AudioPilot.Benchmarks/AudioPilot.Benchmarks.csproj') }
)
foreach ($target in $targets) {
    $arguments = @('list', (Join-Path $repoRoot $target.Path), 'package', '--format', 'json', '--output-version', '1', '--no-restore')
    if ($Mode -eq 'Vulnerabilities') { $arguments += @('--vulnerable', '--include-transitive') }
    else { $arguments += '--outdated' }
    $raw = (& dotnet @arguments | Out-String).Trim()
    $commandExit = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $reportRoot "$($Mode.ToLowerInvariant())-$($target.Name).json") -Value $raw -Encoding utf8
    if ($commandExit -ne 0) { throw "NuGet $Mode query failed for $($target.Name) (exit $commandExit). See $reportRoot." }
    $report = $raw | ConvertFrom-Json -AsHashtable
    if ($report -isnot [Collections.IDictionary] -or $report['version'] -ne 1) { throw 'Unsupported NuGet JSON report.' }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $target.Projects) { [void]$expected.Add([IO.Path]::GetFullPath($path, $repoRoot)) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $problems = [Collections.Generic.List[object]]::new()
    foreach ($problem in @($report['problems'])) { if ($null -ne $problem) { $problems.Add($problem) } }
    foreach ($project in @($report['projects'])) {
        if ($null -eq $project -or [string]::IsNullOrWhiteSpace($project['path'])) { throw 'NuGet report has a missing project path.' }
        $path = [IO.Path]::GetFullPath([string]$project['path'], $repoRoot)
        if (-not $expected.Contains($path) -or -not $seen.Add($path)) { throw "Unexpected or duplicate NuGet report project: $path" }
        foreach ($problem in @($project['problems'])) { if ($null -ne $problem) { $problems.Add($problem) } }
        foreach ($framework in @($project['frameworks'])) {
            if ($null -eq $framework) { continue }
            foreach ($problem in @($framework['problems'])) { if ($null -ne $problem) { $problems.Add($problem) } }
            foreach ($package in @($framework['topLevelPackages']) + @($framework['transitivePackages'])) {
                if ($null -eq $package) { continue }
                if ($Mode -eq 'Vulnerabilities') {
                    foreach ($vulnerability in @($package['vulnerabilities'])) {
                        if ($null -eq $vulnerability) { continue }
                        $findings.Add([pscustomobject]@{
                            Project = $path; Framework = $framework['framework']; Package = $package['id']
                            Resolved = $package['resolvedVersion']; Severity = $vulnerability['severity']; Advisory = $vulnerability['advisoryurl']
                        })
                    }
                }
                elseif ($package['latestVersion']) {
                    $findings.Add([pscustomobject]@{
                        Project = $path; Framework = $framework['framework']; Package = $package['id']
                        Requested = $package['requestedVersion']; Resolved = $package['resolvedVersion']; Latest = $package['latestVersion']
                    })
                }
            }
        }
    }
    if (-not $seen.SetEquals($expected)) { throw "Incomplete NuGet report for $($target.Name)." }
    if ($problems.Count -gt 0) {
        $problems | ConvertTo-Json -Depth 10 | Write-Host
        throw "NuGet reported collection problems for $($target.Name); results cannot be reported as clean."
    }
}
if ($findings.Count -gt 0) {
    $findings | Sort-Object Project, Package | Format-Table -AutoSize | Out-String | Write-Host
    if ($Mode -eq 'Vulnerabilities') { throw 'Dependency vulnerability scan failed.' }
    Write-Warning 'Outdated direct NuGet packages detected. Updates remain advisory; check Dependabot.'
    if ($JobSummaryPath) {
        @('## Outdated direct NuGet packages', '', 'Updates are advisory. Vulnerabilities and query failures fail the workflow.', '', '```json',
            ($findings | ConvertTo-Json -Depth 5), '```') | Add-Content -LiteralPath $JobSummaryPath
    }
}
else {
    $description = if ($Mode -eq 'Vulnerabilities') { 'vulnerable' } else { 'outdated direct' }
    Write-Host "No $description NuGet packages detected across the solution and benchmark project."
}
