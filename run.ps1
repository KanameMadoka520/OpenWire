#requires -version 5
<#
    OpenWire launcher.
    Builds the solution, starts the engine elevated (UAC prompt for ETW + firewall),
    then starts the GUI as the current user.

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

$svc = Join-Path $root "src/OpenWire.Service/bin/$config/net9.0-windows/OpenWire.Service.exe"
$app = Join-Path $root "src/OpenWire.App/bin/$config/net9.0-windows/OpenWire.App.exe"

if (-not (Test-Path $svc)) { throw "Service exe not found: $svc" }
if (-not (Test-Path $app)) { throw "App exe not found: $app" }

Write-Host "Starting engine (elevated)..." -ForegroundColor Cyan
Start-Process -FilePath $svc -Verb RunAs

Start-Sleep -Seconds 2
Write-Host "Starting app..." -ForegroundColor Cyan
Start-Process -FilePath $app

Write-Host "OpenWire is running." -ForegroundColor Green
