Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot '../lib/ReleaseDependencies.ps1')
. (Join-Path $PSScriptRoot '../lib/ReleaseMetadata.ps1')
$fixture = Join-Path $repository "artifacts/testresults/release-metadata/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../package-release.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('Add-ArtifactRecord')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if ($null -eq $function) { throw "Packaging function missing: $name" }
    Invoke-Expression $function.Extent.Text
}
function Assert-Rejected([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid release metadata accepted: $Name" }
    Write-Host "Metadata rejection passed: $Name"
}
try {
    $profile = 'SelfContained-win-x64'
    $deps = @{
        runtimeTarget = @{ name = 'net10.0/win-x64' }
        targets = @{ 'net10.0/win-x64' = @{
            'RuntimeLibrary/1.2.3' = @{ runtime = @{ 'lib/net10.0/RuntimeLibrary.dll' = @{} } }
            'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13' = @{ native = @{ 'coreclr.dll' = @{} } }
            'BuildAnalyzer/1.0.0' = @{}
            'Placeholder/1.0.0' = @{ runtime = @{ 'lib/net10.0/_._' = @{} } }
        } }
        libraries = @{
            'RuntimeLibrary/1.2.3' = @{ type = 'package' }
            'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13' = @{ type = 'runtimepack' }
            'BuildAnalyzer/1.0.0' = @{ type = 'package' }
            'Placeholder/1.0.0' = @{ type = 'package' }
        }
    }
    foreach ($entry in @('AudioPilot', 'AudioPilot.Cli')) {
        $deps | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $fixture "$entry.deps.json")
    }
    $published = @(Get-AudioPilotPublishedDependencies -Directory $fixture -Profile $profile)
    if ($published.Count -ne 2 -or 'Microsoft.NETCore.App.Runtime.win-x64' -notin $published.name) { throw 'Runtime inventory omitted a bundled framework or included build-only assets.' }
    $locks = @(
        [pscustomobject]@{ name = 'RuntimeLibrary'; version = '1.2.3'; type = 'Direct'; packageUrl = 'pkg:nuget/RuntimeLibrary@1.2.3'; projects = @('AudioPilot', 'AudioPilot.Tests') },
        [pscustomobject]@{ name = 'BuildAnalyzer'; version = '1.0.0'; type = 'Direct'; packageUrl = 'pkg:nuget/BuildAnalyzer@1.0.0'; projects = @('AudioPilot') },
        [pscustomobject]@{ name = 'TestLibrary'; version = '1.0.0'; type = 'Direct'; packageUrl = 'pkg:nuget/TestLibrary@1.0.0'; projects = @('AudioPilot.Tests') }
    )
    $inventory = @(Get-AudioPilotReleaseDependencies -LockDependencies $locks -PublishedDependencies $published)
    if (($inventory | Where-Object name -eq 'RuntimeLibrary').scope -ne 'runtime' -or
        ($inventory | Where-Object name -eq 'BuildAnalyzer').scope -ne 'build' -or
        ($inventory | Where-Object name -eq 'TestLibrary').scope -ne 'test') { throw 'Incorrect dependency scopes.' }
    $artifact = Join-Path $fixture 'AudioPilot-1.0.0-SelfContained-win-x64.zip'
    Set-Content -LiteralPath $artifact -Value 'test artifact'
    $records = [Collections.Generic.List[object]]::new()
    $checksums = [Collections.Generic.List[string]]::new()
    Add-ArtifactRecord -List $records -Checksums $checksums -Path $artifact -Metadata @{ profile = $profile }
    $hashes = @{ ([IO.Path]::GetFileName($artifact)) = @{ SHA256 = $records[0].sha256 } }
    $sbomPath = Join-Path $fixture 'sbom.json'
    Write-ReleaseSbom -Path $sbomPath -Version '1.0.0' -Repository 'Haanuwaa/AudioPilot' -ArtifactRecords $records.ToArray() -Dependencies $inventory
    $raw = Get-Content -LiteralPath $sbomPath -Raw
    $sbom = $raw | ConvertFrom-Json
    $arguments = @{ RawText = $raw; Version = '1.0.0'; Artifacts = $records.ToArray(); Hashes = $hashes; PublishedDependencies = $published; Dependencies = $inventory }
    Assert-AudioPilotReleaseSbom -Sbom $sbom @arguments
    foreach ($case in @('checksum', 'missing-framework', 'test-as-runtime', 'build-as-test', 'unknown-reference', 'duplicate-id', 'wrong-root', 'missing-license', 'creation-reference')) {
        $changed = $raw | ConvertFrom-Json
        switch ($case) {
            'checksum' { ($changed.'@graph' | Where-Object type -CEQ 'software_File').verifiedUsing[0].hashValue = '0' * 64 }
            'missing-framework' { $changed.'@graph' = @($changed.'@graph' | Where-Object { $_.type -cne 'software_Package' -or $_.name -notlike 'Microsoft.NETCore.App.Runtime*' }) }
            'test-as-runtime' {
                $package = $changed.'@graph' | Where-Object { $_.type -ceq 'software_Package' -and $_.name -ceq 'TestLibrary' }
                ($changed.'@graph' | Where-Object { $_.type -ceq 'LifecycleScopedRelationship' -and $_.to[0] -ceq $package.spdxId }).scope = 'runtime'
            }
            'unknown-reference' { ($changed.'@graph' | Where-Object type -CEQ 'Relationship' | Select-Object -First 1).to[0] = 'https://invalid.example/missing' }
            'duplicate-id' { $packages = @($changed.'@graph' | Where-Object type -CEQ 'software_Package'); $packages[1].spdxId = $packages[0].spdxId }
            'build-as-test' { ($changed.'@graph' | Where-Object { $_.type -ceq 'LifecycleScopedRelationship' -and $_.scope -ceq 'build' }).scope = 'test' }
            'wrong-root' { ($changed.'@graph' | Where-Object type -CEQ 'SpdxDocument').rootElement[0] = 'https://invalid.example/root' }
            'missing-license' { ($changed.'@graph' | Where-Object type -CEQ 'SpdxDocument').dataLicense = 'https://invalid.example/license' }
            'creation-reference' { ($changed.'@graph' | Where-Object type -CEQ 'software_File').creationInfo = '_:missing-creation' }
        }
        Assert-Rejected $case { Assert-AudioPilotReleaseSbom -Sbom $changed @arguments }
    }
    $badDates = $arguments.Clone()
    $badDates.RawText = $raw -replace '(\d{2}:\d{2}:\d{2})Z', '$1.123Z'
    Assert-Rejected 'fractional-timestamps' { Assert-AudioPilotReleaseSbom -Sbom $sbom @badDates }
    $commit = 'a' * 40
    $provenance = [pscustomobject]@{
        schemaVersion = 'AudioPilot.ReleaseProvenance.v1'; app = 'AudioPilot'; version = '1.0.0'
        source = @{ git = @{ commit = $commit; dirty = $false } }
        build = @{ runner = 'local' }
        subjects = @(@{ name = $records[0].file; sizeBytes = $records[0].sizeBytes; digest = @{ sha256 = $records[0].sha256 } })
    }
    $provenanceText = $provenance | ConvertTo-Json -Depth 8
    $provenanceArguments = @{ Version = '1.0.0'; ExpectedCommit = $commit; Artifacts = $records.ToArray(); AllowDirtySource = $false }
    Assert-AudioPilotReleaseProvenance -Provenance $provenance @provenanceArguments
    foreach ($case in @('source', 'dirty', 'subject', 'duplicate-subject', 'github-source')) {
        $changed = $provenanceText | ConvertFrom-Json
        switch ($case) {
            'source' { $changed.source.git.commit = '0' * 40 }
            'dirty' { $changed.source.git.dirty = $true }
            'subject' { $changed.subjects[0].digest.sha256 = '0' * 64 }
            'duplicate-subject' { $changed.subjects += $changed.subjects[0] }
            'github-source' { $changed.build = @{ runner = 'github-actions'; sha = '0' * 40; runId = '123' } }
        }
        Assert-Rejected $case { Assert-AudioPilotReleaseProvenance -Provenance $changed @provenanceArguments }
    }
    $provenance.source.git.dirty = $true
    Assert-AudioPilotReleaseProvenance -Provenance $provenance -Version '1.0.0' -ExpectedCommit $commit -Artifacts $records.ToArray() -AllowDirtySource $true
    $minimal = @{ packages = @(@{ file = 'fixture.zip' }); packageCount = 1; installers = @(); installerCount = 0; wingetManifests = @(); wingetManifestCount = 0; metadataArtifacts = @(); metadataArtifactCount = 0 }
    $minimal | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixture 'release-manifest.json')
    Set-Content -LiteralPath (Join-Path $fixture 'SHA256SUMS.txt') -Value "$('0' * 64) *release-manifest.json"
    $failed = $false
    try { & (Join-Path $PSScriptRoot '../validate-release-integrity.ps1') -ReleaseRoot $fixture -ExpectedCommit $commit }
    catch { if ($_.Exception.Message -notlike '*exactly one SBOM*') { throw }; $failed = $true }
    if (-not $failed) { throw 'Missing release metadata accepted.' }
    Write-Host 'Release metadata checks passed: SPDX checksums/timestamps, runtime inventory/scopes, relationships, required metadata, and source provenance.'
}
finally {
    $parent = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/testresults/release-metadata')) + '\'
    if (-not $fixture.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe metadata fixture cleanup.' }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
