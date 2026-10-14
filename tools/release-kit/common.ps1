# Helpers shared by install.ps1 and uninstall.ps1 (Solar Expanse Multiplayer beta installer). Plain ASCII, PowerShell 5.1.
$script:AppId = '1369700'
$script:ExeName = 'Solar Expanse.exe'

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Get-StateDir([string]$override) {
    if ($override) { $d = $override } else { $d = Join-Path $env:LOCALAPPDATA 'SolarExpanseMultiplayer-Installer' }
    [void][IO.Directory]::CreateDirectory($d)
    return $d
}
function Get-RecordPath([string]$state) { return (Join-Path $state 'install-record.json') }
function Read-Record([string]$state) {
    $p = Get-RecordPath $state
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    return ([IO.File]::ReadAllText($p) | ConvertFrom-Json)
}
function Write-Record([string]$state, $record) {
    $p = Get-RecordPath $state
    $tmp = "$p.tmp"
    [IO.File]::WriteAllText($tmp, ($record | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $p -Force
}

# Every Steam library that holds Solar Expanse (Steam's own registry key and libraryfolders.vdf, read only).
function Find-SteamGameDirs {
    $found = @()
    $steam = $null
    if ($env:SEMP_INSTALLER_STEAMPATH) { $steam = $env:SEMP_INSTALLER_STEAMPATH }   # the self-test stands in for the registry
    else {
        try { $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath }
        catch { return $found }
    }
    if (-not $steam) { return $found }
    $steam = $steam.Replace('/', '\')
    $libs = @()
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        foreach ($m in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) {
            $libs += $m.Groups[1].Value.Replace('\\', '\')
        }
    }
    $libs += $steam
    $seen = @{}
    foreach ($lib in $libs) {
        $key = $lib.TrimEnd('\').ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $acf = Join-Path $lib "steamapps\appmanifest_$($script:AppId).acf"
        if (-not (Test-Path -LiteralPath $acf)) { continue }
        $name = 'Solar Expanse'
        $m = [regex]::Match([IO.File]::ReadAllText($acf), '"installdir"\s+"([^"]+)"')
        if ($m.Success) { $name = $m.Groups[1].Value }
        $dir = Join-Path $lib ('steamapps\common\' + $name)
        if (Test-Path -LiteralPath (Join-Path $dir $script:ExeName)) { $found += $dir }
    }
    return $found
}
function Test-GameRunning([string]$gameDir) {
    $prefix = [IO.Path]::GetFullPath($gameDir).TrimEnd('\') + '\'
    foreach ($p in @(Get-Process -Name 'Solar Expanse' -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { }
        if ($path -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# Takes back what a record lists: a file we created is deleted, a file we replaced is restored from its backup, both only
# while the file is still exactly what we wrote (anything else changed it since: it is left alone and reported).
# Folders we created are removed when empty. Returns the list of things left behind.
function Invoke-Undo([string]$gameDir, $record, [bool]$removeData) {
    $left = @()
    $entries = @($record.Files)
    [array]::Reverse($entries)
    foreach ($e in $entries) {
        $p = Join-Path $gameDir $e.Path
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $now = Get-Sha256 $p
        if ($now -ne $e.NewSha) { $left += "$($e.Path) (changed since the install, left alone)"; continue }
        [IO.File]::SetAttributes($p, [IO.FileAttributes]::Normal)
        if ($e.Action -eq 'replaced') {
            $bak = Join-Path $record.BackupDir ('files\' + $e.Path)
            if ((Test-Path -LiteralPath $bak) -and (Get-Sha256 $bak) -eq $e.OrigSha) {
                Copy-Item -LiteralPath $bak -Destination $p -Force
            } else { $left += "$($e.Path) (its backup is missing or damaged, so the file was left as installed)" }
        } else { Remove-Item -LiteralPath $p -Force }
    }
    $dirs = @($record.Dirs)
    [array]::Reverse($dirs)
    foreach ($d in $dirs) {
        $full = Join-Path $gameDir $d
        if (-not (Test-Path -LiteralPath $full)) { continue }
        if ($removeData) {
            foreach ($sub in @('data', 'logs')) {
                $s = Join-Path $full $sub
                if (Test-Path -LiteralPath $s) { Remove-Item -LiteralPath $s -Recurse -Force }
            }
        }
        if (@(Get-ChildItem -LiteralPath $full -Force).Count -eq 0) { Remove-Item -LiteralPath $full -Force }
        else { $left += "$d (not empty: the mod's own saved data or logs, or files that are not ours)" }
    }
    return $left
}
