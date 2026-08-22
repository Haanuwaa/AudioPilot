param(
    [switch]$Check,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$actionFlag = if ($Check) { "--check" } else { "--write" }

$runArguments = @('run', '--project', './AudioPilot.CliHost', '--configuration', $Configuration)
if ($NoBuild) { $runArguments += '--no-build' }
& dotnet @runArguments -- internal-docs-sync $actionFlag
exit $LASTEXITCODE
