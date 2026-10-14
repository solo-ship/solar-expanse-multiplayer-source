# Builds the Nexus BETA download (ONE zip) from the CURRENT bin\ build, into the git-ignored dist\release\.
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 [-OutDir <folder>]
# The zip works with or without BepInEx: it holds both payloads in sub-folders and an installer that picks one.
#   install.cmd / install.ps1, uninstall.cmd / uninstall.ps1, common.ps1   (from tools\release-kit\)
#   vanilla\  winhttp.dll + doorstop_config.ini (Unity Doorstop) + SolarExpanseMultiplayer\ (our DLL, Harmony, licences)
#   bepinex\  SolarExpanseMultiplayer\ (our DLL + the BepInEx shim)
#   README.txt, INSTALL.txt, LICENSES.txt (from docs\release\), payload-manifest.txt (SHA-256 of every payload file)
# The zip holds exactly the allowlist below and nothing else (DECISIONS.md "Release on Nexus Mods"): our DLLs, Doorstop and
# Harmony with their licences (each pinned by SHA-256 to the file tools\fetch-deps.ps1 extracts), the installer and the
# texts. Never game files, decompiled code, test profiles, tests, docs, saves, keys or anything personal.
# It REFUSES (exit 2, nothing written or the finished zip deleted) when:
#   - the shown version (src\Core\ModInfo.cs and the built DLL) does not carry "beta", or the build says "dev";
#   - bin\ is older than the sources (run tools\build.ps1 first);
#   - a third-party file is not the pinned one, or a source file is missing;
#   - the default doorstop_config.ini turns on anything for developers (a debugger, a loader switch), or any shipped text,
#     script or DLL mentions a "-semp-" test flag or holds a personal path;
#   - the zip would hold a file that is not on the allowlist, a game file (Assembly-CSharp, UnityEngine, ...) or a
#     secret-looking name.
# Then it writes SHA256SUMS.txt (the zip and every file inside, for the Nexus page) and prints the same.
# Exit codes: 0 built, 2 refused.
param(
    [string]$OutDir = '',
    # The rest is for tools\test-release.ps1 only (SEMP_RELEASE_SELFTEST=1): other inputs, and planted faults.
    [Parameter(DontShow)][string]$BinDir,
    [Parameter(DontShow)][string]$LibDir,
    [Parameter(DontShow)][string]$ConfigFile,
    [Parameter(DontShow)][string]$TextDir,
    [Parameter(DontShow)][string]$ModInfoFile,
    [Parameter(DontShow)][string]$Plant     # extra | gamedll | devflag | textflag | marker
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = Split-Path -Parent $PSScriptRoot
$selfTest = ($env:SEMP_RELEASE_SELFTEST -ceq '1')
if (-not $selfTest) { $BinDir = ''; $LibDir = ''; $ConfigFile = ''; $TextDir = ''; $ModInfoFile = ''; $Plant = '' }
if (-not $BinDir) { $BinDir = Join-Path $repo 'bin' }
if (-not $LibDir) { $LibDir = Join-Path $repo 'lib' }
if (-not $ConfigFile) { $ConfigFile = Join-Path $repo 'dist\doorstop_config.ini' }
if (-not $TextDir) { $TextDir = Join-Path $repo 'docs\release' }
if (-not $ModInfoFile) { $ModInfoFile = Join-Path $repo 'src\Core\ModInfo.cs' }
$kitDir = Join-Path $repo 'tools\release-kit'
$defaultOut = Join-Path $repo 'dist\release'
if (-not $OutDir) { $OutDir = $defaultOut }
function Refuse([string]$t) { Write-Host "REFUSED: $t"; Write-Host 'No release was written.'; exit 2 }

# The third-party files, pinned (the SHA-256 of what tools\fetch-deps.ps1 extracts from the checked packages).
$pinned = @{
    '0Harmony.dll'              = '7b9e756306fa3d7620e02a857c8927a6ab04973f9bd8a77d3866700a6deac55c'   # Harmony 2.4.2 (MIT), net472
    'winhttp.dll'               = '8c6cdbc38836dee87e3368f5de1994d7c0ccebf29e4ce7aba3c0981f9375412c'   # Unity Doorstop 4.5.0 (LGPL-2.1), x64
    'Harmony-LICENSE.txt'       = 'a5883d0c45eb7e176d3d1631220e7f96e958e37064773d40245ae6db560bbd6d'
    'UnityDoorstop-LICENSE.txt' = '20c17d8b8c48a600800dfd14f95d5cb9ff47066a9641ddeab48dc54aec96e331'
}

# ---- the version: "beta" in the source and in the built DLL, never "dev"
$modInfo = [IO.File]::ReadAllText($ModInfoFile)
if ($modInfo -notmatch 'Number\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"') { Refuse "$ModInfoFile has no Number." }
$number = $Matches[1]
if ($modInfo -notmatch 'public\s+const\s+bool\s+Beta\s*=\s*true\s*;') {
    Refuse 'this is not a beta build: ModInfo.Beta is not true (the shown version would be a "-dev" one).'
}
if ($modInfo -notmatch 'Version\s*=\s*Number\s*\+\s*\(\s*Beta\s*\?\s*"-beta"\s*:\s*"-dev"\s*\)') {
    Refuse 'ModInfo.Version is not Number + (Beta ? "-beta" : "-dev"); release.ps1 cannot tell what version this build shows.'
}
$version = "$number-beta"

# ---- the files, all there
$mainDll = Join-Path $BinDir 'SolarExpanseMultiplayer.dll'
$shimDll = Join-Path $BinDir 'SolarExpanseMultiplayer.BepInEx.dll'
$srcFiles = [ordered]@{
    'main'         = $mainDll
    'shim'         = $shimDll
    'harmony'      = Join-Path $LibDir 'harmony\0Harmony.dll'
    'doorstop'     = Join-Path $LibDir 'doorstop\winhttp.dll'
    'harmony-lic'  = Join-Path $LibDir 'licenses\Harmony-LICENSE.txt'
    'doorstop-lic' = Join-Path $LibDir 'licenses\UnityDoorstop-LICENSE.txt'
    'config'       = $ConfigFile
    'readme'       = Join-Path $TextDir 'README.txt'
    'install-txt'  = Join-Path $TextDir 'INSTALL.txt'
    'licenses-txt' = Join-Path $TextDir 'LICENSES.txt'
}
$kitNames = @('install.cmd', 'install.ps1', 'uninstall.cmd', 'uninstall.ps1', 'common.ps1')
foreach ($n in $kitNames) { $srcFiles["kit-$n"] = Join-Path $kitDir $n }
foreach ($k in $srcFiles.Keys) { if (-not (Test-Path -LiteralPath $srcFiles[$k] -PathType Leaf)) { Refuse "missing $($srcFiles[$k])" } }
$extraKit = @(Get-ChildItem -LiteralPath $kitDir -Force | Where-Object { $kitNames -notcontains $_.Name })
if ($extraKit.Count -gt 0) { Refuse ("tools\release-kit holds something not on the list: " + (($extraKit | ForEach-Object { $_.Name }) -join ', ')) }
function Get-Sha([byte[]]$b) {
    $s = [Security.Cryptography.SHA256]::Create()
    try { return (-join ($s.ComputeHash($b) | ForEach-Object { $_.ToString('x2') })) } finally { $s.Dispose() }
}
foreach ($pair in @(@('harmony', '0Harmony.dll'), @('doorstop', 'winhttp.dll'), @('harmony-lic', 'Harmony-LICENSE.txt'),
                    @('doorstop-lic', 'UnityDoorstop-LICENSE.txt'))) {
    $got = Get-Sha ([IO.File]::ReadAllBytes($srcFiles[$pair[0]]))
    if ($got -ne $pinned[$pair[1]]) {
        Refuse "$($pair[1]) is not the pinned third-party file (SHA-256 $got); run tools\fetch-deps.ps1."
    }
}

# ---- bin\ is current and says "beta"
if (-not $selfTest) {
    $newest = Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter *.cs -Recurse -File |
              Where-Object { $_.Name -notlike '*.g.cs' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    foreach ($b in @($mainDll, $shimDll)) {
        if ((Get-Item -LiteralPath $b).LastWriteTimeUtc -lt $newest.LastWriteTimeUtc) {
            Refuse "$b is older than $($newest.FullName): run tools\build.ps1 first."
        }
    }
}
function Test-Contains([byte[]]$hay, [string]$text) {
    foreach ($enc in @([Text.Encoding]::Unicode, [Text.Encoding]::ASCII)) {   # .NET string literals are UTF-16
        $n = $enc.GetBytes($text)
        for ($i = 0; $i -le $hay.Length - $n.Length; $i++) {
            if ($hay[$i] -ne $n[0]) { continue }
            $j = 1
            while ($j -lt $n.Length -and $hay[$i + $j] -eq $n[$j]) { $j++ }
            if ($j -eq $n.Length) { return $true }
        }
    }
    return $false
}
$mainBytes = [IO.File]::ReadAllBytes($mainDll)
if (-not (Test-Contains $mainBytes $version)) {
    Refuse "SolarExpanseMultiplayer.dll does not show the version ${version}: it is an older or a dev build. Run tools\build.ps1."
}
if (Test-Contains $mainBytes "$number-dev") { Refuse "SolarExpanseMultiplayer.dll shows the dev version $number-dev." }

# ---- no personal paths in our DLLs (they are built without a PDB)
$personalRx = '(?i)([a-z]:\\(users|steamlibrary|se-)|mods-source|\.superpowers)'
foreach ($d in @($mainDll, $shimDll)) {
    $bytes = [IO.File]::ReadAllBytes($d)
    foreach ($text in @([Text.Encoding]::ASCII.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes),
                        [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 2))) {
        if ($text -match $personalRx) { Refuse "$(Split-Path -Leaf $d) holds a personal or source path ($($Matches[1]))." }
    }
}

# ---- the texts and scripts: placeholders filled, plain ASCII, CRLF
$gameBuild = '?'
$kbText = [IO.File]::ReadAllText((Join-Path $repo 'src\Core\KnownBuilds.cs'))
$kbText = [regex]::Replace($kbText, '(?m)^\s*//.*$', '')
$kbm = [regex]::Match($kbText, 'new\s+KnownBuild\s*\(\s*"[^"]*"\s*,\s*"[^"]*"\s*,\s*"[^"]*"\s*,\s*"([^"]*)"\s*,\s*"([^"]*)"\s*,\s*"([^"]*)"')
if (-not $kbm.Success) { Refuse 'src\Core\KnownBuilds.cs could not be read.' }
$gameBuild = "$($kbm.Groups[1].Value) (Steam branch '$($kbm.Groups[2].Value)', build $($kbm.Groups[3].Value))"
function Get-TextBytes([string]$path) {
    $t = [IO.File]::ReadAllText($path)
    $t = $t.Replace('{VERSION}', $version).Replace('{GAMEBUILD}', $gameBuild)
    if ($t -match '\{[A-Z]+\}') { Refuse "$(Split-Path -Leaf $path) has an unfilled placeholder $($Matches[0])." }
    foreach ($ch in $t.ToCharArray()) {
        if ([int]$ch -gt 126 -or ([int]$ch -lt 32 -and $ch -ne "`r" -and $ch -ne "`n" -and $ch -ne "`t")) {
            Refuse "$(Split-Path -Leaf $path) is not plain ASCII (character $([int]$ch))."
        }
    }
    return [Text.Encoding]::ASCII.GetBytes(($t -replace "`r?`n", "`r`n"))
}

# ---- the zip's contents: path -> bytes, built only from the allowlist
$cfgBytes = [IO.File]::ReadAllBytes($ConfigFile)
if ($Plant -ceq 'devflag') {
    $cfgBytes = [Text.Encoding]::ASCII.GetBytes(([Text.Encoding]::ASCII.GetString($cfgBytes) -replace 'debug_enabled=false', 'debug_enabled=true'))
}
$readmeBytes = Get-TextBytes $srcFiles['readme']
if ($Plant -ceq 'textflag') { $readmeBytes = [Text.Encoding]::ASCII.GetBytes("Start the game with -semp-host to host.`r`n") }
$payload = [ordered]@{
    'vanilla/winhttp.dll'                                        = [IO.File]::ReadAllBytes($srcFiles['doorstop'])
    'vanilla/doorstop_config.ini'                                = $cfgBytes
    'vanilla/SolarExpanseMultiplayer/SolarExpanseMultiplayer.dll' = $mainBytes
    'vanilla/SolarExpanseMultiplayer/0Harmony.dll'                = [IO.File]::ReadAllBytes($srcFiles['harmony'])
    'vanilla/SolarExpanseMultiplayer/Harmony-LICENSE.txt'         = [IO.File]::ReadAllBytes($srcFiles['harmony-lic'])
    'vanilla/SolarExpanseMultiplayer/UnityDoorstop-LICENSE.txt'   = [IO.File]::ReadAllBytes($srcFiles['doorstop-lic'])
    'bepinex/SolarExpanseMultiplayer/SolarExpanseMultiplayer.dll'        = $mainBytes
    'bepinex/SolarExpanseMultiplayer/SolarExpanseMultiplayer.BepInEx.dll' = [IO.File]::ReadAllBytes($shimDll)
}
$content = [ordered]@{}
foreach ($n in $kitNames) { $content[$n] = Get-TextBytes $srcFiles["kit-$n"] }
$content['README.txt'] = $readmeBytes
$content['INSTALL.txt'] = Get-TextBytes $srcFiles['install-txt']
$content['LICENSES.txt'] = Get-TextBytes $srcFiles['licenses-txt']
foreach ($k in $payload.Keys) { $content[$k] = $payload[$k] }
$manifest = @("# Solar Expanse Multiplayer payload manifest", "# version $version",
              '# SHA-256 of every payload file; install.ps1 refuses a download that does not match.')
foreach ($k in $payload.Keys) { $manifest += ((Get-Sha $payload[$k]) + '  ' + $k) }
$content['payload-manifest.txt'] = [Text.Encoding]::ASCII.GetBytes(($manifest -join "`r`n") + "`r`n")
# Planted faults of the self-check: the allowlist below must catch each.
if ($Plant -ceq 'extra') { $content['vanilla/SolarExpanseMultiplayer/notes-from-dev.txt'] = [Text.Encoding]::ASCII.GetBytes("x`r`n") }
if ($Plant -ceq 'gamedll') { $content['bepinex/SolarExpanseMultiplayer/Assembly-CSharp.dll'] = [Text.Encoding]::ASCII.GetBytes('MZ not really') }
if ($Plant -ceq 'marker') {
    $content['vanilla/SolarExpanseMultiplayer/semp-test-copy.marker'] = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'semp-test-copy.marker'))
}

