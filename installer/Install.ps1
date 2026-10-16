[CmdletBinding()]
param([switch]$Elevated)
$ErrorActionPreference = 'Stop'
$packageDirectory = $PSScriptRoot
$applicationDirectory = Join-Path $packageDirectory 'app'
$driverInstaller = Join-Path $packageDirectory 'prerequisites/vJoySetup_v2.2.2.0_Win10_Win11.exe'
$driverHash = 'EF569A3105CD301B89580F18F60C66B339E95296ACF2C0DFCAF4B4BBF8AB68FE'
$targetDirectory = Join-Path $env:ProgramFiles 'SherpaAxis'
$driverLibrary = Join-Path $env:ProgramFiles 'vJoy/x64/vJoyInterface.dll'

try {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'SherpaAxis requires 64-bit Windows.' }
    if (-not (Test-Path -LiteralPath (Join-Path $applicationDirectory 'SherpaAxis.App.exe'))) { throw 'The application package is incomplete.' }
    if (-not $Elevated) {
        # Only the installer elevates. The installed application runs with normal user permissions.
        $quotedScript = '"' + $PSCommandPath + '"'
        $child = Start-Process -FilePath powershell.exe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $quotedScript, '-Elevated') -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        if ($child.ExitCode -eq 0) { Write-Host 'Installed. Open SherpaAxis from the Start menu.'; exit 0 }
        if ($child.ExitCode -eq 3010) { Write-Host 'Installed. Restart Windows, then open SherpaAxis.'; exit 3010 }
        throw 'Installation did not complete. See the SherpaAxis install log in ProgramData.'
    }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator approval is required to install the driver.' }
    $logDirectory = Join-Path $env:ProgramData 'SherpaAxis'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    Start-Transcript -Path (Join-Path $logDirectory 'install.log') -Append | Out-Null
    if (Get-Process -Name 'SherpaAxis.App' -ErrorAction SilentlyContinue) { throw 'Exit SherpaAxis from its tray menu before installing.' }
    $restartRequired = $false
    if (-not (Test-Path -LiteralPath $driverLibrary)) {
        if ((Get-FileHash -LiteralPath $driverInstaller -Algorithm SHA256).Hash -ne $driverHash) { throw 'The vJoy installer hash does not match the verified release.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $driverInstaller
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Brunner Elektronik AG*') { throw 'The vJoy installer signature is invalid.' }
        # Driver and runtime SDK DLL are installed together by the unmodified signed upstream installer.
        $driverProcess = Start-Process -FilePath $driverInstaller -ArgumentList @('/SILENT', '/NORESTART', '/SUPPRESSMSGBOXES', '/RESTARTEXITCODE=3010', '/COMPONENTS="Apps,Apps\vJoyConf"') -WindowStyle Hidden -PassThru
        if (-not $driverProcess.WaitForExit(180000)) { throw 'vJoy installation is still running. Let it finish; restart Windows if requested, then run setup again.' }
        if ($driverProcess.ExitCode -notin @(0, 3010)) { throw "vJoy installation failed: $($driverProcess.ExitCode)" }
        $restartRequired = $driverProcess.ExitCode -eq 3010
    }
    # Preserve any installed incompatible vJoy version; the helper verifies DLL/driver compatibility.
    New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $applicationDirectory -Force | Copy-Item -Destination $targetDirectory -Recurse -Force
    $applicationPath = Join-Path $targetDirectory 'SherpaAxis.App.exe'
    if (-not $restartRequired) {
        $configure = Start-Process -FilePath $applicationPath -ArgumentList '--configure-vjoy' -WindowStyle Hidden -PassThru
        if (-not $configure.WaitForExit(45000)) { throw 'Automatic device setup did not complete. Restart Windows, then use Automatic vJoy setup in Settings.' }
        if ($configure.ExitCode -eq 3010) { $restartRequired = $true }
        elseif ($configure.ExitCode -ne 0) { throw 'vJoy is unavailable or incompatible. Repair vJoy 2.2.2 and check Settings; an existing driver was not replaced.' }
    }
    $shellObject = New-Object -ComObject WScript.Shell
    $shortcut = $shellObject.CreateShortcut((Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'SherpaAxis.lnk'))
    $shortcut.TargetPath = $applicationPath
    $shortcut.WorkingDirectory = $targetDirectory
    $shortcut.Save()
    Stop-Transcript | Out-Null
    if ($restartRequired) { exit 3010 }
    exit 0
}
catch {
    Write-Error $_ -ErrorAction Continue
    if ($Elevated) { try { Stop-Transcript | Out-Null } catch {} }
    exit 1
}
