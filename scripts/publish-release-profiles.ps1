[CmdletBinding()]
param(
    [string[]]$TargetProfile,
    [string]$Configuration = "Release",
    [string]$Project = "AudioPilot/AudioPilot.csproj",
    [string]$CliProject = "AudioPilot.CliHost/AudioPilot.CliHost.csproj",
    [string]$PublishProfilesDirectory = "AudioPilot/Properties/PublishProfiles",
    [string]$Version,
    [switch]$NoLockedMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot $Project
$cliProjectPath = Join-Path $repoRoot $CliProject
$profilesPath = Join-Path $repoRoot $PublishProfilesDirectory

$versionArguments = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "Version must use numeric SemVer core format (for example, 1.2.3)."
    }

    $versionArguments = @(
        "/p:Version=$Version",
        "/p:AssemblyVersion=$Version.0",
        "/p:FileVersion=$Version.0",
        "/p:InformationalVersion=$Version"
    )
}

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "App project not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $cliProjectPath)) {
    throw "CLI project not found: $cliProjectPath"
}

if (-not (Test-Path -LiteralPath $profilesPath)) {
    throw "Publish profiles directory not found: $profilesPath"
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Step,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Output "=== $Step ==="
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
    Write-Output ("Completed {0} in {1:N1}s" -f $Step, $watch.Elapsed.TotalSeconds)
}

function Get-PubxmlProperty {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Pubxml,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    foreach ($propertyGroup in @($Pubxml.Project.PropertyGroup)) {
        $property = $propertyGroup.SelectSingleNode($Name)
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace($property.InnerText)) {
            return $property.InnerText.Trim()
        }
    }

    return $null
}

function Get-SelectedProfileFiles {
    $profileFiles = Get-ChildItem -LiteralPath $profilesPath -Filter "*.pubxml" | Where-Object { $_.BaseName -ne "FolderProfile" } | Sort-Object BaseName

    if ($TargetProfile -and $TargetProfile.Count -gt 0) {
        $selectedProfiles = @($profileFiles | Where-Object { $TargetProfile -contains $_.BaseName })
        $missing = @($TargetProfile | Where-Object { $profileFiles.BaseName -notcontains $_ })
        if ($missing.Count -gt 0) {
            throw "Publish profile(s) not found: $($missing -join ', ')"
        }
        return $selectedProfiles
    }

    return $profileFiles
}

function Get-RequiredProfileProperty {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Pubxml,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$ProfileName
    )

    $value = Get-PubxmlProperty -Pubxml $Pubxml -Name $Name
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Publish profile '$ProfileName' must define '$Name'."
    }

    return $value
}

$selectedProfiles = @(Get-SelectedProfileFiles)
if ($selectedProfiles.Count -eq 0) {
    throw "No publish profiles selected."
}

$lockedModeArgument = if ($NoLockedMode) { @() } else { @("--locked-mode") }

foreach ($profileFile in $selectedProfiles) {
    $profileName = $profileFile.BaseName
    [xml]$pubxml = Get-Content -LiteralPath $profileFile.FullName -Raw

    $selfContained = Get-PubxmlProperty -Pubxml $pubxml -Name "SelfContained"
    if ([string]::IsNullOrWhiteSpace($selfContained)) {
        $selfContained = "false"
    }

    $publishReadyToRun = Get-PubxmlProperty -Pubxml $pubxml -Name "PublishReadyToRun"
    if ([string]::IsNullOrWhiteSpace($publishReadyToRun)) {
        $publishReadyToRun = "false"
    }

    $rid = Get-RequiredProfileProperty -Pubxml $pubxml -Name 'RuntimeIdentifier' -ProfileName $profileName
    $mode = if ($selfContained -eq 'true') { 'SelfContained' } else { 'FrameworkDependent' }
    if ($rid -notin @('win-x64', 'win-x86', 'win-arm64') -or $profileName -ne "$mode-$rid") {
        throw "Release profile '$profileName' does not match a supported deployment and runtime identifier."
    }
    $publishDirectory = Get-RequiredProfileProperty -Pubxml $pubxml -Name 'PublishDir' -ProfileName $profileName
    $publishDirectory = $publishDirectory.Replace('$(MSBuildProjectDirectory)', (Split-Path -Parent $projectPath))
    $publishDirectory = [IO.Path]::GetFullPath($publishDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $expectedDirectory = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts/publish/$mode/$rid"))
    if (-not $publishDirectory.Equals($expectedDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release profile '$profileName' must publish to '$expectedDirectory'."
    }
    for ($directory = [IO.DirectoryInfo]::new($publishDirectory); $null -ne $directory; $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Release publish cleanup does not follow directory links: $($directory.FullName)"
        }
    }

    $buildIsolationKey = "PublishProfile-$profileName"

    $appRestoreArguments = @(
        "restore",
        $projectPath,
        "--nologo",
        "/p:Configuration=$Configuration",
        "/p:PublishProfile=$profileName"
    ) + $versionArguments + $lockedModeArgument

    Invoke-DotNet -Step "restore app publish profile $profileName" -Arguments $appRestoreArguments

    $cliRestoreArguments = @(
        "restore",
        $cliProjectPath,
        "--nologo",
        "/p:Configuration=$Configuration",
        "/p:SelfContained=$selfContained",
        "/p:PublishReadyToRun=$publishReadyToRun",
        "/p:PublishSingleFile=false",
        "/p:BuildIsolationKey=$buildIsolationKey"
    ) + $versionArguments + $lockedModeArgument

    Invoke-DotNet -Step "restore CLI host for $profileName" -Arguments $cliRestoreArguments

    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    $publishArguments = @(
        "publish",
        $projectPath,
        "--nologo",
        "-c",
        $Configuration,
        "/p:PublishProfile=$profileName",
        "/p:PublishCliHostNoRestore=true",
        "/p:CliHostBuildIsolationKey=$buildIsolationKey",
        "/p:DebugSymbols=false",
        "/p:DebugType=None",
        "--no-restore"
    ) + $versionArguments

    Invoke-DotNet -Step "publish app and CLI profile $profileName" -Arguments $publishArguments
}

Write-Output ""
Write-Output "Published $($selectedProfiles.Count) release profile(s)."
