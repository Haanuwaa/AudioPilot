[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root = Join-Path $repo "artifacts/website-checks/$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($root) | Out-Null
$metadataPath = Join-Path $root 'release.json'
$output = Join-Path $root 'public'
$release = @{
    tag_name = 'v1.2.3'; draft = $false; prerelease = $false
    assets = @(
        foreach ($architecture in @('x64', 'arm64', 'x86')) {
            foreach ($suffix in @("$architecture.msi", "FrameworkDependent-win-$architecture.zip", "SelfContained-win-$architecture.zip")) {
                $name = "AudioPilot-1.2.3-$suffix"
                @{ name = $name; size = 1234567; browser_download_url = "https://github.com/Haanuwaa/AudioPilot/releases/download/v1.2.3/$name" }
            }
        }
    )
}
$builder = Join-Path $repo 'scripts/build-website.ps1'
$release | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding utf8
& $builder -ReleaseMetadataPath $metadataPath -OutputDirectory $output
$html = Get-Content -LiteralPath (Join-Path $output 'index.html') -Raw
$embedded = [regex]::Match($html, '<script id="release-data" type="application/json">(.*?)</script>', 'Singleline')
if (-not $embedded.Success) { throw 'The built site has no download data.' }
$downloads = $embedded.Groups[1].Value | ConvertFrom-Json -AsHashtable
foreach ($architecture in @('x64', 'arm64', 'x86')) {
    foreach ($kind in @('installer', 'framework', 'portable')) {
        if (-not $downloads[$architecture][$kind].url.EndsWith("$architecture.$(if ($kind -eq 'installer') { 'msi' } else { 'zip' })", [StringComparison]::Ordinal)) {
            throw "Wrong $architecture/$kind download."
        }
    }
}
foreach ($match in [regex]::Matches($html, '(?:src|href)="((?:assets/|styles\.css|site\.js)[^"]*)"')) {
    $assetPath = $match.Groups[1].Value.Split('?')[0]
    if (-not (Test-Path -LiteralPath (Join-Path $output $assetPath))) { throw "Missing public asset: $assetPath." }
}
foreach ($asset in @('styles.css', 'site.js')) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $repo "website/$asset") -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()
    if (-not $html.Contains("${asset}?v=$hash", [StringComparison]::Ordinal)) { throw "Missing content version for $asset." }
}
foreach ($failure in @('missing-package', 'foreign-url', 'preview')) {
    $invalid = $release | ConvertTo-Json -Depth 5 | ConvertFrom-Json -AsHashtable
    switch ($failure) {
        missing-package { $invalid.assets = @($invalid.assets | Select-Object -Skip 1) }
        foreign-url { $invalid.assets[0].browser_download_url = 'https://example.com/installer.msi' }
        preview { $invalid.prerelease = $true }
    }
    $invalid | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    $rejected = $false
    try { & $builder -ReleaseMetadataPath $metadataPath -OutputDirectory $output }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid release was accepted: $failure." }
    if ((Get-Content -LiteralPath (Join-Path $output 'index.html') -Raw) -cne $html) { throw 'A failed build changed the valid site.' }
}
$release | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding utf8
$unexpected = Join-Path $output '.private-build-state'
Set-Content -LiteralPath $unexpected -Value 'fixture' -Encoding utf8
if ([OperatingSystem]::IsWindows()) { [IO.File]::SetAttributes($unexpected, [IO.FileAttributes]::Hidden) }
$rejected = $false
try { & $builder -ReleaseMetadataPath $metadataPath -OutputDirectory $output }
catch { $rejected = $true }
if (-not $rejected) { throw 'Unexpected hidden files in the publication directory were accepted.' }
[IO.File]::Delete($unexpected)
foreach ($case in @('output-root', 'output-parent', 'asset-directory', 'extra-directory')) {
    $caseRoot = Join-Path $root $case
    $outside = Join-Path $caseRoot 'outside'
    [IO.Directory]::CreateDirectory($outside) | Out-Null
    $fixture = Join-Path $outside 'private-fixture.txt'
    [IO.File]::WriteAllText($fixture, 'Harmless private fixture.')
    $destination = Join-Path $caseRoot 'public'
    $link = switch ($case) {
        output-root { $destination }
        output-parent { $destination = Join-Path $caseRoot 'redirect/public'; Join-Path $caseRoot 'redirect' }
        asset-directory { [IO.Directory]::CreateDirectory($destination) | Out-Null; Join-Path $destination 'assets' }
        extra-directory { & $builder -ReleaseMetadataPath $metadataPath -OutputDirectory $destination; Join-Path $destination 'extra-assets' }
    }
    $linkType = if ([OperatingSystem]::IsWindows()) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path $link -Target $outside | Out-Null
    try {
        $rejected = $false
        try { & $builder -ReleaseMetadataPath $metadataPath -OutputDirectory $destination }
        catch {
            if ($_.Exception.Message -notlike 'Redirected website output is not allowed:*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "Redirected output was accepted: $case." }
        if (@(Get-ChildItem -LiteralPath $outside -Force).Count -ne 1 -or [IO.File]::ReadAllText($fixture) -cne 'Harmless private fixture.') {
            throw "The rejected build changed files outside the output: $case."
        }
    }
    finally {
        $resolvedLink = [IO.Path]::GetFullPath($link)
        if (-not $resolvedLink.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -or
            -not ((Get-Item -LiteralPath $link -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing to remove an unexpected test link.'
        }
        [IO.Directory]::Delete($link)
    }
}
Write-Host 'Website downloads, public assets, invalid-release handling, and redirected-output guards passed.'
