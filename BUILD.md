# Building Solar Expanse Multiplayer

Everything below runs on Windows from a PowerShell or Command Prompt window opened in the repository folder.

## 1. What you need

- **Windows 10 or 11 (64-bit)** with Windows PowerShell 5.1 (built in).
- **Visual Studio Build Tools** with the C# compiler (the "MSBuild tools" / ".NET desktop build tools" workload):
  https://visualstudio.microsoft.com/downloads/ (under "Tools for Visual Studio"). `tools\build.ps1` uses
  `C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe`; if that is not
  there it takes the first `...\Roslyn\csc.exe` it finds under `C:\Program Files*\Microsoft Visual Studio`. The release
  was compiled with Roslyn `5.9.0-1.26357.3` (VS 18 Build Tools); print yours with `& <path to csc.exe> -version`.
- **Solar Expanse**, the supported build: `0.26.10.9.13 BETA` (Steam branch `experimental`, build 25829824). The build
  only reads its DLLs; it never writes into the game folder.
- Internet access once, for `tools\fetch-deps.ps1`.
- Optional: a game copy with BepInEx 5.4.23.5 installed (for the HarmonyX compile check below).

## 2. Third-party reference DLLs (into `lib\`)

```
powershell -ExecutionPolicy Bypass -File tools\fetch-deps.ps1
```

This downloads the pinned official packages into `lib\_downloads\`, checks each one's SHA-256 (it deletes a download
that does not match), and extracts:

| File | From | Official page |
|---|---|---|
| `lib\harmony\0Harmony.dll` | Harmony 2.4.2 (`lib/net472/0Harmony.dll` from the `Lib.Harmony` NuGet package) | https://www.nuget.org/packages/Lib.Harmony/2.4.2 , https://github.com/pardeike/Harmony/releases |
| `lib\bepinex\BepInEx.dll` | BepInEx 5.4.23.5 (`BepInEx_win_x64_5.4.23.5.zip`, `BepInEx/core/BepInEx.dll`) | https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5 |
| `lib\doorstop\winhttp.dll` | Unity Doorstop 4.5.0 (`doorstop_win_release_4.5.0.zip`, `x64/winhttp.dll`) | https://github.com/NeighTools/UnityDoorstop/releases/tag/v4.5.0 |
| `lib\licenses\*.txt` | the Harmony and Unity Doorstop licence texts | the two projects' GitHub repositories |

Harmony is the compile reference for the mod; `BepInEx.dll` is the compile reference for the small BepInEx plugin
(the shim); Doorstop and the licence texts are only needed to rebuild the release zip. Use exactly these versions: a
different `BepInEx.dll` or `0Harmony.dll` gives different output bytes. If you download by hand, put the files at the
paths above.

`lib\` is git-ignored: nothing in it is ever committed.

## 3. The game's DLLs

The build compiles against the game's own managed DLLs, read in place from
`<game folder>\Solar Expanse_Data\Managed\`. It passes **every** `*.dll` in that folder to the compiler as a reference
(157 files in the supported build; with `/nostdlib+`, so the game's own `mscorlib`, `System*` and `netstandard` are the
framework the mod is compiled against). `Assembly-CSharp.dll` must be there.

Tell the build where the game is, one of:

- `-GameRoot "<game folder>"` on the command line, e.g.
  `-GameRoot "C:\Program Files (x86)\Steam\steamapps\common\Solar Expanse"`;
- or the environment variable `SEMP_GAME_ROOT`;
- or nothing, when the game is in the default Steam library (`C:\Program Files (x86)\Steam\steamapps\common\Solar Expanse`).

If you would rather not point the build at your install, copy the whole `Managed` folder to
`lib\game\Solar Expanse_Data\Managed\` and pass `-GameRoot "$PWD\lib\game"` (inside `lib\`, so it is never committed).

## 4. Build

```
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -GameRoot "<game folder>"
```

Optional: `-BepInExRoot "<game folder with BepInEx 5.4.23.5>"` (or `SEMP_BEPINEX_ROOT`) also compiles the mod against
BepInEx's HarmonyX as a compatibility check (the output is thrown away; without it a warning says the check was
skipped).

What it does: it builds the mod DLL and the BepInEx shim in six passes. The shim's SHA-256 is compiled into the mod DLL
(in the generated, git-ignored `src\Core\Join\ShimPin.g.cs`), and the build fails if a second shim build does not hash
the same. Outputs, in `bin\`:

| File | What it is |
|---|---|
| `SolarExpanseMultiplayer.dll` | the mod |
| `SolarExpanseMultiplayer.BepInEx.dll` | the BepInEx plugin shim |

A good run prints `No tests\ folder here: the test build and run were skipped.` and ends with
`Build OK: <repository>\bin`. Compiler warnings (CS0649, CS0414) are expected.

The test suite is kept private: it is not part of this repository. `build.ps1` still has its switches for it
(`-NoTests`, `-TestFilter`), which do nothing here.

## 5. Compare with the release

```
powershell -Command "Get-FileHash bin\SolarExpanseMultiplayer.dll, bin\SolarExpanseMultiplayer.BepInEx.dll -Algorithm SHA256"
```

The 0.5.0-beta release holds:

| File | SHA-256 |
|---|---|
| `SolarExpanseMultiplayer.dll` | `aece35703a0f4ca079048dd400f3534bca316d2ae8111561cbcc06f547fe819a` |
| `SolarExpanseMultiplayer.BepInEx.dll` | `b76b23d68c8842579d618f77785ffef0ea8d4c26b89c3e6ec27064e8a9254ea7` |

`SHA256SUMS.txt` on the Nexus page lists these and every other file in the zip.

How likely a match is: the build is deterministic by design (`/deterministic+`, no PDB, no paths, dates or machine
names compiled in), and building this source in a different folder on the release machine gave exactly the hashes
above. Roslyn only promises identical output for identical inputs, though: the same compiler version, the same
reference DLLs (the supported game build, Harmony 2.4.2, BepInEx 5.4.23.5) and the same source bytes. A different
compiler version or game build can give a different hash without any change to the code; if your hash differs, check
those three first.

## 6. Rebuild the release zip

After a build (step 4):

```
powershell -ExecutionPolicy Bypass -File tools\release.ps1
```

It writes `dist\release\SEMP-0.5.0-beta.zip` and `dist\release\SHA256SUMS.txt` (both git-ignored). It refuses (exit 2)
when `bin\` is older than the sources, the version is not a beta, a third-party file is not the pinned one, or the zip
would hold anything outside its allowlist (game files, test files, personal paths, the developer test marker). The zip
holds the installer scripts from `tools\release-kit\`, the texts from `docs\release\`, the Doorstop and BepInEx payloads
and `payload-manifest.txt`. Entries are written in a fixed order with a fixed timestamp, so compare the per-file
hashes in `SHA256SUMS.txt` with the published ones. On the release machine this snapshot rebuilt a zip byte-identical
to the published one; on another machine the zip's own hash can still differ, because it also depends on the .NET
compression code that wrote it.
