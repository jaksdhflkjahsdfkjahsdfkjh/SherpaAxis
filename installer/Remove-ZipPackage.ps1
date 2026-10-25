[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][int]$ApplicationPid,
    [Parameter(Mandatory)][datetime]$ApplicationStarted
)
$ErrorActionPreference = 'Stop'
$logDirectory = Join-Path $env:LOCALAPPDATA 'SherpaAxis\Logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
try {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $prefix = $rootPath + '\'
    if ((Get-Item -LiteralPath $rootPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked app folders cannot be removed.' }
    $manifestPath = Join-Path $rootPath 'uninstall-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1 -or $manifest.Files.Count -lt 1 -or $manifest.Files.Count -gt 2000) { throw 'Invalid ZIP uninstall manifest.' }
    function Assert-ContainedPath([string]$Target) {
        if (-not $Target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP file is outside the app folder.' }
        $current = $Target
        while ($current -and ($current -eq $rootPath -or $current.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
            if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'ZIP file path contains a link.' }
            $current = [IO.Path]::GetDirectoryName($current)
        }
    }
    $targets = @()
    $directories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.Files) {
        if ([string]::IsNullOrWhiteSpace($entry.Path) -or [IO.Path]::IsPathRooted($entry.Path) -or $entry.Path -match '(^|[\\/])\.\.?([\\/]|$)|:' -or $entry.Hash -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid ZIP file entry.' }
        $target = [IO.Path]::GetFullPath((Join-Path $rootPath $entry.Path))
        Assert-ContainedPath $target
        $targets += [pscustomobject]@{ Path = $target; Hash = $entry.Hash }
        $parent = [IO.Path]::GetDirectoryName($target)
        while ($parent -and $parent.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $null = $directories.Add($parent)
            $parent = [IO.Path]::GetDirectoryName($parent)
        }
    }
    $application = Get-Process -Id $ApplicationPid -ErrorAction SilentlyContinue
    if ($application -and [Math]::Abs(($application.StartTime.ToUniversalTime() - $ApplicationStarted.ToUniversalTime()).TotalSeconds) -lt 1) {
        if (-not $application.WaitForExit(60000)) { throw 'SherpaAxis is still running; app files were kept.' }
    }
    foreach ($entry in $targets) {
        $target = $entry.Path
        Assert-ContainedPath $target
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $stream = [IO.File]::OpenRead($target)
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $algorithm.Dispose() }
            if ($hash -eq $entry.Hash) { Remove-Item -LiteralPath $target }
        }
    }
    Remove-Item -LiteralPath $manifestPath
    $directories | Sort-Object Length -Descending | ForEach-Object {
        Assert-ContainedPath $_
        if ((Test-Path -LiteralPath $_ -PathType Container) -and -not ((Get-Item -LiteralPath $_).Attributes -band [IO.FileAttributes]::ReparsePoint) -and -not (Get-ChildItem -LiteralPath $_ -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $_
        }
    }
    if (-not (Get-ChildItem -LiteralPath $rootPath -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $rootPath }
    'ZIP cleanup completed. Modified files, extra files and user settings were preserved.' | Add-Content -LiteralPath (Join-Path $logDirectory 'zip-uninstall.log')
}
catch { $_ | Out-String | Add-Content -LiteralPath (Join-Path $logDirectory 'zip-uninstall.log'); exit 1 }
finally { Remove-Item -LiteralPath $PSCommandPath -ErrorAction SilentlyContinue }
