param([Parameter(Mandatory)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$operation = [IO.Path]::GetFullPath($PSScriptRoot)
$resultPath = Join-Path $operation 'result.json'
$abortPath = Join-Path $operation 'abort'
$stage = $null
$backup = $null
$swapped = $false
$mutex = $null
$ownsMutex = $false
$plan = $null

function Hash-File([string]$Path) {
    $input = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($input)).Replace('-', '') }
    finally { $hash.Dispose(); $input.Dispose() }
}
function Check-Abort {
    if ([IO.File]::Exists($abortPath)) { throw [OperationCanceledException]::new('Update cancelled.') }
}
function Plain-Tree([string]$Root, [switch]$IgnoreAbort) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        if (-not $IgnoreAbort) { Check-Abort }
        $path = $pending.Pop()
        $attributes = [IO.File]::GetAttributes($path)
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Automatic updates cannot follow filesystem links.' }
        if ($attributes -band [IO.FileAttributes]::Directory) {
            foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($path)) { $pending.Push($child) }
        }
    }
}
function Relative-Path([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path.Contains('\')) { throw 'Invalid update path.' }
    foreach ($part in $Path.Split('/')) {
        if ($part -in @('', '.', '..') -or $part.TrimEnd(' ', '.') -ne $part -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0)
        { throw 'Unsafe update filename.' }
        if ($part.Split('.')[0] -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') { throw 'Reserved update filename.' }
    }
}
function Read-Manifest([string]$Root) {
    $path = Join-Path $Root 'uninstall-manifest.json'
    if ((Get-Item -LiteralPath $path).Length -gt 1048576) { throw 'Update manifest is too large.' }
    $manifest = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1 -or $manifest.Files.Count -lt 1 -or $manifest.Files.Count -gt 2000) { throw 'Invalid update manifest.' }
    $files = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.Files) {
        Relative-Path $file.Path
        if ($file.Hash -notmatch '^[a-fA-F0-9]{64}$' -or $files.ContainsKey($file.Path)) { throw 'Invalid update manifest hash or duplicate path.' }
        $files.Add($file.Path, $file.Hash)
    }
    foreach ($required in @('SherpaAxis.App.exe', 'SherpaAxis.App.dll', 'Uninstall.exe')) {
        if (-not $files.ContainsKey($required)) { throw 'Update is missing required application files.' }
    }
    return ,$files
}
function Executable-Version([string]$Path) {
    $text = ([Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion).Split('+')[0].Trim()
    $version = [version]$text
    return [version]::new($version.Major, $version.Minor, [Math]::Max(0, $version.Build), [Math]::Max(0, $version.Revision))
}
function Parent-Running {
    try {
        $parent = [Diagnostics.Process]::GetProcessById([int]$plan.parentPid)
        try { return -not $parent.HasExited -and $parent.StartTime.ToUniversalTime().Ticks -eq ([DateTime]$plan.parentStartedUtc).ToUniversalTime().Ticks }
        finally { $parent.Dispose() }
    }
    catch [ArgumentException] { return $false }
}
function Write-Result([bool]$Success, [string]$Detail, [bool]$RestartRequired = $false) {
    $record = @{ version = $plan.version; success = $Success; detail = $Detail; restartRequired = $RestartRequired; backupDirectory = $backup; utc = [DateTime]::UtcNow.ToString('O') }
    [IO.File]::WriteAllText($resultPath, ($record | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
function Restart-App {
    if ($ownsMutex) { $mutex.ReleaseMutex(); $script:ownsMutex = $false }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $plan.applicationDirectory 'SherpaAxis.App.exe'))
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $plan.applicationDirectory
    $start.Arguments = '--update-result "' + $resultPath + '"'
    $app = [Diagnostics.Process]::Start($start)
    if ($null -eq $app) { throw 'Could not reopen SherpaAxis.' }
    $app.Dispose()
}

try {
    if ([IO.Path]::GetFullPath($PlanPath) -ne (Join-Path $operation 'plan.json') -or (Get-Item -LiteralPath $PlanPath).Length -gt 65536) { throw 'Invalid update plan location.' }
    $plan = [IO.File]::ReadAllText($PlanPath) | ConvertFrom-Json
    if ($plan.schemaVersion -ne 1 -or $plan.kind -notin @('Setup', 'Zip') -or $plan.packageHash -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid update plan.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { if ($identity.User.Value -ne $plan.userSid) { throw 'Update must run under the original Windows account.' } }
    finally { $identity.Dispose() }
    $application = [IO.Path]::GetFullPath($plan.applicationDirectory).TrimEnd('\')
    if ($application -eq [IO.Path]::GetPathRoot($application).TrimEnd('\') -or -not [IO.Directory]::Exists($application)) { throw 'Invalid application folder.' }
    $plan.applicationDirectory = $application
    foreach ($special in @('UserProfile', 'DesktopDirectory', 'MyDocuments', 'ProgramFiles', 'ProgramFilesX86', 'Windows', 'System', 'LocalApplicationData', 'ApplicationData', 'CommonApplicationData')) {
        $known = [Environment]::GetFolderPath([Environment+SpecialFolder]$special)
        if ($known -and $application -eq $known.TrimEnd('\')) { throw 'Update requires a dedicated application folder.' }
    }
    if ($application -eq [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -or
        $application -eq (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads')) { throw 'Update requires a dedicated application folder.' }
    for ($ancestor = $application; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) { throw 'The application path contains a filesystem link.' }
    }
    $parent = [Diagnostics.Process]::GetProcessById([int]$plan.parentPid)
    try {
        if (-not (Parent-Running) -or $parent.MainModule.FileName -ne (Join-Path $application 'SherpaAxis.App.exe')) { throw 'Update parent does not match the running application.' }
    }
    finally { $parent.Dispose() }
    $created = $false
    $mutex = [Threading.Mutex]::new($false, 'Local\SherpaAxis.AppUpdate', [ref]$created)
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another app update is already running.' }
    $name = if ($plan.kind -eq 'Zip') { 'SherpaAxis-win-x64.zip' } else { 'Setup.exe' }
    $package = Join-Path $operation $name
    if ([IO.Path]::GetFullPath($plan.packagePath) -ne $package -or (Hash-File $package) -ne $plan.packageHash) { throw 'Update package failed final hash verification.' }
    $expected = [version]$plan.version
    if ($plan.kind -eq 'Zip') {
        Plain-Tree $application
        $oldFiles = Read-Manifest $application
        foreach ($relative in $oldFiles.Keys) {
            $path = Join-Path $application $relative
            if ([IO.File]::Exists($path) -and (Hash-File $path) -ne $oldFiles[$relative]) { throw "A packaged file was modified: $relative. Use manual update to preserve your changes." }
        }
        $parentDirectory = [IO.Path]::GetDirectoryName($application)
        $nonce = [IO.Path]::GetFileName($operation)
        $stage = Join-Path $parentDirectory ('.SherpaAxis-update-' + $nonce)
        $backup = Join-Path $parentDirectory ('.SherpaAxis-backup-' + $nonce)
        if ([IO.Directory]::Exists($stage) -or [IO.Directory]::Exists($backup)) { throw 'An update staging or backup folder already exists.' }
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        [IO.Directory]::SetAccessControl($stage, [IO.Directory]::GetAccessControl($application))
        $zip = [IO.Compression.ZipFile]::OpenRead($package)
        try {
            if ($zip.Entries.Count -gt 2001 -or ($zip.Entries | Measure-Object Length -Sum).Sum -gt 2147483648) { throw 'ZIP update exceeds size limits.' }
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $zip.Entries) {
                Check-Abort
                if ($entry.FullName -eq 'SherpaAxis/') { continue }
                if (-not $entry.FullName.StartsWith('SherpaAxis/', [StringComparison]::Ordinal) -or
                    ($entry.ExternalAttributes -band 1024) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 61440) -eq 40960) { throw 'Invalid ZIP update entry.' }
                $relative = $entry.FullName.Substring(11).TrimEnd('/')
                Relative-Path $relative
                if ($entry.FullName.EndsWith('/')) { continue }
                if (-not $seen.Add($relative)) { throw 'Duplicate ZIP entry.' }
                $path = Join-Path $stage $relative
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
            }
        }
        finally { $zip.Dispose() }
        $newFiles = Read-Manifest $stage
        if ($seen.Count -ne $newFiles.Count + 1) { throw 'ZIP files do not match the update manifest.' }
        foreach ($relative in $newFiles.Keys) {
            if ((Hash-File (Join-Path $stage $relative)) -ne $newFiles[$relative]) { throw 'An extracted update file failed hash verification.' }
        }
        if ((Executable-Version (Join-Path $stage 'SherpaAxis.App.exe')) -ne $expected) { throw 'The staged app version does not match the update.' }
    }
    elseif ((Executable-Version $package) -ne $expected) { throw 'The installer version does not match the update.' }
    [IO.File]::WriteAllText((Join-Path $operation 'ready'), 'ready')
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while (Parent-Running) {
        Check-Abort
        if ($wait.Elapsed.TotalSeconds -gt 180) { throw 'SherpaAxis did not close in time. No update was applied.' }
        Start-Sleep -Milliseconds 100
    }
    Check-Abort
    if ($plan.kind -eq 'Zip') {
        Plain-Tree $application
        foreach ($relative in $oldFiles.Keys) {
            $path = Join-Path $application $relative
            if ([IO.File]::Exists($path) -and (Hash-File $path) -ne $oldFiles[$relative]) { throw "A packaged file changed while preparing the update: $relative." }
        }
        foreach ($directory in [IO.Directory]::EnumerateDirectories($application, '*', [IO.SearchOption]::AllDirectories)) {
            Check-Abort
            $relative = $directory.Substring($application.Length + 1)
            [IO.Directory]::CreateDirectory((Join-Path $stage $relative)) | Out-Null
            [IO.Directory]::SetAccessControl((Join-Path $stage $relative), [IO.Directory]::GetAccessControl($directory))
        }
        foreach ($file in [IO.Directory]::EnumerateFiles($application, '*', [IO.SearchOption]::AllDirectories)) {
            Check-Abort
            $relative = $file.Substring($application.Length + 1).Replace('\', '/')
            if ($relative -eq 'uninstall-manifest.json' -or $oldFiles.ContainsKey($relative)) { continue }
            $target = Join-Path $stage $relative
            if ($newFiles.ContainsKey($relative)) {
                if ((Hash-File $file) -ne $newFiles[$relative]) { throw "Update would overwrite an extra file: $relative." }
            }
            else { [IO.File]::Copy($file, $target, $false); [IO.File]::SetAccessControl($target, [IO.File]::GetAccessControl($file)) }
        }
        [IO.Directory]::Move($application, $backup)
        $swapped = $true
        [IO.Directory]::Move($stage, $application)
        $stage = $null
    }
    else {
        $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /SP- /NORESTART /RESTARTEXITCODE=3010 /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /AUTOUPDATE=1 /DIR="' + $application + '"'
        if ($plan.language -eq 'uk') { $arguments += ' /LANG=ukrainian' } else { $arguments += ' /LANG=english' }
        $arguments += ' /LOG="' + (Join-Path $operation 'setup.log') + '"'
        $installer = Start-Process -FilePath $package -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru -Wait
        try { $code = $installer.ExitCode } finally { $installer.Dispose() }
        if ($code -eq 3010) {
            Write-Result $true 'The update was installed. Restart Windows before using SherpaAxis.' $true
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show('SherpaAxis was updated. Restart Windows before opening the app.', 'SherpaAxis update') | Out-Null
            exit 0
        }
        if ($code -ne 0) { throw "Setup could not finish (code $code)." }
    }
    if ((Executable-Version (Join-Path $application 'SherpaAxis.App.exe')) -ne $expected) { throw 'The installed app version could not be verified.' }
    Write-Result $true ('Updated to v' + $expected.ToString(3) + '.')
    Restart-App
    exit 0
}
catch {
    $detail = $_.Exception.Message
    if ($plan -and $swapped -and [IO.Directory]::Exists($backup)) {
        try {
            if ([IO.Directory]::Exists($application)) {
                $failed = Join-Path ([IO.Path]::GetDirectoryName($application)) ('.SherpaAxis-failed-' + [IO.Path]::GetFileName($operation))
                if ([IO.Directory]::Exists($failed)) { throw 'Failed-update recovery folder already exists.' }
                [IO.Directory]::Move($application, $failed)
            }
            [IO.Directory]::Move($backup, $application)
        }
        catch { $detail += ' Automatic rollback needs attention: ' + $_.Exception.Message }
    }
    try { Write-Result $false $detail } catch { }
    if ($plan -and -not (Parent-Running) -and [IO.File]::Exists((Join-Path $plan.applicationDirectory 'SherpaAxis.App.exe'))) {
        try { Restart-App } catch { }
    }
    exit 1
}
finally {
    if ($stage -and [IO.Directory]::Exists($stage)) {
        try {
            $parent = [IO.Path]::GetDirectoryName($application)
            if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -eq $parent -and [IO.Path]::GetFileName($stage).StartsWith('.SherpaAxis-update-', [StringComparison]::Ordinal)) {
                Plain-Tree $stage -IgnoreAbort
                foreach ($file in [IO.Directory]::EnumerateFiles($stage, '*', [IO.SearchOption]::AllDirectories)) {
                    $attributes = [IO.File]::GetAttributes($file)
                    if ($attributes -band [IO.FileAttributes]::ReadOnly) { [IO.File]::SetAttributes($file, $attributes -band (-bnot [IO.FileAttributes]::ReadOnly)) }
                }
                [IO.Directory]::Delete($stage, $true)
            }
        }
        catch { }
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
