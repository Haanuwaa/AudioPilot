param([string]$InstallerPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../lib/Msi.ps1')

$testRoot = Join-Path $PSScriptRoot "../../artifacts/testresults/msi-helpers/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$fixturePath = [IO.Path]::GetFullPath((Join-Path $testRoot 'fixture.msi'))
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
$summary = $null
try {
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($fixturePath, 3))
    foreach ($sql in @(
        'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)',
        "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('Probe', 'expected')"
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
        $failures.Add("$($case.Name): $($_.Exception.Message)")
    }
}
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Host "MSI helper checks passed ($($cases.Count) cases; no installation performed)."

if (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
    $packagePath = (Resolve-Path -LiteralPath $InstallerPath).Path
    $taskCleanup = @(Invoke-AudioPilotMsiQuery -Path $packagePath -Query 'SELECT `Action`, `Type`, `Target` FROM `CustomAction`' -Columns @('Action', 'Type', 'Target') | Where-Object Action -eq 'CleanupStartupScheduledTask')
    $executeSteps = @(Invoke-AudioPilotMsiQuery -Path $packagePath -Query 'SELECT `Action`, `Condition`, `Sequence` FROM `InstallExecuteSequence`' -Columns @('Action', 'Condition', 'Sequence'))
    $taskCleanupStep = @($executeSteps | Where-Object Action -eq 'CleanupStartupScheduledTask')
    $finalizeStep = @($executeSteps | Where-Object Action -eq 'InstallFinalize')
    if ($taskCleanup.Count -ne 1 -or [int]$taskCleanup[0].Type -ne 65 -or $taskCleanup[0].Target -cne 'WixQuietExec') {
        throw 'Startup task cleanup must run quietly as an immediate, nonfatal custom action.'
    }
    if ($taskCleanupStep.Count -ne 1 -or $finalizeStep.Count -ne 1 -or [int]$taskCleanupStep[0].Sequence -le [int]$finalizeStep[0].Sequence -or
        $taskCleanupStep[0].Condition -cne 'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE') {
        throw 'Startup task cleanup must occur only after successful uninstall and must exclude upgrades.'
    }
    $taskCommand = @(Invoke-AudioPilotMsiQuery -Path $packagePath -Query 'SELECT `Source`, `Target` FROM `CustomAction`' -Columns @('Source', 'Target') | Where-Object Source -eq 'WixQuietExecCmdLine')
    if ($taskCommand.Count -ne 1 -or $taskCommand[0].Target -cne '"[System64Folder]schtasks.exe" /Delete /TN "\AudioPilot Startup [UserSID]" /F') {
        throw 'Startup task cleanup must target only the installing user AudioPilot task.'
    }
    Write-Host 'MSI startup task cleanup checks passed.'
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
