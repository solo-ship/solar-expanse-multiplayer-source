# Installs Solar Expanse Multiplayer (beta) into the Steam copy of Solar Expanse. Run it through install.cmd.
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-GameDir "<Solar Expanse folder>"] [-DryRun]
# It picks the right kind for you:
#   * BepInEx is installed (BepInEx\core\BepInEx.dll): our two DLLs go into BepInEx\plugins\SolarExpanseMultiplayer\, and
#     the Doorstop files (winhttp.dll, doorstop_config.ini) are NEVER touched, because BepInEx loads through them.
#   * No BepInEx: Unity Doorstop (winhttp.dll, doorstop_config.ini) plus SolarExpanseMultiplayer\ in the game folder. A
#     winhttp.dll or doorstop_config.ini that is not ours is never replaced: the install stops and says so.
# Anything it replaces is backed up first; uninstall.cmd puts everything back. A previous install of the other kind is
# taken out so the mod never loads twice. Nothing is written until every check has passed.
# Exit codes: 0 installed (or already), 2 refused (nothing changed), 3 failed (it says whether all was put back).
param(
    [string]$GameDir,
    [switch]$DryRun,
    [switch]$NoPrompt,
    [string]$StateDir    # for the self-test; normally %LOCALAPPDATA%\SolarExpanseMultiplayer-Installer
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$kit = $PSScriptRoot
$state = Get-StateDir $StateDir
function Say([string]$t) { Write-Host $t }
function Refuse([string]$t) { Say ''; Say "NOT INSTALLED: $t"; Say 'Nothing was changed.'; exit 2 }

Say '=== Solar Expanse Multiplayer: install ==='
if ($DryRun) { Say '(DRY RUN: checks only, nothing is written)' }

# ---- the game folder
if (-not $GameDir) {
    $dirs = @(Find-SteamGameDirs)
    if ($dirs.Count -eq 1) { $GameDir = $dirs[0]; Say "Found the game through Steam: $GameDir" }
    elseif ($dirs.Count -gt 1) {
        Say 'Steam has Solar Expanse in more than one library:'
        for ($i = 0; $i -lt $dirs.Count; $i++) { Say ("  {0}. {1}" -f ($i + 1), $dirs[$i]) }
        $n = 0
        $pick = if ($NoPrompt) { '' } else { Read-Host 'Type the number of the one to use' }
        if (-not [int]::TryParse($pick, [ref]$n) -or $n -lt 1 -or $n -gt $dirs.Count) { Refuse 'no game folder was chosen.' }
        $GameDir = $dirs[$n - 1]
    } else {
        Say 'Steam did not say where Solar Expanse is installed.'
        Say 'In Steam: right-click Solar Expanse > Manage > Browse local files, then copy the folder path from the address bar.'
        $GameDir = if ($NoPrompt) { '' } else { (Read-Host 'Paste the Solar Expanse folder here and press Enter').Trim().Trim('"') }
        if (-not $GameDir) { Refuse 'no game folder was given.' }
    }
}
$GameDir = [IO.Path]::GetFullPath($GameDir).TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $GameDir $script:ExeName))) { Refuse "$GameDir has no $($script:ExeName)." }
if (Test-GameRunning $GameDir) { Refuse 'Solar Expanse is running. Close the game first, then run install again.' }
Say "Game folder: $GameDir"

