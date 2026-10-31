[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repository ('research/app-update-worker-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$powershell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
$workerSource = Join-Path $repository 'src/SherpaAxis.App/Services/Updates/Apply-AppUpdate.ps1'
$user = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $userSid = $user.User.Value } finally { $user.Dispose() }

function Assert-Update([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function File-Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Compile-Fixture([string]$Version, [string]$Platform, [string]$Name) {
    $source = Join-Path $root ($Name + '.cs')
    $body = @'
using System;
using System.IO;
using System.Reflection;
using System.Threading;
[assembly: AssemblyVersion("VERSION")]
[assembly: AssemblyFileVersion("VERSION")]
[assembly: AssemblyProduct("SherpaAxis.App")]
class Fixture {
    static void Main(string[] args) {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        if (Array.IndexOf(args, "--wait") >= 0) {
            while (!File.Exists(Path.Combine(root, "exit.fixture"))) Thread.Sleep(20);
        } else File.WriteAllText(Path.Combine(root, "started.fixture"), "VERSION");
    }
}
'@
    [IO.File]::WriteAllText($source, $body.Replace('VERSION', $Version))
    $path = Join-Path $root ($Name + '.exe')
    & $compiler /nologo /nowarn:1607 /target:winexe "/platform:$Platform" "/out:$path" $source | Write-Host
    if ($LASTEXITCODE -ne 0) { throw 'Update fixture compilation failed.' }
    return $path
}
function Write-Manifest([string]$Folder, [string[]]$Names) {
    $files = @($Names | ForEach-Object { @{ Path = $_; Hash = File-Hash (Join-Path $Folder $_) } })
    [IO.File]::WriteAllText((Join-Path $Folder 'uninstall-manifest.json'), (@{ SchemaVersion = 1; Files = $files } | ConvertTo-Json -Depth 5))
}
function Start-Hidden([string]$Path, [string]$Arguments, [string]$WorkingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($Path)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.Arguments = $Arguments; $start.WorkingDirectory = $WorkingDirectory
    return [Diagnostics.Process]::Start($start)
}
function Run-Case([string]$Name, [string]$Failure = '') {
    $case = Join-Path $root $Name
    $app = Join-Path $case 'application'
    $packageRoot = Join-Path $case 'new-files'
    $operation = Join-Path $case ([Guid]::NewGuid().ToString('N'))
    foreach ($folder in @($app, $packageRoot, $operation, (Join-Path $app 'My files'))) { [IO.Directory]::CreateDirectory($folder) | Out-Null }
    Copy-Item -LiteralPath $oldExecutable -Destination (Join-Path $app 'SherpaAxis.App.exe')
    Copy-Item -LiteralPath $oldExecutable -Destination (Join-Path $app 'Uninstall.exe')
    [IO.File]::WriteAllText((Join-Path $app 'SherpaAxis.App.dll'), 'old fixture dll')
    [IO.File]::WriteAllText((Join-Path $app 'obsolete.dll'), 'owned obsolete file')
    Write-Manifest $app @('SherpaAxis.App.exe', 'SherpaAxis.App.dll', 'Uninstall.exe', 'obsolete.dll')
    [IO.File]::WriteAllText((Join-Path $app 'My files/notes.txt'), 'keep user notes')
    [IO.File]::SetAttributes((Join-Path $app 'My files/notes.txt'), [IO.FileAttributes]::ReadOnly)
    $next = if ($Failure -eq 'restart') { $badExecutable } else { $newExecutable }
    Copy-Item -LiteralPath $next -Destination (Join-Path $packageRoot 'SherpaAxis.App.exe')
    Copy-Item -LiteralPath $next -Destination (Join-Path $packageRoot 'Uninstall.exe')
    [IO.File]::WriteAllText((Join-Path $packageRoot 'SherpaAxis.App.dll'), 'new fixture dll')
    Write-Manifest $packageRoot @('SherpaAxis.App.exe', 'SherpaAxis.App.dll', 'Uninstall.exe')
    $archive = Join-Path $operation 'SherpaAxis-win-x64.zip'
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, ('SherpaAxis/' + $file.Name)) | Out-Null
        }
    }
    finally { $zip.Dispose() }
    if ($Failure -eq 'modified') { [IO.File]::WriteAllText((Join-Path $app 'SherpaAxis.App.dll'), 'user modified owned file') }
    Copy-Item -LiteralPath $workerSource -Destination (Join-Path $operation 'Apply-AppUpdate.ps1')
    $parent = Start-Hidden (Join-Path $app 'SherpaAxis.App.exe') '--wait' $app
    $worker = $null
    try {
        $plan = @{ schemaVersion = 1; kind = 'Zip'; version = '1.6.0.0'; applicationDirectory = $app;
            packagePath = $archive; packageHash = File-Hash $archive; parentPid = $parent.Id;
            parentStartedUtc = $parent.StartTime.ToUniversalTime().ToString('O'); userSid = $userSid; language = 'en' }
        $planPath = Join-Path $operation 'plan.json'
        [IO.File]::WriteAllText($planPath, ($plan | ConvertTo-Json))
        $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $operation 'Apply-AppUpdate.ps1') + '" -PlanPath "' + $planPath + '"'
        $worker = Start-Hidden $powershell $arguments $operation
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while (-not $worker.HasExited -and -not (Test-Path -LiteralPath (Join-Path $operation 'ready'))) {
            if ($deadline.Elapsed.TotalSeconds -gt 20) { throw 'Worker preparation timed out.' }
            Start-Sleep -Milliseconds 50
        }
        if ($Failure -eq 'modified') {
            Assert-Update ($worker.WaitForExit(5000)) 'Modified-file worker did not stop.'
            Assert-Update (-not $parent.HasExited) 'A failed preparation closed the old app.'
            Assert-Update ([IO.File]::ReadAllText((Join-Path $app 'SherpaAxis.App.dll')) -eq 'user modified owned file') 'Modified file was overwritten.'
            Assert-Update (-not (Test-Path -LiteralPath (Join-Path $operation 'ready'))) 'Modified package was accepted.'
        }
        elseif ($Failure -eq 'cancel') {
            Assert-Update (-not $worker.HasExited) 'Worker failed before cancellation.'
            [IO.File]::WriteAllText((Join-Path $operation 'abort'), 'abort')
            Assert-Update ($worker.WaitForExit(5000)) 'Cancelled worker did not exit.'
            Assert-Update (-not $parent.HasExited) 'Cancellation closed the app.'
            Assert-Update ((File-Hash (Join-Path $app 'SherpaAxis.App.exe')) -eq (File-Hash $oldExecutable)) 'Cancellation changed the app.'
            Assert-Update (-not (Get-ChildItem -LiteralPath $case -Directory -Filter '.SherpaAxis-update-*')) 'Cancellation left staged files.'
        }
        else {
            if ($worker.HasExited) { throw ('Worker exited before readiness: ' + [IO.File]::ReadAllText((Join-Path $operation 'result.json'))) }
            [IO.File]::WriteAllText((Join-Path $app 'last-second.txt'), 'last extra data')
            [IO.File]::WriteAllText((Join-Path $app 'exit.fixture'), 'close')
            Assert-Update ($parent.WaitForExit(5000)) 'Fixture app did not close.'
            Assert-Update ($worker.WaitForExit(10000)) 'Worker did not finish.'
            $result = Get-Content -LiteralPath (Join-Path $operation 'result.json') -Raw | ConvertFrom-Json
            $clock = [Diagnostics.Stopwatch]::StartNew()
            while (-not (Test-Path -LiteralPath (Join-Path $app 'started.fixture'))) {
                if ($clock.Elapsed.TotalSeconds -gt 10) { throw 'Updated or recovered app did not restart.' }
                Start-Sleep -Milliseconds 50
            }
            $expected = if ($Failure -eq 'restart') { '1.5.0.0' } else { '1.6.0.0' }
            Assert-Update ([IO.File]::ReadAllText((Join-Path $app 'started.fixture')) -eq $expected) 'Wrong app version was restarted.'
            Assert-Update ($result.success -eq ($Failure -ne 'restart')) 'Unexpected update result.'
            Assert-Update ([IO.File]::ReadAllText((Join-Path $app 'My files/notes.txt')) -eq 'keep user notes') 'User file was lost.'
            Assert-Update ([IO.File]::ReadAllText((Join-Path $app 'last-second.txt')) -eq 'last extra data') 'Late user file was lost.'
            if ($Failure -ne 'restart') { Assert-Update (-not (Test-Path -LiteralPath (Join-Path $app 'obsolete.dll'))) 'An obsolete owned DLL survived the update.' }
        }
        Write-Host "PASS: $Name"
    }
    finally {
        if ($worker) { if (-not $worker.HasExited) { $worker.Kill(); $worker.WaitForExit(5000) | Out-Null }; $worker.Dispose() }
        if (-not $parent.HasExited) { [IO.File]::WriteAllText((Join-Path $app 'exit.fixture'), 'close'); if (-not $parent.WaitForExit(3000)) { $parent.Kill() } }
        $parent.Dispose()
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$oldExecutable = Compile-Fixture '1.5.0.0' 'x64' 'old-app'
$newExecutable = Compile-Fixture '1.6.0.0' 'x64' 'new-app'
$badExecutable = Compile-Fixture '1.6.0.0' 'arm' 'wrong-architecture-app'
Run-Case 'ZIP replacement preserves extra files and restarts'
Run-Case 'Modified package files leave the running app untouched' 'modified'
Run-Case 'Cancellation before closing keeps the original app' 'cancel'
Run-Case 'Restart failure rolls back and reopens the old app' 'restart'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $elevated = [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
if ($elevated) {
    $case = Join-Path $root 'Setup handoff uses silent update flags and restarts'
    $app = Join-Path $case 'application'
    $operation = Join-Path $case ([Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($app) | Out-Null
    [IO.Directory]::CreateDirectory($operation) | Out-Null
    Copy-Item -LiteralPath $oldExecutable -Destination (Join-Path $app 'SherpaAxis.App.exe')
    Copy-Item -LiteralPath $newExecutable -Destination (Join-Path $operation 'next-app.exe')
    Copy-Item -LiteralPath $workerSource -Destination (Join-Path $operation 'Apply-AppUpdate.ps1')
    $setupSource = @'
using System;
using System.IO;
using System.Reflection;
[assembly: AssemblyFileVersion("1.6.0.0")]
[assembly: AssemblyProduct("SherpaAxis")]
class SetupFixture {
    static int Main(string[] args) {
        string target = Array.Find(args, value => value.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase));
        if (target == null || Array.IndexOf(args, "/AUTOUPDATE=1") < 0 || Array.IndexOf(args, "/NORESTART") < 0 || Array.IndexOf(args, "/NOCLOSEAPPLICATIONS") < 0) return 51;
        target = Path.GetFullPath(target.Substring(5).Trim('"'));
        if (!string.Equals(target, @"EXPECTED_DIRECTORY", StringComparison.OrdinalIgnoreCase)) return 52;
        File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "next-app.exe"), Path.Combine(target, "SherpaAxis.App.exe"), true);
        File.WriteAllText(Path.Combine(target, "installer-flags.fixture"), string.Join("\n", args));
        return 0;
    }
}
'@
    $source = Join-Path $operation 'SetupFixture.cs'
    [IO.File]::WriteAllText($source, $setupSource.Replace('EXPECTED_DIRECTORY', $app))
    $package = Join-Path $operation 'Setup.exe'
    & $compiler /nologo /target:winexe /platform:x64 "/out:$package" $source | Write-Host
    if ($LASTEXITCODE -ne 0) { throw 'Setup fixture compilation failed.' }
    $parent = Start-Hidden (Join-Path $app 'SherpaAxis.App.exe') '--wait' $app
    $worker = $null
    try {
        $plan = @{ schemaVersion = 1; kind = 'Setup'; version = '1.6.0.0'; applicationDirectory = $app;
            packagePath = $package; packageHash = File-Hash $package; parentPid = $parent.Id;
            parentStartedUtc = $parent.StartTime.ToUniversalTime().ToString('O'); userSid = $userSid; language = 'uk' }
        $planPath = Join-Path $operation 'plan.json'
        [IO.File]::WriteAllText($planPath, ($plan | ConvertTo-Json))
        $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $operation 'Apply-AppUpdate.ps1') + '" -PlanPath "' + $planPath + '"'
        $worker = Start-Hidden $powershell $arguments $operation
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (-not $worker.HasExited -and -not (Test-Path -LiteralPath (Join-Path $operation 'ready'))) {
            if ($clock.Elapsed.TotalSeconds -gt 20) { throw 'Setup worker did not prepare.' }
            Start-Sleep -Milliseconds 50
        }
        Assert-Update (-not $worker.HasExited) 'Setup worker exited before readiness.'
        [IO.File]::WriteAllText((Join-Path $app 'exit.fixture'), 'close')
        Assert-Update ($parent.WaitForExit(5000)) 'Setup fixture parent did not close.'
        Assert-Update ($worker.WaitForExit(15000)) 'Setup worker did not finish.'
        $result = Get-Content -LiteralPath (Join-Path $operation 'result.json') -Raw | ConvertFrom-Json
        Assert-Update $result.success ('Setup worker failed: ' + $result.detail)
        $clock.Restart()
        while (-not (Test-Path -LiteralPath (Join-Path $app 'started.fixture'))) {
            if ($clock.Elapsed.TotalSeconds -gt 10) { throw 'Setup-updated app did not restart.' }
            Start-Sleep -Milliseconds 50
        }
        Assert-Update ([IO.File]::ReadAllText((Join-Path $app 'started.fixture')) -eq '1.6.0.0') 'Setup restarted the wrong version.'
        Assert-Update ([IO.File]::ReadAllText((Join-Path $app 'installer-flags.fixture')).Contains('/LANG=ukrainian')) 'Setup ignored the selected language.'
        Write-Host 'PASS: Setup handoff uses silent update flags and restarts under the original account'
    }
    finally {
        if ($worker) { if (-not $worker.HasExited) { $worker.Kill(); $worker.WaitForExit(5000) | Out-Null }; $worker.Dispose() }
        if (-not $parent.HasExited) { [IO.File]::WriteAllText((Join-Path $app 'exit.fixture'), 'close'); if (-not $parent.WaitForExit(3000)) { $parent.Kill() } }
        $parent.Dispose()
    }
}
else { Write-Host 'SKIP: native Setup handoff fixture needs an already-elevated test process; it does not request UAC during tests.' }
Write-Host "Worker fixture results: $root"
