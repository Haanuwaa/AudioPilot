. (Join-Path $PSScriptRoot 'ReleaseArchive.ps1')

function Assert-AudioPilotStableReleasePackages {
    param(
        [Parameter(Mandatory)][string]$ReleaseRoot,
        [Parameter(Mandatory)][hashtable]$Targets,
        [Parameter(Mandatory)][hashtable]$PackagePaths,
        [Parameter(Mandatory)][string]$PublisherSha256
    )
    $manifest = Get-Content -LiteralPath (Join-Path $ReleaseRoot 'release-manifest.json') -Raw | ConvertFrom-Json
    $version = [string]$manifest.version
    if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Invalid stable release version.' }
    $checked = 0
    foreach ($architecture in @('x64', 'x86', 'arm64')) {
        foreach ($mode in @('SelfContained', 'FrameworkDependent', 'MSI')) {
            $isMsi = $mode -ceq 'MSI'
            $name = if ($isMsi) { "AudioPilot-$version-win-$architecture.msi" } else { "AudioPilot-$version-$mode-win-$architecture.update.zip" }
            if (-not $Targets.ContainsKey($name) -or -not $PackagePaths.ContainsKey($name)) { throw "Stable feed is missing $name." }
            $target = $Targets[$name]
            $identity = $target.custom
            $kind = if ($isMsi) { 'msi' } else { 'portable' }
            $runtime = if ($mode -ceq 'FrameworkDependent') { 'framework-dependent' } else { 'self-contained' }
            if ($identity.product -cne 'AudioPilot' -or $identity.channel -cne 'stable' -or $identity.version -cne $version -or
                $identity.kind -cne $kind -or $identity.architecture -cne "win-$architecture" -or $identity.runtime -cne $runtime -or
                $identity.windowsPublisherSha256 -cne $PublisherSha256 -or $identity.windowsTrust -cne 'self-signed') { throw "Stable package identity differs: $name." }
            $path = $PackagePaths[$name]
            if ((Get-Item -LiteralPath $path).Length -ne $target.length -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $target.hashes.sha256) { throw "Stable package bytes differ: $name." }
            if ($isMsi) {
                $releaseMsi = Join-Path $ReleaseRoot "AudioPilot-$version-$architecture.msi"
                if ((Get-FileHash -LiteralPath $releaseMsi -Algorithm SHA256).Hash.ToLowerInvariant() -cne $target.hashes.sha256) { throw "Stable MSI differs from the release: $name." }
            }
            else {
                $releaseZip = [IO.Compression.ZipFile]::OpenRead((Join-Path $ReleaseRoot "AudioPilot-$version-$mode-win-$architecture.zip"))
                try {
                    $layout = Get-AudioPilotReleaseArchiveLayout $releaseZip
                    if (-not $layout.Managed -or -not $layout.Generation.StartsWith($version + '_', [StringComparison]::Ordinal)) { throw 'Stable releases require a matching managed portable generation.' }
                    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
                    foreach ($entry in $releaseZip.Entries) {
                        if (-not $entry.FullName.StartsWith($layout.Prefix, [StringComparison]::Ordinal) -or $entry.FullName.EndsWith('/')) { continue }
                        $relative = $entry.FullName.Substring($layout.Prefix.Length)
                        if ($relative -cne '.updater-owned.json') { $expected.Add($relative, $entry) }
                    }
                    $updateZip = [IO.Compression.ZipFile]::OpenRead($path)
                    try {
                        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                        $total = 0L
                        foreach ($entry in $updateZip.Entries) {
                            $total += $entry.Length
                            if ($updateZip.Entries.Count -gt 4096 -or $total -gt 1GB -or -not $seen.Add($entry.FullName) -or
                                $entry.FullName.Contains('\') -or $entry.FullName.Contains(':') -or $entry.FullName.StartsWith('/') -or
                                @($entry.FullName.TrimEnd('/') -split '/' | Where-Object { $_ -in @('', '.', '..') }).Count -or
                                (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000 -or ($entry.ExternalAttributes -band 1024)) { throw 'Unsafe stable update archive.' }
                            if ($entry.FullName.EndsWith('/')) { continue }
                            if (-not $expected.ContainsKey($entry.FullName) -or $expected[$entry.FullName].Length -ne $entry.Length) { throw "Stable portable file set differs: $name / $($entry.FullName)." }
                            $left = $expected[$entry.FullName].Open()
                            $right = $entry.Open()
                            try {
                                if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                                    [Security.Cryptography.SHA256]::HashData($left), [Security.Cryptography.SHA256]::HashData($right))) { throw "Stable portable content differs: $name / $($entry.FullName)." }
                            } finally { $left.Dispose(); $right.Dispose() }
                            $expected.Remove($entry.FullName) | Out-Null
                        }
                        if ($expected.Count) { throw "Stable portable files are missing: $name." }
                    } finally { $updateZip.Dispose() }
                } finally { $releaseZip.Dispose() }
            }
            $checked++
        }
    }
    return $checked
}
