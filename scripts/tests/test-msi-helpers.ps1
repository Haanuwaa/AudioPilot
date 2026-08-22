param([string]$InstallerPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../lib/Msi.ps1')

$testRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../artifacts/testresults/msi-helpers/$([Guid]::NewGuid().ToString('N'))"))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$fixturePath = [IO.Path]::GetFullPath((Join-Path $testRoot 'fixture.msi'))
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
$summary = $null
try {
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($fixturePath, 3))
    foreach ($sql in @(
        'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)',
        "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('Probe', 'expected')",
        'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38) NOT NULL, `Attributes` SHORT NOT NULL PRIMARY KEY `Component`)',
        "INSERT INTO ``Component`` (``Component``, ``ComponentId``, ``Attributes``) VALUES ('Probe', '{B523FBB9-F66F-4919-939D-01A5E16FC979}', 256)"
    )) {
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($sql))
        try {
            $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        }
        finally {
            $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
    }
    $summary = $database.GetType().InvokeMember('SummaryInformation', 'GetProperty', $null, $database, @(1))
    $summary.GetType().InvokeMember('Property', 'SetProperty', $null, $summary, @(7, 'x64;1033')) | Out-Null
    $summary.GetType().InvokeMember('Persist', 'InvokeMethod', $null, $summary, $null) | Out-Null
    $database.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $database, $null) | Out-Null
}
finally {
    if ($null -ne $summary) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
    if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}

