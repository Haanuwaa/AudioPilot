Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot '../lib/ReleaseDependencies.ps1')
. (Join-Path $PSScriptRoot '../lib/ReleaseMetadata.ps1')
. (Join-Path $PSScriptRoot '../lib/ReleaseArchive.ps1')
. (Join-Path $PSScriptRoot '../lib/GitHubRelease.ps1')
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
    Set-Content -LiteralPath (Join-Path $fixture 'updater-build-info.txt') -Value "`tdep`texample.com/audio`tv2.3.4+incompatible`th1:fixture"
    $published = @(Get-AudioPilotPublishedDependencies -Directory $fixture -Profile $profile)
    if ($published.Count -ne 3 -or 'Microsoft.NETCore.App.Runtime.win-x64' -notin $published.name -or
        ($published | Where-Object type -CEQ 'GoModule').packageUrl -cne 'pkg:golang/example.com/audio@v2.3.4%2Bincompatible') { throw 'Runtime inventory omitted a bundled dependency or encoded its identity incorrectly.' }
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
    foreach ($case in @('checksum', 'missing-framework', 'test-as-runtime', 'build-as-test', 'unknown-reference', 'duplicate-id', 'wrong-root', 'missing-license', 'creation-reference', 'go-as-nuget', 'nuget-as-go')) {
        $changed = $raw | ConvertFrom-Json
        switch ($case) {
            'go-as-nuget' { ($changed.'@graph' | Where-Object { $_.type -ceq 'software_Package' -and $_.name -ceq 'example.com/audio' }).software_packageUrl = 'pkg:nuget/example.com/audio@v2.3.4%2Bincompatible' }
            'nuget-as-go' { ($changed.'@graph' | Where-Object { $_.type -ceq 'software_Package' -and $_.name -ceq 'RuntimeLibrary' }).software_packageUrl = 'pkg:golang/RuntimeLibrary@1.2.3' }
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
    $archivePath = Join-Path $fixture 'managed.zip'
    $generation = '1.0.1_' + [Guid]::NewGuid().ToString('N')
    $archive = [IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = @{
            'AudioPilot/app/.updater' = 'AudioPilot updater workspace 1'
            'AudioPilot/app/portable-layout.json' = (@{Schema=1;DiagnosticId=[Guid]::NewGuid().ToString('N');Managed=$true}|ConvertTo-Json)
            'AudioPilot/app/deployment.json' = (@{Schema=1;Current=$generation;Previous=$null;PendingHealth=$false}|ConvertTo-Json)
            "AudioPilot/app/versions/$generation/.updater-owned.json" = (@{Schema=1;Kind='generation';Identity=$generation}|ConvertTo-Json)
            'AudioPilot/AudioPilot.exe' = 'launcher'; 'AudioPilot/AudioPilot.Cli.exe' = 'launcher'
            "AudioPilot/app/versions/$generation/AudioPilot.exe" = 'payload'
        }
        foreach ($document in @('ABOUT.txt','licenses/LICENSE','licenses/THIRD-PARTY-NOTICES.txt','licenses/UPDATER-NOTICES.txt')) {
            $entries['AudioPilot/' + $document] = "document-$document"
            $entries["AudioPilot/app/versions/$generation/$document"] = "document-$document"
        }
        foreach ($entry in $entries.GetEnumerator()) { $writer=[IO.StreamWriter]::new($archive.CreateEntry($entry.Key).Open()); try {$writer.Write($entry.Value)} finally {$writer.Dispose()} }
    } finally { $archive.Dispose() }
    $archive=[IO.Compression.ZipFile]::OpenRead($archivePath)
    try { $layout=Get-AudioPilotReleaseArchiveLayout $archive; if (-not $layout.Managed -or $layout.Prefix -cne "AudioPilot/app/versions/$generation/" -or $layout.RootPrefix -cne 'AudioPilot/') { throw 'Managed archive was not recognized.' } } finally {$archive.Dispose()}
    foreach ($document in @('ABOUT.txt','licenses/LICENSE')) {
        $name = 'AudioPilot/' + $document
        $expected = "document-$document"
        $archive=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Update)
        try { $archive.GetEntry($name).Delete(); $writer=[IO.StreamWriter]::new($archive.CreateEntry($name).Open()); try {$writer.Write($expected.Substring(0,$expected.Length-1)+'?')} finally {$writer.Dispose()} } finally {$archive.Dispose()}
        $archive=[IO.Compression.ZipFile]::OpenRead($archivePath)
        try { Assert-Rejected "stale portable document: $document" {Get-AudioPilotReleaseArchiveLayout $archive} } finally {$archive.Dispose()}
        $archive=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Update)
        try { $archive.GetEntry($name).Delete(); $writer=[IO.StreamWriter]::new($archive.CreateEntry($name).Open()); try {$writer.Write($expected)} finally {$writer.Dispose()} } finally {$archive.Dispose()}
    }
    $archive=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Update)
    try { $null=$archive.CreateEntry('AudioPilot/app/versions/another/AudioPilot.exe') } finally {$archive.Dispose()}
    $archive=[IO.Compression.ZipFile]::OpenRead($archivePath)
    try { Assert-Rejected 'mixed managed generations' {Get-AudioPilotReleaseArchiveLayout $archive} } finally {$archive.Dispose()}
    $archive=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Update)
    try { $archive.GetEntry('AudioPilot/app/versions/another/AudioPilot.exe').Delete(); $null=$archive.CreateEntry('AudioPilot/data/settings.json') } finally {$archive.Dispose()}
    $archive=[IO.Compression.ZipFile]::OpenRead($archivePath)
    try { Assert-Rejected 'private portable settings in release' {Get-AudioPilotReleaseArchiveLayout $archive} } finally {$archive.Dispose()}

    $script:draftFixture=[pscustomobject]@{tag_name='v1.0.1';draft=$true;prerelease=$false}
    $script:tagFixture=[pscustomobject]@{object=[pscustomobject]@{type='commit';sha=$commit}}
    $script:draftByTagNotFound=$false
    function Invoke-RestMethod { param($Uri,$Headers,$TimeoutSec)
        if ($Uri -like '*/git/ref/*') { return $script:tagFixture }
        if ($script:draftByTagNotFound -and $Uri -like '*/releases/tags/*') {
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('Not Found', [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::NotFound))
        }
        if ($Uri -match '/releases\?per_page=100$') { return ,@($script:draftFixture, [pscustomobject]@{tag_name='v2.0.0';draft=$true;prerelease=$false}) }
        return $script:draftFixture
    }
    try {
        $null=Assert-AudioPilotReleaseMutable -Repository Haanuwaa/AudioPilot -Version 1.0.1 -RequireDraft -ExpectedCommit $commit
        $script:draftByTagNotFound=$true
        $null=Assert-AudioPilotReleaseMutable -Repository Haanuwaa/AudioPilot -Version 1.0.1 -RequireDraft -ExpectedCommit $commit
        $script:draftFixture.draft=$false
        Assert-Rejected 'published release discovered through draft lookup' {Assert-AudioPilotReleaseMutable -Repository Haanuwaa/AudioPilot -Version 1.0.1 -RequireDraft -ExpectedCommit $commit}
        $script:draftByTagNotFound=$false
        $script:draftFixture.draft=$false
        Assert-Rejected 'published version replacement' {Assert-AudioPilotReleaseMutable -Repository Haanuwaa/AudioPilot -Version 1.0.1}
        $script:draftFixture.draft=$true; $script:tagFixture.object.sha='0'*40
        Assert-Rejected 'draft tag differs from tested source' {Assert-AudioPilotReleaseMutable -Repository Haanuwaa/AudioPilot -Version 1.0.1 -RequireDraft -ExpectedCommit $commit}
    } finally { Remove-Item Function:Invoke-RestMethod }
    Write-Host 'Release metadata checks passed, including managed archive ownership and immutable signed release admission.'
}
finally {
    $parent = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/testresults/release-metadata')) + '\'
    if (-not $fixture.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe metadata fixture cleanup.' }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
