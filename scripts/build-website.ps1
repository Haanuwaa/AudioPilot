[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseMetadataPath,
    [string]$OutputDirectory = 'artifacts/website/public'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Get-Content -LiteralPath $ReleaseMetadataPath -Raw | ConvertFrom-Json
if ($release.draft -or $release.prerelease -or $release.tag_name -cnotmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'The website requires a published stable release with a numeric version.'
}
$version = $release.tag_name.Substring(1)
$repositoryUrl = 'https://github.com/Haanuwaa/AudioPilot'
$downloads = [ordered]@{}
foreach ($architecture in @('x64', 'arm64', 'x86')) {
    $packages = [ordered]@{}
    foreach ($kind in @('installer', 'framework', 'portable')) {
        $name = switch ($kind) {
            installer { "AudioPilot-$version-$architecture.msi" }
            framework { "AudioPilot-$version-FrameworkDependent-win-$architecture.zip" }
            portable { "AudioPilot-$version-SelfContained-win-$architecture.zip" }
        }
        $assets = @($release.assets | Where-Object { $_.name -ceq $name })
        if ($assets.Count -ne 1) { throw "The published release must contain exactly one $name." }
        $asset = $assets[0]
        $expectedUrl = "$repositoryUrl/releases/download/$($release.tag_name)/$name"
        if ($asset.browser_download_url -cne $expectedUrl -or $asset.size -le 0) { throw "Invalid release asset: $name." }
        $packages[$kind] = @{ url = $expectedUrl; size = ('{0:0.0} MB' -f ($asset.size / 1e6)) }
    }
    $downloads[$architecture] = $packages
}
$html = Get-Content -LiteralPath (Join-Path $repo 'website/index.html') -Raw
$tokens = @{
    VERSION = $version
    DOWNLOADS_JSON = $downloads | ConvertTo-Json -Depth 5 -Compress -EscapeHandling EscapeHtml
    STYLE_VERSION = (Get-FileHash -LiteralPath (Join-Path $repo 'website/styles.css') -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()
    SCRIPT_VERSION = (Get-FileHash -LiteralPath (Join-Path $repo 'website/site.js') -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()
}
foreach ($kind in @('installer', 'framework', 'portable')) {
    $tokens["$($kind.ToUpperInvariant())_URL"] = $downloads.x64[$kind].url
    $tokens["$($kind.ToUpperInvariant())_SIZE"] = $downloads.x64[$kind].size
}
foreach ($token in $tokens.GetEnumerator()) { $html = $html.Replace("@@$($token.Key)@@", [string]$token.Value) }
if ($html -match '@@[A-Z_]+@@') { throw 'An unresolved website template token remains.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$assetsDirectory = Join-Path $output 'assets'
$images = @('main_window_dark.png', 'main_window_light.png', 'volume_mixer.png', 'recording_mixer.png', 'routines_dashboard.png')
$allowed = @('index.html', 'styles.css', 'site.js', '.nojekyll', 'assets/audiopilot.svg', 'assets/audiopilot.png') + @($images | ForEach-Object { "assets/$_" })
$ancestor = $output
while ($ancestor) {
    $entry = $null
    try { $entry = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop }
    catch [Management.Automation.ItemNotFoundException] { }
    if ($entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Redirected website output is not allowed: $ancestor"
    }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
if ([IO.Directory]::Exists($output)) {
    foreach ($entry in Get-ChildItem -LiteralPath $output -Recurse -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Redirected website output is not allowed: $($entry.FullName)"
        }
        $relative = [IO.Path]::GetRelativePath($output, $entry.FullName).Replace('\', '/')
        if ($entry.PSIsContainer) {
            if ($relative -cne 'assets') { throw "Unexpected directory in website output: $relative." }
        }
        elseif ($relative -cnotin $allowed) { throw "Unexpected file in website output: $relative. Use an empty output directory." }
    }
}
[IO.Directory]::CreateDirectory($assetsDirectory) | Out-Null
[IO.File]::WriteAllText((Join-Path $output 'index.html'), $html, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $output '.nojekyll'), '')
foreach ($file in @('styles.css', 'site.js')) { Copy-Item -LiteralPath (Join-Path $repo "website/$file") -Destination $output -Force }
foreach ($image in $images) { Copy-Item -LiteralPath (Join-Path $repo "docs/images/$image") -Destination $assetsDirectory -Force }
foreach ($image in @('audiopilot.svg', 'audiopilot.png')) { Copy-Item -LiteralPath (Join-Path $repo "AudioPilot/Images/$image") -Destination $assetsDirectory -Force }
Write-Host "Built AudioPilot $version website at $output"
