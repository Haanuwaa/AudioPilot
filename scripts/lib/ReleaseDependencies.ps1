function Get-AudioPilotPublishedDependencies {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Profile)

    $dependencies = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @('AudioPilot', 'AudioPilot.Cli')) {
        $deps = Get-Content -LiteralPath (Join-Path $Directory "$entry.deps.json") -Raw | ConvertFrom-Json -AsHashtable
        $target = $deps.targets[$deps.runtimeTarget.name]
        foreach ($identity in $deps.libraries.Keys) {
            $library = $deps.libraries[$identity]
            if ($library.type -notin @('package', 'runtimepack') -or -not $target.Contains($identity)) { continue }
            $assets = $target[$identity]
            $hasAssets = $false
            foreach ($kind in @('runtime', 'native', 'resources')) {
                if ($assets.Contains($kind) -and @($assets[$kind].Keys | Where-Object { -not $_.EndsWith('/_._') }).Count) { $hasAssets = $true }
            }
            if (-not $hasAssets) { continue }
            $name, $version = $identity -split '/', 2
            if ($library.type -eq 'runtimepack') { $name = $name -replace '^runtimepack\.', '' }
            $key = "$name@$version"
            $dependencies[$key] = [pscustomobject]@{
                name = $name; version = $version; type = $library.type
                packageUrl = "pkg:nuget/$name@$([Uri]::EscapeDataString($version))"
                profile = $Profile
            }
        }
    }
    $dependencies.Values | Sort-Object name, version
}

function Get-AudioPilotReleaseDependencies {
    param([object[]]$LockDependencies, [object[]]$PublishedDependencies)

    $inventory = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($dependency in $LockDependencies) {
        $scope = if (@($dependency.projects | Where-Object { $_ -ne 'AudioPilot.Tests' }).Count) { 'build' } else { 'test' }
        $inventory["$($dependency.name)@$($dependency.version)"] = [pscustomobject]@{
            name = $dependency.name; version = $dependency.version; type = $dependency.type
            packageUrl = $dependency.packageUrl; projects = @($dependency.projects); scope = $scope
            profiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        }
    }
    foreach ($dependency in $PublishedDependencies) {
        $key = "$($dependency.name)@$($dependency.version)"
        if (-not $inventory.ContainsKey($key)) {
            $inventory[$key] = [pscustomobject]@{
                name = $dependency.name; version = $dependency.version; type = $dependency.type
                packageUrl = $dependency.packageUrl; projects = @(); scope = 'runtime'
                profiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            }
        }
        $inventory[$key].scope = 'runtime'
        [void]$inventory[$key].profiles.Add($dependency.profile)
    }
    foreach ($dependency in ($inventory.Values | Sort-Object name, version)) {
        [pscustomobject]@{
            name = $dependency.name; version = $dependency.version; type = $dependency.type
            packageUrl = $dependency.packageUrl; projects = $dependency.projects; scope = $dependency.scope
            profiles = @($dependency.profiles | Sort-Object)
        }
    }
}
