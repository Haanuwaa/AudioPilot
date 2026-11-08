param(
    [string]$WorkflowPath = ".github/workflows/release-artifacts.yml"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $WorkflowPath)) {
    throw "Workflow file not found: $WorkflowPath"
}

$resolvedWorkflowPath = (Resolve-Path -LiteralPath $WorkflowPath).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$content = Get-Content -Raw -LiteralPath $resolvedWorkflowPath

function Assert-Pattern {
    param(
        [string]$Pattern,
        [string]$Description
    )

    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Release gate workflow policy validation failed: $Description"
    }
}

Assert-Pattern -Pattern 'on:\s*(?:.|\r|\n)*workflow_dispatch:' -Description 'workflow_dispatch trigger is required.'
Assert-Pattern -Pattern 'on:\s*(?:.|\r|\n)*push:\s*(?:.|\r|\n)*tags:\s*(?:.|\r|\n)*-\s*"v\*"' -Description 'tag trigger (v*) is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*release-gate-unit-tests:' -Description 'release-gate-unit-tests job is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*resolve-release-version:' -Description 'resolve-release-version job is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*release-gate-integration-tests:' -Description 'release-gate-integration-tests job is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*release-gate-stress-tests:' -Description 'release-gate-stress-tests job is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*publish-profiles:' -Description 'publish-profiles job is required.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*build-msi-installers:' -Description 'build-msi-installers job is required.'
Assert-Pattern -Pattern 'platform: arm64\s+runner: windows-11-arm\s+runSmoke: true' -Description 'ARM64 MSI installation smoke must run on native ARM64.'
Assert-Pattern -Pattern 'platform: x86\s+runner: windows-latest\s+runSmoke: true' -Description 'x86 MSI installation smoke must run on the Windows runner.'
Assert-Pattern -Pattern 'jobs:\s*(?:.|\r|\n)*publish-and-package:' -Description 'publish-and-package job is required.'
Assert-Pattern -Pattern 'release-gate-unit-tests:\s*(?:.|\r|\n)*?runs-on:\s*windows-latest' -Description 'release-gate-unit-tests must run on windows-latest.'
Assert-Pattern -Pattern 'release-gate-integration-tests:\s*(?:.|\r|\n)*?runs-on:\s*windows-latest' -Description 'release-gate-integration-tests must run on windows-latest.'
Assert-Pattern -Pattern 'release-gate-stress-tests:\s*(?:.|\r|\n)*?runs-on:\s*windows-latest' -Description 'release-gate-stress-tests must run on windows-latest.'
Assert-Pattern -Pattern 'name:\s*Release gate tests \(unit\)' -Description 'unit release gate test step is required.'
Assert-Pattern -Pattern 'name:\s*Release gate tests \(integration\)' -Description 'integration release gate test step is required.'
Assert-Pattern -Pattern 'name:\s*Release gate tests \(stress\)' -Description 'stress release gate test step is required.'
$installerJob = [regex]::Match($content, '(?ms)^  build-msi-installers:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)').Groups['body'].Value
$installerPayloadRules = @{
    'PUBLISH_PROFILE: SelfContained-win-\$\{\{ matrix.platform \}\}' = 'publish the matching self-contained profile in the installer job'
    'publish-release-profiles\.ps1 -TargetProfile \$profile -Configuration Release -Version \$env:RELEASE_VERSION' = 'validate the matching profile before building its installer'
    '\$payload = Join-Path \$PWD "artifacts/publish/SelfContained/win-\$platform"' = 'resolve the matching self-contained payload'
    'dotnet build [^\r\n]*-p:UsePrebuiltPublish=true "-p:PublishDirForMsi=\$payload"' = 'package the validated payload without republishing it'
    'test-publish-payload.ps1 -PublishDir "artifacts/publish/SelfContained/win-\$env:MSI_PLATFORM"' = 'test the same self-contained payload used by the installer'
    'name: publish-SelfContained-win-\$\{\{ matrix.platform \}\}' = 'retain the validated self-contained profile for release packaging'
}
foreach ($pattern in $installerPayloadRules.Keys) {
    if ($installerJob -notmatch $pattern) {
        throw "Release MSI workflow must $($installerPayloadRules[$pattern])."
    }
}
$baselineJob = [regex]::Match($content, '(?ms)^  build-upgrade-baselines:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)').Groups['body'].Value
if ($baselineJob -notmatch '(?m)^    needs: resolve-release-version\r?$' -or
    $baselineJob -notmatch 'dotnet build [^\r\n]*-p:DefaultCompressionLevel=none[^\r\n]*-p:AppVersion=\$baselineVersion' -or
    $baselineJob -notmatch 'name: upgrade-baseline-\$\{\{ matrix.platform \}\}') {
    throw 'Release upgrade baselines must build independently with the existing synthetic version and compression policy.'
}
$smokeJob = [regex]::Match($content, '(?ms)^  test-msi-installers:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)').Groups['body'].Value
foreach ($dependency in @('resolve-release-version', 'build-msi-installers', 'build-upgrade-baselines')) {
    if ($smokeJob -notmatch "(?m)^      - $([regex]::Escape($dependency))\r?$") {
        throw "Installer verification must depend on $dependency."
    }
}
foreach ($pattern in @('platform: arm64\s+runner: windows-11-arm\s+runSmoke: true',
    'name: installer-\$\{\{ matrix.platform \}\}', 'name: upgrade-baseline-\$\{\{ matrix.platform \}\}',
    'test-msi-smoke\.ps1', '-VerifyUpgradeRollback', 'test-msi-option-matrix\.ps1', 'published-upgrade')) {
    if ($smokeJob -notmatch $pattern) { throw "Installer verification scenario or artifact is missing: $pattern" }
}
$publicationJob = [regex]::Match($content, '(?ms)^  publish-and-package:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)').Groups['body'].Value
$preparationJob = [regex]::Match($content, '(?ms)^  prepare-packages:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)').Groups['body'].Value
foreach ($dependency in @('resolve-release-version', 'publish-profiles', 'build-msi-installers')) {
    if ($preparationJob -notmatch "(?m)^      - $([regex]::Escape($dependency))\r?$") {
        throw "Release preparation must depend on $dependency."
    }
}
if ($preparationJob -notmatch 'validate-release-schemas\.py') {
    throw 'Release preparation schema validation must gate release attestation.'
}
if ($preparationJob -match '(?m)^      - (?:test-msi-installers|release-gate-[\w-]+)\r?$' -or
    $preparationJob -notmatch 'package-release\.ps1' -or
    $preparationJob -notmatch 'name: audio-pilot-release-artifacts' -or
    $preparationJob -match '(?m)^\s+(?:contents|id-token|attestations|artifact-metadata): write\r?$' -or
    $preparationJob -match 'uses: (?:actions/attest|softprops/action-gh-release)@') {
    throw 'Release preparation must overlap test gates without attesting or publishing untested packages.'
}
if ($publicationJob -notmatch 'name: Download prepared release outputs[\s\S]*?name: audio-pilot-release-artifacts' -or
    $publicationJob -notmatch 'RELEASE_COMMIT: \$\{\{ github.sha \}\}' -or
    $publicationJob -notmatch 'validate-release-integrity\.ps1 -ExpectedCommit \$env:RELEASE_COMMIT' -or
    $publicationJob.IndexOf('validate-release-integrity.ps1') -gt $publicationJob.IndexOf('uses: actions/attest@')) {
    throw 'Release publication must verify the exact prepared artifacts and source commit before attestation.'
}
$publicationNeeds = [regex]::Match($publicationJob, '(?m)^    needs:\r?\n(?<items>(?:      - [a-zA-Z0-9_-]+\r?\n)+)').Groups['items'].Value
foreach ($dependency in @('resolve-release-version', 'release-gate-unit-tests', 'release-gate-integration-tests', 'release-gate-stress-tests', 'publish-profiles', 'build-msi-installers', 'build-upgrade-baselines', 'test-msi-installers', 'prepare-packages')) {
    if ($publicationNeeds -notmatch "(?m)^      - $([regex]::Escape($dependency))\r?$") {
        throw "Release publication must depend on $dependency."
    }
}
Assert-Pattern -Pattern 'name:\s*Release Artifacts\s*(?:\r?\n)+permissions:\s*(?:\r?\n)\s+contents:\s*read' -Description 'release workflow must default to read-only repository contents.'
Assert-Pattern -Pattern 'publish-and-package:\s*(?:.|\r|\n)*?permissions:\s*(?:.|\r|\n)*?contents:\s*write\s*(?:\r?\n)\s+id-token:\s*write\s*(?:\r?\n)\s+attestations:\s*write\s*(?:\r?\n)\s+artifact-metadata:\s*write' -Description 'only publish-and-package may receive release write and attestation permissions.'
Assert-Pattern -Pattern 'publish-release-profiles\.ps1\s+-TargetProfile\s+\$profile\s+-Configuration\s+Release' -Description 'publish-profiles must use the shared publish-release-profiles script.'
Assert-Pattern -Pattern 'publish-release-profiles\.ps1\s+-TargetProfile\s+\$profile\s+-Configuration\s+Release\s+-Version\s+\$env:RELEASE_VERSION' -Description 'publish profiles must receive the resolved release version.'
Assert-Pattern -Pattern 'Requested release version.*does not match Version\.props' -Description 'release version must be validated against Version.props before building.'
Assert-Pattern -Pattern 'needs\.resolve-release-version\.outputs\.version' -Description 'release jobs must consume the centralized version output.'
Assert-Pattern -Pattern 'softprops/action-gh-release@[0-9a-fA-F]{40}' -Description 'release creation must use an immutable softprops/action-gh-release commit.'
Assert-Pattern -Pattern 'validate-release-schemas\.py[^\r\n]*--sbom[^\r\n]*--winget[^\r\n]*--self-test' -Description 'SPDX structural/semantic and winget schema validation must gate release attestation.'
Assert-Pattern -Pattern 'name: audio-pilot-release-metadata\s+path: \|\s+artifacts/release/SHA256SUMS.txt\s+artifacts/release/release-manifest.json\s+artifacts/release/\*-sbom.spdx.json\s+artifacts/release/\*-provenance.json\s+artifacts/release/winget/' -Description 'small verification metadata artifact must retain checksums, manifest, SBOM, provenance, and winget files.'
Assert-Pattern -Pattern 'predicate-type:\s*https://spdx\.dev/Document/v3\s' -Description 'SBOM attestation must use the standard SPDX v3 predicate.'
Assert-Pattern -Pattern 'predicate-path:\s*\$\{\{\s*steps\.release-metadata\.outputs\.sbom-path\s*\}\}' -Description 'SPDX v3 attestation must sign the validated JSON-LD file.'

