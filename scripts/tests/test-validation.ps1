Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testRoot = Join-Path $repositoryRoot "artifacts/testresults/validation-scripts/$([Guid]::NewGuid().ToString('N'))"
$toolsRoot = Join-Path $testRoot 'tools'
$scriptsRoot = Join-Path $testRoot 'scripts'
New-Item -ItemType Directory -Path $toolsRoot, (Join-Path $scriptsRoot 'tests') -Force | Out-Null
foreach ($script in @('validate-format.ps1', 'validate-all.ps1')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "scripts/$script") -Destination $scriptsRoot
}

Set-Content -LiteralPath (Join-Path $toolsRoot 'dotnet.ps1') -Value @'
ConvertTo-Json -InputObject @($args) | Set-Content -LiteralPath $env:AUDIOPILOT_FORMAT_TEST_RECORD
exit ([int]$env:AUDIOPILOT_FORMAT_TEST_EXIT)
'@
$stepStub = @'
@{ script = Split-Path -Leaf $PSCommandPath; arguments = @($args) } |
    ConvertTo-Json -Compress | Add-Content -LiteralPath $env:AUDIOPILOT_VALIDATION_TEST_RECORD
if ((Split-Path -Leaf $PSCommandPath) -eq $env:AUDIOPILOT_VALIDATION_TEST_FAILURE) { exit 9 }
exit 0
'@
foreach ($step in @(
    'build.ps1', 'run-tests.ps1', 'validate-coverage.ps1', 'validate-test-isolation.ps1',
    'validate-line-endings.ps1', 'update-cli-docs.ps1', 'validate-doc-links.ps1', 'validate-release-gate-policy.ps1',
    'tests/test-runner.ps1', 'tests/test-validation.ps1', 'tests/test-msi-helpers.ps1'
)) {
    Set-Content -LiteralPath (Join-Path $scriptsRoot $step) -Value $stepStub
}

