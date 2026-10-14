# Downloads the pinned dependencies into lib\ and checks each against its SHA-256.
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$repo = Split-Path $PSScriptRoot -Parent
$lib  = Join-Path $repo "lib"
$dl   = Join-Path $lib "_downloads"
New-Item -ItemType Directory -Force $dl | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

$deps = @(
    @{ Name = "Harmony 2.4.2"; File = "lib.harmony.2.4.2.nupkg"
       Url = "https://api.nuget.org/v3-flatcontainer/lib.harmony/2.4.2/lib.harmony.2.4.2.nupkg"
       Sha256 = "d64592e53090464559fce48612c9ca7c8dc73113841376b7aa3455f46fc5d579"
       Take = @{ "lib/net472/0Harmony.dll" = "harmony\0Harmony.dll" } },
    @{ Name = "Unity Doorstop 4.5.0"; File = "doorstop_win_release_4.5.0.zip"
       Url = "https://github.com/NeighTools/UnityDoorstop/releases/download/v4.5.0/doorstop_win_release_4.5.0.zip"
       Sha256 = "7bb953e8d883c8bde76ced96f6d0e45660ad6e0151880d8ab5856bf4f532b147"
       Take = @{ "x64/winhttp.dll" = "doorstop\winhttp.dll" } },
    @{ Name = "BepInEx 5.4.23.5"; File = "BepInEx_win_x64_5.4.23.5.zip"
       Url = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"
       Sha256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4"
       Take = @{ "BepInEx/core/BepInEx.dll" = "bepinex\BepInEx.dll" } }
)

foreach ($d in $deps) {
    $file = Join-Path $dl $d.File
    if (-not (Test-Path $file)) {
        Write-Host "Downloading $($d.Name) ..."
        Invoke-WebRequest -Uri $d.Url -OutFile $file -UseBasicParsing
    }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $d.Sha256) { Remove-Item $file -Force; throw "$($d.Name): SHA-256 mismatch (got $hash). Download removed." }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($file)
    try {
        foreach ($entryName in $d.Take.Keys) {
            $entry = $zip.GetEntry($entryName)
            if (-not $entry) { throw "$($d.Name): $entryName not found in the package" }
            $out = Join-Path $lib $d.Take[$entryName]
            New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
        }
    } finally { $zip.Dispose() }
    Write-Host "OK  $($d.Name)"
}

# Licence texts shipped next to the bundled binaries.
$lic = Join-Path $lib "licenses"
New-Item -ItemType Directory -Force $lic | Out-Null
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/pardeike/Harmony/master/LICENSE" -OutFile (Join-Path $lic "Harmony-LICENSE.txt") -UseBasicParsing
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/NeighTools/UnityDoorstop/master/LICENSE" -OutFile (Join-Path $lic "UnityDoorstop-LICENSE.txt") -UseBasicParsing
Write-Host "Dependencies ready in $lib"
