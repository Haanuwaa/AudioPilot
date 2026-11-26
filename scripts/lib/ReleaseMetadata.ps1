. (Join-Path $PSScriptRoot 'ReleaseSbom.ps1')

function Assert-AudioPilotReleaseProvenance {
    param($Provenance, [string]$Version, [string]$ExpectedCommit, [object[]]$Artifacts, [bool]$AllowDirtySource)

    if ($Provenance.schemaVersion -cne 'AudioPilot.ReleaseProvenance.v1' -or $Provenance.app -cne 'AudioPilot' -or $Provenance.version -cne $Version) { throw 'Invalid release provenance identity.' }
    if ($Provenance.source.git.commit -cne $ExpectedCommit -or $Provenance.source.git.dirty -isnot [bool] -or
        ($Provenance.source.git.dirty -and -not $AllowDirtySource)) { throw 'Release source provenance does not match the expected clean build.' }
    if ($Provenance.build.runner -eq 'github-actions' -and ($Provenance.build.sha -cne $ExpectedCommit -or -not $Provenance.build.runId)) { throw 'GitHub build provenance does not match the expected commit.' }
    if ($Provenance.subjects.Count -ne $Artifacts.Count) { throw 'Provenance subject count mismatch.' }
    foreach ($artifact in $Artifacts) {
        $subjects = @($Provenance.subjects | Where-Object name -CEQ $artifact.file)
        if ($subjects.Count -ne 1 -or $subjects[0].digest.sha256 -cne $artifact.sha256 -or $subjects[0].sizeBytes -ne $artifact.sizeBytes) { throw "Provenance subject mismatch: $($artifact.file)" }
    }
}