$requiredEnvMappings = @(
    'RELEASE_REF_TYPE:\s*\$\{\{\s*github\.ref_type\s*\}\}',
    'RUNNER_ENVIRONMENT:\s*\$\{\{\s*runner\.environment\s*\}\}',
    'AUDIOPILOT_TEST_OUTPUT_DEVICE_A:\s*\$\{\{\s*secrets\.AUDIOPILOT_TEST_OUTPUT_DEVICE_A\s*\}\}',
    'AUDIOPILOT_TEST_OUTPUT_DEVICE_B:\s*\$\{\{\s*secrets\.AUDIOPILOT_TEST_OUTPUT_DEVICE_B\s*\}\}',
    'AUDIOPILOT_TEST_INPUT_DEVICE_A:\s*\$\{\{\s*secrets\.AUDIOPILOT_TEST_INPUT_DEVICE_A\s*\}\}',
    'AUDIOPILOT_TEST_INPUT_DEVICE_B:\s*\$\{\{\s*secrets\.AUDIOPILOT_TEST_INPUT_DEVICE_B\s*\}\}'
)

foreach ($envPattern in $requiredEnvMappings) {
    Assert-Pattern -Pattern $envPattern -Description "required release-gate env mapping missing: $envPattern"
}

$requiredFilterPatterns = @(
    'run-tests\.ps1.*-Category unit',
    'run-tests\.ps1.*-Category integration',
    'run-tests\.ps1.*-Category stress'
)