# ---- the allowlist: exactly these names, and what the allowed files may hold
$allow = @($kitNames) + @('README.txt', 'INSTALL.txt', 'LICENSES.txt', 'payload-manifest.txt') + @($payload.Keys)
$allow = @($allow | Sort-Object)
$gameRx = '(?i)(^|/)(assembly-csharp|assembly-csharp-firstpass|unityengine[^/]*|mscorlib|system(\.[^/]*)?|mono\.[^/]*|boo\.[^/]*|unity\.[^/]*|gravityengine[^/]*|newtonsoft[^/]*|bepinex)\.dll$'
$secretRx = '(?i)(join-code|rejoin-key|\.p12|host-cert|\.semd|\.json\.gz|\.superpowers|evidence|(^|/)tests?/|(^|/)userdata/|(^|/)data/|(^|/)logs?/|\.pdb$|\.log$|decompiled|\.zip$|test-)'
function Test-Release($entries) {
    $problems = @()
    foreach ($p in $entries.Keys) {
        if ($allow -cnotcontains $p) {
            $why = 'not on the allowlist'
            if ($p -match '(?i)semp-test-copy\.marker$') { $why = 'the test-copy marker (it turns the -semp- test flags on)' }
            elseif ($p -match $gameRx) { $why = 'a game or engine file' }
            elseif ($p -match $secretRx) { $why = 'looks like a secret, a log, a test file or a save' }
            $problems += "$p ($why)"
        }
    }
    foreach ($a in $allow) { if (-not $entries.Contains($a)) { $problems += "$a is missing" } }
    if ($entries.Contains('vanilla/doorstop_config.ini')) {
        $cfg = [Text.Encoding]::ASCII.GetString($entries['vanilla/doorstop_config.ini'])
        $want = [ordered]@{ enabled = 'true'; target_assembly = 'SolarExpanseMultiplayer\SolarExpanseMultiplayer.dll';
            redirect_output_log = 'false'; boot_config_override = ''; ignore_disable_switch = 'false';
            dll_search_path_override = ''; debug_enabled = 'false'; debug_address = '127.0.0.1:10000'; debug_suspend = 'false';
            coreclr_path = ''; corlib_dir = '' }
        $seen = @{}
        foreach ($line in ($cfg -split "`r?`n")) {
            if ($line -match '^\s*(#|;|\[|$)') { continue }
            if ($line -notmatch '^([a-z_0-9]+)=(.*)$') { $problems += "doorstop_config.ini line not understood: $line"; continue }
            $seen[$Matches[1]] = $Matches[2].Trim()
        }
        foreach ($k in $seen.Keys) {
            if (-not $want.Contains($k)) { $problems += "doorstop_config.ini has an unknown key $k (a dev default?)" }
            elseif ($seen[$k] -cne $want[$k]) {
                $problems += "doorstop_config.ini $k=$($seen[$k]) is not the release default ($($want[$k]))"
            }
        }
        foreach ($k in $want.Keys) { if (-not $seen.ContainsKey($k)) { $problems += "doorstop_config.ini lacks $k" } }
    }
    foreach ($p in $entries.Keys) {
        if ($p -match '\.(txt|ini|ps1|cmd)$') {
            $t = [Text.Encoding]::ASCII.GetString($entries[$p])
            if ($t -match '(?i)-semp-[a-z]') { $problems += "$p mentions a -semp- test flag" }
            if ($t -match $personalRx) { $problems += "$p holds a personal path" }
        }
    }
    return $problems
}
$bad = @(Test-Release $content)
if ($bad.Count -gt 0) { Refuse ("the contents do not match the allowlist:`n  " + ($bad -join "`n  ")) }

