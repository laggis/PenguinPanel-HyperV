$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Write-Host '=== PenguinPanel Step 1 build ===' -ForegroundColor Cyan
dotnet restore
dotnet build -c Release
Write-Host ''
Write-Host 'Build completed. Test Create VPS from /vms as an admin.' -ForegroundColor Green
