Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'This integration proof runs only in Windows Sandbox.' }
$result = @{ status = 'running'; phase = 'signing'; publicLiveUpdates = $false; testOnly = $true }
function Save-Result { $result | ConvertTo-Json -Depth 8 | Set-Content C:\SigningResults\staging.json -Encoding utf8NoBOM }
Save-Result
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\SigningInput\sign.ps1 > C:\SigningResults\signing.log 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Initial Authenticode proof failed.' }
    $signed = Get-Content C:\SigningResults\signing.json -Raw | ConvertFrom-Json
    $sign = @{ CertificateThumbprint = $signed.thumbprint; SignTool = 'C:\AudioPilotSigning\tools\signtool.exe'; TestIdentity = $true }
    [IO.Directory]::CreateDirectory('C:\StagingWork\payload') | Out-Null
    Copy-Item C:\CandidatePayload\* C:\StagingWork\payload -Recurse
    & C:\SigningInput\scripts\sign-update-payload.ps1 -PayloadDirectory C:\StagingWork\payload -ReceiptPath C:\SigningResults\payload-signing.json @sign > C:\SigningResults\payload-signing.log
    [IO.Directory]::CreateDirectory('C:\SigningResults\signed-payload') | Out-Null
    foreach ($entry in (Get-Content C:\SigningResults\payload-signing.json -Raw | ConvertFrom-Json).files) {
        Copy-Item -LiteralPath (Join-Path C:\StagingWork\payload $entry.name) -Destination C:\SigningResults\signed-payload
    }
    $result.phase = 'awaiting-msi'; Save-Result
    [IO.Compression.ZipFile]::CreateFromDirectory('C:\StagingWork\payload', 'C:\StagingWork\AudioPilot-1.0.1-win-x64.zip', [IO.Compression.CompressionLevel]::SmallestSize, $false)
    $deadline = [DateTime]::UtcNow.AddMinutes(15)
    while (-not (Test-Path C:\DeploymentInput\candidate.ready)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Timed out waiting for the MSI built from the signed payload.' }
        Start-Sleep -Milliseconds 500
    }
    Copy-Item C:\DeploymentInput\candidate.msi C:\StagingWork\AudioPilot-1.0.1-win-x64.msi
    $null = & C:\SigningInput\scripts\sign-update-artifacts.ps1 -Files C:\StagingWork\AudioPilot-1.0.1-win-x64.msi @sign
    $result.phase = 'package-approval'; Save-Result
    foreach ($kind in @('portable', 'msi')) {
        $extension = if ($kind -eq 'portable') { 'zip' } else { 'msi' }
        $approval = if ($kind -eq 'msi') { @{ PayloadArchive = 'C:\StagingWork\AudioPilot-1.0.1-win-x64.zip' } } else { @{} }
        & C:\SigningInput\scripts\approve-update-package.ps1 -Package "C:\StagingWork\AudioPilot-1.0.1-win-x64.$extension" -Kind $kind -Version 1.0.1 -Architecture win-x64 -Runtime self-contained -OutputManifest "C:\StagingWork\$kind.json" @sign @approval > "C:\SigningResults\approval-$kind.log"
    }
    $result.phase = 'signed-msi-install-check'; Save-Result
    $install = Start-Process -FilePath msiexec.exe -ArgumentList '/i C:\StagingWork\AudioPilot-1.0.1-win-x64.msi /qn /norestart REBOOT=ReallySuppress INSTALLFOLDER=C:\StagingInstalled AUDIOPILOT_DATA_FOLDER=C:\StagingData INSTALLDESKTOPSHORTCUT=0 INSTALLSTARTMENUSHORTCUT=0 ADD_CLI_TO_PATH=0 /l*vx! C:\SigningResults\signed-install.log' -WindowStyle Hidden -PassThru
    $null = $install.Handle
    try {
        if (-not $install.WaitForExit(180000)) { throw 'Signed MSI installation exceeded three minutes; inspect the retained process and log before retrying.' }
        if ($install.ExitCode -ne 0) { throw "Signed MSI installation returned $($install.ExitCode). Reboots are not accepted." }
    }
    finally { $install.Dispose() }
    foreach ($entry in (Get-Content C:\SigningResults\payload-signing.json -Raw | ConvertFrom-Json).files) {
        $installedFile = Join-Path C:\StagingInstalled $entry.name
        if ((Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash -cne $entry.sha256 -or (Get-AuthenticodeSignature -LiteralPath $installedFile).Status -ne 'Valid') { throw 'Installed MSI payload differs from approved signed bytes.' }
    }
    $options = Get-ItemProperty HKCU:\Software\Haanuwaa\AudioPilot
    if ($options.InstallFolder.TrimEnd('\') -cne 'C:\StagingInstalled' -or $options.DataFolder.TrimEnd('\') -cne 'C:\StagingData') { throw 'Signed MSI changed selected installation options.' }
    $uninstall = Start-Process -FilePath msiexec.exe -ArgumentList '/x C:\StagingWork\AudioPilot-1.0.1-win-x64.msi /qn /norestart REBOOT=ReallySuppress /l*vx! C:\SigningResults\signed-uninstall.log' -WindowStyle Hidden -PassThru
    $null = $uninstall.Handle
    try {
        if (-not $uninstall.WaitForExit(180000)) { throw 'Signed MSI uninstall exceeded three minutes; inspect the retained process and log before retrying.' }
        if ($uninstall.ExitCode -ne 0) { throw "Signed MSI uninstall returned $($uninstall.ExitCode)." }
    }
    finally { $uninstall.Dispose() }
    $result.signedMsiPayloadVerified = $true
    $packages = @((Get-Content C:\StagingWork\portable.json -Raw | ConvertFrom-Json -AsHashtable).packages) + @((Get-Content C:\StagingWork\msi.json -Raw | ConvertFrom-Json -AsHashtable).packages)
    @{ schema = 1; channel = 'staging'; packages = $packages } | ConvertTo-Json -Depth 10 | Set-Content C:\StagingWork\packages.json -Encoding utf8NoBOM
    $publish = @{ AuthorityDirectory = 'C:\StagingAuthority'; Publisher = 'C:\SigningInput\publisher.exe' }
    $authority = & C:\SigningInput\scripts\publish-update-staging.ps1 -Operation Initialize @publish
    $first = & C:\SigningInput\scripts\publish-update-staging.ps1 -Operation Publish -PackageManifest C:\StagingWork\packages.json -OutputDirectory C:\StagingWork\feed-one @publish
    if ($first.version -ne 1 -or $first.publicLiveUpdates -ne $false) { throw 'Unexpected first staging publication.' }
    $rootLock = [IO.File]::Open('C:\StagingAuthority\root.dpapi', [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { $renewed = & C:\SigningInput\scripts\publish-update-staging.ps1 -Operation Renew -OutputDirectory C:\StagingWork\feed-two @publish }
    finally { $rootLock.Dispose() }
    if ($renewed.version -ne 2) { throw 'Renewal did not advance without the offline root key.' }
    $command = "& C:\SigningInput\scripts\publish-update-staging.ps1 -Operation Renew -AuthorityDirectory C:\StagingAuthority -Publisher C:\SigningInput\publisher.exe -OutputDirectory C:\StagingWork\feed-three | ConvertTo-Json | Set-Content C:\StagingWork\third.json"
    & C:\PowerShell\pwsh.exe -NoProfile -Command $command
    if ($LASTEXITCODE -ne 0 -or (Get-Content C:\StagingWork\third.json -Raw | ConvertFrom-Json).version -ne 3) { throw 'Authority did not survive a separate publisher process.' }
    Copy-Item C:\StagingWork\feed-three C:\SigningResults\public-feed -Recurse
    Copy-Item C:\StagingWork\AudioPilot-1.0.1-win-x64.msi, C:\StagingWork\AudioPilot-1.0.1-win-x64.zip -Destination C:\SigningResults
    $result.status = 'passed'; $result.phase = 'complete'; $result.bootstrapHash = $authority.bootstrapHash
    $result.metadataVersions = @(1, 2, 3); $result.deploymentKinds = @('portable', 'msi'); $result.rootKeyRequiredForRenewal = $false
    $result.authoritySecretsExported = $false; $result.hostTrustChanged = $false
}
catch { $result.status = 'failed'; $result.error = $_.ToString(); $result.stack = $_.ScriptStackTrace }
Save-Result
if ($result.status -ne 'passed') { exit 1 }
