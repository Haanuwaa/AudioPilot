[CmdletBinding()]
param(
    [string]$PublishRoot = "artifacts/publish",
    [string]$OutputRoot = "artifacts/release",
    [string]$InstallerRoot = "AudioPilot.Installer/bin",
    [string]$Version,
    [string]$Repository = "Haanuwaa/AudioPilot",
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-Version {
    param([string]$ExplicitVersion)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitVersion)) {
        return $ExplicitVersion.Trim()
    }

    $versionPropsPath = Join-Path $PSScriptRoot "../Version.props"
    if (Test-Path $versionPropsPath) {
        try {
            [xml]$projXml = Get-Content -Path $versionPropsPath -Raw
            $fromVersion = $projXml.Project.PropertyGroup.AudioPilotVersion | Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace($fromVersion)) {
                return $fromVersion.Trim()
            }
        }
        catch {
        }
    }

    return (Get-Date).ToString("yyyy.MM.dd-HHmm")
}

function Add-ArtifactRecord {
    param(
        [System.Collections.Generic.List[object]]$List,
        [System.Collections.Generic.List[string]]$Checksums,
        [string]$Path,
        [hashtable]$Metadata
    )

    $item = Get-Item -LiteralPath $Path
    $hash = Get-FileHash -LiteralPath $Path -Algorithm SHA256

    $record = [ordered]@{
        file = $item.Name
        sizeBytes = $item.Length
        sha256 = $hash.Hash.ToLowerInvariant()
    }

    foreach ($key in $Metadata.Keys) {
        $record[$key] = $Metadata[$key]
    }

    $List.Add([pscustomobject]$record)
    $Checksums.Add("$($hash.Hash.ToLowerInvariant()) *$($item.Name)")
}

function ConvertTo-SpdxIdSegment {
    param([string]$Value)

    $segment = ($Value -replace '[^A-Za-z0-9\.-]', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($segment)) {
        return "unknown"
    }

    return $segment
}

