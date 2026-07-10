#requires -version 5
<#
    OpenWire launcher.
    Builds the solution and starts the GUI as the current user. The GUI launches the
    engine with a parent-PID binding and raises the UAC prompt when needed.

    Usage:
      ./run.ps1            # Debug build
      ./run.ps1 -Release   # Release build
#>
param(
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$config = if ($Release) { 'Release' } else { 'Debug' }
$root = $PSScriptRoot

Write-Host "Building OpenWire ($config)..." -ForegroundColor Cyan
dotnet build "$root/OpenWire.sln" -c $config -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$app = Join-Path $root "src/OpenWire.App/bin/$config/net9.0-windows/OpenWire.App.exe"

if (-not (Test-Path $app)) { throw "App exe not found: $app" }

Write-Host "Starting app (the engine UAC prompt follows if needed)..." -ForegroundColor Cyan
Start-Process -FilePath $app

Write-Host "OpenWire is running." -ForegroundColor Green
