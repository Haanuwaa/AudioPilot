[CmdletBinding()]
param(
    [switch]$Fix
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$gitCommand = Get-Command git -ErrorAction SilentlyContinue
$gitExecutable = if ($null -ne $gitCommand) {
    $gitCommand.Source
}
elseif (Test-Path "C:\Program Files\Git\cmd\git.exe") {
    "C:\Program Files\Git\cmd\git.exe"
}
else {
    throw "Git is required to validate repository line endings."
}

function Set-FileLineEnding {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [ValidateSet("lf", "crlf")]
        [string]$LineEnding
    )

    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    $normalized = [Collections.Generic.List[byte]]::new($bytes.Length + 128)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        [byte]$value = $bytes[$index]
        if ($value -ne 10 -and $value -ne 13) {
            $normalized.Add($value)
            continue
        }

        if ($value -eq 13 -and $index + 1 -lt $bytes.Length -and $bytes[$index + 1] -eq 10) {
            $index++
        }

        if ($LineEnding -eq "crlf") {
            $normalized.Add(13)
        }

        $normalized.Add(10)
    }

    [IO.File]::WriteAllBytes($Path, $normalized.ToArray())
}

Push-Location $repoRoot
try {
    $trackedOutput = (& $gitExecutable ls-files --eol -z --cached | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files failed while reading tracked files."
    }

    $untrackedOutput = (& $gitExecutable ls-files --eol -z --others --exclude-standard | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files failed while reading untracked files."
    }

    $trackedEntries = @($trackedOutput -split [char]0 | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $untrackedEntries = @($untrackedOutput -split [char]0 | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    $violations = [Collections.Generic.List[object]]::new()
    foreach ($entry in @($trackedEntries + $untrackedEntries)) {
        if ($entry -notmatch '^i/\S*\s+w/(?<actual>\S*)\s+attr/(?<attributes>[^\t]*)\t(?<path>.+)$') {
            continue
        }

        $actual = $Matches.actual
        $attributes = $Matches.attributes
        $path = $Matches.path
        if ($actual -in @("", "none", "-text") -or $attributes -notmatch '(?:^|\s)eol=(?<expected>lf|crlf)(?:\s|$)') {
            continue
        }

        $expected = $Matches.expected
        if ($actual -ne "mixed" -and $actual -eq $expected) {
            continue
        }

        $violations.Add([pscustomobject]@{
            Path = $path
            Actual = $actual
            Expected = $expected
        })
    }

    if ($Fix) {
        foreach ($violation in $violations) {
            Set-FileLineEnding -Path $violation.Path -LineEnding $violation.Expected
            Write-Host "Normalized $($violation.Path) to $($violation.Expected.ToUpperInvariant())."
        }

        if ($violations.Count -gt 0) {
            & $PSCommandPath
            exit $LASTEXITCODE
        }
    }

    if ($violations.Count -gt 0) {
        Write-Host "Line-ending validation failed for $($violations.Count) file(s). Run scripts/validate-line-endings.ps1 -Fix."
        foreach ($violation in $violations) {
            Write-Host " - $($violation.Path): actual=$($violation.Actual) expected=$($violation.Expected)"
        }

        exit 1
    }

    Write-Host "Line endings match .gitattributes."
}
finally {
    Pop-Location
}
