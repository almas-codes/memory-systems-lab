$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Join-Path $scriptDir "..")

dotnet run --project src/NeuroCache.Cli -c Release -- compare --latest
