param(
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$actionFlag = if ($Check) { "--check" } else { "--write" }

& dotnet run --project .\AudioPilot.CliHost -- internal-docs-sync $actionFlag
exit $LASTEXITCODE
