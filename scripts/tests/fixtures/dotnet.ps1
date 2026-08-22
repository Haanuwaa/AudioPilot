$arguments = @($args)
$index = [Array]::IndexOf($arguments, '--results-directory')
if ($index -lt 0) { throw 'The test runner did not specify an output directory.' }
$directory = [string]$arguments[$index + 1]
New-Item -ItemType Directory -Force -Path $directory | Out-Null
@{ Arguments = $arguments; Logging = $env:AUDIOPILOT_DISABLE_CONSOLE_LOGGING } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directory 'record.json')
$category = Split-Path -Leaf $directory
if ($env:AUDIOPILOT_SCRIPT_TEST_FAIL_CATEGORY -eq $category) { exit 7 }
exit 0
