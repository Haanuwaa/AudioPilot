param(
    [switch]$Strict,
    [switch]$SelfTest,
    [string]$TestsRoot = "AudioPilot.Tests",
    [string[]]$SourceRoot = @("AudioPilot", "AudioPilot.Core")
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

function Get-TestTypeDeclarations {
    param($Members, [string]$Prefix, [string]$File, [string]$Owner = '')

    foreach ($member in $Members) {
        if ($member -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax]) {
            Get-TestTypeDeclarations -Members $member.Members -Prefix "$Prefix$($member.Name)." -File $File
        }
        elseif ($member -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax]) {
            $name = "$Prefix$($member.Identifier.ValueText)"
            if ($null -ne $member.TypeParameterList) { $name += '#' + $member.TypeParameterList.Parameters.Count }
            $attributes = $member.AttributeLists.ToString()
            $body = [Collections.Generic.List[string]]::new()
            $hasTests = $false
            foreach ($child in $member.Members) {
                if ($child -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax]) { continue }
                $body.Add($child.ToString())
                if ($child -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and
                    $child.AttributeLists.ToString() -match '\[(?:\w+\.)?\w*(Fact|Theory)(?:\(|\])') {
                    $hasTests = $true
                }
            }
            [pscustomobject]@{
                Name = $name; File = $File; Attributes = $attributes; Body = ($body -join [Environment]::NewLine)
                HasTests = $hasTests; Owner = $Owner; Private = 'private' -cin @($member.Modifiers | ForEach-Object ValueText)
            }
            Get-TestTypeDeclarations -Members $member.Members -Prefix "$name+" -File $File -Owner $name
        }
    }
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
    # PowerShell ships the Roslyn parser used by Add-Type. Only type boundaries and attributes require parsing.
    if (-not ('Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree' -as [type])) {
        Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
        Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
    }
    $types = @(foreach ($file in $files) {
        $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName))
        Get-TestTypeDeclarations -Members $tree.GetRoot().Members -Prefix '' -File $file.FullName
    })
    $isolatedCollections = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($type in $types) {
        foreach ($match in [regex]::Matches($type.Attributes, '\[CollectionDefinition\("([^"]+)"[^\]]*DisableParallelization\s*=\s*true')) {
            [void]$isolatedCollections.Add($match.Groups[1].Value)
        }
    }

    $groups = $types | Group-Object Name -CaseSensitive
    $testAttributes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $testFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($group in $groups) {
        if ($group.Group.HasTests -contains $true) {
            $testAttributes.Add($group.Name, ($group.Group.Attributes -join [Environment]::NewLine))
            foreach ($type in $group.Group) { [void]$testFiles.Add($type.File) }
        }
    }
    foreach ($group in $groups) {
        $attributes = $group.Group.Attributes -join [Environment]::NewLine
        $body = $group.Group.Body -join [Environment]::NewLine
        $declaration = $group.Group[0]
        # A private fixture owned by a test runs inside that test's collection. Its reset still has to belong to the fixture.
        if ($group.Group.HasTests -notcontains $true -and $group.Group.Private -contains $true -and
            $testAttributes.ContainsKey($declaration.Owner)) {
            $attributes = $testAttributes[$declaration.Owner]
        }
        $hasIsolation = @([regex]::Matches($attributes, '\[Collection\("([^"]+)"\)\]') |
            Where-Object { $isolatedCollections.Contains($_.Groups[1].Value) }).Count -gt 0
        foreach ($rule in $Rules) {
            $mutations = @($group.Group.Where({ $testFiles.Contains($_.File) -and $_.Body -match $rule.MutationPattern }))
            if (!$mutations.Count) { continue }
            $hasReset = $body -match $rule.ResetPattern
            if ($hasIsolation -and $hasReset) { continue }
            $missing = @()
            if (-not $hasIsolation) { $missing += 'a declared collection with DisableParallelization=true' }
            if (-not $hasReset) { $missing += "restoration matching $($rule.ResetPattern)" }
            $findings.Add([pscustomobject]@{
                File = $mutations[0].File
                Type = $group.Name
                Hook = $rule.Name
                Missing = $missing -join ' and '
            })
        }
    }
    return $findings.ToArray()
}

function Get-ProductionHookDeclarations {
    param([string[]]$ResolvedSourceRoots)

    $results = @()
    foreach ($root in $ResolvedSourceRoots) {
        if (-not (Test-Path $root)) {
            continue
        }

        $results += @(Get-ChildItem -Path $root -Recurse -Filter "*.cs" |
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

    return $results
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
        @{ Name = 'IsolatedSibling'; Expected = 1; Source = '[Collection("Isolated")] public class Sibling { [Fact] public void Run() { MediaKeyHelper.ResetTestHooks(); } } public class Tests { [Fact] public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; } }' },
        @{ Name = 'SiblingReset'; Expected = 1; Source = '[Collection("Isolated")] public class Tests { [Fact] public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; } } public class Sibling { public void Dispose() { MediaKeyHelper.ResetTestHooks(); } }' },
        @{ Name = 'PrivateFixture'; Expected = 0; Source = '[Collection("Isolated")] public class Tests { [Fact] public void Run() {} private class Fixture { public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; MediaKeyHelper.ResetTestHooks(); } } }' },
        @{ Name = 'NestedTestNeedsIsolation'; Expected = 1; Source = '[Collection("Isolated")] public class Tests { [Fact] public void Run() {} public class Nested { [Fact] public void Run() { MediaKeyHelper.SendInputOverrideForTests = null; MediaKeyHelper.ResetTestHooks(); } } }' },
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
$declarations = @(Get-ProductionHookDeclarations -ResolvedSourceRoots $SourceRoot)
$uncoveredHooks = @()
foreach ($decl in $declarations) {
    if ($decl.Text -match '\b(\w+(?:ForTests|ProviderOverride))\s*\{') {
        $propName = $matches[1]
        $typeName = [IO.Path]::GetFileNameWithoutExtension($decl.File).Split('.')[0]
        $probe = "$typeName.$propName = null;"
        $covered = $false
        foreach ($rule in $hookRules) {
            if ($probe -match $rule.MutationPattern) { $covered = $true; break }
        }
        if (-not $covered) {
            $uncoveredHooks += [pscustomobject]@{ File = $decl.File; Line = $decl.Line; Property = "$typeName.$propName" }
        }
    }
}

Write-Host "Mutable static test-hook properties found: $($declarations.Count)"

if ($findings.Count -eq 0 -and $uncoveredHooks.Count -eq 0) {
    Write-Host "Static test-hook isolation audit passed."
    exit 0
}

foreach ($finding in $findings) {
    Write-Warning ("{0} ({1}): static hook '{2}' is mutated without {3}." -f $finding.File, $finding.Type, $finding.Hook, $finding.Missing)
}
foreach ($hook in $uncoveredHooks) {
    Write-Warning ("{0}:{1}: mutable static hook '{2}' is not covered by any rule in validate-test-isolation.ps1." -f $hook.File, $hook.Line, $hook.Property)
}

$totalIssues = $findings.Count + $uncoveredHooks.Count
if ($Strict) {
    throw "Static test-hook isolation audit failed with $totalIssues finding(s)."
}

Write-Warning "Static test-hook isolation audit found $totalIssues issue(s). CI runs this script with -Strict."
