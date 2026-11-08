Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = Join-Path $repo "artifacts/testresults/go-dependencies/$([guid]::NewGuid().ToString('N'))"
$saved = @{}
foreach ($name in @('GOOS','GOARCH','CGO_ENABLED','GOBIN','PATH')) { $saved[$name]=[Environment]::GetEnvironmentVariable($name) }
try {
    $env:GOOS='sentinel-os'; $env:GOARCH='sentinel-arch'; $env:CGO_ENABLED='sentinel-cgo'; $env:GOBIN='sentinel-bin'
    $env:PATH = $saved['PATH'] + [IO.Path]::PathSeparator + 'AudioPilot-sentinel-path'
    $expectedEnvironment = @{}
    foreach ($name in $saved.Keys) { $expectedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
    foreach ($case in @('success','linux-host','module-failure','host-failure','build-failure','json-failure','finding','publisher-finding')) {
        $calls = [Collections.Generic.List[object]]::new()
        $toolDirectories = [Collections.Generic.List[string]]::new()
        $hostOS = if ($case -ceq 'linux-host') { 'linux' } else { 'windows' }
        function go {
            $calls.Add(@{Tool='go'; Arguments=@($args)})
            $global:LASTEXITCODE = 0
            if ($env:CGO_ENABLED -cne '0') { throw 'Unexpected CGO setting.' }
            switch ($args[0]) {
                'mod' {
                    if ($case -ceq 'module-failure') { $global:LASTEXITCODE=3 }
                }
                'env' {
                    if ($case -ceq 'host-failure') { $global:LASTEXITCODE=3; return }
                    @{GOHOSTOS=$hostOS;GOHOSTARCH='amd64'} | ConvertTo-Json -Compress
                }
                'install' {
                    if ($env:GOOS -cne $hostOS -or $env:GOARCH -cne 'amd64') { throw 'Scanner was not built for its host.' }
                    if ($args.Count -ne 3 -or $args[1] -cne '-trimpath' -or $args[2] -cne 'golang.org/x/vuln/cmd/govulncheck@v1.9.0') {
                        throw 'Unpinned vulnerability tool.'
                    }
                    $toolDirectories.Add($env:GOBIN)
                    $name = if ($hostOS -ceq 'windows') { 'govulncheck.exe' } else { 'govulncheck' }
                    [IO.File]::WriteAllText((Join-Path $env:GOBIN $name), 'fixture scanner')
                    if ($case -ceq 'build-failure') { $global:LASTEXITCODE=3 }
                }
                default { throw 'Unexpected Go command.' }
            }
        }
        function govulncheck {
            if ($env:CGO_ENABLED -cne '0' -or -not $env:PATH.StartsWith($env:GOBIN + [IO.Path]::PathSeparator, [StringComparison]::Ordinal)) {
                throw 'Scanner environment was not isolated.'
            }
            $structured = $args -ccontains '-json'
            $packages = $args[-1]
            $calls.Add(@{Tool='scanner';OS=$env:GOOS;Architecture=$env:GOARCH;Structured=$structured;Packages=$packages})
            $global:LASTEXITCODE = if (($case -ceq 'json-failure' -and $env:GOARCH -ceq '386' -and $structured) -or
                ($case -ceq 'finding' -and $env:GOARCH -ceq 'arm64' -and -not $structured) -or
                ($case -ceq 'publisher-finding' -and $env:GOOS -ceq 'linux' -and -not $structured)) { 3 } else { 0 }
            if ($structured) { '{"config":{"goos":"fixture"}}' } else { 'fixture scan result' }
        }
        $failed=$false
        $location=Get-Location
        $destination=Join-Path $root $case
        try { & (Join-Path $repo 'scripts/scan-go-dependencies.ps1') -ReportDirectory $destination }
        catch {
            if ($_.Exception.Message -notmatch 'verification failed|discovery failed|build failed|scan fail|scan failure') { throw }
            $failed=$true
        }
        if ($failed -ne ($case -cnotin @('success','linux-host'))) { throw "Scan did not preserve its outcome: $case" }
        $expected = switch ($case) { 'module-failure' {1}; 'host-failure' {2}; 'build-failure' {3}; 'json-failure' {6}; 'finding' {9}; default {11} }
        if ($calls.Count -ne $expected -or (Get-Location).Path -cne $location.Path) { throw 'Scan continued after failure or changed caller location.' }
        foreach ($name in $expectedEnvironment.Keys) {
            if ([Environment]::GetEnvironmentVariable($name) -cne $expectedEnvironment[$name]) { throw "Scan changed caller environment: $name" }
        }
        foreach ($directory in $toolDirectories) { if (Test-Path -LiteralPath $directory) { throw 'Temporary scanner was retained.' } }
        if (-not $failed) {
            $scans = @($calls | Where-Object Tool -CEQ 'scanner')
            $actual = @($scans | ForEach-Object { "$($_.OS)/$($_.Architecture):$($_.Packages):$($_.Structured)" })
            $expectedTargets = foreach ($target in @('windows/amd64','windows/386','windows/arm64','linux/amd64')) {
                $packages = if ($target.StartsWith('linux/')) { './cmd/staging-publisher' } else { './...' }
                "${target}:${packages}:True"; "${target}:${packages}:False"
            }
            if (($actual -join '|') -cne ($expectedTargets -join '|') -or @(Get-ChildItem -LiteralPath $destination -File).Count -ne 8) {
                throw 'Scan did not cover every shipping architecture and the Linux publisher in both report modes.'
            }
        }
    }
}
finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) } }
Write-Host 'Go vulnerability checks cover all platforms, preserve failures and caller state, and remove the temporary pinned scanner.'
exit 0
