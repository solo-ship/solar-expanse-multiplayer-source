# Solar Expanse Multiplayer (BETA)

## Summary

Play Solar Expanse with friends: up to 5 players share one world, each running their own space agency. One player
hosts from the new Multiplayer menu and invites friends over Steam; the others join from the invite or with a code.

## BETA WARNING

**This is a beta. It will have bugs, and a session can break or get out of step.** 

## Features

- Up to 5 players in one shared world, one hosting.
- One shared clock: the same date, speed and pause for everyone (the slowest player sets the pace).
- Shared research, buildings and production: what any player does reaches everyone.
- Shared flights: launches and missions planned by any player fly in every game.
- Shared market, and the AI rivals run on the host and reach everyone.
- A Multiplayer button on the main menu and a lobby: host from a new game, your own save or a Multiplayer save; every
  player picks an agency and presses Ready; the host starts. Unpicked agencies are AI rivals. Start alone and let
  friends join later.
- **Steam invites:** invite friends from the lobby (any player can invite) or join with Join Game in the Steam friends
  list. "Who can join": Friends, Invite only or Code only. A Steam join code for players who are not your friends.
- Over the internet through Steam's relay (no IP addresses shared), or directly on a home network with a join code.
- The host can remove or ban a player (by Steam account) and unban later.
- Multiplayer saves: any player can save, everyone keeps a copy, and a save can be played alone.
- A clear "what differs" list when a join is refused (game build, mod version, other mods).

## Requirements

- Solar Expanse on Steam, on the supported build **only**: 0.26.10.9.13 BETA (Steam build 25829824), Steam's
  **experimental** branch. **Switch the game to `experimental` first** (Steam: right-click Solar Expanse, Properties,
  Betas). `open_beta`, the default branch and any other build are not supported: the mod will not start a session
  and says so.
- Windows 10 or 11 (Windows 11 tested). Linux, Steam Deck and Mac are untested.
- Everyone in a session needs the same mod version and the same game build.

## Install

One download works with or without BepInEx.

1. Switch Solar Expanse to Steam's `experimental` branch and let it update.
2. Extract the whole zip to your own folder (not the game folder).
3. Close the game and double-click `install.cmd`.
4. Start the game from Steam. The main menu has a Multiplayer button.

`install.cmd` finds your game and checks for BepInEx. With BepInEx 5.4.23.5 it installs the mod as a plugin and never
touches the Doorstop files (`winhttp.dll`, `doorstop_config.ini`), so BepInEx keeps working. Without BepInEx it
installs Unity Doorstop and the mod. It backs up anything it replaces, refuses to overwrite a `winhttp.dll` that is not
ours, and `uninstall.cmd` puts everything back. Switching between the two kinds is handled (run `install.cmd` again).
Your other BepInEx plugins are left alone, but everyone you play with needs the same ones.

**Uninstall:** close the game and double-click `uninstall.cmd`. Your saves stay (Multiplayer saves are in the game's
save folder, under "Multiplayer"); the mod's own data and logs stay unless you run `uninstall.ps1 -RemoveData`.
Or delete the mod by hand: without BepInEx, `winhttp.dll`, `doorstop_config.ini` and the `SolarExpanseMultiplayer`
folder in the game folder; with BepInEx, `BepInEx\plugins\SolarExpanseMultiplayer`.

**After installing** you can delete the downloaded zip and the extracted folder; the game does not need them (keep
them only if you want `uninstall.cmd` later).

**Vortex and other mod managers are not supported yet.** Use `install.cmd`, or copy the files by hand as `INSTALL.txt`
describes. Never extract the zip over the game folder: it could overwrite BepInEx's files.

## How to host and join

- Host: main menu, Multiplayer, Host a game; pick a new game or a save, then Next. In the lobby press Invite friends,
  or send a join code **privately** to players who are not in your Steam friend list.
- Join: accept the Steam invitation or use Join Game in the Steam friends list; or main menu, Multiplayer, Join a
  game, paste the code.
- Start the game from Steam so the overlay and invites work.
- Windows may ask once about network access: allow **Private** networks only, never Public.

## Known limits

- **Co-op (several players running one agency together) is paused: not in this beta.** Each player has their own agency.
- Some buttons are greyed out in a session ("That is not available in multiplayer yet"): buying what is missing, the
  observatory paths and other actions that are not shared yet.
- Only the `experimental` branch build. `open_beta` is not supported. After a game update, the mod waits for its own
  update.
- Everyone needs the same mods: other code mods and BepInEx plugins must match on every machine, or the join is refused.
- After leaving a session, restart the game before hosting or joining again.
- Steam cannot remove a member from its lobby: a removed or banned player may still show there, but the host turns
  them away.
- **Bugs are expected.** Games can drift out of step; leaving and rejoining usually fixes it.

## Connecting has risks

Joining someone's game lets their computer send data to yours. The mod is built defensively: the link is encrypted
and pinned to the host's key, a join code is needed, the host's game data is checked against an allowlist before it is
loaded, and nothing from the network can run code on your PC. But this is a beta and no software is risk free.

- Only host for, and join, people you trust.
- A LAN code shows your network address to whoever has it. Share every join code privately. Steam
  connections go through Steam's relay and share no address.
- The host decides the game, as in any host-run game, and can give themselves anything.
- The mod uses Unity Doorstop's `winhttp.dll` to start. Some antivirus programs distrust that name; it is the same
  file BepInEx and many mods use. Compare the checksums below if you are unsure.

## Privacy

The mod has no telemetry, no accounts and no automatic uploads. It stores a log, a host key if you host, your ban list
and the names you typed, inside its own folder in the game folder, and Multiplayer saves in the game's save folder.
Other players in your session see your Steam name (or your typed name on a home network) and, on a direct home-network
connection, your network address. Nothing is sent anywhere else. Uninstalling removes the files (your mod
data folder is kept unless you ask for it to be removed).

## Checksums (SHA-256)

These let you check that your download is exactly the file uploaded here, not damaged or changed.

Paste from `dist\release\SHA256SUMS.txt`: the zip, then each file inside.

## Credits and licences

- Solar Expanse Multiplayer by solo-ship-it. Not made by or endorsed by the makers of Solar Expanse.
- Harmony 2.4.2 by Andreas Pardeike (MIT), unmodified, in the no-BepInEx install.
- Unity Doorstop 4.5.0 (LGPL-2.1), unmodified, in the no-BepInEx install. Source:
  https://github.com/NeighTools/UnityDoorstop
- Licence texts are in the download (`LICENSES.txt`). No game files are included.