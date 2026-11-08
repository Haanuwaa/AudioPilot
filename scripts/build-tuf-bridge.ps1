[CmdletBinding()]
param(
    [string]$OutputRoot = 'artifacts/tuf-proof/bridge',
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')][string[]]$Runtimes = @('win-x64', 'win-x86', 'win-arm64'),
    [switch]$ScanVulnerabilities
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$module = Join-Path $repo 'AudioPilot.Updater/TufBridge'
$output = [IO.Path]::GetFullPath($OutputRoot, $repo)
if (-not $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep proof outputs inside the repository.' }
for ($current = $output; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected build output.' }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$oldEnvironment = @{}
foreach ($name in @('GOOS', 'GOARCH', 'CGO_ENABLED', 'TEMP', 'TMP')) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $module
try {
    $format = @(& gofmt -l .)
    if ($LASTEXITCODE -ne 0 -or $format.Count) { throw 'Format the TUF adapter with gofmt before building.' }
    & go mod verify
    if ($LASTEXITCODE -ne 0) { throw 'Go module checksum verification failed.' }
    $env:CGO_ENABLED = '0'
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'
    $testTemp = Join-Path $output 'test-temp'
    [IO.Directory]::CreateDirectory($testTemp) | Out-Null
    $env:TEMP = $testTemp
    $env:TMP = $testTemp
    & go test -count=1 ./...
    if ($LASTEXITCODE -ne 0) { throw 'TUF adapter tests failed.' }
    $builds = @()
    foreach ($runtime in $Runtimes) {
        $env:GOARCH = @{ 'win-x64'='amd64'; 'win-x86'='386'; 'win-arm64'='arm64' }[$runtime]
        $directory = Join-Path $output $runtime
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $exe = Join-Path $directory 'AudioPilot.TufBridge.exe'
        & go build -mod=readonly -trimpath -ldflags '-s -w' -o $exe .
        if ($LASTEXITCODE -ne 0) { throw "TUF bridge build failed for $runtime." }
        & go version -m $exe | Set-Content -LiteralPath (Join-Path $directory 'build-info.txt') -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw 'Missing Go build provenance.' }
        $builds += @{ runtime=$runtime; bytes=(Get-Item -LiteralPath $exe).Length; sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash }
    }
    $env:GOARCH = 'amd64'
    if ($ScanVulnerabilities) {
        & go run golang.org/x/vuln/cmd/govulncheck@v1.9.0 -json ./... > (Join-Path $output 'govulncheck.json')
        if ($LASTEXITCODE -ne 0) { throw 'Go vulnerability scan failed. See govulncheck.json.' }
        # JSON mode is an event stream; normal mode also enforces the scanner's finding exit status.
        & go run golang.org/x/vuln/cmd/govulncheck@v1.9.0 ./... > (Join-Path $output 'govulncheck.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Reachable Go vulnerability or scan failure. See govulncheck.txt.' }
    }
    $modules = (& go list -m -json all | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Go module inventory failed.' }
    Set-Content -LiteralPath (Join-Path $output 'modules.json-stream') -Value $modules -Encoding utf8
    $goRoot = (& go env GOROOT).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Missing Go toolchain license.' }
    Copy-Item -LiteralPath (Join-Path $goRoot 'LICENSE') -Destination (Join-Path $output 'Go-LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $module 'go.mod'), (Join-Path $module 'go.sum') -Destination $output
    $dependencies = @(& go list -deps -f '{{if .Module}}{{if not .Module.Main}}{{.Module.Path}}|{{.Module.Dir}}{{end}}{{end}}' . | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'Go license inventory failed.' }
    foreach ($dependency in $dependencies) {
        if (-not $dependency) { continue }
        $parts = $dependency.Split('|', 2)
        $licenses = @(Get-ChildItem -LiteralPath $parts[1] -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)' })
        if ($licenses.Count -eq 0) { throw "Missing dependency license: $($parts[0])" }
        $destination = Join-Path $output ('licenses/' + $parts[0].Replace('/', '_'))
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $licenses | Copy-Item -Destination $destination -Force
    }
    @{ library='go-tuf/v2 v2.4.2'; go=(& go version); builds=$builds; transports=@('local-files','loopback-https','pinned-github-staging-sandbox-only'); publicNetworkEnabledByDefault=$false; publicLiveUpdates=$false } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
}
finally {
    Pop-Location
    foreach ($name in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') }
}
