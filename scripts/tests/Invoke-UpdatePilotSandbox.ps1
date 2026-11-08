[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'This driver installs packages only inside Windows Sandbox.' }
$local = 'C:\AudioPilotPilotTest'
$output = 'C:\PilotResults'
$result = @{schema=1;status='running';phase='copy';publicLiveUpdates=$false;startedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
function Save-Result { $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$output\result.json" -Encoding utf8NoBOM }
function Wait-Until([scriptblock]$Predicate,[int]$Seconds=90) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Predicate)) { if($clock.Elapsed.TotalSeconds -ge $Seconds){throw 'The pilot probe timed out.'}; Start-Sleep -Milliseconds 100 }
}
function Invoke-Child([string]$Name,[string]$Executable,[string[]]$Arguments,[int]$Seconds=900) {
    $installer=[IO.Path]::GetFileName($Executable) -ieq 'msiexec.exe'
    $quoted=@($Arguments | ForEach-Object {
        if($_.Contains('"') -or $_.EndsWith('\')){throw 'Unexpected quoting in a test child argument.'}
        if($installer -and $_ -cmatch '^([A-Z_]+)=(.*)$'){$Matches[1]+'="'+$Matches[2]+'"'}
        elseif($_ -match '\s'){'"'+$_+'"'}else{$_}
    })
    # A restarted app deliberately outlives its helper and can inherit its standard handles. Do not await pipe EOF.
    $child=Start-Process -FilePath $Executable -ArgumentList ($quoted -join ' ') -PassThru -WindowStyle Hidden -RedirectStandardOutput "$output\$Name.log" -RedirectStandardError "$output\$Name-error.log"
    try {
        $null=$child.Handle
        if(-not $child.WaitForExit($Seconds*1000)){throw "$Name is still active. Do not terminate an installer transaction."}
        if($child.ExitCode -ne 0){
            $errorText=(Get-Content -LiteralPath "$output\$Name-error.log" -Tail 40) -join [Environment]::NewLine
            throw "$Name failed ($($child.ExitCode)): $errorText"
        }
    } finally {$child.Dispose()}
}
function Read-AppProcess([string]$Path) {
    Get-CimInstance Win32_Process -Filter "Name = 'AudioPilot.exe'" | Where-Object {$_.ExecutablePath -ieq $Path} | Select-Object -First 1
}
function Find-Control($Root,[string]$Id) {
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Read-AppWindow([int]$ProcessId) {
    [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId))
}
function Open-FromNormalApp([string]$App,[string]$Name) {
    Wait-Until { $null -ne (Read-AppProcess $App) }
    $process=Read-AppProcess $App
    Wait-Until {$null -ne (Read-AppWindow $process.ProcessId)}
    $window=Read-AppWindow $process.ProcessId
    $tabs=$window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TabItem))
    $settings=@($tabs | Where-Object {$_.Current.Name -eq 'Settings'}) | Select-Object -First 1
    if($null -eq $settings){throw "The normal $Name Settings tab is missing."}
    $settings.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-Until {$null -ne (Find-Control $window 'UpdaterPilotButton')}
    (Find-Control $window 'UpdaterPilotButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until {@(Get-CimInstance Win32_Process -Filter "Name = 'AudioPilot.Updater.exe'" | Where-Object {$_.CommandLine -like '*--pilot-ui*'}).Count -eq 1}
    $updater=Get-CimInstance Win32_Process -Filter "Name = 'AudioPilot.Updater.exe'" | Where-Object {$_.CommandLine -like '*--pilot-ui*'} | Select-Object -First 1
    Wait-Until {$null -ne (Read-AppWindow $updater.ProcessId)}
    $ui=Read-AppWindow $updater.ProcessId
    Wait-Until {$null -ne (Find-Control $ui 'VerifyButton')}
    $arguments=[PilotCommandLine]::Split($updater.CommandLine)
    $job=$arguments[3]
    if(Test-Path -LiteralPath (Join-Path $job 'job-download/package.partial')){throw 'Opening the updater downloaded a package without approval.'}
    (Find-Control $ui 'CloseButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Until {$null -eq (Get-Process -Id $updater.ProcessId -ErrorAction SilentlyContinue)}
    if($null -eq (Read-AppProcess $App)){throw 'Closing an idle updater stopped the normal app.'}
    return @{workspace=$job;ui=$updater.ExecutablePath;appPid=$process.ProcessId;argv=$arguments}
}
Save-Result
try {
    if(Test-Path -LiteralPath $local){throw 'Use a fresh Sandbox.'}
    [IO.Directory]::CreateDirectory($local) | Out-Null
    Copy-Item -Path C:\PilotInput\* -Destination $local -Recurse
    $probe=Get-Content -LiteralPath "$local\probe.json" -Raw | ConvertFrom-Json
    if((Get-FileHash -LiteralPath "$local\pilot-publisher.cer" -Algorithm SHA256).Hash -cne $probe.publisherSha256){throw 'The pilot certificate differs from its independent build receipt.'}
    $result.phase='guest-public-certificate-import';Save-Result
    foreach($store in @('Root','TrustedPublisher','My')) {
        $storeArguments=@('-f','-addstore',$store,"$local\pilot-publisher.cer")
        if($store -eq 'My'){$storeArguments=@('-user')+$storeArguments}
        & certutil.exe @storeArguments | Out-Null
        if($LASTEXITCODE -ne 0){throw 'Sandbox publisher import failed.'}
    }
    $certificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes("$local\pilot-publisher.cer"))
    $approvals=@()
    foreach($kind in @('portable','msi')) {
        $result.phase="$kind-signature-verification";Save-Result
        $extension=if($kind -eq 'portable'){'zip'}else{'msi'}
        $manifest="$output\approved-$kind.json"
        $null=& "$local\scripts\approve-update-package.ps1" -Package "$local\candidate.$extension" -Kind $kind -Version $probe.candidateVersion -Architecture win-x64 -Runtime self-contained -CertificateThumbprint $certificate.Thumbprint -SignTool "$local\signtool.exe" -OutputManifest $manifest -PayloadArchive "$local\candidate.zip" -TestIdentity
        $approvals+=@(Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).packages
    }
    @{schema=1;channel='staging';packages=$approvals} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$output\approved-packages.json" -Encoding utf8NoBOM
    $result.signaturesVerified=$true
    $result.phase='normal-app-entry-preflight'; Save-Result
    Expand-Archive -LiteralPath "$local\portable-initial.zip" -DestinationPath "$local\portable"
    Expand-Archive -LiteralPath "$local\baseline.zip" -DestinationPath "$local\baseline-payload"
    foreach($assembly in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll')){Add-Type -LiteralPath (Join-Path "$local\baseline-payload" $assembly)}
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PilotCommandLine {
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string value, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    public static string[] Split(string value) {
        int count; var pointer=CommandLineToArgvW(value,out count); if(pointer==IntPtr.Zero) throw new InvalidOperationException();
        try { var result=new string[count]; for(int i=0;i<count;i++) result[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer,i*IntPtr.Size)); return result; }
        finally { LocalFree(pointer); }
    }
}
'@
    # PowerShell can bind $null as an empty string; diagnostic opt-ins require removal, not an empty value.
    foreach($name in @('AUDIOPILOT_DIAGNOSTIC_SESSION','AUDIOPILOT_UPDATE_LIFECYCLE','AUDIOPILOT_UPDATE_TEST_SESSION')){Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue}
    $installations=@()
    foreach($kind in @('portable','msi')) {
        $result.phase="$kind-normal-entry"; Save-Result
        if($kind -eq 'portable') {
            $layout=Get-Content "$local\portable\AudioPilot\app\deployment.json" -Raw | ConvertFrom-Json
            $app=Join-Path "$local\portable\AudioPilot\app\versions\$($layout.Current)" 'AudioPilot.exe'
            $data="$local\portable\AudioPilot\data"
        } else {
            $data="$local\msi-settings"; $install="$local\installed app with spaces"
            Invoke-Child 'msi-baseline-install' msiexec.exe @('/i',"$local\baseline.msi",'/qn','/norestart','/l*v',"$output\msi-baseline-install-verbose.log","INSTALLFOLDER=$install","AUDIOPILOT_DATA_FOLDER=$data",'INSTALLDESKTOPSHORTCUT=1','INSTALLSTARTMENUSHORTCUT=1','ADD_CLI_TO_PATH=1','REBOOT=ReallySuppress')
            $app=Join-Path $install 'AudioPilot.exe'
        }
        [IO.Directory]::CreateDirectory($data) | Out-Null
        $settings=Get-Content "$local\settings.json" -Raw | ConvertFrom-Json -AsHashtable
        function Clear-Hotkeys($Value) { foreach($key in @($Value.Keys)){if($Value[$key] -is [string]){$Value[$key]=''}elseif($Value[$key] -is [Collections.IDictionary]){Clear-Hotkeys $Value[$key]}} }
        Clear-Hotkeys $settings.Hotkeys
        $settings.Theme='Dark';$settings.RunAtStartup=$false;$settings.Hotkeys.Mute.PushToTalkEnabled=$false
        foreach($direction in @('Output','Input')){$settings.DeviceSwitching[$direction].CycleDevices=@();$settings.DeviceSwitching[$direction].HotkeysEnabled=$false;$settings.DeviceSwitching[$direction].SwitchHotkey='';$settings.DeviceSwitching[$direction].ReverseSwitchHotkey=''}
        $settings.Routines.Items=@();$settings.Miscellaneous.CheckForUpdates=$false;$settings.Miscellaneous.AutoSaveEnabled=$true
        $settings | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8NoBOM
        if($kind -eq 'portable') {
            Invoke-Child 'portable-launcher-help' "$local\portable\AudioPilot\AudioPilot.Cli.exe" @('--help') 60
            Start-Process -FilePath "$local\portable\AudioPilot\AudioPilot.exe" -WindowStyle Hidden | Out-Null
        } else {Start-Process -FilePath $app -WorkingDirectory ([IO.Path]::GetDirectoryName($app)) -WindowStyle Hidden | Out-Null}
        $entry=Open-FromNormalApp $app $kind
        Invoke-Child "$kind-preflight-exit" (Join-Path ([IO.Path]::GetDirectoryName($app)) 'AudioPilot.Cli.exe') @('exit') 90
        Wait-Until {$null -eq (Read-AppProcess $app)}
        $installations+=@{kind=$kind;app=$app}
    }
    $result.phase='awaiting-staging-publication';$result.normalEntriesVerified=$true;Save-Result
    Wait-Until {Test-Path -LiteralPath "$output\feed-ready.json"} 1800
    $probes=@()
    foreach($installation in $installations) {
        $kind=$installation.kind;$app=$installation.app
        Start-Process -FilePath $app -WorkingDirectory ([IO.Path]::GetDirectoryName($app)) -WindowStyle Hidden | Out-Null
        $entry=Open-FromNormalApp $app "$kind-restart"
        $result.phase="$kind-approvals-transfer-recovery"; Save-Result
        Invoke-Child "$kind-pilot" $entry.ui @('--pilot-smoke',$entry.workspace,"$output\$kind")
        $summary=Get-Content "$output\$kind\staging-summary.json" -Raw | ConvertFrom-Json
        if(-not $summary.optInPilot -or -not $summary.singleApproval -or -not $summary.rollbackPassed -or -not $summary.autosavePreserved){throw 'The pilot did not preserve its required safeguards.'}
        $binding=Get-Content (Join-Path $entry.workspace 'installation.json') -Raw | ConvertFrom-Json
        $recovery=Get-Content (Join-Path $entry.workspace 'recovery.json') -Raw | ConvertFrom-Json
        $updated=$recovery.Candidate.Path
        $reopened=Open-FromNormalApp $updated "$kind-reopen"
        $probes+=@{kind=$kind;normalAppEntry=$true;openedAndClosedWithoutDownload=$true;reopenedAfterCommit=$true;finalVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path ([IO.Path]::GetDirectoryName($updated)) 'AudioPilot.dll')).ProductVersion;workspace=$entry.workspace}
        Invoke-Child "$kind-cleanup" "$local\baseline-payload\AudioPilot.Updater.exe" @('feed-cleanup',$entry.workspace)
    }
    $result.probes=$probes;$result.status='passed';$result.phase='complete';$result.publicLiveUpdates=$false
} catch {$result.status='failed';$result.error=$_.ToString()}
finally {$result.completedUtc=[DateTimeOffset]::UtcNow.ToString('O');Save-Result}
if($result.status -ne 'passed'){exit 1}
