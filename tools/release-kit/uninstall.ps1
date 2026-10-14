# Removes Solar Expanse Multiplayer and puts back what the install replaced (from its backups). Run it through uninstall.cmd.
#   powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-RemoveData]
# A file that changed since the install (an update, another mod) is left alone and listed. The mod's own saved data
# and logs (host certificate, rejoin keys) are kept unless you add -RemoveData.
# Exit codes: 0 removed (or nothing to remove), 2 refused, 3 some things were left.
param([switch]$RemoveData, [string]$StateDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$state = Get-StateDir $StateDir
$record = Read-Record $state
if (-not $record) { Write-Host 'Nothing to remove: this installer has no install on record.'; exit 0 }
$game = [string]$record.GameDir
if (Test-GameRunning $game) { Write-Host 'Solar Expanse is running. Close the game first, then run uninstall again.'; exit 2 }
$left = @(Invoke-Undo $game $record ([bool]$RemoveData))
$done = Join-Path $state ('install-record.uninstalled-' + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.json')
Move-Item -LiteralPath (Get-RecordPath $state) -Destination $done -Force
if ($left.Count -eq 0) { Write-Host 'REMOVED: the game folder is as it was before the install.'; exit 0 }
Write-Host 'REMOVED, except for:'
$left | ForEach-Object { Write-Host "  $_" }
exit 3
