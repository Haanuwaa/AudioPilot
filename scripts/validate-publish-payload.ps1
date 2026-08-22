[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-x86', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('SelfContained', 'FrameworkDependent')][string]$DeploymentMode = 'SelfContained',
    [string]$ExpectedCommit,
    [switch]$FromArchive,
    [switch]$WriteManifest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\', '/')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = "$root.payload.json"
function Require-File([string]$RelativePath) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    if (-not $path.StartsWith("$root\", [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.File]::Exists($path)) { throw "Incomplete publish payload: $RelativePath" }
    return $path
}
function Assert-Machine([string]$RelativePath) {
    $stream = [IO.File]::OpenRead((Require-File $RelativePath))
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid PE file: $RelativePath" }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 0x40 -or $offset -gt $stream.Length - 6) { throw "Invalid PE header: $RelativePath" }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $RelativePath" }
        $expected = @{ 'win-x64' = 0x8664; 'win-x86' = 0x14c; 'win-arm64' = 0xaa64 }[$RuntimeIdentifier]
        if ($reader.ReadUInt16() -ne $expected) { throw "Payload architecture mismatch: $RelativePath ($RuntimeIdentifier expected)." }
    }
    finally { $reader.Dispose() }
}
if ($FromArchive -and $WriteManifest) { throw 'Archive validation cannot write a publish manifest.' }
foreach ($file in @('ABOUT.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) { $null = Require-File $file }
foreach ($file in @('LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
    if ((Get-Item -LiteralPath (Require-File $file)).Length -eq 0) { throw "Empty publish notice: $file" }
}
$selfContained = $DeploymentMode -eq 'SelfContained'
$deploymentLabel = if ($selfContained) { 'Self-contained' } else { 'Framework-dependent' }
if ($selfContained) {
    foreach ($file in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')) { Assert-Machine $file }
}
$about = [IO.File]::ReadAllText((Require-File 'ABOUT.txt'))
foreach ($line in @("Version: $Version", "Runtime identifier: $RuntimeIdentifier", "Build configuration: $Configuration", "Deployment: $deploymentLabel")) {
    if ($line -cnotin ($about -split '\r?\n')) { throw "Payload build information mismatch: $line" }
}
$productVersions = @()
foreach ($entry in @('AudioPilot', 'AudioPilot.Cli')) {
    Assert-Machine "$entry.exe"
    $assembly = Require-File "$entry.dll"
    if ([Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString() -ne "$Version.0") { throw "Payload assembly version mismatch: $entry" }
    $productVersions += [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion
    $runtime = Get-Content -LiteralPath (Require-File "$entry.runtimeconfig.json") -Raw | ConvertFrom-Json -AsHashtable
    if ($selfContained) {
        if ($runtime.runtimeOptions.Contains('framework') -or $runtime.runtimeOptions.Contains('frameworks') -or
            -not $runtime.runtimeOptions.Contains('includedFrameworks')) { throw "Payload is not self-contained: $entry" }
        $frameworks = @($runtime.runtimeOptions.includedFrameworks.name)
    }
    else {
        if ($runtime.runtimeOptions.Contains('includedFrameworks')) { throw "Payload is not framework-dependent: $entry" }
        $frameworks = @()
        foreach ($key in @('framework', 'frameworks')) {
            if ($runtime.runtimeOptions.Contains($key)) { $frameworks += @($runtime.runtimeOptions[$key].name) }
        }
    }
    if ('Microsoft.NETCore.App' -notin $frameworks) { throw "Payload is missing its .NET runtime configuration: $entry" }
    if ($entry -eq 'AudioPilot' -and 'Microsoft.WindowsDesktop.App' -notin $frameworks) { throw 'Payload is missing its Windows Desktop runtime configuration.' }
    $deps = Get-Content -LiteralPath (Require-File "$entry.deps.json") -Raw | ConvertFrom-Json -AsHashtable
    if (-not $deps.runtimeTarget.name.EndsWith("/$RuntimeIdentifier", [StringComparison]::Ordinal)) { throw "Dependency runtime mismatch: $entry" }
    foreach ($library in $deps.targets[$deps.runtimeTarget.name].Values) {
        foreach ($kind in @('runtime', 'native', 'resources')) {
            if (-not $library.Contains($kind)) { continue }
            foreach ($asset in $library[$kind].GetEnumerator()) {
                if ($asset.Key.EndsWith('/_._')) { continue }
                $name = [IO.Path]::GetFileName($asset.Key)
                if ($kind -eq 'resources') { $name = Join-Path $asset.Value.locale $name }
                $null = Require-File $name
                if ($kind -eq 'native' -and [IO.Path]::GetExtension($name) -in @('.dll', '.exe')) { Assert-Machine $name }
            }
        }
    }
}
if ($productVersions[0] -cne $productVersions[1] -or ($productVersions[0] -split '\+', 2)[0] -cne $Version) { throw 'App and CLI build provenance do not match.' }
$commit = $ExpectedCommit
if ([string]::IsNullOrWhiteSpace($commit)) {
    $commit = & git -C $repository rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine payload source commit.' }
}
if ($commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid expected payload source commit.' }
if ($productVersions[0] -cne "$Version+$commit") { throw 'Payload assemblies were not built from the current source commit.' }
if ($FromArchive) {
    Write-Host "Validated complete archived $DeploymentMode $RuntimeIdentifier payload."
    return
}
$inputs = & git -C $repository ls-files --cached --others --exclude-standard -- AudioPilot AudioPilot.CliHost Directory.Build.props Directory.Build.targets Directory.Packages.props Version.props global.json nuget.config LICENSE THIRD-PARTY-NOTICES.txt
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine publish source inputs.' }
$sourceLines = foreach ($inputPath in ($inputs | Sort-Object -Unique)) {
    $path = Join-Path $repository $inputPath
    if ([IO.File]::Exists($path)) { "$inputPath $((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)" }
}
$sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($sourceLines -join "`n"))))
$files = [ordered]@{}
if (@(Get-ChildItem -LiteralPath $root -Directory -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Publish payload contains a directory link.' }
foreach ($file in (Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName)) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Payload contains a file link: $($file.Name)" }
    $files[[IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
if ($WriteManifest) {
    [ordered]@{ schema = 2; commit = $commit; sourceHash = $sourceHash; version = $Version; configuration = $Configuration; rid = $RuntimeIdentifier; deploymentMode = $DeploymentMode; files = $files } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
}
else {
    if (-not [IO.File]::Exists($manifestPath)) { throw 'Publish provenance manifest is missing. Republish the payload before packaging.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.schema -ne 2 -or $manifest.deploymentMode -cne $DeploymentMode -or $manifest.commit -cne $commit -or $manifest.sourceHash -cne $sourceHash -or
        $manifest.version -cne $Version -or $manifest.configuration -cne $Configuration -or $manifest.rid -cne $RuntimeIdentifier) {
        throw 'Publish provenance is stale or does not match this build. Republish the payload before packaging.'
    }
    if ($manifest.files.Count -ne $files.Count) { throw 'Publish payload file set changed after publishing.' }
    foreach ($file in $files.Keys) {
        if (-not $manifest.files.Contains($file) -or $manifest.files[$file] -cne $files[$file]) { throw "Publish payload changed after publishing: $file" }
    }
}
Write-Host "Validated complete $RuntimeIdentifier payload ($($files.Count) files, version $Version)."
