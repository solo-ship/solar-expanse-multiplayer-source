# Installing without BepInEx

## Can it install with no loader at all?

**No.** Solar Expanse has no mod support of its own: the game code never loads outside assemblies (no
`Assembly.Load*`, no Workshop/UGC code; checked in the decompiled game, 2026-10-09). Something has to put our code
into the game at start-up. The options:

| Option | What it is | Verdict |
|---|---|---|
| **Unity Doorstop** | A 26 KB `winhttp.dll` placed next to the game exe. Windows loads it with the game; it starts our DLL inside the game's Mono runtime before anything else. BepInEx itself is built on it. LGPL-2.1, may be redistributed. | **Use this.** Smallest possible footprint: two files plus our folder. Steam's "verify files" leaves it alone. |
| Patch `Assembly-CSharp.dll` on disk | An installer rewrites the game's code file to call our DLL. | No. Every game update or "verify files" undoes it, and it changes the game's own files. |
| BepInEx | The full mod framework. | Supported when it's already installed (our DLL also works as a BepInEx plugin), never required. |

So the vanilla install is: **game folder + `winhttp.dll` + `doorstop_config.ini` + `SolarExpanseMultiplayer\`**.
Uninstall = delete those.

## How it works

- `doorstop_config.ini` points Doorstop at `SolarExpanseMultiplayer\SolarExpanseMultiplayer.dll`.
- Doorstop calls `Doorstop.Entrypoint.Start()` in our DLL very early, before Unity and the game are ready. It only
  waits: it watches for the game's code (`Assembly-CSharp`) to load, then applies our Harmony patches, and creates
  the mod's object once the main menu exists.
- Harmony (MIT) ships in our folder for the vanilla install; under BepInEx we use BepInEx's copy.
- **With BepInEx present** (it already uses Doorstop's `winhttp.dll`), the installer doesn't touch Doorstop: it puts
  our folder in `BepInEx\plugins\`, where the same DLL starts through its BepInEx plugin class. The mod notices which
  way it was started and never runs twice.

## For players

Goal: one download, one step.

- **Installer** (`install.cmd` in the download, from `tools\release-kit\`): finds the game through Steam's library
  list, detects BepInEx, copies the right files, and `uninstall.cmd` puts things back. No admin rights needed when the
  game is in a normal Steam library. The step-by-step player instructions are `docs\release\INSTALL.txt`.
- **Manual**: copy the `vanilla` payload into the game folder, or the `bepinex` payload into `BepInEx\plugins\`.
- **Steam must be running (and signed in, online) for play over the internet.** The game starts it for you if you
  launch from Steam. If it is closed, offline or Steam's network can't be reached, the Multiplayer screen says so, and
  playing on the same home network (the direct join code) still works without Steam.
- **Joining over the internet: the Steam join code.** The host gets two join codes for the same one-time invite: the
  home-network one and a Steam join code. The Steam join code holds the host's Steam profile id and the one-time
  secret, never an IP address; the joiner pastes it and the game connects through Steam's relay, so nobody opens a
  port and neither player sees the other's address. Either code works once; send it privately (it is a secret), and
  the host gets a fresh pair after each join.
- **Fallback when Steam is down: Tailscale.** If Steam's network is unavailable, both players can join the same
  Tailscale network (a private network you set up yourselves) and use the home-network join code over it: the host
  listens on its Tailscale address (100.64.x.x to 100.127.x.x, or fd7a:115c:a1e0::/48), which the mod treats like a
  home-network address. Public addresses stay refused, and the join code shows your Tailscale address to whoever holds
  it. For now the address can only be set with the developer test flag `-semp-bind` (test copies only); the host
  screens will offer it later.

## Playing together (what has to match)

A join checks everything below before any game data is sent, and a mismatch is
refused with a plain list of what differs.

- **The same mod version** on every machine.
- **The same game build, and a supported one.** The mod supports only the current **beta** build, `0.26.10.9.13 BETA`
  (Steam branch `experimental`, build 25829824). The stable (public) branch is not supported for now, so players on
  it can use the game as normal but cannot host or join. On any other build the session does not start and the mod's
  status line says "This game version isn't supported by this version of the mod yet." After a game update, hosting
  waits for a mod update that supports the new build.
- **The same code mods.** Any difference in the mods that run code (BepInEx plugins, other loaded assemblies, a changed
  file in the game's `Managed` folder) blocks the join. Our own loader files are left out of that comparison.
- **Doorstop and BepInEx players can play together**, with one condition: **BepInEx must be 5.4.23.5.** The loader files
  the comparison leaves out are pinned to that version's core files. With 5.4.23.4 the BepInEx player's core files do
  not match the pinned ones, so a join against a Doorstop player is refused. That is safe but confusing (the report
  lists BepInEx's own files as differences); update BepInEx to 5.4.23.5 and try again. Two BepInEx players, or two
  Doorstop players, are unaffected.