# ---- this download's own files must be whole
$manifest = Join-Path $kit 'payload-manifest.txt'
if (-not (Test-Path -LiteralPath $manifest)) { Refuse 'payload-manifest.txt is missing. Extract the whole zip again.' }
$listed = @{}
$version = '?'
foreach ($line in [IO.File]::ReadAllLines($manifest)) {
    if ($line -match '^# version (\S+)') { $version = $Matches[1] }
    if ($line -match '^([0-9a-f]{64})  (.+)$') { $listed[$Matches[2]] = $Matches[1] }
}
foreach ($name in $listed.Keys) {
    $f = Join-Path $kit $name
    if (-not (Test-Path -LiteralPath $f)) { Refuse "$name is missing from this download. Extract the whole zip again." }
    if ((Get-Sha256 $f) -ne $listed[$name]) { Refuse "$name is damaged. Download the zip and extract it again." }
}
foreach ($sub in @('vanilla', 'bepinex')) {
    foreach ($f in @(Get-ChildItem -LiteralPath (Join-Path $kit $sub) -Recurse -File)) {
        $rel = $f.FullName.Substring($kit.Length + 1).Replace('\', '/')
        if (-not $listed.ContainsKey($rel)) { Refuse "$rel is in this download but not in its manifest." }
    }
}

# ---- which kind
$bepDir = Join-Path $GameDir 'BepInEx'
$mode = 'doorstop'
if (Test-Path -LiteralPath (Join-Path $bepDir 'core\BepInEx.dll')) { $mode = 'bepinex' }
elseif (Test-Path -LiteralPath $bepDir) {
    Refuse 'the BepInEx folder is here but BepInEx\core\BepInEx.dll is not. Repair BepInEx (reinstall it), then run this again.'
}
if (Test-Path -LiteralPath (Join-Path $GameDir 'MelonLoader')) { Refuse 'MelonLoader is installed; this mod supports no loader but BepInEx.' }
if ($mode -eq 'bepinex') { Say 'BepInEx found: installing as a BepInEx plugin (the Doorstop files are left alone).' }
else { Say 'No BepInEx: installing with Unity Doorstop.' }

# ---- what goes where (payload paths are inside this download; targets are relative to the game folder)
$plan = @()
if ($mode -eq 'doorstop') { $root = Join-Path $kit 'vanilla'; $prefix = '' }
else { $root = Join-Path $kit 'bepinex'; $prefix = 'BepInEx\plugins\' }
foreach ($f in @(Get-ChildItem -LiteralPath $root -Recurse -File)) {
    $plan += @{ From = $f.FullName; To = $prefix + $f.FullName.Substring($root.Length + 1); Sha = (Get-Sha256 $f.FullName) }
}
if ($mode -eq 'bepinex' -and @($plan | Where-Object { $_.To -in @('winhttp.dll', 'doorstop_config.ini') }).Count -gt 0) {
    Refuse 'internal error: the BepInEx plan would write a Doorstop file.'
}

# ---- the earlier install
$record = Read-Record $state
if ($record -and -not ([string]$record.GameDir).Equals($GameDir, [StringComparison]::OrdinalIgnoreCase)) {
    Refuse "the mod was installed into $($record.GameDir). Run uninstall.cmd first."
}
$prior = @{}
if ($record -and $record.Mode -eq $mode) { foreach ($e in @($record.Files)) { $prior[$e.Path] = $e } }
$otherKind = [bool]($record -and $record.Mode -ne $mode)

# ---- Doorstop's files are replaced only when they are ours (recorded as ours, or byte for byte ours)
foreach ($p in $plan) {
    $dst = Join-Path $GameDir $p.To
    if (-not (Test-Path -LiteralPath $dst)) { continue }
    $now = Get-Sha256 $dst
    if ($now -eq $p.Sha) { continue }
    if ($p.To -in @('winhttp.dll', 'doorstop_config.ini')) {
        $ours = $prior.ContainsKey($p.To) -and $prior[$p.To].NewSha -eq $now
        if (-not $ours -and $otherKind) {
            $o = @($record.Files) | Where-Object { $_.Path -eq $p.To } | Select-Object -First 1
            $ours = ($o -and $o.NewSha -eq $now)
        }
        if (-not $ours) {
            Refuse ("$($p.To) is already in the game folder and is not from this mod. Another mod or loader uses it, so it " +
                    'is left alone. If you use BepInEx, install BepInEx properly first (this installer then uses it); ' +
                    'otherwise remove that file yourself and run this again.')
        }
    }
}
function Get-Kind($p) {
    $dst = Join-Path $GameDir $p.To
    if (-not (Test-Path -LiteralPath $dst)) { return 'created' }
    if ((Get-Sha256 $dst) -eq $p.Sha) { return 'same' }
    return 'replaced'
}
Say ''
Say 'The install will:'
if ($otherKind) { Say "  take out the earlier $($record.Mode) install first" }
foreach ($p in $plan) { Say ("  {0,-9} {1}" -f (Get-Kind $p), $p.To) }
if ($DryRun) { Say ''; Say 'DRY RUN: all checks passed; nothing was written.'; exit 0 }

# ---- take the other kind out
if ($otherKind) {
    $left = @(Invoke-Undo $GameDir $record $false)
    $old = Join-Path $state ('install-record.replaced-' + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.json')
    Move-Item -LiteralPath (Get-RecordPath $state) -Destination $old -Force
    foreach ($l in $left) { Say "  left in place: $l" }
    $record = $null
}
foreach ($p in $plan) { $p.Kind = Get-Kind $p }

# ---- backups, each checked (an update keeps the very first backup)
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$backup = if ($record) { [string]$record.BackupDir } else { Join-Path $state "backups\$stamp" }
[void][IO.Directory]::CreateDirectory((Join-Path $backup 'files'))
foreach ($p in $plan) {
    if ($prior.ContainsKey($p.To) -or $p.Kind -ne 'replaced') { continue }
    $src = Join-Path $GameDir $p.To
    $bak = Join-Path $backup ('files\' + $p.To)
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $bak))
    Copy-Item -LiteralPath $src -Destination $bak -Force
    if ((Get-Sha256 $bak) -ne (Get-Sha256 $src)) { Say "FAILED: the backup of $($p.To) did not verify. Nothing was changed."; exit 3 }
}

# ---- the record, before the first change
$newDirs = @()
if ($record -and $record.Dirs) { $newDirs = @($record.Dirs) }
foreach ($p in $plan) {
    $c = Split-Path -Parent (Join-Path $GameDir $p.To)
    $chain = @()
    while ($c.Length -gt $GameDir.Length -and -not (Test-Path -LiteralPath $c)) {
        $chain = @($c.Substring($GameDir.Length + 1)) + $chain
        $c = Split-Path -Parent $c
    }
    foreach ($d in $chain) { if ($newDirs -notcontains $d) { $newDirs += $d } }
}
$files = @()
foreach ($p in $plan) {
    if ($prior.ContainsKey($p.To)) {
        $e = $prior[$p.To]
        $files += [pscustomobject]@{ Path = $p.To; Action = $e.Action; OrigSha = $e.OrigSha; NewSha = $p.Sha }
    } elseif ($p.Kind -eq 'replaced') {
        $files += [pscustomobject]@{ Path = $p.To; Action = 'replaced'; OrigSha = (Get-Sha256 (Join-Path $GameDir $p.To)); NewSha = $p.Sha }
    } elseif ($p.Kind -eq 'created') {
        $files += [pscustomobject]@{ Path = $p.To; Action = 'created'; OrigSha = ''; NewSha = $p.Sha }
    }   # 'same' and not recorded: already ours byte for byte, not ours to delete
}
$record = [pscustomobject]@{
    State = 'installing'; Mode = $mode; Version = $version; GameDir = $GameDir; BackupDir = $backup
    Installed = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); Dirs = @($newDirs); Files = @($files)
}
Write-Record $state $record

# ---- install; on a failure, put back what this run did
try {
    foreach ($d in $newDirs) { [void][IO.Directory]::CreateDirectory((Join-Path $GameDir $d)) }
    foreach ($p in $plan) {
        if ($p.Kind -eq 'same') { continue }
        $dst = Join-Path $GameDir $p.To
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $dst))
        $tmp = "$dst.semp-tmp"
        Copy-Item -LiteralPath $p.From -Destination $tmp -Force
        if ((Get-Sha256 $tmp) -ne $p.Sha) { throw "$($p.To) did not verify before it was moved into place" }
        if (Test-Path -LiteralPath $dst) { [IO.File]::SetAttributes($dst, [IO.FileAttributes]::Normal) }
        Move-Item -LiteralPath $tmp -Destination $dst -Force
        if ((Get-Sha256 $dst) -ne $p.Sha) { throw "$($p.To) did not verify after it was moved into place" }
    }
} catch {
    Say "FAILED: $($_.Exception.Message). Putting back what was done..."
    foreach ($p in $plan) { Remove-Item -LiteralPath ((Join-Path $GameDir $p.To) + '.semp-tmp') -Force -ErrorAction SilentlyContinue }
    $left = @(Invoke-Undo $GameDir $record $false)
    if ($left.Count -eq 0) {
        Move-Item -LiteralPath (Get-RecordPath $state) -Destination (Join-Path $state "install-record.failed-$stamp.json") -Force
        Say 'Put back: the game folder is as it was before the install.'
    } else { Say 'Could not put everything back:'; $left | ForEach-Object { Say "  $_" }; Say 'Run uninstall.cmd to finish.' }
    exit 3
}
$record.State = 'installed'
Write-Record $state $record
Say ''
Say "INSTALLED ($mode, version $version). Start Solar Expanse from Steam as usual; the main menu then has a Multiplayer button."
Say 'To remove it, run uninstall.cmd. Everyone in a session needs the same mod version and game build (see README.txt).'
exit 0
