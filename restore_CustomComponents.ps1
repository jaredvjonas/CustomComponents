Param(
    [string]$Source = 'C:\Users\jared\Downloads\BattleTech\RogueTech-master\Core\CustomComponents',
    [string]$BattleTechGameDir = $env:BattleTechGameDir
)

if (-not $BattleTechGameDir) {
    Write-Host "BattleTechGameDir environment variable is not set."
    $BattleTechGameDir = Read-Host "Enter BattleTech game directory (e.g. C:\Games\BattleTech\BATTLETECH)"
}

$Dst = Join-Path $BattleTechGameDir 'mods\CustomComponents'

Write-Host "Source: $Source"
Write-Host "Destination: $Dst"

Write-Host "Removing existing destination (if present)..."
Remove-Item -Recurse -Force $Dst -ErrorAction SilentlyContinue

Write-Host "Copying files..."
Copy-Item -Path $Source -Destination $Dst -Recurse -Force

Write-Host "Verifying..."
if (Test-Path $Dst) {
    Write-Host "Restore complete. Showing first 10 entries in destination:"
    Get-ChildItem -Path $Dst -Recurse | Select-Object -First 10
    exit 0
} else {
    Write-Host "Restore failed: destination not found."
    exit 1
}