[CmdletBinding()]
param([switch]$SkipFormat)

$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    dotnet build -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test -c Release --no-build --logger trx --results-directory TestResults
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if (-not $SkipFormat) {
        dotnet format --verify-no-changes --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Formatting verification failed.' }
    }
}
finally { Pop-Location }
