# Setup script for NeuroCache.ResearchLab
Write-Host "Setting up NeuroCache.ResearchLab solution..."
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Join-Path $scriptDir "..")

dotnet restore
dotnet build -c Release
Write-Host "Setup completed successfully."
