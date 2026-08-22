function Write-ReleaseSbom {
    param([string]$Path, [string]$Version, [string]$Repository, [object[]]$ArtifactRecords, [object[]]$Dependencies)

    $base = "https://github.com/$Repository/releases/download/v$Version/spdx/$([Guid]::NewGuid())/"
    $appId = $base + 'application'
    $documentId = $base + 'document'
    $sbomId = $base + 'sbom'
    $creatorId = $base + 'creator'
    $toolId = $base + 'tool'
    $licenseId = $base + 'data-license'
    $creationId = '_:creationInfo'
    $elements = [Collections.Generic.List[object]]::new()
    $elements.Add([ordered]@{ type = 'CreationInfo'; '@id' = $creationId; specVersion = '3.0.1';
        created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'); createdBy = @($creatorId); createdUsing = @($toolId) })
    $elements.Add([ordered]@{ type = 'Organization'; spdxId = $creatorId; creationInfo = $creationId; name = 'Haanuwaa' })
    $elements.Add([ordered]@{ type = 'Tool'; spdxId = $toolId; creationInfo = $creationId; name = 'AudioPilot release packaging' })
    $elements.Add([ordered]@{ type = 'simplelicensing_LicenseExpression'; spdxId = $licenseId; creationInfo = $creationId;
        simplelicensing_licenseExpression = 'CC0-1.0' })
    $elements.Add([ordered]@{ type = 'software_Package'; spdxId = $appId; creationInfo = $creationId; name = 'AudioPilot';
        software_packageVersion = $Version; software_primaryPurpose = 'application';
        software_downloadLocation = "https://github.com/$Repository/releases/tag/v$Version" })
    $packages = @{}
    foreach ($dependency in $Dependencies) {
        $id = $base + 'package/' + [Uri]::EscapeDataString($dependency.name) + '@' + [Uri]::EscapeDataString($dependency.version)
        $packages["$($dependency.name)@$($dependency.version)"] = $id
        $elements.Add([ordered]@{ type = 'software_Package'; spdxId = $id; creationInfo = $creationId;
            name = [string]$dependency.name; software_packageVersion = [string]$dependency.version;
            software_packageUrl = [string]$dependency.packageUrl;
            comment = "NuGet dependency; scope=$($dependency.scope); type=$($dependency.type); projects=$(@($dependency.projects) -join ','); profiles=$(@($dependency.profiles) -join ',')" })
    }
    $files = @{}
    foreach ($artifact in $ArtifactRecords) {
        $id = $base + 'file/' + [Uri]::EscapeDataString($artifact.file)
        $files[$artifact.file] = $id
        $elements.Add([ordered]@{ type = 'software_File'; spdxId = $id; creationInfo = $creationId; name = [string]$artifact.file;
            verifiedUsing = @([ordered]@{ type = 'Hash'; algorithm = 'sha256'; hashValue = [string]$artifact.sha256 }) })
        $elements.Add([ordered]@{ type = 'Relationship'; spdxId = $base + "relationship/$($elements.Count)";
            creationInfo = $creationId; from = $appId; relationshipType = 'hasDistributionArtifact'; to = @($id) })
    }
    foreach ($dependency in $Dependencies) {
        $id = $packages["$($dependency.name)@$($dependency.version)"]
        $elements.Add([ordered]@{ type = 'LifecycleScopedRelationship'; spdxId = $base + "relationship/$($elements.Count)";
            creationInfo = $creationId; from = $appId; relationshipType = 'dependsOn'; to = @($id); scope = [string]$dependency.scope })
        if ($dependency.scope -eq 'runtime') {
            foreach ($artifact in $ArtifactRecords) {
                $profile = if ($artifact.PSObject.Properties['profile']) { $artifact.profile } else { "SelfContained-win-$($artifact.architecture)" }
                if ($profile -cin $dependency.profiles) {
                    $elements.Add([ordered]@{ type = 'LifecycleScopedRelationship'; spdxId = $base + "relationship/$($elements.Count)";
                        creationInfo = $creationId; from = $files[$artifact.file]; relationshipType = 'dependsOn'; to = @($id); scope = 'runtime' })
                }
            }
        }
    }
    $elements.Add([ordered]@{ type = 'Relationship'; spdxId = $base + "relationship/$($elements.Count)";
        creationInfo = $creationId; from = $documentId; relationshipType = 'describes'; to = @($appId) })
    $elements.Add([ordered]@{ type = 'software_Sbom'; spdxId = $sbomId; creationInfo = $creationId;
        name = "AudioPilot $Version release SBOM"; rootElement = @($appId); software_sbomType = @('build');
        element = @($elements | Where-Object { $_.Contains('spdxId') } | ForEach-Object { $_.spdxId }) })
    $elements.Add([ordered]@{ type = 'SpdxDocument'; spdxId = $documentId; creationInfo = $creationId;
        name = "AudioPilot $Version release SBOM"; dataLicense = $licenseId; rootElement = @($sbomId);
        profileConformance = @('core', 'software', 'simpleLicensing');
        element = @($elements | Where-Object { $_.Contains('spdxId') } | ForEach-Object { $_.spdxId }) })
    [ordered]@{ '@context' = 'https://spdx.org/rdf/3.0.1/spdx-context.jsonld'; '@graph' = $elements.ToArray() } |
        ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Assert-AudioPilotReleaseSbom {
    param($Sbom, [string]$RawText, [string]$Version, [object[]]$Artifacts, [hashtable]$Hashes,
        [object[]]$PublishedDependencies, [object[]]$Dependencies)

    if ($Sbom.'@context' -cne 'https://spdx.org/rdf/3.0.1/spdx-context.jsonld' -or -not $Sbom.'@graph'.Count) { throw 'Invalid SPDX 3.0.1 document header.' }
    $graph = @($Sbom.'@graph')
    $ids = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($node in $graph) {
        $id = if ($node.PSObject.Properties['spdxId']) { [string]$node.spdxId } elseif ($node.PSObject.Properties['@id']) { [string]$node.'@id' } else { '' }
        if (-not $id -or (-not $id.StartsWith('_:') -and -not [Uri]::IsWellFormedUriString($id,[UriKind]::Absolute)) -or -not $ids.TryAdd($id,$node)) { throw 'Duplicate or invalid SPDX identifier.' }
    }
    $creation = @($graph | Where-Object type -CEQ 'CreationInfo')
    if ($creation.Count -ne 1 -or $creation[0].specVersion -cne '3.0.1' -or -not $creation[0].createdBy.Count) { throw 'Invalid SPDX creation information.' }
    foreach ($reference in @($creation[0].createdBy) + @($creation[0].createdUsing)) {
        if (-not $ids.ContainsKey($reference)) { throw 'Invalid SPDX creator reference.' }
    }
    $timestamps = [regex]::Matches($RawText, '"created"\s*:\s*"([^"]+)"')
    if ($timestamps.Count -ne 1 -or $timestamps[0].Groups[1].Value -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') { throw 'SPDX timestamps must use whole-second UTC format.' }
    [void][DateTime]::ParseExact($timestamps[0].Groups[1].Value,'yyyy-MM-ddTHH:mm:ssZ',[Globalization.CultureInfo]::InvariantCulture)
    $documents = @($graph | Where-Object type -CEQ 'SpdxDocument')
    $sboms = @($graph | Where-Object type -CEQ 'software_Sbom')
    $licenses = @($graph | Where-Object type -CEQ 'simplelicensing_LicenseExpression')
    if ($documents.Count -ne 1 -or $sboms.Count -ne 1 -or $licenses.Count -ne 1 -or $licenses[0].simplelicensing_licenseExpression -cne 'CC0-1.0' -or $documents[0].dataLicense -cne $licenses[0].spdxId) { throw 'Invalid SPDX document, SBOM or data license.' }
    foreach ($node in $graph | Where-Object type -CNE 'CreationInfo') {
        if ($node.creationInfo -cne $creation[0].'@id') { throw 'Invalid SPDX creation reference.' }
    }
    $packages = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in $graph | Where-Object type -CEQ 'software_Package') {
        if (-not $packages.TryAdd("$($package.name)@$($package.software_packageVersion)",$package)) { throw 'Duplicate SPDX package identity.' }
    }
    $app = $null
    if ($packages.Count -ne $Dependencies.Count + 1 -or -not $packages.TryGetValue("AudioPilot@$Version",[ref]$app)) { throw 'SBOM application identity or dependency count mismatch.' }
    $expectedScopes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($dependency in $Dependencies) {
        if ($dependency.scope -cnotin @('runtime','build','test') -or -not $expectedScopes.TryAdd("$($dependency.name)@$($dependency.version)",$dependency.scope)) { throw 'Invalid release dependency inventory.' }
    }
    if (@($documents[0].rootElement).Count -ne 1 -or $documents[0].rootElement[0] -cne $sboms[0].spdxId -or @($sboms[0].rootElement).Count -ne 1 -or $sboms[0].rootElement[0] -cne $app.spdxId) { throw 'Invalid SPDX root elements.' }
    foreach ($collection in @($documents[0],$sboms[0])) {
        $expectedMembers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($node in $graph | Where-Object { $_.type -cne 'CreationInfo' -and $_.spdxId -cne $documents[0].spdxId -and ($collection.type -ceq 'SpdxDocument' -or $_.spdxId -cne $sboms[0].spdxId) }) { [void]$expectedMembers.Add($node.spdxId) }
        $members = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($reference in $collection.element) {
            if (-not $expectedMembers.Contains($reference) -or -not $members.Add($reference)) { throw 'Invalid SPDX collection member.' }
        }
        if ($members.Count -ne $expectedMembers.Count) { throw 'Incomplete SPDX element collection.' }
    }
    $files = @{}
    foreach ($file in $graph | Where-Object type -CEQ 'software_File') {
        if ($files.ContainsKey($file.name)) { throw 'Duplicate SPDX artifact.' }
        $files[$file.name] = $file
    }
    if ($files.Count -ne $Artifacts.Count) { throw 'SBOM artifact count mismatch.' }
    foreach ($artifact in $Artifacts) {
        $file = $files[$artifact.file]
        if ($null -eq $file) { throw 'Missing SPDX artifact.' }
        $checksums = @($file.verifiedUsing | Where-Object { $_.type -ceq 'Hash' -and $_.algorithm -ceq 'sha256' })
        if ($checksums.Count -ne 1 -or $checksums[0].hashValue -cne $Hashes[$artifact.file].SHA256) { throw 'SPDX artifact checksum mismatch.' }
    }
    $relations = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($relation in $graph | Where-Object { $_.type -cin @('Relationship','LifecycleScopedRelationship') }) {
        if (-not $ids.ContainsKey($relation.from) -or @($relation.to).Count -ne 1 -or -not $ids.ContainsKey($relation.to[0])) { throw 'Invalid SPDX relationship reference.' }
        $scope = if ($relation.type -ceq 'LifecycleScopedRelationship') { $relation.scope } else { '' }
        if (-not $relations.Add("$($relation.from)|$($relation.relationshipType)|$($relation.to[0])|$scope")) { throw 'Duplicate SPDX relationship.' }
    }
    if (-not $relations.Contains("$($documents[0].spdxId)|describes|$($app.spdxId)|")) { throw 'SPDX document does not describe AudioPilot.' }
    $runtime = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($dependency in $PublishedDependencies) {
        $package = $null
        if (-not $packages.TryGetValue("$($dependency.name)@$($dependency.version)",[ref]$package)) { throw "Missing published dependency: $($dependency.name)" }
        [void]$runtime.Add($package.spdxId)
    }
    foreach ($package in $packages.Values | Where-Object { $_.spdxId -cne $app.spdxId }) {
        $edges = @('runtime','build','test' | Where-Object { $relations.Contains("$($app.spdxId)|dependsOn|$($package.spdxId)|$_") })
        $scope = $null
        if (-not $expectedScopes.TryGetValue("$($package.name)@$($package.software_packageVersion)",[ref]$scope) -or $edges.Count -ne 1 -or $edges[0] -cne $scope -or (($scope -ceq 'runtime') -ne $runtime.Contains($package.spdxId))) { throw "Incorrect SPDX dependency scope: $($package.name)" }
        if ($package.software_packageUrl -cne "pkg:nuget/$($package.name)@$([Uri]::EscapeDataString($package.software_packageVersion))") { throw 'Invalid SPDX package URL.' }
    }
    $expectedRelationCount = 1 + $Artifacts.Count + $Dependencies.Count
    foreach ($artifact in $Artifacts) {
        $file = $files[$artifact.file]
        if (-not $relations.Contains("$($app.spdxId)|hasDistributionArtifact|$($file.spdxId)|")) { throw 'Missing SPDX artifact relationship.' }
        $profile = if ($artifact.PSObject.Properties['profile']) { $artifact.profile } else { "SelfContained-win-$($artifact.architecture)" }
        $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($dependency in $PublishedDependencies | Where-Object profile -CEQ $profile) { [void]$expected.Add($packages["$($dependency.name)@$($dependency.version)"].spdxId) }
        $expectedRelationCount += $expected.Count
        $actual = @($graph | Where-Object { $_.type -ceq 'LifecycleScopedRelationship' -and $_.from -ceq $file.spdxId -and $_.relationshipType -ceq 'dependsOn' })
        if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { $_.scope -cne 'runtime' -or -not $expected.Contains($_.to[0]) }).Count) { throw 'SPDX artifact dependency mismatch.' }
    }
    if ($relations.Count -ne $expectedRelationCount) { throw 'Unexpected SPDX relationship.' }
}