$cases = @(
    @{ Name = 'property'; Run = {
        if ((Get-AudioPilotMsiProperty -Path $casePath -PropertyName Probe) -cne 'expected') { throw 'Property value changed.' }
    } },
    @{ Name = 'empty-query'; Run = {
        if ($null -ne (Get-AudioPilotMsiProperty -Path $casePath -PropertyName Missing -AllowMissing)) { throw 'Missing property was not empty.' }
    } },
    @{ Name = 'missing-table'; Run = {
        if (@(Invoke-AudioPilotMsiQuery -Path $casePath -Query 'SELECT `Value` FROM `MissingTable`' -Columns @('Value')).Count -ne 0) { throw 'Missing table was not empty.' }
    } },
    @{ Name = 'bitness-conflict'; Run = {
        Assert-AudioPilotMsiComponentBitness -Paths @($casePath, $fixturePath)
        $otherPath = Join-Path $testRoot 'other-bitness.msi'
        Copy-Item -LiteralPath $fixturePath -Destination $otherPath
        $editor = New-Object -ComObject WindowsInstaller.Installer
        $database = $null
        $view = $null
        try {
            $database = $editor.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $editor, @([string]$otherPath, [int16]1))
            $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @('UPDATE `Component` SET `Attributes`=0'))
            $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
            $database.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $database, $null) | Out-Null
        }
        finally {
            if ($null -ne $view) { $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null; [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
            if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($editor)
        }
        $rejected = $false
        try { Assert-AudioPilotMsiComponentBitness -Paths @($casePath, $otherPath) }
        catch { if ($_.Exception.Message -notlike '*shared across 32-bit and 64-bit*') { throw }; $rejected = $true }
        if (-not $rejected) { throw 'Conflicting component bitness was accepted.' }
    } },
    @{ Name = 'payload-content'; Run = {
        $payloadPath = Join-Path $testRoot 'payload'
        $cabinetPath = Join-Path $testRoot 'payload.cab'
        [IO.File]::WriteAllBytes($payloadPath, [Text.Encoding]::ASCII.GetBytes('ABCD'))
        $cabinetOutput = & (Join-Path $env:SystemRoot 'System32/makecab.exe') /D CompressionType=MSZIP $payloadPath $cabinetPath 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Could not build fixture cabinet: $cabinetOutput" }
        $editor = New-Object -ComObject WindowsInstaller.Installer
        $database = $null
        $record = $null
        try {
            $database = $editor.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $editor, @([string]$casePath, [int16]1))
            foreach ($sql in @(
                'ALTER TABLE `Component` ADD `Directory_` CHAR(72)',
                "UPDATE ``Component`` SET ``Directory_``='INSTALLFOLDER'",
                'CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL PRIMARY KEY `Directory`)',
                "INSERT INTO ``Directory`` (``Directory``, ``DefaultDir``) VALUES ('INSTALLFOLDER', '.')",
                'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `FileSize` LONG NOT NULL PRIMARY KEY `File`)',
                "INSERT INTO ``File`` (``File``, ``Component_``, ``FileName``, ``FileSize``) VALUES ('payload', 'Probe', 'LICENSE', 4)",
                'CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `Cabinet` CHAR(255) PRIMARY KEY `DiskId`)',
                "INSERT INTO ``Media`` (``DiskId``, ``Cabinet``) VALUES (1, '#payload.cab')"
            )) {
                $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($sql))
                try { $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null }
                finally { $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null; [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
            }
            $record = $editor.GetType().InvokeMember('CreateRecord', 'InvokeMethod', $null, $editor, @([int16]1))
            $record.GetType().InvokeMember('SetStream', 'InvokeMethod', $null, $record, @([int16]1, [string]$cabinetPath)) | Out-Null
            $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("INSERT INTO ``_Streams`` (``Name``, ``Data``) VALUES ('payload.cab', ?)"))
            try { $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, @($record)) | Out-Null }
            finally { $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null; [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
            $database.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $database, $null) | Out-Null
        }
        finally {
            if ($record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
            if ($database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($editor)
        }
        $zipPath = Join-Path $testRoot 'payload.zip'
        foreach ($content in @('ABCD', 'ABCE')) {
            if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
            $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
            try {
                $entry = $zip.CreateEntry('AudioPilot/LICENSE')
                $stream = $entry.Open()
                try { $stream.Write([Text.Encoding]::ASCII.GetBytes($content)) } finally { $stream.Dispose() }
            }
            finally { $zip.Dispose() }
            if ($content -eq 'ABCD') { Assert-AudioPilotMsiPayload -Path $casePath -PublishArchive $zipPath }
            else {
                $rejected = $false
                try { Assert-AudioPilotMsiPayload -Path $casePath -PublishArchive $zipPath }
                catch { if ($_.Exception.Message -notlike '*payload content differs*') { throw }; $rejected = $true }
                if (-not $rejected) { throw 'An equal-size payload content mismatch was accepted.' }
            }
        }
    } },
    @{ Name = 'summary'; Run = {
        if ((Get-AudioPilotMsiSummaryProperty -Path $casePath -PropertyId 7) -cne 'x64;1033') { throw 'Summary value changed.' }
    } }
)
$failures = [Collections.Generic.List[string]]::new()
foreach ($case in $cases) {
    $casePath = [IO.Path]::GetFullPath((Join-Path $testRoot "$($case.Name).msi"))
    Copy-Item -LiteralPath $fixturePath -Destination $casePath
    try {
        & $case.Run
        # The caller must be able to replace the MSI immediately, without forcing a GC.
        $exclusive = [IO.File]::Open($casePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $exclusive.Dispose()
        Write-Host "MSI helper check passed: $($case.Name)"
    }
    catch {
        $failures.Add("$($case.Name): $($_.Exception.Message)`n$($_.ScriptStackTrace)")
    }
}
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Host "MSI helper checks passed ($($cases.Count) cases; no installation performed)."

if (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
    $packagePath = (Resolve-Path -LiteralPath $InstallerPath).Path
    $nativeActions = @(Invoke-AudioPilotMsiQuery -Path $packagePath -Query 'SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`' -Columns @('Action', 'Type', 'Source', 'Target'))
    $executeSteps = @(Invoke-AudioPilotMsiQuery -Path $packagePath -Query 'SELECT `Action`, `Condition`, `Sequence` FROM `InstallExecuteSequence`' -Columns @('Action', 'Condition', 'Sequence'))
    $cleanup = @($nativeActions | Where-Object Action -eq 'CleanupOwnedStartup')
    $cleanupStep = @($executeSteps | Where-Object Action -eq 'CleanupOwnedStartup')
    $finalizeStep = @($executeSteps | Where-Object Action -eq 'InstallFinalize')
    if ($cleanup.Count -ne 1 -or [int]$cleanup[0].Type -ne 65 -or $cleanup[0].Source -cne 'AudioPilotInstallerActions' -or $cleanup[0].Target -cne 'CleanupOwnedStartup' -or
        $cleanupStep.Count -ne 1 -or [int]$cleanupStep[0].Sequence -le [int]$finalizeStep[0].Sequence -or $cleanupStep[0].Condition -cne 'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE') {
        throw 'Owned startup cleanup must be native, nonfatal, and run only after successful non-upgrade uninstall.'
    }
    $validationStep = @($executeSteps | Where-Object Action -eq 'ValidateDataFolder')
    $folderStep = @($executeSteps | Where-Object Action -like 'Wix4RemoveFoldersEx_*')
    $defaultStep = @($executeSteps | Where-Object Action -eq 'SetAUDIOPILOT_DATA_FOLDER')
    if ($validationStep.Count -ne 1 -or $folderStep.Count -ne 1 -or $defaultStep.Count -ne 1 -or
        [int]$validationStep[0].Sequence -ge [int]$folderStep[0].Sequence -or [int]$defaultStep[0].Sequence -ge [int]$validationStep[0].Sequence) {
        throw 'Data folder validation must occur after resolution and before recursive removal is planned.'
    }
    $equalVersion = @(Invoke-AudioPilotMsiQuery -Path $packagePath -Query 'SELECT `VersionMin`, `VersionMax`, `Attributes`, `ActionProperty` FROM `Upgrade`' -Columns @('VersionMin', 'VersionMax', 'Attributes', 'ActionProperty') | Where-Object ActionProperty -eq 'AUDIOPILOT_SAME_VERSION_DETECTED')
    if ($equalVersion.Count -ne 1 -or $equalVersion[0].VersionMin -cne $equalVersion[0].VersionMax -or ([int]$equalVersion[0].Attributes -band 770) -ne 770) {
        throw 'Same-version products must be detected inclusively without automatic removal.'
    }
    Write-Host 'MSI native ownership, cleanup ordering, and same-version detection checks passed.'
    $optionNames = @('INSTALLDESKTOPSHORTCUT', 'INSTALLSTARTMENUSHORTCUT', 'ADD_CLI_TO_PATH', 'AUDIOPILOT_CLEAN_UNINSTALL')
    $actions = @{}
    foreach ($action in @(Invoke-AudioPilotMsiQuery -Path $packagePath -Query 'SELECT `Action`, `Type`, `Source` FROM `CustomAction`' -Columns @('Action', 'Type', 'Source'))) {
        if ($action.Source -in $optionNames) {
            if (([int]$action.Type -band 63) -ne 51 -or ([int]$action.Type -band 256) -eq 0) {
                throw "Option action $($action.Action) must only set a property and run in the first sequence."
            }
            $actions[$action.Action] = $action
        }
    }
    if ($actions.Count -eq 0) { throw 'No installer option actions were found.' }
    $sequence = @(Invoke-AudioPilotMsiQuery -Path $packagePath -Query 'SELECT `Action`, `Condition`, `Sequence` FROM `InstallUISequence`' -Columns @('Action', 'Condition', 'Sequence') |
        Where-Object { $actions.ContainsKey($_.Action) } | Sort-Object { [int]$_.Sequence })
    $optionCases = @(
        @{ Name = 'fresh-defaults'; Properties = @{}; Expected = @('1', '1', '', '') },
        @{ Name = 'saved-disabled'; Properties = @{ PREVIOUS_AUDIOPILOT_DATA_FOLDER = 'C:\FixtureData'; PREVIOUS_INSTALLDESKTOPSHORTCUT = '0'; PREVIOUS_INSTALLSTARTMENUSHORTCUT = '0'; PREVIOUS_ADD_CLI_TO_PATH = '0' }; Expected = @('', '', '', '') },
        @{ Name = 'saved-unchecked'; Properties = @{ PREVIOUS_AUDIOPILOT_DATA_FOLDER = 'C:\FixtureData' }; Expected = @('', '', '', '') },
        @{ Name = 'saved-enabled'; Properties = @{ PREVIOUS_AUDIOPILOT_DATA_FOLDER = 'C:\FixtureData'; PREVIOUS_INSTALLDESKTOPSHORTCUT = '1'; PREVIOUS_INSTALLSTARTMENUSHORTCUT = '1'; PREVIOUS_ADD_CLI_TO_PATH = '1' }; Expected = @('1', '1', '1', '') },
        @{ Name = 'explicit-disable-overrides-saved'; Properties = @{ PREVIOUS_AUDIOPILOT_DATA_FOLDER = 'C:\FixtureData'; PREVIOUS_INSTALLDESKTOPSHORTCUT = '1'; PREVIOUS_INSTALLSTARTMENUSHORTCUT = '1'; PREVIOUS_ADD_CLI_TO_PATH = '1'; INSTALLDESKTOPSHORTCUT = '0'; INSTALLSTARTMENUSHORTCUT = '0'; ADD_CLI_TO_PATH = '0'; AUDIOPILOT_CLEAN_UNINSTALL = '0' }; Expected = @('', '', '', '') },
        @{ Name = 'explicit-enable-overrides-saved'; Properties = @{ PREVIOUS_AUDIOPILOT_DATA_FOLDER = 'C:\FixtureData'; PREVIOUS_INSTALLDESKTOPSHORTCUT = '0'; PREVIOUS_INSTALLSTARTMENUSHORTCUT = '0'; PREVIOUS_ADD_CLI_TO_PATH = '0'; INSTALLDESKTOPSHORTCUT = '1'; INSTALLSTARTMENUSHORTCUT = '1'; ADD_CLI_TO_PATH = '1'; AUDIOPILOT_CLEAN_UNINSTALL = '1' }; Expected = @('1', '1', '1', '1') }
    )
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $previousUi = $installer.GetType().InvokeMember('UILevel', 'GetProperty', $null, $installer, $null)
    try {
        $installer.GetType().InvokeMember('UILevel', 'SetProperty', $null, $installer, @(2)) | Out-Null
        $validationConditions = @(Invoke-AudioPilotMsiQuery -Path $packagePath -Query 'SELECT `Condition`, `Description` FROM `LaunchCondition`' -Columns @('Condition', 'Description') |
            Where-Object Description -like '*must be 0 or 1 when specified.')
        if ($validationConditions.Count -ne $optionNames.Count) { throw 'Installer switch validation is incomplete.' }
        $validationSession = $null
        try {
            $validationSession = $installer.GetType().InvokeMember('OpenPackage', 'InvokeMethod', $null, $installer, @($packagePath, 1))
            foreach ($optionName in $optionNames) {
                $rule = @($validationConditions | Where-Object Description -eq "$optionName must be 0 or 1 when specified.")
                if ($rule.Count -ne 1) { throw "Missing validation for $optionName." }
                foreach ($value in @('', '0', '1', '2', 'true', 'false', 'yes')) {
                    $validationSession.GetType().InvokeMember('Property', 'SetProperty', $null, $validationSession, @($optionName, $value)) | Out-Null
                    $actual = $validationSession.GetType().InvokeMember('EvaluateCondition', 'InvokeMethod', $null, $validationSession, @($rule[0].Condition))
                    $expected = if ($value -in @('', '0', '1')) { 1 } else { 0 }
                    if ($actual -ne $expected) { throw "$optionName incorrectly validated '$value': $actual" }
                }
                $validationSession.GetType().InvokeMember('Property', 'SetProperty', $null, $validationSession, @($optionName, '')) | Out-Null
            }
            Write-Host 'MSI switch validation checks passed.'
            $result = $validationSession.GetType().InvokeMember('DoAction', 'InvokeMethod', $null, $validationSession, @('SetAUDIOPILOT_DATA_FOLDER'))
            $roamingFolder = $validationSession.GetType().InvokeMember('Property', 'GetProperty', $null, $validationSession, @('AppDataFolder'))
            $defaultDataFolder = $validationSession.GetType().InvokeMember('Property', 'GetProperty', $null, $validationSession, @('AUDIOPILOT_DATA_FOLDER'))
            if ($result -ne 1 -or -not $defaultDataFolder.Equals([IO.Path]::Combine($roamingFolder, 'AudioPilot'), [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The default data folder must resolve before costing and native folder validation.'
            }
            $sameVersionRule = @(Invoke-AudioPilotMsiQuery -Path $packagePath -Query 'SELECT `Condition` FROM `LaunchCondition`' -Columns @('Condition') | Where-Object Condition -like '*AUDIOPILOT_SAME_VERSION_DETECTED*')
            if ($sameVersionRule.Count -ne 1) { throw 'Same-version launch protection is missing.' }
            foreach ($case in @(
                @{ Installed = ''; Detected = ''; Expected = 1 },
                @{ Installed = ''; Detected = '{OTHER-PRODUCT}'; Expected = 0 },
                @{ Installed = '1'; Detected = '{OTHER-PRODUCT}'; Expected = 1 }
            )) {
                $validationSession.GetType().InvokeMember('Property', 'SetProperty', $null, $validationSession, @('Installed', $case.Installed)) | Out-Null
                $validationSession.GetType().InvokeMember('Property', 'SetProperty', $null, $validationSession, @('AUDIOPILOT_SAME_VERSION_DETECTED', $case.Detected)) | Out-Null
                $actual = $validationSession.GetType().InvokeMember('EvaluateCondition', 'InvokeMethod', $null, $validationSession, @($sameVersionRule[0].Condition))
                if ($actual -ne $case.Expected) { throw 'Same-version protection must allow fresh install and original-product maintenance, while rejecting a duplicate product.' }
            }
            Write-Host 'MSI same-version fresh-install, duplicate, and maintenance conditions passed.'


        }
        finally {
            if ($null -ne $validationSession) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($validationSession) }
        }
        foreach ($optionCase in $optionCases) {
            $session = $null
            try {
                # Safe sessions ignore installed state and cannot change the computer. Only type-51 property actions run.
                $session = $installer.GetType().InvokeMember('OpenPackage', 'InvokeMethod', $null, $installer, @($packagePath, 1))
                foreach ($entry in $optionCase.Properties.GetEnumerator()) {
                    $session.GetType().InvokeMember('Property', 'SetProperty', $null, $session, @($entry.Key, $entry.Value)) | Out-Null
                }
                foreach ($step in $sequence) {
                    $condition = $session.GetType().InvokeMember('EvaluateCondition', 'InvokeMethod', $null, $session, @($step.Condition))
                    if ($condition -eq 3) { throw "Invalid condition for $($step.Action)." }
                    if ($condition -in @(1, 2)) {
                        $result = $session.GetType().InvokeMember('DoAction', 'InvokeMethod', $null, $session, @($step.Action))
                        if ($result -ne 1) { throw "Property action $($step.Action) failed: $result" }
                    }
                }
                for ($index = 0; $index -lt $optionNames.Count; $index++) {
                    $actual = $session.GetType().InvokeMember('Property', 'GetProperty', $null, $session, @($optionNames[$index]))
                    if ($actual -cne $optionCase.Expected[$index]) { throw "$($optionCase.Name): $($optionNames[$index]) expected '$($optionCase.Expected[$index])', found '$actual'." }
                }
                Write-Host "MSI option check passed: $($optionCase.Name)"
            }
            finally {
                if ($null -ne $session) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($session) }
            }
        }
    }
    finally {
        $installer.GetType().InvokeMember('UILevel', 'SetProperty', $null, $installer, @($previousUi)) | Out-Null
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}