$savedEnvironment = @{}
foreach ($name in @('PATH', 'GITHUB_EVENT_NAME', 'GITHUB_BASE_REF', 'AUDIOPILOT_FORMAT_TEST_RECORD',
    'AUDIOPILOT_FORMAT_TEST_EXIT', 'AUDIOPILOT_VALIDATION_TEST_RECORD', 'AUDIOPILOT_VALIDATION_TEST_FAILURE')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
Push-Location $testRoot
try {
    $env:PATH = $toolsRoot + [IO.Path]::PathSeparator + $env:PATH
    $env:GITHUB_EVENT_NAME = ''
    $env:GITHUB_BASE_REF = ''
    $env:AUDIOPILOT_FORMAT_TEST_RECORD = Join-Path $testRoot 'format.json'
    $env:AUDIOPILOT_FORMAT_TEST_EXIT = '0'
    $env:AUDIOPILOT_VALIDATION_TEST_RECORD = Join-Path $testRoot 'steps.jsonl'
    $env:AUDIOPILOT_VALIDATION_TEST_FAILURE = ''
    if ((Get-Command dotnet).Source -ne (Join-Path $toolsRoot 'dotnet.ps1')) { throw 'The fake formatter was not selected.' }

    & git init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create the disposable Git fixture.' }
    Set-Content -LiteralPath 'AudioPilot.Format.slnf' -Value '{}'
    Set-Content -LiteralPath 'tracked.cs' -Value '// initial'
    Set-Content -LiteralPath 'deleted.cs' -Value '// removed later'
    Set-Content -LiteralPath '.gitignore' -Value "ignored.cs`nartifacts/`ntools/`nscripts/`n*.json`n*.jsonl"
    & git add AudioPilot.Format.slnf tracked.cs deleted.cs .gitignore
    if ($LASTEXITCODE -ne 0) { throw 'Failed to stage the disposable fixture.' }
    & git -c user.name=ValidationFixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) { throw 'Failed to commit the disposable fixture.' }
    Add-Content -LiteralPath 'tracked.cs' -Value '// working change'
    Set-Content -LiteralPath 'new file.cs' -Value '// untracked'
    Set-Content -LiteralPath 'ignored.cs' -Value '// ignored'
    Remove-Item -LiteralPath 'deleted.cs'
    & pwsh -NoProfile -File scripts/validate-format.ps1 -ChangedOnly
    if ($LASTEXITCODE -ne 0) { throw 'Scoped formatting failed.' }
    $formatArguments = Get-Content -LiteralPath $env:AUDIOPILOT_FORMAT_TEST_RECORD -Raw | ConvertFrom-Json
    if ('tracked.cs' -notin $formatArguments -or 'new file.cs' -notin $formatArguments -or
        'deleted.cs' -in $formatArguments -or 'ignored.cs' -in $formatArguments) {
        throw 'Scoped formatting did not select working changes and untracked C# files correctly.'
    }

    $env:AUDIOPILOT_FORMAT_TEST_EXIT = '7'
    & pwsh -NoProfile -File scripts/validate-format.ps1 -ChangedOnly
    if ($LASTEXITCODE -ne 7) { throw 'The formatter exit code was lost.' }
    $env:AUDIOPILOT_FORMAT_TEST_EXIT = '0'

    # The same PR path used by CI must include files changed since its merge base.
    & git branch fixture-base
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create the fixture base branch.' }
    & git remote add origin $testRoot
    if ($LASTEXITCODE -ne 0) { throw 'Failed to configure the local fixture remote.' }
    & git add tracked.cs 'new file.cs' deleted.cs
    if ($LASTEXITCODE -ne 0) { throw 'Failed to stage the fixture changes.' }
    & git -c user.name=ValidationFixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit --quiet -m changes
    if ($LASTEXITCODE -ne 0) { throw 'Failed to commit the fixture changes.' }
    $env:GITHUB_EVENT_NAME = 'pull_request'
    $env:GITHUB_BASE_REF = 'fixture-base'
    & pwsh -NoProfile -File scripts/validate-format.ps1 -ChangedOnly
    if ($LASTEXITCODE -ne 0) { throw 'PR formatting failed.' }
    $formatArguments = Get-Content -LiteralPath $env:AUDIOPILOT_FORMAT_TEST_RECORD -Raw | ConvertFrom-Json
    if ('tracked.cs' -notin $formatArguments -or 'new file.cs' -notin $formatArguments) { throw 'PR changes were missed.' }
    $env:GITHUB_BASE_REF = 'missing-fixture-base'
    & pwsh -NoProfile -File scripts/validate-format.ps1 -ChangedOnly *> (Join-Path $testRoot 'expected-fetch-failure.log')
    if ($LASTEXITCODE -eq 0) { throw 'A failed Git fetch was treated as a successful style check.' }
    $env:GITHUB_EVENT_NAME = ''

    foreach ($selection in @(
        @{ switches = @(); categories = @('unit') },
        @{ switches = @('-IncludeIntegration'); categories = @('unit', 'integration') },
        @{ switches = @('-IncludeStress'); categories = @('unit', 'stress') },
        @{ switches = @('-IncludeIntegration', '-IncludeStress'); categories = @('unit', 'integration', 'stress') }
    )) {
        Set-Content -LiteralPath $env:AUDIOPILOT_VALIDATION_TEST_RECORD -Value ''
        & pwsh -NoProfile -File scripts/validate-all.ps1 @($selection.switches) -Coverage
        if ($LASTEXITCODE -ne 0) { throw 'Local validation failed.' }
        $steps = @(Get-Content -LiteralPath $env:AUDIOPILOT_VALIDATION_TEST_RECORD | Where-Object { $_ } | ConvertFrom-Json)
        $tests = @($steps | Where-Object script -eq 'run-tests.ps1')
        $categories = @($tests | ForEach-Object { $_.arguments[1] })
        if (($categories -join ',') -cne ($selection.categories -join ',')) { throw 'Optional suites replaced unit validation.' }
        if (@($steps | Where-Object script -eq 'validate-coverage.ps1').Count -ne 1) { throw 'Coverage was collected without enforcing its gate.' }
        $docsStep = $steps | Where-Object script -eq 'update-cli-docs.ps1'
        if ('-NoBuild' -notin $docsStep.arguments) { throw 'CLI documentation checks rebuilt the solution.' }
        foreach ($test in $tests) {
            if ('-NoBuild' -notin $test.arguments -or '-NoRestore' -notin $test.arguments) { throw 'The aggregate validator rebuilt an already built solution.' }
        }
    }
    $env:AUDIOPILOT_VALIDATION_TEST_FAILURE = 'validate-coverage.ps1'
    Set-Content -LiteralPath $env:AUDIOPILOT_VALIDATION_TEST_RECORD -Value ''
    & pwsh -NoProfile -File scripts/validate-all.ps1 -Coverage
    if ($LASTEXITCODE -ne 9) { throw 'The coverage gate failure was lost.' }
    $steps = @(Get-Content -LiteralPath $env:AUDIOPILOT_VALIDATION_TEST_RECORD | Where-Object { $_ } | ConvertFrom-Json)
    if ($steps[-1].script -ne 'validate-coverage.ps1') { throw 'Validation continued after a failed coverage gate.' }
    Write-Host 'Validation script checks passed: local/PR file selection, formatter/Git failures, unit retention, build reuse, and coverage enforcement.'
}
finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
}
exit 0
