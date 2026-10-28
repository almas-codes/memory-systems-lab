$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Join-Path $scriptDir "..")

Write-Host "Running Smoke Benchmark Suite..."
dotnet run --project src/NeuroCache.Cli -c Release -- benchmark run --suite smoke
