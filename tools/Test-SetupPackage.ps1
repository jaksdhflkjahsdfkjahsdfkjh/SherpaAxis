[CmdletBinding()]
param([string]$CompilerPath)

$ErrorActionPreference = 'Stop'
$repositoryDirectory = Split-Path $PSScriptRoot -Parent
if (-not $CompilerPath) {
    $CompilerPath = Join-Path $repositoryDirectory 'research/tools/innosetup-6.7.3/portable/tools/ISCC.exe'
}
$testRoot = Join-Path $repositoryDirectory ('research/setup-validation-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $testRoot 'payload'
$installDirectory = Join-Path $testRoot 'installed'
$library = Join-Path $testRoot 'driver.dll'
$trace = Join-Path $testRoot 'calls.txt'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SherpaAxis-Installer-Test_is1'
$startupKey = 'HKCU:\Software\SherpaAxisInstallerTest\Startup'
$startupName = 'SherpaAxis-Installer-Test'
if (Test-Path -LiteralPath $uninstallKey) { throw 'Uninstall the previous SherpaAxis Installer Test before running this check.' }
if (Get-ItemProperty -LiteralPath $startupKey -Name $startupName -ErrorAction SilentlyContinue) { throw 'A previous test startup entry exists.' }
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$stub = Join-Path $payload 'SherpaAxis.App.exe'
$stubSource = Join-Path $testRoot 'Stub.cs'
@'
using System;
using System.IO;
class SetupStub {
    static int Main(string[] args) {
        if (Array.IndexOf(args, "--hold") >= 0) { System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite); return 0; }
        bool configure = Array.IndexOf(args, "--configure-vjoy") >= 0;
        bool dfu = Array.IndexOf(args, "--configure-dfuse") >= 0;
        bool release = Array.IndexOf(args, "--release-input-configuration") >= 0;
        if (Array.IndexOf(args, "--record-prerequisite-ownership") >= 0) return 0;
        bool hidHide = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]).Equals("HidHideSetup", StringComparison.OrdinalIgnoreCase);
        if (hidHide && (args.Length != 3 || Array.IndexOf(args, "/exenoui") < 0 || Array.IndexOf(args, "/qn") < 0 || Array.IndexOf(args, "/norestart") < 0)) return 1639;
        if (release) { File.AppendAllText(Environment.GetEnvironmentVariable("SHERPAAXIS_SETUP_TEST_TRACE"), "release\n"); return 0; }
        File.AppendAllText(Environment.GetEnvironmentVariable("SHERPAAXIS_SETUP_TEST_TRACE"), hidHide ? "hidhide\n" : dfu ? "dfuse\n" : configure ? "configure\n" : "driver\n");
        int result = int.Parse(Environment.GetEnvironmentVariable(hidHide ? "SHERPAAXIS_SETUP_TEST_HIDHIDE" : dfu ? "SHERPAAXIS_SETUP_TEST_DFUSE" : configure ? "SHERPAAXIS_SETUP_TEST_CONFIGURE" : "SHERPAAXIS_SETUP_TEST_DRIVER"));
        if (!configure && !dfu && !hidHide && result == 0) File.WriteAllText(Environment.GetEnvironmentVariable("SHERPAAXIS_SETUP_TEST_LIBRARY"), "installed");
        if (hidHide && result == 0) File.WriteAllText(Environment.GetEnvironmentVariable("SHERPAAXIS_SETUP_TEST_LIBRARY") + ".hidhide", "installed");
        return result;
    }
}
'@ | Set-Content -LiteralPath $stubSource -Encoding utf8
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:winexe /platform:x64 "/out:$stub" $stubSource
if ($LASTEXITCODE -ne 0) { throw 'Installer test stub compilation failed.' }
$vendorExecutable = Join-Path $testRoot 'SherpaAxis.SetupTest.Vendor.exe'
Copy-Item -LiteralPath $stub -Destination $vendorExecutable
$vendorProcess = $null
$stubHash = (Get-FileHash -LiteralPath $stub -Algorithm SHA256).Hash
$variables = @('SHERPAAXIS_SETUP_TEST_HIDHIDE', 'SHERPAAXIS_SETUP_TEST_DFUSE', 'SHERPAAXIS_SETUP_TEST_TRACE', 'SHERPAAXIS_SETUP_TEST_CONFIGURE', 'SHERPAAXIS_SETUP_TEST_DRIVER', 'SHERPAAXIS_SETUP_TEST_LIBRARY')
$previousEnvironment = @{}
foreach ($name in $variables) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$env:SHERPAAXIS_SETUP_TEST_TRACE = $trace
$env:SHERPAAXIS_SETUP_TEST_LIBRARY = $library

