[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Publisher,
    [string]$OutputDirectory = 'artifacts/update-authority-proof',
    [string]$PowerShellDirectory = $PSHOME
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory, $repo)
if (-not $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) { throw 'Choose a fresh proof output under the checkout.' }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('AudioPilot-authority-proof-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
[IO.Directory]::CreateDirectory($output) | Out-Null
$tool = (Resolve-Path -LiteralPath $Publisher).Path
$powerShell = (Resolve-Path -LiteralPath $PowerShellDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $powerShell 'pwsh.exe') -PathType Leaf)) { throw 'The Sandbox proof requires the PowerShell 7 runtime.' }
$authority = Join-Path $temporary 'authority'
$script:fakePassphrase = 'Test authority only ' + [guid]::NewGuid().ToString('N')
# Only this isolated test overrides the secure prompt. It never opens the real staging authority.
function Read-Host { param([string]$Prompt, [switch]$AsSecureString) ConvertTo-SecureString $fakePassphrase -AsPlainText -Force }
try {
    $operator = Join-Path $repo 'scripts/publish-update-staging.ps1'
    $created = & $operator -Operation Initialize -AuthorityDirectory $authority -Publisher $tool
    $package = Join-Path $temporary 'fixture.zip'
    [IO.File]::WriteAllText($package, 'Synthetic metadata fixture. Not an installable application.')
    $manifest = Join-Path $temporary 'approval.json'
    @{ schema = 1; channel = 'staging'; packages = @(@{
        path = $package; name = 'AudioPilot-1.0.1-win-x64.zip'; sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        identity = @{ product = 'AudioPilot'; channel = 'staging'; version = '1.0.1'; kind = 'portable'; architecture = 'win-x64'; runtime = 'self-contained'; minimumHelperVersion = '1.0.0'; windowsPublisherSha256 = ('A' * 64); windowsTrust = 'self-signed-staging' }
    }) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM
    $null = & $operator -Operation Publish -AuthorityDirectory $authority -Publisher $tool -PackageManifest $manifest -OutputDirectory (Join-Path $output 'public')
    $backup = Join-Path $output 'temporary-authority.age'
    $null = & $operator -Operation Backup -AuthorityDirectory $authority -Publisher $tool -BackupPath $backup
    $restored = Join-Path $temporary 'restored'
    $null = & $operator -Operation Restore -AuthorityDirectory $restored -Publisher $tool -BackupPath $backup -PublicRepository (Join-Path $output 'public') -BootstrapHash $created.bootstrapHash -MinimumVersion 1
    $renewed = & $operator -Operation Renew -AuthorityDirectory $restored -Publisher $tool
    if ($renewed.version -ne 2) { throw 'Restored signing keys did not renew the feed.' }
    $externalRenewalCheckpoint = Join-Path $output 'external-renewal-checkpoint'
    $null = & $operator -Operation Export -AuthorityDirectory $restored -Publisher $tool -OutputDirectory $externalRenewalCheckpoint
    $rotated = & $operator -Operation Rotate -AuthorityDirectory $restored -Publisher $tool
    if ($rotated.rootVersion -ne 2 -or $rotated.rootThreshold -ne 2 -or $rotated.version -ne 3) { throw 'Rotation did not select its verified authority generation.' }
    $null = & $operator -Operation Renew -AuthorityDirectory $restored -Publisher $tool
    $renewalSource = Join-Path $output 'renewal-public'
    $null = & $operator -Operation Export -AuthorityDirectory $restored -Publisher $tool -OutputDirectory $renewalSource
    $metadataReads = [Collections.Generic.List[string]]::new()
    $assetPaths = @{}
    $releaseFixture = @{ draft = $false; prerelease = $true; assets = @() }
    foreach ($file in Get-ChildItem -LiteralPath $renewalSource -File -Recurse) {
        $assetPaths[$file.Name] = $file.FullName
        $releaseFixture.assets += @{ name = $file.Name; size = $file.Length; digest = ('sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) }
    }
    # Exercise the real maintenance script against a local fake GitHub boundary; no network or real authority keys.
    function gh {
        $arguments = @($args)
        $global:LASTEXITCODE = 0
        if ($arguments[0] -eq 'api') { $releaseFixture | ConvertTo-Json -Depth 8; return }
        if ($arguments[0] -ne 'release' -or $arguments[1] -ne 'download') { throw 'Unexpected network command in the authority fixture.' }
        $name = $arguments[[Array]::IndexOf($arguments, '--pattern') + 1]
        $destination = $arguments[[Array]::IndexOf($arguments, '--output') + 1]
        $metadataReads.Add($name)
        Copy-Item -LiteralPath $assetPaths[$name] -Destination $destination
    }
    $null = & (Join-Path $repo 'scripts/renew-update-staging.ps1') -Publisher $tool -BootstrapHash $created.bootstrapHash -MinimumRootVersion 2 -OutputDirectory (Join-Path $output 'health-proof') -CheckOnly
    if ($metadataReads.Count -ne 6 -or $metadataReads.Contains('1.snapshot.json') -or $metadataReads.Contains('2.targets.json')) { throw 'On-demand health unnecessarily downloaded historical metadata.' }
    if ($env:UPDATE_STAGING_RENEWAL_KEYS) { throw 'Run the disposable renewal fixture without a real renewal-key environment variable.' }
    $state = Get-Content -LiteralPath (Join-Path $restored 'authority.json') -Raw | ConvertFrom-Json
    $onlineKeys = @{}
    foreach ($role in @('snapshot', 'timestamp')) {
        $blob = [IO.File]::ReadAllBytes((Join-Path $restored "keys/$($state.generation)/$role.dpapi"))
        $plain = [Security.Cryptography.ProtectedData]::Unprotect($blob, [Text.Encoding]::UTF8.GetBytes("AudioPilot staging $role"), [Security.Cryptography.DataProtectionScope]::CurrentUser)
        try { $onlineKeys[$role] = [Convert]::ToBase64String($plain) }
        finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($plain) }
    }
    try {
        $env:UPDATE_STAGING_RENEWAL_KEYS = $onlineKeys | ConvertTo-Json -Compress
        $null = & (Join-Path $repo 'scripts/renew-update-staging.ps1') -Publisher $tool -BootstrapHash $created.bootstrapHash -MinimumRootVersion 2 -OutputDirectory (Join-Path $output 'freshness-proof') -RenewBeforeHours 18
        $fresh = Get-Content -LiteralPath (Join-Path $output 'freshness-proof/health.json') -Raw | ConvertFrom-Json
        if ($fresh.action -ne 'unchanged' -or $fresh.metadataVersion -ne 4) { throw 'Fresh metadata was unnecessarily renewed.' }
        if ((Get-FileHash -LiteralPath (Join-Path $renewalSource 'metadata/timestamp.json')).Hash -cne (Get-FileHash -LiteralPath (Join-Path $output 'freshness-proof/metadata/timestamp.json')).Hash) { throw 'A freshness check changed the timestamp.' }
        $env:UPDATE_STAGING_RENEWAL_KEYS = $onlineKeys | ConvertTo-Json -Compress
        $null = & (Join-Path $repo 'scripts/renew-update-staging.ps1') -Publisher $tool -BootstrapHash $created.bootstrapHash -MinimumRootVersion 2 -OutputDirectory (Join-Path $output 'renewal-proof')
    }
    finally { Remove-Item Env:UPDATE_STAGING_RENEWAL_KEYS -ErrorAction SilentlyContinue; $onlineKeys.Clear() }
    $health = Get-Content -LiteralPath (Join-Path $output 'renewal-proof/health.json') -Raw | ConvertFrom-Json
    if ($health.metadataVersion -ne 5 -or $health.rootVersion -ne 2) { throw 'The protected online-key fixture did not renew metadata.' }
    if ((Get-FileHash -LiteralPath (Join-Path $renewalSource 'metadata/3.targets.json')).Hash -cne (Get-FileHash -LiteralPath (Join-Path $output 'renewal-proof/metadata/3.targets.json')).Hash) { throw 'Online maintenance changed targets approval.' }
    $quorum = & $operator -Operation Rotate -AuthorityDirectory $restored -Publisher $tool -RootSignerRoles root-1,root-2
    if ($quorum.rootVersion -ne 3 -or $quorum.rootThreshold -ne 2) { throw 'A surviving two-key quorum could not rotate.' }
    $nextManifest = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
    $nextManifest.packages[0].name = 'AudioPilot-1.0.2-win-x64.zip'
    $nextManifest.packages[0].identity.version = '1.0.2'
    $nextManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM
    $fromExternal = & $operator -Operation Publish -AuthorityDirectory $authority -Publisher $tool -PackageManifest $manifest -PublicRepository $externalRenewalCheckpoint
    if ($fromExternal.version -ne 3) { throw 'Publication did not advance from the externally renewed checkpoint.' }
    $checkpointBefore = (Get-FileHash -LiteralPath (Join-Path $authority 'authority.json')).Hash
    $rollbackRejected = $false
    try { $null = & $operator -Operation Publish -AuthorityDirectory $authority -Publisher $tool -PackageManifest $manifest -PublicRepository (Join-Path $output 'public') }
    catch { $rollbackRejected = $true }
    if (-not $rollbackRejected -or (Get-FileHash -LiteralPath (Join-Path $authority 'authority.json')).Hash -cne $checkpointBefore) { throw 'A regressed public checkpoint changed the selected authority.' }
    $afterAdoption = & $operator -Operation Rotate -AuthorityDirectory $authority -Publisher $tool
    $afterAdoptionRenewal = & $operator -Operation Renew -AuthorityDirectory $authority -Publisher $tool
    if ($afterAdoption.rootVersion -ne 2 -or $afterAdoptionRenewal.version -ne 5) { throw 'Rotation after repository adoption selected the wrong repository.' }
    $inputDirectory = Join-Path $output 'sandbox-input'
    [IO.Directory]::CreateDirectory((Join-Path $inputDirectory 'scripts')) | Out-Null
    Copy-Item -LiteralPath $operator -Destination (Join-Path $inputDirectory 'scripts/publish-update-staging.ps1')
    Copy-Item -LiteralPath $tool -Destination (Join-Path $inputDirectory 'publisher.exe')
    Copy-Item -LiteralPath $backup -Destination $inputDirectory
    Copy-Item -LiteralPath (Join-Path $authority 'root.dpapi') -Destination (Join-Path $inputDirectory 'unusable-original.dpapi')
    Copy-Item -LiteralPath (Join-Path $output 'public') -Destination $inputDirectory -Recurse
    @{ bootstrapHash = $created.bootstrapHash; testPassphrase = $script:fakePassphrase } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputDirectory 'test-only.json') -Encoding utf8NoBOM
    @'
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$fixture = Get-Content C:\AuthorityInput\test-only.json -Raw | ConvertFrom-Json
$script:fakePassphrase = $fixture.testPassphrase
function Read-Host { param([string]$Prompt, [switch]$AsSecureString) ConvertTo-SecureString $fakePassphrase -AsPlainText -Force }
try {
    $dpapiRejected = $false
    try { $null = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes('C:\AuthorityInput\unusable-original.dpapi'), [Text.Encoding]::UTF8.GetBytes('AudioPilot staging root'), [Security.Cryptography.DataProtectionScope]::CurrentUser) }
    catch [Security.Cryptography.CryptographicException] { $dpapiRejected = $true }
    if (-not $dpapiRejected) { throw 'Original machine DPAPI material unexpectedly worked.' }
    $operator = 'C:\AuthorityInput\scripts\publish-update-staging.ps1'
    $tool = 'C:\AuthorityInput\publisher.exe'
    $null = & $operator -Operation Restore -AuthorityDirectory C:\RecoveredAuthority -Publisher $tool -BackupPath C:\AuthorityInput\temporary-authority.age -PublicRepository C:\AuthorityInput\public -BootstrapHash $fixture.bootstrapHash -MinimumVersion 1
    $renewed = & $operator -Operation Renew -AuthorityDirectory C:\RecoveredAuthority -Publisher $tool
    $rotated = & $operator -Operation Rotate -AuthorityDirectory C:\RecoveredAuthority -Publisher $tool
    $next = & $operator -Operation Renew -AuthorityDirectory C:\RecoveredAuthority -Publisher $tool
    if ($renewed.version -ne 2 -or $rotated.rootVersion -ne 2 -or $rotated.rootThreshold -ne 2 -or $next.version -ne 4) { throw 'Cross-profile recovery/rotation failed.' }
    @{ passed = $true; originalDpapiRejected = $dpapiRejected; ageBackupRestored = $true; metadataVersion = $next.version; rootVersion = $next.rootVersion; rootThreshold = $next.rootThreshold; publicLiveUpdates = $false } | ConvertTo-Json | Set-Content C:\AuthorityResults\result.json -Encoding utf8
}
catch { @{ passed = $false; error = $_.Exception.Message } | ConvertTo-Json | Set-Content C:\AuthorityResults\result.json -Encoding utf8; exit 1 }
'@ | Set-Content -LiteralPath (Join-Path $inputDirectory 'run.ps1') -Encoding utf8NoBOM
    $results = Join-Path $output 'sandbox-results'
    [IO.Directory]::CreateDirectory($results) | Out-Null
    $inputXml = [Security.SecurityElement]::Escape($inputDirectory)
    $resultsXml = [Security.SecurityElement]::Escape($results)
    $powerShellXml = [Security.SecurityElement]::Escape($powerShell)
    $commandXml = [Security.SecurityElement]::Escape('cmd.exe /c C:\AuthorityPowerShell\pwsh.exe -NoProfile -ExecutionPolicy Bypass -File C:\AuthorityInput\run.ps1 > C:\AuthorityResults\run.log 2>&1')
    @"
<Configuration><Networking>Disable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB><MappedFolders><MappedFolder><HostFolder>$inputXml</HostFolder><SandboxFolder>C:\AuthorityInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder><MappedFolder><HostFolder>$powerShellXml</HostFolder><SandboxFolder>C:\AuthorityPowerShell</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder><MappedFolder><HostFolder>$resultsXml</HostFolder><SandboxFolder>C:\AuthorityResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder></MappedFolders><LogonCommand><Command>$commandXml</Command></LogonCommand></Configuration>
"@ | Set-Content -LiteralPath (Join-Path $output 'authority.wsb') -Encoding utf8NoBOM
    @{ passed = $true; temporaryAuthorityOnly = $true; restoredRenewalVersion = $renewed.version; rotatedRootVersion = $rotated.rootVersion; survivingQuorumRootVersion = $quorum.rootVersion; onDemandMetadataReads = 6; onlineRenewalVersion = $health.metadataVersion; sandboxConfiguration = (Join-Path $output 'authority.wsb') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'local-result.json') -Encoding utf8NoBOM
}
finally {
    $script:fakePassphrase = $null
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe temporary authority cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
