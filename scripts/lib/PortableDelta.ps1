function New-AudioPilotPortableDeltas {
    param(
        [Parameter(Mandatory)][string]$PreviousSignedBuildRoot,
        [Parameter(Mandatory)]$FullTargets,
        [Parameter(Mandatory)][string]$PublisherVerifier,
        [Parameter(Mandatory)][string]$OutputDirectory
    )
    $previous = Get-Content -LiteralPath (Join-Path $PreviousSignedBuildRoot 'update-targets.json') -Raw | ConvertFrom-Json -AsHashtable
    $build = Get-Content -LiteralPath (Join-Path $PreviousSignedBuildRoot 'update-build.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($previous.schema -ne 1 -or $build.channel -cne 'stable' -or $build.sourceDirty -isnot [bool] -or $build.sourceDirty -or $previous.targets.Count -ne 9) {
        throw 'Portable deltas require the previous signed stable package proposal.'
    }
    $directory = Join-Path $OutputDirectory 'deltas'
    [void][IO.Directory]::CreateDirectory($directory)
    foreach ($target in $FullTargets | Where-Object { $_.identity.kind -ceq 'portable' }) {
        $base = @($previous.targets | Where-Object { $_.identity.kind -ceq 'portable' -and
            $_.identity.architecture -ceq $target.identity.architecture -and $_.identity.runtime -ceq $target.identity.runtime })
        if ($base.Count -ne 1 -or $base[0].identity.channel -cne 'stable' -or
            $base[0].identity.windowsPublisherSha256 -cne $target.identity.windowsPublisherSha256 -or
            $base[0].identity.version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
            [version]$base[0].identity.version -ge [version]$target.identity.version -or
            (Get-Item -LiteralPath $base[0].path).Length -ne $base[0].length -or
            (Get-FileHash -LiteralPath $base[0].path -Algorithm SHA256).Hash -cne $base[0].sha256) {
            throw 'The portable baseline differs from its previous signed build proposal.'
        }
        $version = $base[0].identity.version
        $name = $target.name.Substring(0,$target.name.Length-4) + ".from-$version.delta.zip"
        if ($name.Length -gt 160 -or $name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.delta\.zip$') {
            throw 'Unsafe portable delta name.'
        }
        $path = Join-Path $directory $name
        $start = [Diagnostics.ProcessStartInfo]::new($PublisherVerifier)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        foreach ($argument in @('build-portable-delta',$base[0].path,$target.path,$version,$path)) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) { throw "Portable delta build failed: $name." }
        }
        finally { $process.Dispose() }
        $length = (Get-Item -LiteralPath $path).Length
        if ($length -gt ($target.length - [math]::Floor($target.length / 4))) {
            Remove-Item -LiteralPath $path
            continue
        }
        $identity = @{}
        foreach ($property in $target.identity.PSObject.Properties) { $identity[$property.Name] = $property.Value }
        $identity.kind = 'portable-delta'
        @{name=$name;path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;identity=$identity}
    }
}
