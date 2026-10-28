param(
    [long]$Records = 100000000,
    [string]$Shape = "small",
    [string]$Path = "data"
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Join-Path $scriptDir "..")

Write-Host "Executing Dataset Generation: $Records records (Shape: $Shape) into $Path..."
dotnet run --project src/NeuroCache.Cli -c Release -- dataset generate --records $Records --shape $Shape --path $Path