foreach ($filterPattern in $requiredFilterPatterns) {
    Assert-Pattern -Pattern $filterPattern -Description "required release-gate filter missing: $filterPattern"
}

$requiredLogicPatterns = @(
    '\$isTagRelease\s*=\s*\$env:RELEASE_REF_TYPE\s*-eq\s*"tag"',
    '\$isSelfHostedRunner\s*=\s*\$env:RUNNER_ENVIRONMENT\s*-eq\s*"self-hosted"',
    '\$env:AUDIOPILOT_REQUIRE_INTEGRATION_HARDWARE\s*=\s*if\s*\(\$isSelfHostedRunner\)\s*\{\s*"1"\s*\}\s*else\s*\{\s*"0"\s*\}',
    'if\s*\(\$missing\.Count\s*-eq\s*0\)',
    'validate-release-hardware\.ps1\s*-Configuration\s+Release\s+-NoBuild\s+-Strict:\$isSelfHostedRunner',
    'if\s*\(\$isSelfHostedRunner\)\s*\{\s*throw',
    'if\s*\(\$isTagRelease\)',
    'Hardware-specific integration tests will dynamically skip for this tag release because no self-hosted hardware runner is configured',
    'Hardware-specific integration tests will dynamically skip \(missing secrets:',
    'Hardware-specific integration tests will dynamically skip because device-id preflight failed:',
    'Tests that only need a default endpoint can still run\.'
)

