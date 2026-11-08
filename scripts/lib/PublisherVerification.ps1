function Invoke-AudioPilotPublisherVerification {
    param(
        [Parameter(Mandatory)][string]$Verifier,
        [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PublisherSha256,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$Files
    )
    $start = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Verifier).Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('verify-publisher', $PublisherSha256) + $Files) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Pinned Windows verification timed out.'
        }
        $null = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Pinned Windows signature verification failed.' }
    }
    finally { $process.Dispose() }
}
