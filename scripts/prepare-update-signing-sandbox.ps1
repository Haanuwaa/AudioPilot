[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$HelperDirectory,
    [Parameter(Mandatory)][string]$SignTool,
    [Parameter(Mandatory)][string]$OutputRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) { throw 'Choose a fresh signing proof folder.' }
for ($current = $root; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected proof output.' }
}
$inputRoot = Join-Path $root 'input'
$results = Join-Path $root 'results'
$deployment = Join-Path $root 'deployment'
New-Item -ItemType Directory -Path $inputRoot, $results, (Join-Path $deployment 'input'), (Join-Path $deployment 'results') -Force | Out-Null
$payloadNames = @('AudioPilot.exe', 'AudioPilot.dll', 'AudioPilot.Cli.exe', 'AudioPilot.Cli.dll', 'AudioPilot.UpdateProtocol.dll')
foreach ($item in @(
    @{ Root=$BaselineDirectory; Name='baseline'; Files=$payloadNames },
    @{ Root=$CandidateDirectory; Name='candidate'; Files=$payloadNames },
    @{ Root=$HelperDirectory; Name='helper'; Files=@('AudioPilot.Updater.exe', 'AudioPilot.Updater.dll', 'AudioPilot.UpdateProtocol.dll', 'AudioPilot.TufBridge.exe') }
)) {
    $directory = Join-Path $inputRoot $item.Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    foreach ($name in $item.Files) {
        $source = (Resolve-Path -LiteralPath (Join-Path $item.Root $name)).Path
        for ($current = $source; $current; $current = [IO.Path]::GetDirectoryName($current)) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected signing source.' }
        }
        Copy-Item -LiteralPath $source -Destination $directory
    }
}
$toolDirectory = [IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath $SignTool).Path)
$tools = Join-Path $inputRoot 'tools'
New-Item -ItemType Directory -Path $tools | Out-Null
Copy-Item -LiteralPath (Join-Path $toolDirectory 'signtool.exe') -Destination $tools
$manifests = [Collections.Generic.Queue[string]]::new()
$manifests.Enqueue('signtool.exe.manifest')
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
while ($manifests.Count) {
    $name = $manifests.Dequeue()
    if (-not $seen.Add($name)) { continue }
    if ([IO.Path]::GetFileName($name) -ne $name) { throw 'Unsafe SDK manifest reference.' }
    $source = Join-Path $toolDirectory $name
    Copy-Item -LiteralPath $source -Destination $tools
    [xml]$manifest = Get-Content -LiteralPath $source -Raw
    foreach ($file in $manifest.SelectNodes('//*[local-name()="file"]')) {
        $reference = $file.GetAttribute('name')
        if ([IO.Path]::GetFileName($reference) -ne $reference) { throw 'Unsafe SDK file reference.' }
        Copy-Item -LiteralPath (Join-Path $toolDirectory $reference) -Destination $tools
    }
    foreach ($dependency in $manifest.SelectNodes('//*[local-name()="dependentAssembly"]/*[local-name()="assemblyIdentity"]')) {
        $manifests.Enqueue($dependency.GetAttribute('name') + '.manifest')
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sign-update-artifacts.ps1') -Destination $inputRoot
$runner = @'
$ErrorActionPreference='Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Sandbox only.' }
$result=@{ status='running'; phase='signing'; testOnly=$true }
$result | ConvertTo-Json | Set-Content C:\SigningResults\signing.json
try {
    New-Item -ItemType Directory C:\AudioPilotSigning | Out-Null
    Copy-Item C:\SigningInput\* C:\AudioPilotSigning -Recurse
    $certificate=New-SelfSignedCertificate -Type CodeSigningCert -Subject ('CN=AudioPilot temporary ' + [guid]::NewGuid().ToString('N')) -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -HashAlgorithm SHA256 -KeyLength 3072 -NotAfter (Get-Date).AddDays(2)
    $certificate.Thumbprint | Set-Content C:\AudioPilotSigning\thumbprint.txt
    $public=Export-Certificate -Cert $certificate -FilePath C:\AudioPilotSigning\test-public.cer
    & certutil.exe -f -addstore Root $public.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox test-root import failed.' }
    & certutil.exe -user -f -addstore TrustedPublisher $public.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox test-publisher import failed.' }
    $sign=@{ CertificateThumbprint=$certificate.Thumbprint; SignTool='C:\AudioPilotSigning\tools\signtool.exe'; TestIdentity=$true }
    $reports=@()
    foreach ($directory in @('baseline','candidate','helper')) {
        $files=@(Get-ChildItem -LiteralPath "C:\AudioPilotSigning\$directory" -File | ForEach-Object FullName)
        $reports += & C:\AudioPilotSigning\sign-update-artifacts.ps1 -Files $files @sign
        Copy-Item -LiteralPath "C:\AudioPilotSigning\$directory" -Destination C:\SigningResults -Recurse
    }
    function Expect-Rejection([string]$Name,[scriptblock]$Action) {
        $rejected=$false
        try { & $Action | Out-Null } catch { $rejected=$true }
        if (-not $rejected) { throw "$Name was not rejected." }
    }
    Copy-Item C:\SigningInput\helper\AudioPilot.TufBridge.exe C:\AudioPilotSigning\unsigned.exe
    Expect-Rejection 'unsigned' { & C:\AudioPilotSigning\sign-update-artifacts.ps1 -Files C:\AudioPilotSigning\unsigned.exe -VerifyOnly @sign }
    Copy-Item C:\AudioPilotSigning\helper\AudioPilot.TufBridge.exe C:\AudioPilotSigning\tampered.exe
    $stream=[IO.File]::Open('C:\AudioPilotSigning\tampered.exe',[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite)
    try { $stream.Position=512; $value=$stream.ReadByte(); $stream.Position=512; $stream.WriteByte($value -bxor 1) } finally { $stream.Dispose() }
    Expect-Rejection 'tamper' { & C:\AudioPilotSigning\sign-update-artifacts.ps1 -Files C:\AudioPilotSigning\tampered.exe -VerifyOnly @sign }
    $other=New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=AudioPilot temporary wrong publisher' -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(1)
    try {
        $wrong=@{ CertificateThumbprint=$other.Thumbprint; SignTool=$sign.SignTool; TestIdentity=$true }
        Expect-Rejection 'wrong publisher' { & C:\AudioPilotSigning\sign-update-artifacts.ps1 -Files C:\AudioPilotSigning\helper\AudioPilot.TufBridge.exe -VerifyOnly @wrong }
    } finally { Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($other.Thumbprint)" -DeleteKey }
    Expect-Rejection 'production self-signed identity' { & C:\AudioPilotSigning\sign-update-artifacts.ps1 -Files C:\AudioPilotSigning\helper\AudioPilot.TufBridge.exe -CertificateThumbprint $certificate.Thumbprint -SignTool $sign.SignTool -TimestampUrl https://timestamp.invalid/ -VerifyOnly }
    Expect-Rejection 'missing production timestamp' { & C:\AudioPilotSigning\sign-update-artifacts.ps1 -Files C:\AudioPilotSigning\helper\AudioPilot.TufBridge.exe -CertificateThumbprint $certificate.Thumbprint -SignTool $sign.SignTool -VerifyOnly }
    $reports | ConvertTo-Json -Depth 4 | Set-Content C:\SigningResults\signatures.json
    $result.status='passed'; $result.thumbprint=$certificate.Thumbprint; $result.rejections=@('unsigned','tamper','wrong publisher','self-signed production','missing production timestamp')
} catch { $result.status='failed'; $result.error=$_.ToString(); $result.stack=$_.ScriptStackTrace }
$result | ConvertTo-Json -Depth 4 | Set-Content C:\SigningResults\signing.json
if ($result.status -ne 'passed') { exit 1 }
'@
Set-Content -LiteralPath (Join-Path $inputRoot 'sign.ps1') -Value $runner -Encoding utf8
[xml]$configuration = '<Configuration><VGpu>Disable</VGpu><Networking>Disable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB><MappedFolders/><LogonCommand><Command/></LogonCommand></Configuration>'
foreach ($mapping in @(
    @{HostFolder=$inputRoot; SandboxFolder='C:\SigningInput'; ReadOnly='true'},
    @{HostFolder=$results; SandboxFolder='C:\SigningResults'; ReadOnly='false'},
    @{HostFolder=(Join-Path $deployment 'input'); SandboxFolder='C:\DeploymentInput'; ReadOnly='true'},
    @{HostFolder=(Join-Path $deployment 'results'); SandboxFolder='C:\DeploymentResults'; ReadOnly='false'}
)) {
    $folder=$configuration.CreateElement('MappedFolder')
    foreach ($name in @('HostFolder','SandboxFolder','ReadOnly')) { $element=$configuration.CreateElement($name); $element.InnerText=$mapping[$name]; [void]$folder.AppendChild($element) }
    [void]$configuration.SelectSingleNode('/Configuration/MappedFolders').AppendChild($folder)
}
$configuration.SelectSingleNode('/Configuration/LogonCommand/Command').InnerText='powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\SigningInput\sign.ps1'
$configuration.Save((Join-Path $root 'Signing.wsb'))
Write-Host "Prepared $root\Signing.wsb. Stage one signs first-party files; stage two must package those exact bytes before signing the MSI and generating TUF metadata."
