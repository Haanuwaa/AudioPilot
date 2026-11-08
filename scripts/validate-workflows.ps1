Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.12 -shellcheck= -pyflakes=
    if ($LASTEXITCODE -ne 0) { throw 'GitHub Actions workflow validation failed.' }
}
finally { Pop-Location }
