param(
    [string]$SolutionPath = "AudioPilot.sln",
    [string]$Configuration = "Release",
    [string[]]$DotnetBuildArgs = @("--nologo"),
    [switch]$Clean,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $SolutionPath)) {
    throw "Solution file not found: $SolutionPath"
}

if ($Clean) {
    & dotnet clean $SolutionPath -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if (-not $NoRestore) {
    & dotnet restore $SolutionPath --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

& dotnet build $SolutionPath -c $Configuration --no-restore @DotnetBuildArgs
exit $LASTEXITCODE