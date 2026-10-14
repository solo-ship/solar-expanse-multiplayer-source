# Solar Expanse Multiplayer

A multiplayer mod for [Solar Expanse](https://store.steampowered.com/app/1369700/Solar_Expanse/): up to five people
play the same game at the same time, each running their own space agency. One player hosts; everyone else plays in
their own copy of the game, kept in step by the host. Agencies nobody picks play as AI rivals.

**Status: public BETA (0.5.0-beta).** Expect bugs. It supports only one game build: Solar Expanse
`0.26.10.9.13 BETA` (Steam branch `experimental`, build 25829824).

- Download and player instructions: Nexus Mods page: `<NEXUS PAGE LINK>`
- Bug reports and questions: `<NEXUS POSTS / BUGS LINK>`

## What this repository is

The source code of the 0.5.0-beta release, published so the mod can be reviewed. It is a snapshot of the
release build: the mod DLL built from this source with the steps in [BUILD.md](BUILD.md) should have the same SHA-256
as the DLL in the release download (BUILD.md says how to check, and what has to match for that).

| Folder | What it holds |
|---|---|
| `src/Core`, `src/Net` | Game-independent code: the session, join, clock, commands, network and security rules. |
| `src/Game` | The parts that touch the game (Harmony patches, UI, reading and applying game state). |
| `src/Loader`, `src/BepInExShim` | Start-up: through Unity Doorstop (no BepInEx needed), or as a BepInEx plugin. |
| `tools/` | The build (`build.ps1`), dependency download (`fetch-deps.ps1`) and release packaging (`release.ps1`, `release-kit/`: the installer the zip carries). |
| `docs/` | [Security model](docs/SECURITY.md), [how the loader works](docs/INSTALL.md), and the release texts (`docs/release/`). |

The test suite is kept private: it is not part of this repository.

No file of Solar Expanse is included: no game DLLs, decompiled code, saves or assets. The build reads the game's DLLs
from your own install. Third-party DLLs (Harmony, Unity Doorstop, BepInEx) are not in the repository either;
`tools\fetch-deps.ps1` downloads the pinned official releases and checks their SHA-256.

## Security

Multiplayer means your game accepts data from another computer. [docs/SECURITY.md](docs/SECURITY.md) describes what the
mod protects against and how: encrypted connections pinned to the host's key, one-time join secrets, strict size and
type limits on everything received (including save data), loopback-only test listeners, and no telemetry. Only play
with people you trust.

## Licence

Free to use with Solar Expanse; no redistribution, edits or re-uploads without the author's permission. See
[LICENSE](LICENSE). Third-party parts keep their own licences (Harmony: MIT; Unity Doorstop: LGPL-2.1; see
`docs/release/LICENSES.txt`).

Solar Expanse Multiplayer is not made by or endorsed by the makers of Solar Expanse. It was built with the assistance
of AI coding tools.