# ---- the out folder: never inside a game folder or the source tree (except the git-ignored dist\release)
$OutDir = [IO.Path]::GetFullPath($OutDir).TrimEnd('\')
$isDefault = $OutDir.Equals($defaultOut, [StringComparison]::OrdinalIgnoreCase)
foreach ($n in (@($repo, $env:SEMP_GAME_ROOT, $env:SEMP_BEPINEX_ROOT) | Where-Object { $_ })) {
    if (-not $isDefault -and ($OutDir + '\').StartsWith($n.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Refuse "-OutDir lies inside $n (only the git-ignored dist\release\ is allowed in the repo)."
    }
}
[void][IO.Directory]::CreateDirectory($OutDir)
$zipName = "SEMP-$version.zip"
$path = Join-Path $OutDir $zipName
Get-ChildItem -LiteralPath $OutDir -Filter 'SEMP-*.zip' | Remove-Item -Force

# ---- write the zip (fixed timestamps and order, so the same inputs give the same zip)
$stamp = New-Object DateTimeOffset(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$fs = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
try {
    $zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($k in ($content.Keys | Sort-Object)) {
            $e = $zip.CreateEntry($k, [IO.Compression.CompressionLevel]::Optimal)
            $e.LastWriteTime = $stamp
            $s = $e.Open()
            try { $s.Write($content[$k], 0, $content[$k].Length) } finally { $s.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $fs.Dispose() }

# ---- check the finished zip against the allowlist again (from the file, not from memory), then the hashes
$entries = [ordered]@{}
$za = [IO.Compression.ZipFile]::OpenRead($path)
try {
    foreach ($e in $za.Entries) {
        $ms = New-Object IO.MemoryStream
        $s = $e.Open(); try { $s.CopyTo($ms) } finally { $s.Dispose() }
        $entries[$e.FullName] = $ms.ToArray()
    }
} finally { $za.Dispose() }
$again = @(Test-Release $entries)
if ($again.Count -gt 0) {
    Remove-Item -LiteralPath $path -Force
    Refuse ("the finished zip does not match the allowlist (deleted):`n  " + ($again -join "`n  "))
}
$zipSha = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
$sums = @("# SHA-256 for the Nexus page. Solar Expanse Multiplayer $version. The zip first, then every file inside it.", "$zipSha  $zipName")
$report = @("$zipName  ($([Math]::Round((Get-Item -LiteralPath $path).Length / 1KB)) KB)  SHA-256 $zipSha")
foreach ($k in $entries.Keys) {
    $h = Get-Sha $entries[$k]
    $report += ("  {0,10:N0}  {1}  {2}" -f $entries[$k].Length, $k, $h)
    $sums += "$h  $zipName!$k"
}
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), ($sums -join "`r`n") + "`r`n", [Text.Encoding]::ASCII)
Write-Host "RELEASE $version written to $OutDir"
$report | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host 'SHA256SUMS.txt written beside the zip. Nothing was uploaded: upload the zip on Nexus yourself.'
exit 0
