[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][ValidateSet('win-x86', 'win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('SelfContained', 'FrameworkDependent')][string]$DeploymentMode = 'SelfContained'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot '../validate-publish-payload.ps1'
$source = (Resolve-Path -LiteralPath $PublishDir).Path
$testParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/testresults/payload'))
$fixture = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $fixture -Recurse -Force
$arguments = @{ PublishDir = $fixture; RuntimeIdentifier = $RuntimeIdentifier; Version = $Version; DeploymentMode = $DeploymentMode }
try {
    & $validator @arguments -WriteManifest
    & $validator @arguments
    $manifestText = Get-Content -LiteralPath "$fixture.payload.json" -Raw
    $cases = @(
        @{ Name = 'missing-about'; File = 'ABOUT.txt'; Pattern = 'Incomplete publish payload' },
        @{ Name = 'missing-runtimeconfig'; File = 'AudioPilot.Cli.runtimeconfig.json'; Pattern = 'Incomplete publish payload' },
        @{ Name = 'missing-dependency'; File = 'NAudio.Wasapi.dll'; Pattern = 'Incomplete publish payload' },
        @{ Name = 'missing-license'; File = 'LICENSE'; Pattern = 'Incomplete publish payload' }
    )
    if ($DeploymentMode -eq 'SelfContained') { $cases += @{ Name = 'missing-runtime'; File = 'hostpolicy.dll'; Pattern = 'Incomplete publish payload' } }
    foreach ($case in $cases) {
        $file = Join-Path $fixture $case.File
        Move-Item -LiteralPath $file -Destination "$file.saved"
        try {
            $rejected = $false
            try { & $validator @arguments } catch { if ($_.Exception.Message -notmatch $case.Pattern) { throw }; $rejected = $true }
            if (-not $rejected) { throw "Invalid payload accepted: $($case.Name)" }
        }
        finally { Move-Item -LiteralPath "$file.saved" -Destination $file }
        Write-Host "Payload rejection passed: $($case.Name)"
    }
    $notice = Join-Path $fixture 'LICENSE'
    $noticeBytes = [IO.File]::ReadAllBytes($notice)
    try {
        [IO.File]::WriteAllBytes($notice, [byte[]]::new(0))
        $rejected = $false
        try { & $validator @arguments -FromArchive } catch { if ($_.Exception.Message -notlike '*Empty publish notice*') { throw }; $rejected = $true }
        if (-not $rejected) { throw 'Empty publish notice accepted.' }
    }
    finally { [IO.File]::WriteAllBytes($notice, $noticeBytes) }
    foreach ($field in @('schema', 'commit', 'sourceHash', 'rid', 'deploymentMode')) {
        $manifest = $manifestText | ConvertFrom-Json -AsHashtable
        $manifest[$field] = 'stale'
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$fixture.payload.json"
        $rejected = $false
        try { & $validator @arguments } catch { if ($_.Exception.Message -notlike '*provenance is stale*') { throw }; $rejected = $true }
        if (-not $rejected) { throw "Invalid provenance accepted: $field" }
        Write-Host "Payload rejection passed: stale-$field"
    }
    Set-Content -LiteralPath "$fixture.payload.json" -Value $manifestText
    $hostPath = Join-Path $fixture $(if ($DeploymentMode -eq 'SelfContained') { 'hostfxr.dll' } else { 'AudioPilot.Cli.exe' })
    $bytes = [IO.File]::ReadAllBytes($hostPath)
    $wrongBytes = [byte[]]$bytes.Clone()
    $offset = [BitConverter]::ToInt32($bytes, 0x3c) + 4
    $wrongBytes[$offset] = 0
    [IO.File]::WriteAllBytes($hostPath, $wrongBytes)
    $rejected = $false
    try { & $validator @arguments } catch { if ($_.Exception.Message -notlike '*architecture mismatch*') { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Mixed-architecture payload accepted.' }
    [IO.File]::WriteAllBytes($hostPath, $bytes)
    Add-Content -LiteralPath (Join-Path $fixture 'LICENSE') -Value 'changed after publish'
    $rejected = $false
    try { & $validator @arguments } catch { if ($_.Exception.Message -notlike '*changed after publishing*') { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Modified publish file accepted.' }
    Write-Host 'Payload checks passed (valid publish, dependency completeness, architecture, provenance, and file integrity).'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $items = @(
        Get-Item -LiteralPath $resolved
        Get-ChildItem -LiteralPath $resolved -Recurse -Force
    )
    if (-not $resolved.StartsWith("$testParent\", [StringComparison]::OrdinalIgnoreCase) -or
        @($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Refusing unsafe payload fixture cleanup.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    Remove-Item -LiteralPath "$resolved.payload.json" -Force -ErrorAction SilentlyContinue
}
