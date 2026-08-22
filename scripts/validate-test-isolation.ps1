param(
    [switch]$Strict,
    [switch]$SelfTest,
    [string]$TestsRoot = "AudioPilot.Tests",
    [string]$SourceRoot = "AudioPilot"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-HookRule {
    param(
        [string]$Name,
        [string]$MutationPattern,
        [string]$ResetPattern,
        [string[]]$Collections
    )

    [pscustomobject]@{
        Name = $Name
        MutationPattern = $MutationPattern
        ResetPattern = $ResetPattern
        Collections = $Collections
    }
}

$hookRules = @(
    New-HookRule -Name "MediaKeyHelper" `
        -MutationPattern "MediaKeyHelper\.(SendInputOverrideForTests|SystemMediaCommandOverrideForTests|LoggerOverrideForTests)\s*=" `
        -ResetPattern "MediaKeyHelper\.ResetTestHooks\(" `
        -Collections @("MediaKeyHelperIsolation")

    New-HookRule -Name "MessageBoxService" `
        -MutationPattern "MessageBoxService\.SetNativeForTests\(" `
        -ResetPattern "MessageBoxService\.ResetNativeForTests\(" `
        -Collections @("MessageBoxServiceIsolation")

    New-HookRule -Name "AppViewModel static test hooks" `
        -MutationPattern "AppViewModel\.(TryApplyStartupChangeOverrideForTests|ExitApplicationOverrideForTests|ApplyRoutineAbsoluteVolumeOverrideForTests|ExportSettingsDialogForTests|ImportSettingsDialogForTests)\s*=" `
        -ResetPattern "AppViewModel\.(ResetTestHooks|ResetSettingsTransferDialogsForTests)\(" `
        -Collections @("MessageBoxServiceIsolation", "WpfApplicationIsolation")

    New-HookRule -Name "AudioDeviceService mute overrides" `
        -MutationPattern "AudioDeviceService\.(SetMicrophoneMuteOverrideForTests|SetPlaybackMuteOverrideForTests)\s*=" `
        -ResetPattern "AudioDeviceService\.ResetTestHooks\(" `
        -Collections @("MessageBoxServiceIsolation", "AudioHardwareStressIsolation")

    New-HookRule -Name "BackgroundTaskHelper delay override" `
        -MutationPattern "BackgroundTaskHelper\.DelayAsyncForTests\s*=" `
        -ResetPattern "BackgroundTaskHelper\.DelayAsyncForTests\s*=\s*Task\.Delay" `
        -Collections @("BackgroundTaskHelperDelayIsolation")

    New-HookRule -Name "Runtime tuning overrides" `
        -MutationPattern "RuntimeTuningConfig\.\w+\s*=" `
        -ResetPattern "RuntimeTuningConfig\.\w+\s*=\s*(original|original\w*|AppConstants\.)" `
        -Collections @("RuntimeTuningConfigIsolation")
)

function Get-FileCollections {
    param([string]$Content)

    $matches = [regex]::Matches($Content, '\[Collection\("([^"]+)"\)\]')
    return @($matches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
}

function Get-TestHookAuditFindings {
    param(
        [string]$ResolvedTestsRoot,
        [object[]]$Rules
    )

    if (-not (Test-Path $ResolvedTestsRoot)) {
        throw "Tests root not found: $ResolvedTestsRoot"
    }

    $findings = New-Object System.Collections.Generic.List[object]
    $testFiles = Get-ChildItem -Path $ResolvedTestsRoot -Recurse -Filter "*.cs" |
        Where-Object {
            $_.FullName -notmatch "\\bin\\" -and
            $_.FullName -notmatch "\\obj\\" -and
            $_.FullName -notmatch "\\Helpers\\"
        } |
        Sort-Object FullName

    $partialClassCollections = @{}
    foreach ($file in $testFiles) {
        $content = Get-Content -Raw -Path $file.FullName
        $collections = @(Get-FileCollections -Content $content)
        if ($collections.Count -eq 0) {
            continue
        }

        foreach ($match in [regex]::Matches($content, '\bpartial\s+class\s+([A-Za-z_][A-Za-z0-9_]*)')) {
            $className = $match.Groups[1].Value
            if (-not $partialClassCollections.ContainsKey($className)) {
                $partialClassCollections[$className] = New-Object System.Collections.Generic.HashSet[string]
            }

            foreach ($collection in $collections) {
                [void]$partialClassCollections[$className].Add($collection)
            }
        }
    }

    foreach ($file in $testFiles) {
        $content = Get-Content -Raw -Path $file.FullName
        $collectionSet = New-Object System.Collections.Generic.HashSet[string]
        foreach ($collection in (Get-FileCollections -Content $content)) {
            [void]$collectionSet.Add($collection)
        }

        foreach ($match in [regex]::Matches($content, '\bpartial\s+class\s+([A-Za-z_][A-Za-z0-9_]*)')) {
            $className = $match.Groups[1].Value
            if ($partialClassCollections.ContainsKey($className)) {
                foreach ($collection in $partialClassCollections[$className]) {
                    [void]$collectionSet.Add($collection)
                }
            }
        }

        $collections = @($collectionSet)

        foreach ($rule in $Rules) {
            if ($content -notmatch $rule.MutationPattern) {
                continue
            }

            $hasCollection = $false
            foreach ($collection in $rule.Collections) {
                if ($collections -contains $collection) {
                    $hasCollection = $true
                    break
                }
            }

            $hasReset = $content -match $rule.ResetPattern
            if ($hasCollection -or $hasReset) {
                continue
            }

            $findings.Add([pscustomobject]@{
                File = $file.FullName
                Hook = $rule.Name
                RequiredCollections = ($rule.Collections -join ", ")
                ResetPattern = $rule.ResetPattern
            })
        }
    }

    return $findings.ToArray()
}

function Get-ProductionHookDeclarations {
    param([string]$ResolvedSourceRoot)

    if (-not (Test-Path $ResolvedSourceRoot)) {
        return @()
    }

    return @(Get-ChildItem -Path $ResolvedSourceRoot -Recurse -Filter "*.cs" |
        Where-Object { $_.FullName -notmatch "\\bin\\" -and $_.FullName -notmatch "\\obj\\" } |
        Select-String -Pattern "internal static .*ForTests|OverrideForTests|Reset.*ForTests|Set.*ForTests" |
        ForEach-Object {
            [pscustomobject]@{
                File = $_.Path
                Line = $_.LineNumber
                Text = $_.Line.Trim()
            }
        })
}

function Invoke-SelfTest {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("AudioPilot.TestIsolationAudit." + [guid]::NewGuid().ToString("N"))
    $tests = Join-Path $root "AudioPilot.Tests"
    New-Item -ItemType Directory -Path $tests | Out-Null

    try {
        @"
using Xunit;
[Collection("MediaKeyHelperIsolation")]
public sealed class GoodTests
{
    public void Test()
    {
        MediaKeyHelper.SendInputOverrideForTests = _ => (2u, 0);
    }
}
"@ | Set-Content -Path (Join-Path $tests "GoodTests.cs") -Encoding UTF8

        $passFindings = @(Get-TestHookAuditFindings -ResolvedTestsRoot $tests -Rules $hookRules)
        if ($passFindings.Count -ne 0) {
            throw "Self-test pass case unexpectedly produced findings."
        }

        @"
public sealed class BadTests
{
    public void Test()
    {
        MediaKeyHelper.SendInputOverrideForTests = _ => (2u, 0);
    }
}
"@ | Set-Content -Path (Join-Path $tests "BadTests.cs") -Encoding UTF8

        $failFindings = @(Get-TestHookAuditFindings -ResolvedTestsRoot $tests -Rules $hookRules)
        if ($failFindings.Count -ne 1) {
            throw "Self-test strict-fail case expected exactly one finding, found $($failFindings.Count)."
        }

        Write-Host "Test-hook isolation audit self-test passed."
    }
    finally {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

$findings = @(Get-TestHookAuditFindings -ResolvedTestsRoot $TestsRoot -Rules $hookRules)
$declarations = Get-ProductionHookDeclarations -ResolvedSourceRoot $SourceRoot

Write-Host "Static test-hook declarations found: $($declarations.Count)"

if ($findings.Count -eq 0) {
    Write-Host "Static test-hook isolation audit passed."
    exit 0
}

foreach ($finding in $findings) {
    Write-Warning ("{0}: static hook '{1}' is mutated without an approved collection ({2}) or reset pattern ({3})." -f $finding.File, $finding.Hook, $finding.RequiredCollections, $finding.ResetPattern)
}

if ($Strict) {
    throw "Static test-hook isolation audit failed with $($findings.Count) finding(s)."
}

Write-Warning "Static test-hook isolation audit found $($findings.Count) issue(s). CI runs this script with -Strict."