function Get-RelativePathSafe {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )

    $baseUri = [Uri]((Resolve-Path -LiteralPath $BasePath).Path.TrimEnd('\') + '\')
    $targetUri = [Uri](Resolve-Path -LiteralPath $TargetPath).Path
    return $baseUri.MakeRelativeUri($targetUri).ToString()
}

function Resolve-GitExecutable {
    $gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($null -ne $gitCommand) {
        return $gitCommand.Source
    }

    $installRoots = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($installRoot in $installRoots) {
        foreach ($relativePath in @("Git/cmd/git.exe", "Git/bin/git.exe")) {
            $candidate = Join-Path $installRoot $relativePath
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    return $null
}

function Get-GitCommitMetadata {
    param([string]$RepoRoot)

    $gitExecutable = Resolve-GitExecutable
    $gitDirectory = Join-Path $RepoRoot ".git"
    if ([string]::IsNullOrWhiteSpace($gitExecutable)) {
        if (Test-Path -LiteralPath $gitDirectory) {
            throw "Git is required to capture release provenance for this repository, but git.exe could not be found."
        }

        return [ordered]@{
            commit = $null
            branch = $null
            tag = $null
            dirty = $null
        }
    }

    $commit = (& $gitExecutable -C $RepoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        throw "Unable to capture the Git commit for release provenance."
    }

    $branch = (& $gitExecutable -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
        throw "Unable to capture the Git branch for release provenance."
    }

    $tag = (& $gitExecutable -C $RepoRoot describe --tags --exact-match 2>$null)
    if ($LASTEXITCODE -ne 0) {
        $tag = $null
    }

    $status = (& $gitExecutable -C $RepoRoot status --porcelain 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to capture the Git working-tree state for release provenance."
    }

    $dirty = @($status).Count -gt 0

    [ordered]@{
        commit = if ($commit) { "$commit".Trim() } else { $null }
        branch = if ($branch) { "$branch".Trim() } else { $null }
        tag = if ($tag) { "$tag".Trim() } else { $null }
        dirty = $dirty
    }
}

function Get-DotNetSdkVersion {
    try {
        $version = (& dotnet --version 2>$null)
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($version)) {
            return "$version".Trim()
        }
    }
    catch {
    }

    return $null
}

function Get-LockFileDependencyInventory {
    param([string]$RepoRoot)

    $byIdentity = [ordered]@{}
    $lockFiles = Get-ChildItem -Path $RepoRoot -Recurse -File -Filter "packages.lock.json" |
        Where-Object { $_.FullName -notmatch "\\bin\\" -and $_.FullName -notmatch "\\obj\\" } |
        Sort-Object FullName

    foreach ($lockFile in $lockFiles) {
        $lock = Get-Content -Path $lockFile.FullName -Raw | ConvertFrom-Json
        if ($null -eq $lock.dependencies) {
            continue
        }

        $projectPath = Get-RelativePathSafe -BasePath $RepoRoot -TargetPath (Split-Path -Parent $lockFile.FullName)
        foreach ($tfmProperty in $lock.dependencies.PSObject.Properties) {
            foreach ($dependencyProperty in $tfmProperty.Value.PSObject.Properties) {
                $dependencyName = [string]$dependencyProperty.Name
                $dependency = $dependencyProperty.Value
                $resolvedProperty = $dependency.PSObject.Properties["resolved"]
                if ($null -eq $resolvedProperty) {
                    continue
                }

                $resolvedVersion = [string]$resolvedProperty.Value
                if ([string]::IsNullOrWhiteSpace($resolvedVersion)) {
                    continue
                }

                $identity = "$dependencyName@$resolvedVersion"
                if (-not $byIdentity.Contains($identity)) {
                    $typeProperty = $dependency.PSObject.Properties["type"]
                    $contentHashProperty = $dependency.PSObject.Properties["contentHash"]
                    $byIdentity[$identity] = [ordered]@{
                        name = $dependencyName
                        version = $resolvedVersion
                        type = if ($null -ne $typeProperty) { [string]$typeProperty.Value } else { $null }
                        contentHash = if ($null -ne $contentHashProperty) { [string]$contentHashProperty.Value } else { $null }
                        packageUrl = "pkg:nuget/$dependencyName@$resolvedVersion"
                        targetFrameworks = New-Object System.Collections.Generic.HashSet[string]
                        projects = New-Object System.Collections.Generic.HashSet[string]
                    }
                }

                [void]$byIdentity[$identity].targetFrameworks.Add([string]$tfmProperty.Name)
                [void]$byIdentity[$identity].projects.Add($projectPath)
            }
        }
    }

    foreach ($entry in $byIdentity.Values) {
        [pscustomobject][ordered]@{
            name = $entry.name
            version = $entry.version
            type = $entry.type
            contentHash = $entry.contentHash
            packageUrl = $entry.packageUrl
            targetFrameworks = @($entry.targetFrameworks | Sort-Object)
            projects = @($entry.projects | Sort-Object)
        }
    }
}

function Get-ProvenanceMaterial {
    param([string]$RepoRoot)

    $materialPaths = @(
        "Version.props",
        "Directory.Packages.props",
        "global.json",
        "AudioPilot.sln",
        "AudioPilot/packages.lock.json",
        "AudioPilot.CliHost/packages.lock.json",
        "AudioPilot.Installer/packages.lock.json",
        "AudioPilot.Tests/packages.lock.json"
    )

    foreach ($relativePath in $materialPaths) {
        $path = Join-Path $RepoRoot $relativePath
        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }

        $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
        [pscustomobject][ordered]@{
            path = $relativePath.Replace('\', '/')
            sha256 = $hash.Hash.ToLowerInvariant()
        }
    }
}

function Write-ReleaseSbom {
    param(
        [string]$Path,
        [string]$Version,
        [string]$Repository,
        [object[]]$ArtifactRecords,
        [object[]]$Dependencies
    )

    $createdUtc = (Get-Date).ToUniversalTime().ToString("o")
    $artifactFiles = foreach ($artifact in $ArtifactRecords) {
        $fileName = [string]$artifact.file
        [ordered]@{
            SPDXID = "SPDXRef-File-" + (ConvertTo-SpdxIdSegment $fileName)
            fileName = $fileName
            checksums = @(
                [ordered]@{
                    algorithm = "SHA256"
                    checksumValue = [string]$artifact.sha256
                }
            )
        }
    }

    $dependencyPackages = foreach ($dependency in $Dependencies) {
        $dependencyId = "SPDXRef-Package-" + (ConvertTo-SpdxIdSegment "$($dependency.name)-$($dependency.version)")
        [ordered]@{
            name = [string]$dependency.name
            SPDXID = $dependencyId
            versionInfo = [string]$dependency.version
            downloadLocation = "NOASSERTION"
            filesAnalyzed = $false
            supplier = "NOASSERTION"
            externalRefs = @(
                [ordered]@{
                    referenceCategory = "PACKAGE-MANAGER"
                    referenceType = "purl"
                    referenceLocator = [string]$dependency.packageUrl
                }
            )
            annotations = @(
                [ordered]@{
                    annotationDate = $createdUtc
                    annotationType = "OTHER"
                    annotator = "Tool: AudioPilot release packaging"
                    comment = "NuGet lock-file dependency; type=$($dependency.type); projects=$(@($dependency.projects) -join ',')"
                }
            )
        }
    }

    $relationships = @(
        [ordered]@{
            spdxElementId = "SPDXRef-DOCUMENT"
            relationshipType = "DESCRIBES"
            relatedSpdxElement = "SPDXRef-Package-AudioPilot"
        }
    )

    foreach ($file in $artifactFiles) {
        $relationships += [ordered]@{
            spdxElementId = "SPDXRef-Package-AudioPilot"
            relationshipType = "CONTAINS"
            relatedSpdxElement = $file.SPDXID
        }
    }

    foreach ($package in $dependencyPackages) {
        $relationships += [ordered]@{
            spdxElementId = "SPDXRef-Package-AudioPilot"
            relationshipType = "DEPENDS_ON"
            relatedSpdxElement = $package.SPDXID
        }
    }

    $sbom = [ordered]@{
        spdxVersion = "SPDX-2.3"
        dataLicense = "CC0-1.0"
        SPDXID = "SPDXRef-DOCUMENT"
        name = "AudioPilot $Version release SBOM"
        documentNamespace = "https://github.com/$Repository/releases/download/v$Version/audiopilot-$Version-sbom"
        creationInfo = [ordered]@{
            created = $createdUtc
            creators = @("Tool: AudioPilot release packaging")
        }
        packages = @(
            [ordered]@{
                name = "AudioPilot"
                SPDXID = "SPDXRef-Package-AudioPilot"
                versionInfo = $Version
                downloadLocation = "https://github.com/$Repository/releases/tag/v$Version"
                filesAnalyzed = $true
                supplier = "Organization: Haanuwaa"
            }
        ) + @($dependencyPackages)
        files = @($artifactFiles)
        relationships = @($relationships)
    }

    $sbom | ConvertTo-Json -Depth 12 | Set-Content -Path $Path -Encoding UTF8
}

function Write-ReleaseProvenance {
    param(
        [string]$Path,
        [string]$Version,
        [string]$Repository,
        [string]$PublishRoot,
        [string]$OutputRoot,
        [object[]]$ArtifactRecords,
        [object[]]$Materials,
        [object]$GitMetadata
    )

    $subjects = foreach ($artifact in $ArtifactRecords) {
        $artifactTypeProperty = $artifact.PSObject.Properties["artifactType"]
        $installerTypeProperty = $artifact.PSObject.Properties["installerType"]
        $artifactType = if ($null -ne $artifactTypeProperty -and -not [string]::IsNullOrWhiteSpace([string]$artifactTypeProperty.Value)) {
            [string]$artifactTypeProperty.Value
        }
        elseif ($null -ne $installerTypeProperty -and -not [string]::IsNullOrWhiteSpace([string]$installerTypeProperty.Value)) {
            [string]$installerTypeProperty.Value
        }
        else {
            "release-package"
        }

        [ordered]@{
            name = [string]$artifact.file
            digest = [ordered]@{
                sha256 = [string]$artifact.sha256
            }
            sizeBytes = [long]$artifact.sizeBytes
            artifactType = $artifactType
        }
    }

    $githubRunUrl = if ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY -and $env:GITHUB_RUN_ID) {
        "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID"
    }
    else {
        $null
    }

    $provenance = [ordered]@{
        schemaVersion = "AudioPilot.ReleaseProvenance.v1"
        app = "AudioPilot"
        version = $Version
        repository = $Repository
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        source = [ordered]@{
            git = $GitMetadata
        }
        build = [ordered]@{
            runner = if ($env:GITHUB_ACTIONS -eq "true") { "github-actions" } else { "local" }
            workflow = $env:GITHUB_WORKFLOW
            runId = $env:GITHUB_RUN_ID
            runAttempt = $env:GITHUB_RUN_ATTEMPT
            runUrl = $githubRunUrl
            actor = $env:GITHUB_ACTOR
            eventName = $env:GITHUB_EVENT_NAME
            ref = $env:GITHUB_REF
            sha = $env:GITHUB_SHA
            dotnetSdkVersion = Get-DotNetSdkVersion
            os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            packageScript = "scripts/package-release.ps1"
            publishRoot = $PublishRoot
            outputRoot = $OutputRoot
        }
        materials = @($Materials)
        subjects = @($subjects)
        verification = [ordered]@{
            checksumFile = "SHA256SUMS.txt"
            releaseManifest = "release-manifest.json"
            sbom = "AudioPilot-$Version-sbom.spdx.json"
        }
    }

    $provenance | ConvertTo-Json -Depth 10 | Set-Content -Path $Path -Encoding UTF8
}

function New-ReleaseZipPackage {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    if (-not $PSCmdlet.ShouldProcess($DestinationPath, "Create ZIP package")) {
        return
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    $files = @(Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw "Cannot package empty publish directory: $SourceDirectory"
    }

    $archive = [System.IO.Compression.ZipFile]::Open($DestinationPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $relativePath = [System.IO.Path]::GetRelativePath($SourceDirectory, $file.FullName).Replace('\', '/')
            $entryPath = "AudioPilot/$relativePath"
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $entryPath,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishRootPath = Join-Path $repoRoot $PublishRoot
$outputRootPath = Join-Path $repoRoot $OutputRoot
$installerRootPath = Join-Path $repoRoot $InstallerRoot
$resolvedVersion = Resolve-Version -ExplicitVersion $Version

if (-not (Test-Path $publishRootPath)) {
    throw "Publish root not found: $publishRootPath"
}

if ($Clean -and (Test-Path $outputRootPath)) {
    Remove-Item -Path $outputRootPath -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

$targets = @(
    @{ Profile = "FrameworkDependent-win-arm64"; Mode = "FrameworkDependent"; Rid = "win-arm64"; Source = "FrameworkDependent/win-arm64" },
    @{ Profile = "FrameworkDependent-win-x64";   Mode = "FrameworkDependent"; Rid = "win-x64";   Source = "FrameworkDependent/win-x64" },
    @{ Profile = "FrameworkDependent-win-x86";   Mode = "FrameworkDependent"; Rid = "win-x86";   Source = "FrameworkDependent/win-x86" },
    @{ Profile = "SelfContained-win-arm64";      Mode = "SelfContained";      Rid = "win-arm64"; Source = "SelfContained/win-arm64" },
    @{ Profile = "SelfContained-win-x64";        Mode = "SelfContained";      Rid = "win-x64";   Source = "SelfContained/win-x64" },
    @{ Profile = "SelfContained-win-x86";        Mode = "SelfContained";      Rid = "win-x86";   Source = "SelfContained/win-x86" }
)

$packages = New-Object System.Collections.Generic.List[object]
$installers = New-Object System.Collections.Generic.List[object]
$wingetManifests = New-Object System.Collections.Generic.List[object]
$metadataArtifacts = New-Object System.Collections.Generic.List[object]
$checksumLines = New-Object System.Collections.Generic.List[string]

foreach ($target in $targets) {
    $sourceDir = Join-Path $publishRootPath $target.Source
    if (-not (Test-Path $sourceDir)) {
        throw "Expected publish output missing for profile '$($target.Profile)': $sourceDir"
    }

    $cliExePath = Join-Path $sourceDir "AudioPilot.Cli.exe"
    if (-not (Test-Path $cliExePath)) {
        throw "Expected CLI host executable missing for profile '$($target.Profile)': $cliExePath"
    }

    $zipFileName = "AudioPilot-$resolvedVersion-$($target.Profile).zip"
    $zipPath = Join-Path $outputRootPath $zipFileName
    New-ReleaseZipPackage -SourceDirectory $sourceDir -DestinationPath $zipPath

    $packageRecord = @{
        profile = $target.Profile
        mode = $target.Mode
        runtimeIdentifier = $target.Rid
        packageFile = [IO.Path]::GetFileName($zipPath)
        cliHostExecutable = "AudioPilot.Cli.exe"
    }

    Add-ArtifactRecord -List $packages -Checksums $checksumLines -Path $zipPath -Metadata $packageRecord

    Write-Output "Packaged $($target.Profile) -> $([IO.Path]::GetFileName($zipPath))"
}

$installerTargets = @(
    @{ Architecture = "x64"; Source = "x64/Release/AudioPilot-$resolvedVersion-x64.msi" },
    @{ Architecture = "arm64"; Source = "arm64/Release/AudioPilot-$resolvedVersion-arm64.msi" }
)

foreach ($target in $installerTargets) {
    $sourcePath = Join-Path $installerRootPath $target.Source
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Expected MSI output missing for architecture '$($target.Architecture)': $sourcePath"
    }

    $destinationPath = Join-Path $outputRootPath ([IO.Path]::GetFileName($sourcePath))
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force

    Add-ArtifactRecord -List $installers -Checksums $checksumLines -Path $destinationPath -Metadata @{
        architecture = $target.Architecture
        installerFile = [IO.Path]::GetFileName($destinationPath)
        installerType = "msi"
    }

    Write-Output "Staged installer $($target.Architecture) -> $([IO.Path]::GetFileName($destinationPath))"
}

$repositoryParts = $Repository.Split("/", 2, [System.StringSplitOptions]::RemoveEmptyEntries)
if ($repositoryParts.Count -ne 2) {
    throw "Repository must be in 'owner/name' format. Actual value: $Repository"
}

$wingetOutputRoot = Join-Path $outputRootPath "winget"
$wingetScriptPath = Join-Path $repoRoot "packaging/winget/generate-winget-manifest.ps1"
if (-not (Test-Path -LiteralPath $wingetScriptPath)) {
    throw "Winget manifest generator not found: $wingetScriptPath"
}

& $wingetScriptPath `
    -Version $resolvedVersion `
    -RepositoryOwner $repositoryParts[0] `
    -RepositoryName $repositoryParts[1] `
    -OutputRoot $wingetOutputRoot `
    -X64InstallerPath (Join-Path $outputRootPath "AudioPilot-$resolvedVersion-x64.msi") `
    -Arm64InstallerPath (Join-Path $outputRootPath "AudioPilot-$resolvedVersion-arm64.msi")

& (Join-Path $repoRoot "scripts/validate-winget-manifests.ps1") `
    -ManifestRoot $wingetOutputRoot `
    -ReleaseRoot $outputRootPath `
    -Version $resolvedVersion `
    -Repository $Repository

$generatedWingetFiles = Get-ChildItem -Path $wingetOutputRoot -Recurse -File -Filter "*.yaml" | Sort-Object FullName
if ($generatedWingetFiles.Count -eq 0) {
    throw "No winget manifest files were generated in: $wingetOutputRoot"
}

foreach ($file in $generatedWingetFiles) {
    Add-ArtifactRecord -List $wingetManifests -Checksums $checksumLines -Path $file.FullName -Metadata @{
        manifestFile = $file.Name
        relativePath = Get-RelativePathSafe -BasePath $outputRootPath -TargetPath $file.FullName
        artifactType = "winget-manifest"
    }
}

$publicArtifactRecords = @($packages.ToArray()) + @($installers.ToArray())
$dependencyInventory = @(Get-LockFileDependencyInventory -RepoRoot $repoRoot)
$gitMetadata = Get-GitCommitMetadata -RepoRoot $repoRoot
$provenanceMaterials = @(Get-ProvenanceMaterial -RepoRoot $repoRoot)

$sbomPath = Join-Path $outputRootPath "AudioPilot-$resolvedVersion-sbom.spdx.json"
Write-ReleaseSbom `
    -Path $sbomPath `
    -Version $resolvedVersion `
    -Repository $Repository `
    -ArtifactRecords $publicArtifactRecords `
    -Dependencies $dependencyInventory
Add-ArtifactRecord -List $metadataArtifacts -Checksums $checksumLines -Path $sbomPath -Metadata @{
    artifactType = "sbom"
    format = "SPDX-2.3"
}
Write-Output "Generated SBOM -> $([IO.Path]::GetFileName($sbomPath))"

$provenancePath = Join-Path $outputRootPath "AudioPilot-$resolvedVersion-provenance.json"
Write-ReleaseProvenance `
    -Path $provenancePath `
    -Version $resolvedVersion `
    -Repository $Repository `
    -PublishRoot $PublishRoot `
    -OutputRoot $OutputRoot `
    -ArtifactRecords $publicArtifactRecords `
    -Materials $provenanceMaterials `
    -GitMetadata $gitMetadata
Add-ArtifactRecord -List $metadataArtifacts -Checksums $checksumLines -Path $provenancePath -Metadata @{
    artifactType = "provenance"
    format = "AudioPilot.ReleaseProvenance.v1"
}
Write-Output "Generated provenance -> $([IO.Path]::GetFileName($provenancePath))"

$manifest = [ordered]@{
    app = "AudioPilot"
    version = $resolvedVersion
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    publishRoot = $PublishRoot
    outputRoot = $OutputRoot
    installerRoot = $InstallerRoot
    packageCount = $packages.Count
    installerCount = $installers.Count
    wingetManifestCount = $wingetManifests.Count
    metadataArtifactCount = $metadataArtifacts.Count
    packages = $packages
    installers = $installers
    wingetManifests = $wingetManifests
    metadataArtifacts = $metadataArtifacts
    dependencyCount = $dependencyInventory.Count
}

$manifestPath = Join-Path $outputRootPath "release-manifest.json"
$checksumsPath = Join-Path $outputRootPath "SHA256SUMS.txt"

$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding UTF8
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumLines.Add("$manifestHash *release-manifest.json")
$checksumLines | Set-Content -Path $checksumsPath -Encoding UTF8

Write-Output ""
Write-Output "Release packaging complete."
Write-Output "Manifest:  $manifestPath"
Write-Output "Checksums: $checksumsPath"
Write-Output "Packages:  $($packages.Count)"
