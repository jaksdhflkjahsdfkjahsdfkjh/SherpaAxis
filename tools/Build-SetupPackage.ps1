[CmdletBinding()]
param(
    [string]$VJoyInstaller,
    [string]$CompilerPath,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
$repositoryDirectory = Split-Path $PSScriptRoot -Parent
$dfuDirectory = Join-Path $repositoryDirectory 'vendor/dfuse'
$dfuManifest = Get-Content -LiteralPath (Join-Path $dfuDirectory 'sha256.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($entry in $dfuManifest.GetEnumerator()) {
    if ((Get-FileHash -LiteralPath (Join-Path $dfuDirectory $entry.Key) -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "Unexpected DfuSe payload hash: $($entry.Key)"
    }
}
foreach ($signedFile in @('Driver/sttube.cat', 'Driver/x64/STTub30.sys')) {
    $driverSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $dfuDirectory $signedFile)
    if ($driverSignature.Status -ne 'Valid' -or $driverSignature.SignerCertificate.Subject -notlike '*Microsoft Windows Hardware Compatibility Publisher*') {
        throw "Invalid DfuSe driver signature: $signedFile"
    }
}
if ((Get-FileHash -LiteralPath (Join-Path $repositoryDirectory 'firmware/PE0017.dfu') -Algorithm SHA256).Hash -ne '5D525D43D7EF434E61E0DD077379629C420F95CD863E6C607201752368157757') {
    throw 'Unexpected pedal firmware hash.'
}
$stageDirectory = Join-Path $repositoryDirectory 'publish/setup-package'
$appDirectory = Join-Path $stageDirectory 'app'
$prerequisiteDirectory = Join-Path $stageDirectory 'prerequisites'
New-Item -ItemType Directory -Path $prerequisiteDirectory -Force | Out-Null
$driverDestination = Join-Path $prerequisiteDirectory 'vJoySetup_v2.2.2.0_Win10_Win11.exe'
if ($VJoyInstaller) {
    if ([IO.Path]::GetFullPath($VJoyInstaller) -ne [IO.Path]::GetFullPath($driverDestination)) {
        Copy-Item -LiteralPath $VJoyInstaller -Destination $driverDestination -Force
    }
}
elseif (-not (Test-Path -LiteralPath $driverDestination)) {
    Invoke-WebRequest -Uri 'https://github.com/BrunnerInnovation/vJoy/releases/download/v2.2.2.0/vJoySetup_v2.2.2.0_Win10_Win11.exe' -OutFile $driverDestination
}
if ((Get-FileHash -LiteralPath $driverDestination -Algorithm SHA256).Hash -ne 'EF569A3105CD301B89580F18F60C66B339E95296ACF2C0DFCAF4B4BBF8AB68FE') {
    throw 'Unexpected vJoy installer hash.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $driverDestination
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Brunner Elektronik AG*') {
    throw 'Invalid vJoy installer signature.'
}

if (-not $CompilerPath) {
    $compilerDirectory = Join-Path $repositoryDirectory 'research/tools/innosetup-6.7.3/portable'
    $CompilerPath = Join-Path $compilerDirectory 'tools/ISCC.exe'
    if (-not (Test-Path -LiteralPath $CompilerPath)) {
        $packagePath = Join-Path $prerequisiteDirectory 'Tools.InnoSetup.6.7.3.nupkg'
        Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/6.7.3/tools.innosetup.6.7.3.nupkg' -OutFile $packagePath
        if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne 'F780898E402FF80612CC8D9FCB8C6E02932BD1CB4C900FFDAA31F9341CFB49F4') {
            throw 'Unexpected portable Inno Setup package hash.'
        }
        [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $compilerDirectory)
    }
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $CompilerPath
if ($compilerSignature.Status -ne 'Valid' -or $compilerSignature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') {
    throw 'Invalid Inno Setup compiler signature.'
}

dotnet publish (Join-Path $repositoryDirectory 'src/SherpaAxis.App') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:NuGetLockFilePath=obj/packages.setup.lock.json -o $appDirectory
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
& $CompilerPath "/DAppVersion=$Version" "/DAppSource=$appDirectory" "/DVJoyInstaller=$driverDestination" "/DSetupOutput=$(Join-Path $repositoryDirectory 'publish')" (Join-Path $repositoryDirectory 'installer/Setup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
Write-Output (Join-Path $repositoryDirectory 'publish/Setup.exe')
