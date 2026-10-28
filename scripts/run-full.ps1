$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Join-Path $scriptDir "..")

Write-Host "Running Micro, Full, Budget, Ablation, and Adversarial Benchmark Suites..."
dotnet run --project src/NeuroCache.Cli -c Release -- benchmark run --suite micro
dotnet run --project src/NeuroCache.Cli -c Release -- benchmark run --suite full
dotnet run --project src/NeuroCache.Cli -c Release -- benchmark run --suite budget
dotnet run --project src/NeuroCache.Cli -c Release -- benchmark run --suite ablation
dotnet run --project src/NeuroCache.Cli -c Release -- benchmark run --suite adversarial
dotnet run --project src/NeuroCache.Cli -c Release -- report --format markdown