function Assert-Setup {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-RealStartupSnapshot {
    $snapshot = [ordered]@{}
    foreach ($path in @('Software\Microsoft\Windows\CurrentVersion\Run', 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run')) {
        $values = [ordered]@{}
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
        try {
            if ($key) {
                foreach ($name in ($key.GetValueNames() | Sort-Object)) {
                    $values[$name] = @($key.GetValueKind($name).ToString(), $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames))
                }
            }
        }
        finally { if ($key) { $key.Dispose() } }
        $snapshot[$path] = $values
    }
    return ConvertTo-Json -InputObject $snapshot -Depth 6 -Compress
}

function Build-Fixture {
    param([string]$Name, [string]$Version = '0.1.0', [string]$Hash = $stubHash, [string]$HidHideHash = $stubHash)
    $output = Join-Path $testRoot $Name
    $compileOutput = & $CompilerPath /Q /DInstallerTest=1 /DVendorControlProcess=SherpaAxis.SetupTest.Vendor.exe "/DAppSource=$payload" "/DVJoyInstaller=$stub" "/DHidHideInstaller=$stub" "/DHidHideHash=$HidHideHash" "/DHidHideCli=$library.hidhide" "/DVJoyHash=$Hash" "/DVJoyLibrary=$library" "/DSetupOutput=$output" "/DAppVersion=$Version" (Join-Path $repositoryDirectory 'installer/Setup.iss') 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Fixture compilation failed: $compileOutput" }
    $executable = Join-Path $output 'Setup.exe'
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    Assert-Setup ($metadata.FileVersionRaw -eq [Version]"$Version.0") 'Setup file version does not match its registered version.'
    return $executable
}

function Invoke-Fixture {
    param([string]$Executable, [string]$Name, [int]$Expected, [string]$Language = 'english')
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LANG=$Language", '/RESTARTEXITCODE=3010', "/DIR=`"$installDirectory`"", "/LOG=`"$(Join-Path $testRoot ($Name + '.log'))`"")
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    Assert-Setup ($process.ExitCode -eq $Expected) "$Name returned $($process.ExitCode), expected $Expected."
}

function Reset-Fixture {
    $env:SHERPAAXIS_SETUP_TEST_HIDHIDE = '0'
    $env:SHERPAAXIS_SETUP_TEST_DFUSE = '0'
    $env:SHERPAAXIS_SETUP_TEST_DRIVER = '0'
    $env:SHERPAAXIS_SETUP_TEST_CONFIGURE = '0'
    foreach ($file in @($trace, $library, "$library.deferred", "$library.hidhide")) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
}

function Uninstall-Fixture {
    $record = Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*.dat' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if ($record) { Invoke-Fixture ([IO.Path]::ChangeExtension($record.FullName, '.exe')) 'uninstall' 0 }
    Assert-Setup (-not (Test-Path -LiteralPath $uninstallKey)) 'Test uninstall entry remains.'
    Assert-Setup (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'SherpaAxis.App.exe'))) 'Test app was not removed.'
    Assert-Setup (-not (Get-ItemProperty -LiteralPath $startupKey -Name $startupName -ErrorAction SilentlyContinue)) 'Test startup entry remains.'
}

$originalStartup = Get-RealStartupSnapshot
try {
    $setup = Build-Fixture 'setup'
    $upgrade = Build-Fixture 'upgrade' '0.1.1'
    $badHash = Build-Fixture 'bad-hash' '0.1.0' ('0' * 64)
    $badHidHideHash = Build-Fixture 'bad-hidhide-hash' '0.1.0' $stubHash ('0' * 64)

    Reset-Fixture
    $vendorProcess = Start-Process -FilePath $vendorExecutable -ArgumentList '--hold' -WindowStyle Hidden -PassThru
    Invoke-Fixture $setup 'vendor-running-english' 7
    Invoke-Fixture $setup 'vendor-running-ukrainian' 7 'ukrainian'
    Assert-Setup (-not $vendorProcess.HasExited) 'Setup terminated the conflicting application.'
    Assert-Setup (-not (Test-Path -LiteralPath $trace)) 'Setup ran a prerequisite while the vendor application was open.'
    Assert-Setup (-not (Test-Path -LiteralPath $uninstallKey)) 'Setup installed the app while the vendor application was open.'
    Assert-Setup (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'SherpaAxis.App.exe'))) 'Blocked setup copied the app.'
    Assert-Setup ((Get-Content -LiteralPath (Join-Path $testRoot 'vendor-running-english.log') -Raw) -match 'including its tray instance') 'English setup did not explain how to close FEEL-VR Control.'
    Assert-Setup ((Get-Content -LiteralPath (Join-Path $testRoot 'vendor-running-ukrainian.log') -Raw -Encoding utf8) -match 'Повністю закрийте програму') 'Ukrainian setup did not show the localized conflict message.'
    $vendorProcess.Kill()
    Assert-Setup ($vendorProcess.WaitForExit(5000)) 'Test vendor process did not stop.'
    $vendorProcess.Dispose()
    $vendorProcess = $null
    Write-Output 'PASS: vendor conflict blocks installation before prerequisites, with localized guidance and no forced shutdown'

    Reset-Fixture
    Invoke-Fixture $setup 'fresh-install' 0
    Assert-Setup ((Get-Content -LiteralPath $trace -Raw).Trim() -eq "driver`nhidhide`nconfigure`ndfuse") 'Fresh setup did not install and configure in order.'
    Assert-Setup (Test-Path -LiteralPath $uninstallKey) 'Uninstall registration is missing.'
    Assert-Setup (-not (Get-ItemProperty -LiteralPath $startupKey -Name $startupName -ErrorAction SilentlyContinue)) 'Setup enabled Windows startup without consent.'
    $personalFile = Join-Path $installDirectory 'personal-settings.txt'
    Set-Content -LiteralPath $personalFile -Value 'keep'
    if (-not (Test-Path -LiteralPath $startupKey)) {
        $testStartup = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\SherpaAxisInstallerTest\Startup')
        $testStartup.Dispose()
    }
    New-ItemProperty -LiteralPath $startupKey -Name $startupName -Value '"C:\Previous Portable Build\SherpaAxis.App.exe" --startup' -PropertyType String -Force | Out-Null
    $previousTrace = Get-Content -LiteralPath $trace -Raw
    $vendorProcess = Start-Process -FilePath $vendorExecutable -ArgumentList '--hold' -WindowStyle Hidden -PassThru
    Invoke-Fixture $upgrade 'vendor-running-upgrade' 7
    Assert-Setup ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -eq '0.1.0') 'Blocked upgrade changed the installed version.'
    Assert-Setup ((Get-Content -LiteralPath $trace -Raw) -ceq $previousTrace) 'Blocked upgrade ran device configuration.'
    $vendorProcess.Kill()
    Assert-Setup ($vendorProcess.WaitForExit(5000)) 'Test vendor process did not stop.'
    $vendorProcess.Dispose()
    $vendorProcess = $null
    Invoke-Fixture $upgrade 'upgrade' 0
    Assert-Setup ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -eq '0.1.1') 'Upgrade version was not registered.'
    Assert-Setup ((Get-Content -LiteralPath $trace | Where-Object { $_ -eq 'driver' }).Count -eq 1) 'Upgrade reinstalled an existing driver.'
    Assert-Setup ((Get-ItemPropertyValue -LiteralPath $startupKey -Name $startupName) -eq ('"' + (Join-Path $installDirectory 'SherpaAxis.App.exe') + '" --startup')) 'Upgrade did not migrate the existing startup entry.'
    Uninstall-Fixture
    Assert-Setup (Test-Path -LiteralPath $personalFile) 'Uninstall removed an unowned settings file.'
    Write-Output 'PASS: fresh install, upgrade, uninstall, settings and startup migration'

    Reset-Fixture
    Set-Content -LiteralPath $library -Value 'existing'
    Invoke-Fixture $setup 'existing-driver' 0
    Assert-Setup ((Get-Content -LiteralPath $trace -Raw).Trim() -eq "hidhide`nconfigure`ndfuse") 'Existing driver was replaced.'
    Uninstall-Fixture
    Assert-Setup (Test-Path -LiteralPath $library) 'Uninstall removed the shared driver.'
    Write-Output 'PASS: existing driver reuse and shared driver preservation'

    Reset-Fixture
    Set-Content -LiteralPath $library -Value 'existing'
    Set-Content -LiteralPath "$library.hidhide" -Value 'existing'
    Invoke-Fixture $setup 'existing-hidhide' 0
    Assert-Setup ((Get-Content -LiteralPath $trace -Raw).Trim() -eq "configure`ndfuse") 'Existing HidHide was reinstalled.'
    Uninstall-Fixture
    Assert-Setup (Test-Path -LiteralPath "$library.hidhide") 'Uninstall removed the shared HidHide installation.'
    Assert-Setup ((Get-Content -LiteralPath $trace)[-1] -eq 'release') 'Uninstall did not run input-configuration cleanup.'
    Write-Output 'PASS: existing HidHide reuse, cleanup and shared driver preservation'

    Reset-Fixture
    Set-Content -LiteralPath $library -Value 'existing'
    Invoke-Fixture $badHidHideHash 'invalid-hidhide-hash' 7
    Assert-Setup (-not (Test-Path -LiteralPath $trace)) 'An unverified HidHide installer ran.'
    Write-Output 'PASS: HidHide prerequisite integrity failure'


    Reset-Fixture
    Invoke-Fixture $badHash 'invalid-hash' 7
    Assert-Setup (-not (Test-Path -LiteralPath $trace)) 'An unverified prerequisite ran.'
    Assert-Setup (-not (Test-Path -LiteralPath $uninstallKey)) 'Hash failure installed the app.'
    Write-Output 'PASS: prerequisite integrity failure'

    Reset-Fixture
    $env:SHERPAAXIS_SETUP_TEST_DRIVER = '1603'
    Invoke-Fixture $setup 'driver-failure' 7
    Assert-Setup ((Get-Content -LiteralPath $trace -Raw).Trim() -eq 'driver') 'Configuration ran after failed driver installation.'
    Assert-Setup (-not (Test-Path -LiteralPath $uninstallKey)) 'Driver failure installed the app.'
    Write-Output 'PASS: prerequisite installation failure'

    Reset-Fixture
    $env:SHERPAAXIS_SETUP_TEST_DRIVER = '3010'
    Invoke-Fixture $setup 'driver-restart' 3010
    Assert-Setup ((Get-Content -LiteralPath $trace -Raw).Trim() -eq "driver`nhidhide`ndfuse") 'Configuration ran before the required restart.'
    Assert-Setup (Test-Path -LiteralPath "$library.deferred") 'Restart did not schedule device setup.'
    Uninstall-Fixture
    Write-Output 'PASS: driver restart and deferred setup'

    Reset-Fixture
    Set-Content -LiteralPath $library -Value 'existing'
    $env:SHERPAAXIS_SETUP_TEST_CONFIGURE = '3010'
    Invoke-Fixture $setup 'configuration-restart' 3010
    Assert-Setup (Test-Path -LiteralPath "$library.deferred") 'Configuration restart did not schedule a readiness check.'
    Uninstall-Fixture
    Write-Output 'PASS: configuration restart'

    Reset-Fixture
    Set-Content -LiteralPath $library -Value 'existing'
    $env:SHERPAAXIS_SETUP_TEST_CONFIGURE = '1'
    Invoke-Fixture $setup 'configuration-failure' 1
    Assert-Setup (Test-Path -LiteralPath $uninstallKey) 'Configuration failure left an app without an uninstaller.'
    Uninstall-Fixture
    Write-Output 'PASS: configuration failure and recovery through uninstall'

    Reset-Fixture
    Set-Content -LiteralPath $library -Value 'existing'
    $env:SHERPAAXIS_SETUP_TEST_DFUSE = '5'
    Invoke-Fixture $setup 'dfuse-driver-failure' 1
    Assert-Setup (Test-Path -LiteralPath $uninstallKey) 'DfuSe failure left an app without an uninstaller.'
    Uninstall-Fixture
    Write-Output 'PASS: DfuSe driver staging failure'

    Reset-Fixture
    Set-Content -LiteralPath $library -Value 'existing'
    $env:SHERPAAXIS_SETUP_TEST_DFUSE = '3010'
    Invoke-Fixture $setup 'dfuse-driver-restart' 3010
    Uninstall-Fixture
    Write-Output 'PASS: DfuSe driver restart'

    Reset-Fixture
    Set-Content -LiteralPath $library -Value 'existing'
    $env:SHERPAAXIS_SETUP_TEST_HIDHIDE = '1603'
    Invoke-Fixture $setup 'hidhide-failure' 7
    Assert-Setup ((Get-Content -LiteralPath $trace -Raw).Trim() -eq 'hidhide') 'Configuration ran after failed HidHide installation.'
    Write-Output 'PASS: HidHide installation failure'

    Reset-Fixture
    Set-Content -LiteralPath $library -Value 'existing'
    $env:SHERPAAXIS_SETUP_TEST_HIDHIDE = '3010'
    Invoke-Fixture $setup 'hidhide-restart' 3010
    Assert-Setup (Test-Path -LiteralPath "$library.deferred") 'HidHide restart did not defer device configuration.'
    Uninstall-Fixture
    Write-Output 'PASS: HidHide restart and uninstall cleanup'


}
finally {
    if ($vendorProcess) {
        try {
            if (-not $vendorProcess.HasExited) {
                $vendorProcess.Kill()
                $vendorProcess.WaitForExit(5000) | Out-Null
            }
        }
        finally { $vendorProcess.Dispose() }
    }
    Uninstall-Fixture
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
    Assert-Setup ((Get-RealStartupSnapshot) -ceq $originalStartup) 'Installer tests modified real Windows startup settings.'
}
Write-Output "Installer verification completed. Logs: $testRoot"
