function Get-AudioPilotUpdateFeedCleanupPlan {
    param(
        [Parameter(Mandatory)][object[]]$Assets,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Catalogs,
        [Parameter(Mandatory)][long]$CurrentCatalogVersion,
        [Parameter(Mandatory)][DateTimeOffset]$Now
    )

    if (-not $Catalogs.Contains($CurrentCatalogVersion)) { throw 'The verified current catalog is missing.' }
    $current = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $latestExpiry = [Collections.Generic.Dictionary[string,DateTimeOffset]]::new([StringComparer]::Ordinal)
    $targetNamePattern = '^[A-Za-z0-9][A-Za-z0-9._-]{0,199}\.(?:zip|msi)$'
    foreach ($version in $Catalogs.Keys) {
        $catalog = $Catalogs[$version]
        if ($catalog.targets -isnot [System.Collections.IDictionary] -or -not $catalog.expires) {
            throw "Invalid targets catalog $version."
        }
        $expiry = if ($catalog.expires -is [datetime]) { [DateTimeOffset]$catalog.expires }
        else { [DateTimeOffset]::Parse($catalog.expires, [Globalization.CultureInfo]::InvariantCulture) }
        foreach ($target in $catalog.targets.GetEnumerator()) {
            $hash = $target.Value.hashes.sha256
            if ($target.Key -cnotmatch $targetNamePattern -or $hash -cnotmatch '^[a-f0-9]{64}$') {
                throw "Invalid package reference in targets catalog $version."
            }
            $name = "$hash.$($target.Key)"
            if ($version -eq $CurrentCatalogVersion) { $null = $current.Add($name) }
            if (-not $latestExpiry.ContainsKey($name) -or $expiry -gt $latestExpiry[$name]) {
                $latestExpiry[$name] = $expiry
            }
        }
    }

    $grace = [TimeSpan]::FromDays(7)
    $seenCurrent = 0
    $rows = @(foreach ($asset in $Assets) {
        $referencedUntil = $null
        $reviewAfter = $null
        $reason = if ($asset.name -cnotmatch ('^[a-f0-9]{64}\.' + $targetNamePattern.Substring(1))) { 'metadata-or-unrecognized' }
        elseif ($current.Contains($asset.name)) {
            $seenCurrent++
            'current-catalog'
        }
        elseif (-not $latestExpiry.ContainsKey($asset.name)) { 'no-catalog-history' }
        else {
            $referencedUntil = $latestExpiry[$asset.name]
            $updated = [DateTimeOffset]$asset.updated_at
            $reviewAfter = $(if ($updated -gt $referencedUntil) { $updated } else { $referencedUntil }) + $grace
            if ($reviewAfter -le $Now) { 'expired-history' } else { 'historical-catalog-or-grace' }
        }
        [pscustomobject]@{
            name = $asset.name
            bytes = $asset.size
            digest = $asset.digest
            reason = $reason
            lastCatalogExpiry = $referencedUntil
            reviewAfter = $reviewAfter
        }
    })
    if ($seenCurrent -ne $current.Count) { throw 'The inventory is missing a current package.' }

    $candidates = @($rows | Where-Object reason -CEQ 'expired-history')
    $protected = @($rows | Where-Object reason -CEQ 'historical-catalog-or-grace')
    [pscustomobject]@{
        assets = $rows.Count
        currentPackages = $seenCurrent
        protectedHistoricalPackages = $protected.Count
        protectedHistoricalBytes = if ($protected.Count) { [long](($protected | Measure-Object bytes -Sum).Sum) } else { 0L }
        candidateCount = $candidates.Count
        candidateBytes = if ($candidates.Count) { [long](($candidates | Measure-Object bytes -Sum).Sum) } else { 0L }
        graceDays = $grace.Days
        nextReviewAfter = ($protected | Sort-Object reviewAfter | Select-Object -First 1 -ExpandProperty reviewAfter)
        files = $rows
    }
}