foreach ($logicPattern in $requiredLogicPatterns) {
    Assert-Pattern -Pattern $logicPattern -Description "required release-gate logic missing: $logicPattern"
}

$githubConfigurationFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot ".github/workflows") -File |
        Where-Object { $_.Extension -in @('.yml', '.yaml') }
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot ".github/actions") -Recurse -File |
        Where-Object { $_.Extension -in @('.yml', '.yaml') }
)

foreach ($configurationFile in $githubConfigurationFiles) {
    $configurationLines = Get-Content -LiteralPath $configurationFile.FullName
    $runBlockIndent = $null

    for ($lineIndex = 0; $lineIndex -lt $configurationLines.Count; $lineIndex++) {
        $line = $configurationLines[$lineIndex]
        $trimmedLine = $line.Trim()
        $indent = $line.Length - $line.TrimStart().Length

        if ($null -ne $runBlockIndent) {
            if (-not [string]::IsNullOrWhiteSpace($line) -and $indent -le $runBlockIndent) {
                $runBlockIndent = $null
            }
            elseif ($line -match '\$\{\{') {
                throw "GitHub Actions security validation failed: expression interpolation is not allowed inside run blocks ($($configurationFile.FullName):$($lineIndex + 1)). Map the value through env instead."
            }
        }

        $runMatch = [regex]::Match($line, '^(?<indent>\s*)run:\s*(?<command>.*)$')
        if ($runMatch.Success) {
            if ($runMatch.Groups["command"].Value -match '\$\{\{') {
                throw "GitHub Actions security validation failed: expression interpolation is not allowed in run commands ($($configurationFile.FullName):$($lineIndex + 1)). Map the value through env instead."
            }

            if ($runMatch.Groups["command"].Value.Trim() -in @('|', '>', '|-', '>-')) {
                $runBlockIndent = $runMatch.Groups["indent"].Value.Length
            }
        }

        $usesMatch = [regex]::Match($trimmedLine, '^uses:\s*(?<target>[^\s#]+)')
        if (-not $usesMatch.Success) {
            continue
        }

        $target = $usesMatch.Groups["target"].Value
        if ($target.StartsWith('./', [StringComparison]::Ordinal)) {
            continue
        }

        $atIndex = $target.LastIndexOf('@')
        $reference = if ($atIndex -ge 0) { $target.Substring($atIndex + 1) } else { [string]::Empty }
        if ($reference -notmatch '^[0-9a-fA-F]{40}$') {
            throw "GitHub Actions security validation failed: external action must be pinned to a full commit SHA ($($configurationFile.FullName):$($lineIndex + 1), target=$target)."
        }
    }
}

Write-Host "Release gate workflow policy validation passed."
