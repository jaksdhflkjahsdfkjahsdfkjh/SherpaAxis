[CmdletBinding()]
param([string]$VJoyInstaller)
$ErrorActionPreference = 'Stop'
$repositoryDirectory = Split-Path $PSScriptRoot -Parent
$stageDirectory = Join-Path $repositoryDirectory 'publish/setup-package'
$appDirectory = Join-Path $stageDirectory 'app'
$prerequisiteDirectory = Join-Path $stageDirectory 'prerequisites'
New-Item -ItemType Directory -Path $prerequisiteDirectory -Force | Out-Null
$driverDestination = Join-Path $prerequisiteDirectory 'vJoySetup_v2.2.2.0_Win10_Win11.exe'
if ($VJoyInstaller) { Copy-Item -LiteralPath $VJoyInstaller -Destination $driverDestination -Force }
elseif (-not (Test-Path -LiteralPath $driverDestination)) {
    Invoke-WebRequest -Uri 'https://github.com/BrunnerInnovation/vJoy/releases/download/v2.2.2.0/vJoySetup_v2.2.2.0_Win10_Win11.exe' -OutFile $driverDestination
}
if ((Get-FileHash -LiteralPath $driverDestination -Algorithm SHA256).Hash -ne 'EF569A3105CD301B89580F18F60C66B339E95296ACF2C0DFCAF4B4BBF8AB68FE') { throw 'Unexpected vJoy installer hash.' }
$signature = Get-AuthenticodeSignature -LiteralPath $driverDestination
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Brunner Elektronik AG*') { throw 'Invalid vJoy installer signature.' }
dotnet publish (Join-Path $repositoryDirectory 'src/SherpaAxis.App') -c Release -r win-x64 --self-contained true -p:NuGetLockFilePath=obj/packages.setup.lock.json -o $appDirectory
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
Copy-Item -LiteralPath (Join-Path $repositoryDirectory 'installer/Install.cmd'), (Join-Path $repositoryDirectory 'installer/Install.ps1') -Destination $stageDirectory -Force
Copy-Item -LiteralPath (Join-Path $repositoryDirectory 'installer/README.txt'), (Join-Path $repositoryDirectory 'installer/vJoy-LICENSE.txt') -Destination $stageDirectory -Force
$archive = Join-Path $repositoryDirectory 'publish/SherpaAxis-Setup-win-x64.zip'
Compress-Archive -Path (Join-Path $stageDirectory '*') -DestinationPath $archive -Force
Write-Host $archive
