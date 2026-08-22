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
        [string]$ResetPattern
    )

    [pscustomobject]@{
        Name = $Name
        MutationPattern = $MutationPattern
        ResetPattern = $ResetPattern
    }
}

$hookRules = @(
    New-HookRule -Name "MediaKeyHelper" `
        -MutationPattern 'MediaKeyHelper\.\w+OverrideForTests\s*=(?![=>])' `
        -ResetPattern 'MediaKeyHelper\.ResetTestHooks\('

    New-HookRule -Name "AppViewModel static test hooks" `
        -MutationPattern 'AppViewModel\.(TryApplyStartupChangeOverrideForTests|ExitApplicationOverrideForTests|ApplyRoutineAbsoluteVolumeOverrideForTests|ExportSettingsDialogForTests|ImportSettingsDialogForTests|RoutineReconnectPostAttemptDelayAsyncForTests)\s*=(?![=>])' `
        -ResetPattern 'AppViewModel\.(ResetTestHooks|ResetSettingsTransferDialogsForTests)\(|AppViewModel\.RoutineReconnectPostAttemptDelayAsyncForTests\s*=\s*original\w*'

    New-HookRule -Name "AudioDeviceService mute overrides" `
        -MutationPattern 'AudioDeviceService\.(SetMicrophoneMuteOverrideForTests|SetPlaybackMuteOverrideForTests)\s*=(?![=>])' `
        -ResetPattern 'AudioDeviceService\.ResetTestHooks\('

    New-HookRule -Name "BackgroundTaskHelper delay override" `
        -MutationPattern 'BackgroundTaskHelper\.DelayAsyncForTests\s*=(?![=>])' `
        -ResetPattern 'BackgroundTaskHelper\.DelayAsyncForTests\s*=\s*(Task\.Delay|original\w*)'

    New-HookRule -Name "Runtime tuning overrides" `
        -MutationPattern '(RuntimeTuningConfig|BluetoothReconnectRuntimeConfig)\.\w+\s*=(?![=>])|CliRuntimeManager\.TrySet\(' `
        -ResetPattern '(RuntimeTuningConfig|BluetoothReconnectRuntimeConfig)\.\w+\s*=\s*_?original\w*|CliRuntimeManager\.TrySet\(\w+\.Key,\s*\w+\.CurrentValue'

    New-HookRule -Name "AppDataPaths providers" `
        -MutationPattern 'AppDataPaths\.\w+ProviderOverride\s*=(?![=>])' `
        -ResetPattern 'AppDataPaths\.\w+ProviderOverride\s*=\s*original\w*'

    New-HookRule -Name "Log privacy" `
        -MutationPattern 'LogPrivacy\.ApplySettings\(' `
        -ResetPattern 'LogPrivacy\.ApplySettings\((null|_?original\w*)\)'

    New-HookRule -Name "Console output" `
        -MutationPattern 'Console\.SetOut\(' `
        -ResetPattern 'Console\.SetOut\(original\w*\)'
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
    $files = @(Get-ChildItem -Path $ResolvedTestsRoot -Recurse -Filter "*.cs" |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object FullName)
    $contents = @{}
    $partialContents = @{}
    $isolatedCollections = New-Object System.Collections.Generic.HashSet[string]
    foreach ($file in $files) {
        $content = Get-Content -Raw -LiteralPath $file.FullName
        $contents[$file.FullName] = $content
        foreach ($match in [regex]::Matches($content, '\[CollectionDefinition\("([^"]+)"[^\]]*DisableParallelization\s*=\s*true')) {
            [void]$isolatedCollections.Add($match.Groups[1].Value)
        }
        $namespace = [regex]::Match($content, '\bnamespace\s+([\w.]+)').Groups[1].Value
        foreach ($match in [regex]::Matches($content, '\bpartial\s+class\s+(\w+)')) {
            $key = "$namespace.$($match.Groups[1].Value)"
            if (-not $partialContents.ContainsKey($key)) { $partialContents[$key] = '' }
            $partialContents[$key] += "`n$content"
        }
    }

    foreach ($file in $files) {
        $content = $contents[$file.FullName]
        $classContent = $content
        $namespace = [regex]::Match($content, '\bnamespace\s+([\w.]+)').Groups[1].Value
        foreach ($match in [regex]::Matches($content, '\bpartial\s+class\s+(\w+)')) {
            $classContent += $partialContents["$namespace.$($match.Groups[1].Value)"]
        }
        if ($classContent -notmatch '\[(?:\w+\.)?\w*(Fact|Theory)(?:\(|\])') { continue }
        $hasIsolation = @(Get-FileCollections -Content $classContent | Where-Object { $isolatedCollections.Contains($_) }).Count -gt 0
        foreach ($rule in $Rules) {
            if ($content -notmatch $rule.MutationPattern) { continue }
            $hasReset = $classContent -match $rule.ResetPattern
            if ($hasIsolation -and $hasReset) { continue }
            $missing = @()
            if (-not $hasIsolation) { $missing += 'a declared collection with DisableParallelization=true' }
            if (-not $hasReset) { $missing += "restoration matching $($rule.ResetPattern)" }
            $findings.Add([pscustomobject]@{
                File = $file.FullName
                Hook = $rule.Name
                Missing = $missing -join ' and '
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
        Select-String -Pattern '^\s*internal static .*\b\w*(ForTests|ProviderOverride)\s*\{\s*get;\s*set;' |
        ForEach-Object {
            [pscustomobject]@{
                File = $_.Path
                Line = $_.LineNumber
                Text = $_.Line.Trim()
            }
        })
}

function Invoke-SelfTest {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $root = [IO.Path]::GetFullPath((Join-Path $tempRoot ("AudioPilot.TestIsolationAudit." + [guid]::NewGuid().ToString("N"))))
    if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Self-test path escaped the temporary root.' }
    $cases = @(
        @{ Name = 'BothProtections'; Expected = 0; Source = '[Collection("Isolated")] public class Tests { [Fact] public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; MediaKeyHelper.ResetTestHooks(); } }' },
        @{ Name = 'ResetOnly'; Expected = 1; Source = 'public class Tests { [Fact] public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; MediaKeyHelper.ResetTestHooks(); } }' },
        @{ Name = 'CollectionOnly'; Expected = 1; Source = '[Collection("Isolated")] public class Tests { [Fact] public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; } }' },
        @{ Name = 'UndefinedCollection'; Expected = 1; Source = '[Collection("Missing")] public class Tests { [Fact] public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; MediaKeyHelper.ResetTestHooks(); } }' },
        @{ Name = 'ParallelCollection'; Expected = 1; Source = '[Collection("Parallel")] public class Tests { [Fact] public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; MediaKeyHelper.ResetTestHooks(); } }' },
        @{ Name = 'Helpers'; Expected = 1; Source = 'public class Tests { [Fact] public void Run() { MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = null; MediaKeyHelper.ResetTestHooks(); } }' },
        @{ Name = 'Partial'; Expected = 0; Source = 'namespace First; public partial class Tests { [Fact] public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; } }'; Other = 'namespace First; [Collection("Isolated")] public partial class Tests { public void Dispose() { MediaKeyHelper.ResetTestHooks(); } }' },
        @{ Name = 'DifferentNamespaces'; Expected = 1; Source = 'namespace First; public partial class Tests { [Fact] public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; } }'; Other = 'namespace Second; [Collection("Isolated")] public partial class Tests { public void Dispose() { MediaKeyHelper.ResetTestHooks(); } }' },
        @{ Name = 'Comparison'; Expected = 0; Source = 'public class Tests { [Fact] public void Run() { Assert.True(RuntimeTuningConfig.AutoSaveDebounceMs == 100); } }' }
    )
    try {
        foreach ($case in $cases) {
            $tests = Join-Path $root $case.Name
            New-Item -ItemType Directory -Path $tests -Force | Out-Null
            '[CollectionDefinition("Isolated", DisableParallelization = true)] public class Definition {} [CollectionDefinition("Parallel")] public class ParallelDefinition {}' |
                Set-Content -LiteralPath (Join-Path $tests 'Collections.cs') -Encoding utf8
            $fixtureRoot = if ($case.Name -eq 'Helpers') { Join-Path $tests 'Helpers' } else { $tests }
            New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
            $case.Source | Set-Content -LiteralPath (Join-Path $fixtureRoot 'Tests.cs') -Encoding utf8
            if ($case.ContainsKey('Other')) { $case.Other | Set-Content -LiteralPath (Join-Path $tests 'Other.cs') -Encoding utf8 }
            $findings = @(Get-TestHookAuditFindings -ResolvedTestsRoot $tests -Rules $hookRules)
            if ($findings.Count -ne $case.Expected) { throw "Self-test $($case.Name) expected $($case.Expected) findings, found $($findings.Count)." }
        }
        Write-Host "Test-hook isolation audit self-test passed ($($cases.Count) cases)."
    }
    finally {
        if (Test-Path -LiteralPath $root) {
            if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to clean up a linked self-test root.' }
            Remove-Item -LiteralPath $root -Recurse -Force
        }
    }
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}

$findings = @(Get-TestHookAuditFindings -ResolvedTestsRoot $TestsRoot -Rules $hookRules)
$declarations = Get-ProductionHookDeclarations -ResolvedSourceRoot $SourceRoot

Write-Host "Mutable static test-hook properties found: $($declarations.Count)"

if ($findings.Count -eq 0) {
    Write-Host "Static test-hook isolation audit passed."
    exit 0
}

foreach ($finding in $findings) {
    Write-Warning ("{0}: static hook '{1}' is mutated without {2}." -f $finding.File, $finding.Hook, $finding.Missing)
}

if ($Strict) {
    throw "Static test-hook isolation audit failed with $($findings.Count) finding(s)."
}

Write-Warning "Static test-hook isolation audit found $($findings.Count) issue(s). CI runs this script with -Strict."
