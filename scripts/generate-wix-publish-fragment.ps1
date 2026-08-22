[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$ManufacturerName,

    [Parameter(Mandatory = $true)]
    [string]$ProductName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-StableId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prefix,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $normalized = $Value.Replace('\', '/').ToLowerInvariant()
    $bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
    $hashBytes = [Security.Cryptography.SHA256]::HashData($bytes)
    $hash = [Convert]::ToHexString($hashBytes).Substring(0, 24)
    return "$Prefix$hash"
}

function Get-StableGuid {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $normalized = $Value.Replace('\', '/').ToLowerInvariant()
    $bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
    $hashBytes = [Security.Cryptography.SHA256]::HashData($bytes)
    $guidBytes = New-Object byte[] 16
    [Array]::Copy($hashBytes, $guidBytes, 16)
    return ([Guid]::new($guidBytes)).ToString().ToUpperInvariant()
}

function Get-XmlSafeValue {
    param([string]$Value)

    return [Security.SecurityElement]::Escape($Value)
}

function Write-Indent {
    param(
        [Parameter(Mandatory = $true)]
        [System.Text.StringBuilder]$Builder,

        [Parameter(Mandatory = $true)]
        [int]$Level,

        [Parameter(Mandatory = $true)]
        [string]$Line
    )

    $null = $Builder.Append(('  ' * $Level))
    $null = $Builder.AppendLine($Line)
}

function Write-DirectoryNodes {
    param(
        [Parameter(Mandatory = $true)]
        [System.Text.StringBuilder]$Builder,

        [Parameter(Mandatory = $true)]
        [hashtable]$ChildrenMap,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$ParentPath,

        [Parameter(Mandatory = $true)]
        [int]$Level
    )

    if (-not $ChildrenMap.ContainsKey($ParentPath)) {
        return
    }

    foreach ($childPath in ($ChildrenMap[$ParentPath] | Sort-Object)) {
        $directoryId = if ([string]::IsNullOrEmpty($childPath)) {
            "INSTALLFOLDER"
        }
        else {
            Get-StableId -Prefix "dir" -Value $childPath
        }

        $directoryName = Split-Path -Path $childPath -Leaf
        Write-Indent -Builder $Builder -Level $Level -Line ('<Directory Id="{0}" Name="{1}">' -f $directoryId, (Get-XmlSafeValue $directoryName))
        Write-DirectoryNodes -Builder $Builder -ChildrenMap $ChildrenMap -ParentPath $childPath -Level ($Level + 1)
        Write-Indent -Builder $Builder -Level $Level -Line '</Directory>'
    }
}

$resolvedPublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$files = Get-ChildItem -LiteralPath $resolvedPublishDir -File -Recurse | Sort-Object FullName
if ($files.Count -eq 0) {
    throw "Publish directory contains no files: $resolvedPublishDir"
}

$relativeDirectories = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
$childrenMap = @{}
$componentRows = New-Object System.Collections.Generic.List[object]

foreach ($file in $files) {
    $relativePath = [IO.Path]::GetRelativePath($resolvedPublishDir, $file.FullName)
    $relativePath = $relativePath.Replace('/', '\')
    $relativeDirectory = Split-Path -Path $relativePath -Parent
    if ($relativeDirectory -eq '.') {
        $relativeDirectory = ''
    }

    $parentPath = $relativeDirectory
    while (-not [string]::IsNullOrEmpty($parentPath)) {
        $null = $relativeDirectories.Add($parentPath)
        $parentPath = Split-Path -Path $parentPath -Parent
        if ($parentPath -eq '.') {
            $parentPath = ''
        }
    }

    if (-not $childrenMap.ContainsKey('')) {
        $childrenMap[''] = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
    }

    $currentPath = $relativeDirectory
    while (-not [string]::IsNullOrEmpty($currentPath)) {
        $parent = Split-Path -Path $currentPath -Parent
        if ($parent -eq '.') {
            $parent = ''
        }

        if (-not $childrenMap.ContainsKey($parent)) {
            $childrenMap[$parent] = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
        }

        $null = $childrenMap[$parent].Add($currentPath)
        $currentPath = $parent
    }

    $componentId = Get-StableId -Prefix "cmp" -Value $relativePath
    $fileId = Get-StableId -Prefix "fil" -Value $relativePath
    $directoryId = if ([string]::IsNullOrEmpty($relativeDirectory)) {
        "INSTALLFOLDER"
    }
    else {
        Get-StableId -Prefix "dir" -Value $relativeDirectory
    }

    $componentRows.Add([pscustomobject]@{
            RelativePath = $relativePath
            ComponentId = $componentId
            ComponentGuid = Get-StableGuid -Value $relativePath
            FileId = $fileId
            DirectoryId = $directoryId
            RegistryName = $componentId
        }) | Out-Null
}

$builder = New-Object System.Text.StringBuilder
Write-Indent -Builder $builder -Level 0 -Line '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">'
Write-Indent -Builder $builder -Level 1 -Line '<Fragment>'
Write-Indent -Builder $builder -Level 2 -Line '<DirectoryRef Id="INSTALLFOLDER">'
Write-DirectoryNodes -Builder $builder -ChildrenMap $childrenMap -ParentPath '' -Level 3
Write-Indent -Builder $builder -Level 2 -Line '</DirectoryRef>'
Write-Indent -Builder $builder -Level 1 -Line '</Fragment>'
Write-Indent -Builder $builder -Level 1 -Line '<Fragment>'

$componentsByDirectory = $componentRows | Group-Object DirectoryId | Sort-Object Name
foreach ($directoryGroup in $componentsByDirectory) {
    Write-Indent -Builder $builder -Level 2 -Line ('<DirectoryRef Id="{0}">' -f $directoryGroup.Name)
    foreach ($component in ($directoryGroup.Group | Sort-Object RelativePath)) {
        $sourcePath = '!(bindpath.PublishDir)\{0}' -f $component.RelativePath
        Write-Indent -Builder $builder -Level 3 -Line ('<Component Id="{0}" Guid="{1}">' -f $component.ComponentId, $component.ComponentGuid)
        Write-Indent -Builder $builder -Level 4 -Line ('<File Id="{0}" Source="{1}" />' -f $component.FileId, (Get-XmlSafeValue $sourcePath))
        Write-Indent -Builder $builder -Level 4 -Line ('<RegistryValue Root="HKCU" Key="Software\{0}\{1}\InstalledFiles" Name="{2}" Type="integer" Value="1" KeyPath="yes" />' -f (Get-XmlSafeValue $ManufacturerName), (Get-XmlSafeValue $ProductName), $component.RegistryName)
        Write-Indent -Builder $builder -Level 3 -Line '</Component>'
    }

    Write-Indent -Builder $builder -Level 2 -Line '</DirectoryRef>'
}

Write-Indent -Builder $builder -Level 1 -Line '</Fragment>'
Write-Indent -Builder $builder -Level 1 -Line '<Fragment>'
Write-Indent -Builder $builder -Level 2 -Line '<DirectoryRef Id="INSTALLFOLDER">'
Write-Indent -Builder $builder -Level 3 -Line ('<Component Id="{0}" Guid="{1}">' -f 'cmpInstallFolderCleanup', (Get-StableGuid -Value 'cleanup:'))
Write-Indent -Builder $builder -Level 4 -Line '<RemoveFolder Id="rmvInstallFolder" On="uninstall" />'
Write-Indent -Builder $builder -Level 4 -Line ('<RegistryValue Root="HKCU" Key="Software\{0}\{1}\InstalledDirectories" Name="{2}" Type="integer" Value="1" KeyPath="yes" />' -f (Get-XmlSafeValue $ManufacturerName), (Get-XmlSafeValue $ProductName), 'install-root')
Write-Indent -Builder $builder -Level 3 -Line '</Component>'
Write-Indent -Builder $builder -Level 2 -Line '</DirectoryRef>'

foreach ($directoryPath in ($relativeDirectories | Sort-Object)) {
    $directoryId = Get-StableId -Prefix "dir" -Value $directoryPath
    $cleanupComponentId = Get-StableId -Prefix "cmpcleanup" -Value $directoryPath
    $cleanupGuid = Get-StableGuid -Value ("cleanup:" + $directoryPath)
    $registryName = Get-StableId -Prefix "dircleanup" -Value $directoryPath

    Write-Indent -Builder $builder -Level 2 -Line ('<DirectoryRef Id="{0}">' -f $directoryId)
    Write-Indent -Builder $builder -Level 3 -Line ('<Component Id="{0}" Guid="{1}">' -f $cleanupComponentId, $cleanupGuid)
    Write-Indent -Builder $builder -Level 4 -Line ('<RemoveFolder Id="{0}" On="uninstall" />' -f (Get-StableId -Prefix "rmv" -Value $directoryPath))
    Write-Indent -Builder $builder -Level 4 -Line ('<RegistryValue Root="HKCU" Key="Software\{0}\{1}\InstalledDirectories" Name="{2}" Type="integer" Value="1" KeyPath="yes" />' -f (Get-XmlSafeValue $ManufacturerName), (Get-XmlSafeValue $ProductName), $registryName)
    Write-Indent -Builder $builder -Level 3 -Line '</Component>'
    Write-Indent -Builder $builder -Level 2 -Line '</DirectoryRef>'
}

Write-Indent -Builder $builder -Level 1 -Line '</Fragment>'
Write-Indent -Builder $builder -Level 1 -Line '<Fragment>'
Write-Indent -Builder $builder -Level 2 -Line '<ComponentGroup Id="GeneratedPublishFiles">'
foreach ($component in ($componentRows | Sort-Object RelativePath)) {
    Write-Indent -Builder $builder -Level 3 -Line ('<ComponentRef Id="{0}" />' -f $component.ComponentId)
}

Write-Indent -Builder $builder -Level 3 -Line '<ComponentRef Id="cmpInstallFolderCleanup" />'
foreach ($directoryPath in ($relativeDirectories | Sort-Object)) {
    Write-Indent -Builder $builder -Level 3 -Line ('<ComponentRef Id="{0}" />' -f (Get-StableId -Prefix "cmpcleanup" -Value $directoryPath))
}

Write-Indent -Builder $builder -Level 2 -Line '</ComponentGroup>'
Write-Indent -Builder $builder -Level 1 -Line '</Fragment>'
Write-Indent -Builder $builder -Level 0 -Line '</Wix>'

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($resolvedOutputPath, $builder.ToString(), $utf8NoBom)
Write-Host "Generated WiX publish fragment: $resolvedOutputPath"
