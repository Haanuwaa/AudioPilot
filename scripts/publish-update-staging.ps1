[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Initialize', 'Publish', 'Renew', 'RenewCatalog', 'Export', 'Backup', 'Restore', 'Rotate', 'Status')][string]$Operation,
    [Parameter(Mandatory)][string]$AuthorityDirectory,
    [Parameter(Mandatory)][string]$Publisher,
    [string]$PackageManifest,
    [string]$OutputDirectory,
    [string]$BackupPath,
    [string]$PublicRepository,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$BootstrapHash,
    [ValidateRange(1, 1000000)][long]$MinimumVersion = 1,
    [ValidateRange(1, 32)][long]$MinimumRootVersion = 1,
    [ValidateSet('root', 'root-1', 'root-2', 'root-3')][string[]]$RootSignerRoles
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($RootSignerRoles -and $Operation -ne 'Rotate') { throw 'RootSignerRoles applies only to the explicit rotation ceremony.' }
if (-not $IsWindows) { throw 'The local signing authority uses Windows CurrentUser DPAPI.' }
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData

function Assert-DirectPath([string]$Path) {
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected authority or export path.' }
    }
}
function Invoke-Publisher([hashtable]$Request) {
    $start = [Diagnostics.ProcessStartInfo]::new($script:tool)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write(($Request | ConvertTo-Json -Depth 12 -Compress))
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(600000)) { $process.Kill($true); $process.WaitForExit(); throw 'Staging publisher timed out. Inspect the retained lock and repository before retrying.' }
        $result = $output.GetAwaiter().GetResult()
        $failure = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Staging publisher failed: $failure" }
        if ($result.Length -gt 65536) { throw 'Oversized staging publisher result.' }
        return ($result | ConvertFrom-Json -AsHashtable)
    }
    finally { $process.Dispose() }
}
function Protect-Role([string]$Role, [string]$Seed) {
    if ($Role -cnotmatch '^(root(-[1-3])?|targets|snapshot|timestamp)$') { throw 'Invalid authority role.' }
    $bytes = [Convert]::FromBase64String($Seed)
    try {
        $encrypted = [Security.Cryptography.ProtectedData]::Protect($bytes, [Text.Encoding]::UTF8.GetBytes("AudioPilot staging $Role"), [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $file = [IO.File]::Open((Join-Path $script:keyDirectory "$Role.dpapi"), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $file.Write($encrypted); $file.Flush($true) } finally { $file.Dispose() }
    }
    finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }
}
function Read-Role([string]$Role) {
    if ($Role -cnotmatch '^(root(-[1-3])?|targets|snapshot|timestamp)$') { throw 'Invalid authority role.' }
    $path = Join-Path $script:keyDirectory "$Role.dpapi"
    Assert-DirectPath $path
    if ((Get-Item -LiteralPath $path).Length -gt 4096) { throw 'Oversized encrypted role key.' }
    $encrypted = [IO.File]::ReadAllBytes($path)
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect($encrypted, [Text.Encoding]::UTF8.GetBytes("AudioPilot staging $Role"), [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try { return [Convert]::ToBase64String($bytes) }
    finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }
}
function Save-AuthorityState([hashtable]$State) {
    $checkpoint = Join-Path $script:authority ('authority-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $bytes = [Text.Encoding]::UTF8.GetBytes(($State | ConvertTo-Json))
    $file = [IO.File]::Open($checkpoint, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $file.Write($bytes); $file.Flush($true) } finally { $file.Dispose() }
    [IO.File]::Move($checkpoint, (Join-Path $script:authority 'authority.json'), $true)
}
$authority = [IO.Path]::GetFullPath($AuthorityDirectory)
$tool = (Resolve-Path -LiteralPath $Publisher).Path
Assert-DirectPath $authority; Assert-DirectPath $tool
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($authority.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $authority.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep durable signing authority material outside the checkout and build-artifact folders.'
}
function Read-BackupPassphrase([bool]$Confirm) {
    $secret = Read-Host 'Backup passphrase (at least 16 characters; keep it separately from the backup)' -AsSecureString
    $repeat = $null
    try {
        $plain = [PSCredential]::new('backup', $secret).GetNetworkCredential().Password
        if ($plain.Length -lt 16) { throw 'Use a backup passphrase of at least 16 characters.' }
        if ($Confirm) {
            $repeat = Read-Host 'Confirm backup passphrase' -AsSecureString
            if ($plain -cne [PSCredential]::new('backup', $repeat).GetNetworkCredential().Password) { throw 'Backup passphrases differ.' }
        }
        return $plain
    }
    finally { $secret.Dispose(); if ($repeat) { $repeat.Dispose() } }
}
if ($Operation -in @('Initialize', 'Restore')) {
    if (Test-Path -LiteralPath $authority) { throw 'Authority already exists. Never reinitialize a published root.' }
    [IO.Directory]::CreateDirectory($authority) | Out-Null
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in @($identity, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow'))
    }
    $acl.SetOwner($identity)
    Set-Acl -LiteralPath $authority -AclObject $acl
    $keyDirectory = $authority
    $repository = Join-Path $authority 'repository'
    if ($Operation -eq 'Restore') {
        if (-not $BackupPath -or -not $PublicRepository -or -not $BootstrapHash) { throw 'Restore requires BackupPath, a current verified PublicRepository, its independent BootstrapHash and MinimumVersion.' }
        $repository = [IO.Path]::GetFullPath($PublicRepository)
        Assert-DirectPath $repository
        $request = @{ operation = 'restore'; repository = $repository; bootstrapHash = $BootstrapHash; backup = [IO.Path]::GetFullPath($BackupPath); minimumVersion = $MinimumVersion; minimumRootVersion = $MinimumRootVersion }
        try { $request.passphrase = Read-BackupPassphrase $false; $created = Invoke-Publisher $request }
        finally { $request.Remove('passphrase') }
        # Keep a private, verified repository copy so recovery never depends on a removable export.
        $recoveredRepository = Join-Path $authority 'repository'
        foreach ($relative in $created.files) {
            $destination = Join-Path $recoveredRepository $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath (Join-Path $repository $relative) -Destination $destination
        }
        $null = Invoke-Publisher @{ operation = 'check'; repository = $recoveredRepository; bootstrapHash = $created.bootstrapHash }
    }
    else { $created = Invoke-Publisher @{ operation = 'initialize'; repository = $repository } }
    foreach ($role in $created.keys.Keys) { Protect-Role $role $created.keys[$role] }
    $rootRoles = @($created.keys.Keys | Where-Object { $_ -eq 'root' -or $_ -cmatch '^root-[1-3]$' } | Sort-Object)
    $created.Remove('keys')
    $state = @{ schema = 1; channel = 'staging'; bootstrapHash = $created.bootstrapHash; publicLiveUpdates = $false; lastMetadataVersion = $created.version; rootRoles = $rootRoles; rootVersion = [Math]::Max(1, $created.rootVersion) }
    Save-AuthorityState $state
    Write-Output $state
    return
}
$lock = [IO.File]::Open((Join-Path $authority '.authority.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Assert-DirectPath (Join-Path $authority 'authority.json')
    if ((Get-Item -LiteralPath (Join-Path $authority 'authority.json')).Length -gt 4096) { throw 'Oversized authority checkpoint.' }
    $state = Get-Content -LiteralPath (Join-Path $authority 'authority.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($state.schema -ne 1 -or $state.channel -cne 'staging' -or $state.publicLiveUpdates -ne $false -or $state.bootstrapHash -cnotmatch '^[A-F0-9]{64}$') { throw 'Invalid staging authority.' }
    $keyDirectory = $authority
    $repository = Join-Path $authority 'repository'
    if ($state.ContainsKey('generation')) {
        if ($state.generation -cnotmatch '^[a-f0-9]{32}$') { throw 'Invalid authority generation.' }
        $keyDirectory = Join-Path $authority "keys/$($state.generation)"
        $repository = Join-Path $authority "repositories/$($state.generation)"
    }
    $rootRoles = if ($state.ContainsKey('rootRoles')) { @($state.rootRoles) } else { @('root') }
    Assert-DirectPath $keyDirectory; Assert-DirectPath $repository
    $rootFloor = if ($state.ContainsKey('rootVersion')) { $state.rootVersion } else { 1 }
    $request = @{ operation = $Operation.ToLowerInvariant(); repository = $repository; bootstrapHash = $state.bootstrapHash; minimumRootVersion = $rootFloor }
    if ($Operation -eq 'RenewCatalog') { $request.operation = 'renew' }
    $timestamp = Join-Path $request.repository 'metadata/timestamp.json'
    if ($state.lastMetadataVersion -gt 0) {
        Assert-DirectPath $timestamp
        if ((Get-Item -LiteralPath $timestamp).Length -gt 16KB) { throw 'Oversized timestamp checkpoint.' }
        if ((Get-Content -LiteralPath $timestamp -Raw | ConvertFrom-Json).signed.version -lt $state.lastMetadataVersion) { throw 'Authority metadata is older than its publication checkpoint.' }
    }
    if ($Operation -eq 'Backup') {
        if (-not $BackupPath) { throw 'Backup requires an explicit BackupPath.' }
        $request.backup = [IO.Path]::GetFullPath($BackupPath)
        $request.keys = @{}
        foreach ($role in @($rootRoles) + @('targets', 'snapshot', 'timestamp')) { $request.keys[$role] = Read-Role $role }
        try { $request.passphrase = Read-BackupPassphrase $true; $null = Invoke-Publisher $request }
        finally { $request.Remove('passphrase'); $request.Remove('keys') }
    }
    if ($Operation -eq 'Rotate') {
        $generation = [guid]::NewGuid().ToString('N')
        $request.destination = Join-Path $authority "repositories/$generation"
        $selectedSigners = @(if ($RootSignerRoles) { $RootSignerRoles } else { $rootRoles })
        if (@($selectedSigners | Where-Object { $rootRoles -cnotcontains $_ }).Count -or @($selectedSigners | Sort-Object -Unique).Count -ne $selectedSigners.Count) { throw 'Select distinct current root signer roles.' }
        $request.rootSigners = @($selectedSigners | ForEach-Object { Read-Role $_ })
        try { $rotated = Invoke-Publisher $request }
        finally { $request.Remove('rootSigners') }
        $keyDirectory = Join-Path $authority "keys/$generation"
        [IO.Directory]::CreateDirectory($keyDirectory) | Out-Null
        foreach ($role in $rotated.keys.Keys) { Protect-Role $role $rotated.keys[$role] }
        $state.rootRoles = @($rotated.keys.Keys | Where-Object { $_ -cmatch '^root-[1-3]$' } | Sort-Object)
        $rotated.Remove('keys')
        $state.generation = $generation
        $state.lastMetadataVersion = $rotated.version
        $state.rootVersion = $rotated.rootVersion
        Save-AuthorityState $state
        $request.repository = $request.destination
    }
    if ($Operation -in @('Publish', 'Renew', 'RenewCatalog')) {
        $request.keys = @{}
        $roles = if ($Operation -in @('Publish', 'RenewCatalog')) { @('targets', 'snapshot', 'timestamp') } else { @('snapshot', 'timestamp') }
        foreach ($role in $roles) { $request.keys[$role] = Read-Role $role }
        if ($Operation -eq 'Publish') {
            $manifest = Get-Content -LiteralPath $PackageManifest -Raw | ConvertFrom-Json -AsHashtable
            if ($manifest.schema -ne 1 -or $manifest.channel -cne 'staging' -or $manifest.packages.Count -lt 1 -or $manifest.packages.Count -gt 9) { throw 'Invalid explicit signed-package approval manifest.' }
            $request.packages = @($manifest.packages | ForEach-Object {
                $package = $_
                $path = [IO.Path]::GetFullPath($package.path)
                Assert-DirectPath $path
                if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $package.sha256) { throw 'Package bytes changed after signing approval.' }
                @{ path = $path; name = $package.name; identity = $package.identity }
            })
        }
        $published = Invoke-Publisher $request
        $request.Remove('keys')
    }
    $verified = Invoke-Publisher @{ operation = 'check'; repository = $request.repository; bootstrapHash = $state.bootstrapHash; minimumRootVersion = $rootFloor }
    $state.lastMetadataVersion = $verified.version
    $state.rootVersion = $verified.rootVersion
    Save-AuthorityState $state
    if ($OutputDirectory) {
        $output = [IO.Path]::GetFullPath($OutputDirectory)
        Assert-DirectPath $output
        if (Test-Path -LiteralPath $output) { throw 'Use a new export directory.' }
        if ($output.StartsWith($authority + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Do not export into the authority.' }
        foreach ($relative in $verified.files) {
            if ($relative -match '[\\:]' -or $relative -match '(^|/)\.\.?(/|$)') { throw 'Unsafe public export path.' }
            $source = Join-Path $request.repository $relative
            $destination = Join-Path $output $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
        }
        $null = Invoke-Publisher @{ operation = 'check'; repository = $output; bootstrapHash = $state.bootstrapHash }
    }
    Write-Output @{ channel = 'staging'; version = $verified.version; rootVersion = $verified.rootVersion; rootThreshold = $verified.rootThreshold; expires = $verified.expires; bootstrapHash = $verified.bootstrapHash; publicLiveUpdates = $false; output = $OutputDirectory }
}
finally { $lock.Dispose() }
