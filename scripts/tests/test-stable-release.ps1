Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../lib/StableRelease.ps1')
$root = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))) "artifacts/testresults/stable-release/$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($root) | Out-Null
$version = '1.2.3'
$publisher = 'A' * 64
@{version=$version} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'release-manifest.json')
function Write-TestZip([string]$Path, [hashtable]$Files) {
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Files.Keys) {
            $stream = $archive.CreateEntry($name).Open()
            try { $stream.Write([Text.Encoding]::UTF8.GetBytes($Files[$name])) } finally { $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
}
function Assert-Rejected([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid stable release was accepted: $Name." }
    Write-Output "Rejected: $Name."
}
$targets = @{}
$paths = @{}
foreach ($architecture in @('x64', 'x86', 'arm64')) {
    foreach ($mode in @('SelfContained', 'FrameworkDependent', 'MSI')) {
        $isMsi = $mode -ceq 'MSI'
        $name = if ($isMsi) { "AudioPilot-$version-win-$architecture.msi" } else { "AudioPilot-$version-$mode-win-$architecture.update.zip" }
        $path = Join-Path $root $name
        if ($isMsi) {
            Set-Content -LiteralPath $path -Value "installer-$architecture"
            Copy-Item -LiteralPath $path -Destination (Join-Path $root "AudioPilot-$version-$architecture.msi")
        }
        else {
            $payload = @{'AudioPilot.exe'="binary-$architecture-$mode";'ABOUT.txt'='About';'licenses/LICENSE'='License';'licenses/THIRD-PARTY-NOTICES.txt'='Notices';'licenses/UPDATER-NOTICES.txt'='Updater notices'}
            Write-TestZip $path $payload
            $generation = $version + '_' + ('a' * 32)
            $outer = @{'AudioPilot/AudioPilot.exe'='launcher';'AudioPilot/AudioPilot.Cli.exe'='cli';'AudioPilot/app/.updater'='AudioPilot updater workspace 1'}
            $outer['AudioPilot/app/portable-layout.json'] = @{Schema=1;DiagnosticId=('b'*32);Managed=$true} | ConvertTo-Json -Compress
            $outer['AudioPilot/app/deployment.json'] = @{Schema=1;Current=$generation;Previous=$null;PendingHealth=$false} | ConvertTo-Json -Compress
            $outer["AudioPilot/app/versions/$generation/.updater-owned.json"] = @{Schema=1;Kind='generation';Identity=$generation} | ConvertTo-Json -Compress
            foreach ($file in $payload.Keys) {
                $outer["AudioPilot/app/versions/$generation/$file"] = $payload[$file]
                if ($file -cne 'AudioPilot.exe') { $outer["AudioPilot/$file"] = $payload[$file] }
            }
            Write-TestZip (Join-Path $root "AudioPilot-$version-$mode-win-$architecture.zip") $outer
        }
        $paths[$name] = $path
        $targets[$name] = @{length=(Get-Item -LiteralPath $path).Length;hashes=@{sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()};custom=@{
            product='AudioPilot';channel='stable';version=$version;kind=$(if ($isMsi) {'msi'} else {'portable'});architecture="win-$architecture";
            runtime=$(if ($mode -ceq 'FrameworkDependent') {'framework-dependent'} else {'self-contained'});windowsPublisherSha256=$publisher;windowsTrust='self-signed'}}
    }
}
$parameters = @{ReleaseRoot=$root;Targets=$targets;PackagePaths=$paths;PublisherSha256=$publisher}
if ((Assert-AudioPilotStableReleasePackages @parameters) -ne 9) { throw 'The complete matching release was not accepted.' }
$msi = "AudioPilot-$version-win-x64.msi"
$target = $targets[$msi]
$targets.Remove($msi)
Assert-Rejected 'missing variant' { Assert-AudioPilotStableReleasePackages @parameters }
$targets[$msi] = $target
$target.custom.channel = 'staging'
Assert-Rejected 'cross-channel identity' { Assert-AudioPilotStableReleasePackages @parameters }
$target.custom.channel = 'stable'
Add-Content -LiteralPath $paths[$msi] -Value 'old installer'
Assert-Rejected 'corrupt package' { Assert-AudioPilotStableReleasePackages @parameters }
$target.length = (Get-Item -LiteralPath $paths[$msi]).Length
$target.hashes.sha256 = (Get-FileHash -LiteralPath $paths[$msi]).Hash.ToLowerInvariant()
Assert-Rejected 'validly hashed but stale MSI' { Assert-AudioPilotStableReleasePackages @parameters }
Copy-Item -LiteralPath (Join-Path $root "AudioPilot-$version-x64.msi") -Destination $paths[$msi] -Force
$target.length = (Get-Item -LiteralPath $paths[$msi]).Length
$target.hashes.sha256 = (Get-FileHash -LiteralPath $paths[$msi]).Hash.ToLowerInvariant()
$portable = "AudioPilot-$version-FrameworkDependent-win-arm64.update.zip"
$archive = [IO.Compression.ZipFile]::Open($paths[$portable], [IO.Compression.ZipArchiveMode]::Update)
try {
    $archive.GetEntry('AudioPilot.exe').Delete()
    $stream = $archive.CreateEntry('AudioPilot.exe').Open()
    try { $stream.Write([Text.Encoding]::UTF8.GetBytes('stale binary')) } finally { $stream.Dispose() }
} finally { $archive.Dispose() }
$targets[$portable].length = (Get-Item -LiteralPath $paths[$portable]).Length
$targets[$portable].hashes.sha256 = (Get-FileHash -LiteralPath $paths[$portable]).Hash.ToLowerInvariant()
Assert-Rejected 'validly hashed but stale portable payload' { Assert-AudioPilotStableReleasePackages @parameters }
Write-Output 'Stable release synchronization checks passed; no network or signing keys used.'
