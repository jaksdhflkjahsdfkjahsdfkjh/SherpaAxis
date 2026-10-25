[CmdletBinding()]
param(
    [string]$VJoyInstaller,
    [string]$CompilerPath,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw)).Project.PropertyGroup.Version
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
$publishDirectory = Join-Path $repositoryDirectory 'publish'
$outputDirectory = Join-Path $publishDirectory "v$Version"
$appDirectory = Join-Path $outputDirectory 'app'
$prerequisiteDirectory = Join-Path $repositoryDirectory 'research/tools/setup-prerequisites'
foreach ($generatedDirectory in @($publishDirectory, $outputDirectory, $appDirectory)) {
    if ((Test-Path -LiteralPath $generatedDirectory) -and ((Get-Item -LiteralPath $generatedDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Build output must not be a filesystem link: $generatedDirectory"
    }
}
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
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

$hidHideDestination = Join-Path $prerequisiteDirectory 'HidHide_1.5.230_x64.exe'
if (-not (Test-Path -LiteralPath $hidHideDestination)) {
    Invoke-WebRequest -Uri 'https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe' -OutFile $hidHideDestination
}
if ((Get-FileHash -LiteralPath $hidHideDestination -Algorithm SHA256).Hash -ne 'F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6') {
    throw 'Unexpected HidHide installer hash.'
}
$hidHideSignature = Get-AuthenticodeSignature -LiteralPath $hidHideDestination
if ($hidHideSignature.Status -ne 'Valid' -or $hidHideSignature.SignerCertificate.Subject -notlike '*Nefarius Software Solutions e.U.*') {
    throw 'Invalid HidHide installer signature.'
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

if (Test-Path -LiteralPath $appDirectory) {
    $resolvedAppDirectory = (Resolve-Path -LiteralPath $appDirectory).Path
    if ([IO.Path]::GetDirectoryName($resolvedAppDirectory) -ne [IO.Path]::GetFullPath($outputDirectory) -or
        (Get-ChildItem -LiteralPath $resolvedAppDirectory -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1)) {
        throw "Cannot clean application output outside its version folder or containing filesystem links: $resolvedAppDirectory"
    }
    Remove-Item -LiteralPath $resolvedAppDirectory -Recurse -Force
}
dotnet publish (Join-Path $repositoryDirectory 'src/SherpaAxis.App') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:NuGetLockFilePath=obj/packages.setup.lock.json -o $appDirectory
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
& $CompilerPath "/DAppVersion=$Version" "/DAppSource=$appDirectory" "/DVJoyInstaller=$driverDestination" "/DHidHideInstaller=$hidHideDestination" "/DSetupOutput=$outputDirectory" (Join-Path $repositoryDirectory 'installer/Setup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
$zipPrerequisites = Join-Path $appDirectory 'Prerequisites'
$zipLicenses = Join-Path $appDirectory 'licenses'
New-Item -ItemType Directory -Path $zipPrerequisites, $zipLicenses -Force | Out-Null
Copy-Item -LiteralPath $driverDestination, $hidHideDestination -Destination $zipPrerequisites
Copy-Item -LiteralPath (Join-Path $repositoryDirectory 'installer/vJoy-LICENSE.txt'), (Join-Path $repositoryDirectory 'installer/HidHide-LICENSE.txt') -Destination $zipLicenses
Copy-Item -LiteralPath (Join-Path $repositoryDirectory 'LICENSE') -Destination $appDirectory
Copy-Item -LiteralPath (Join-Path $repositoryDirectory 'installer/Zip-README.txt') -Destination (Join-Path $appDirectory 'START-HERE.txt')
$manifest = @{
    SchemaVersion = 1
    Files = @(Get-ChildItem -LiteralPath $appDirectory -File -Recurse | ForEach-Object {
        @{ Path = [IO.Path]::GetRelativePath($appDirectory, $_.FullName).Replace('\', '/'); Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
[IO.File]::WriteAllText((Join-Path $appDirectory 'uninstall-manifest.json'), ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
$archive = Join-Path $outputDirectory 'SherpaAxis-win-x64.zip'
$zipStream = [IO.File]::Open($archive, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $appDirectory -Recurse -File | Sort-Object FullName)) {
        $relativePath = [IO.Path]::GetRelativePath($appDirectory, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "SherpaAxis/$relativePath", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $zip.Dispose(); $zipStream.Dispose() }
$setup = Join-Path $outputDirectory 'Setup.exe'
$checksums = foreach ($artifact in @($setup, $archive)) {
    $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($artifact))"
}
[IO.File]::WriteAllText((Join-Path $outputDirectory 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Output $outputDirectory
