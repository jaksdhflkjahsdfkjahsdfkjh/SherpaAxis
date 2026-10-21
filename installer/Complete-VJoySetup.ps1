$ErrorActionPreference = 'Stop'
$taskName = 'SherpaAxis.DeviceSetup.48CBF14C'
$logDirectory = Join-Path $env:ProgramData 'SherpaAxis'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory 'device-setup.log'
try {
    $application = Join-Path $PSScriptRoot 'SherpaAxis.App.exe'
    $process = Start-Process -FilePath $application -ArgumentList '--configure-vjoy' -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) {
        $process.Kill()
        throw 'Virtual device setup timed out.'
    }
    Add-Content -LiteralPath $logPath -Value "$(Get-Date -Format o) Virtual device setup returned $($process.ExitCode)."
    if ($process.ExitCode -ne 0) { exit $process.ExitCode }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
    exit 0
}
catch {
    Add-Content -LiteralPath $logPath -Value "$(Get-Date -Format o) $($_.Exception.Message)"
    exit 1
}
