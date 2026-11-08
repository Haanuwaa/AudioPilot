[CmdletBinding()]
param([string]$ReportDirectory = 'artifacts/dependencies/go')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($ReportDirectory, $repo)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$saved = @{}
foreach ($name in @('GOOS', 'GOARCH', 'CGO_ENABLED', 'GOBIN', 'PATH')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$tools = Join-Path ([IO.Path]::GetTempPath()) ('AudioPilot-govulncheck-' + [guid]::NewGuid().ToString('N'))
$scanner = $null
Push-Location (Join-Path $repo 'AudioPilot.Updater/TufBridge')
try {
    $env:GOOS = 'windows'; $env:GOARCH = 'amd64'; $env:CGO_ENABLED = '0'
    & go mod verify
    if ($LASTEXITCODE -ne 0) { throw 'Go module checksum verification failed.' }
    $hostJson = & go env -json GOHOSTOS GOHOSTARCH
    if ($LASTEXITCODE -ne 0) { throw 'Go scanner host discovery failed.' }
    $goHost = ($hostJson -join "`n") | ConvertFrom-Json
    if ($goHost.GOHOSTOS -cnotin @('windows', 'linux', 'darwin') -or $goHost.GOHOSTARCH -cnotin @('amd64', 'arm64', '386')) {
        throw 'Unsupported Go scanner host.'
    }
    $env:GOOS = $goHost.GOHOSTOS; $env:GOARCH = $goHost.GOHOSTARCH
    New-Item -ItemType Directory -Path $tools | Out-Null
    $env:GOBIN = $tools
    $scanner = Join-Path $tools $(if ($goHost.GOHOSTOS -ceq 'windows') { 'govulncheck.exe' } else { 'govulncheck' })
    & go install -trimpath golang.org/x/vuln/cmd/govulncheck@v1.9.0
    if ($LASTEXITCODE -ne 0) { throw 'Go vulnerability scanner build failed.' }
    if (-not (Test-Path -LiteralPath $scanner -PathType Leaf)) { throw 'Go vulnerability scanner is missing.' }
    $env:PATH = $tools + [IO.Path]::PathSeparator + $saved['PATH']
    foreach ($target in @(
        @{ OS = 'windows'; Architecture = 'amd64'; Packages = './...' },
        @{ OS = 'windows'; Architecture = '386'; Packages = './...' },
        @{ OS = 'windows'; Architecture = 'arm64'; Packages = './...' },
        @{ OS = 'linux'; Architecture = 'amd64'; Packages = './cmd/staging-publisher' }
    )) {
        $env:GOOS = $target.OS; $env:GOARCH = $target.Architecture
        $report = "govulncheck-$($target.OS)-$($target.Architecture)"
        Write-Host "Scanning Go vulnerabilities: $($target.OS)/$($target.Architecture)"
        & govulncheck -json $target.Packages > (Join-Path $output "$report.json")
        if ($LASTEXITCODE -ne 0) { throw "Go vulnerability scan failed. See $report.json." }
        # JSON is an event stream; normal mode also enforces the scanner's finding exit status.
        & govulncheck $target.Packages > (Join-Path $output "$report.txt")
        if ($LASTEXITCODE -ne 0) { throw "Reachable Go vulnerability or scan failure. See $report.txt." }
    }
}
finally {
    Pop-Location
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    try {
        if ($scanner) { [IO.File]::Delete($scanner) }
        if (Test-Path -LiteralPath $tools) { [IO.Directory]::Delete($tools) }
    }
    catch { Write-Warning "Temporary Go scanner cleanup deferred: $($_.Exception.GetType().Name)" }
}
