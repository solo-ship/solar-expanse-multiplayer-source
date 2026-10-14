# Changelog

## 0.5.0-beta (first public beta)

First public release, labelled BETA: expect bugs.

Playing together
- Up to 5 players in one shared world, one player hosting, each running their own agency.
- Shared date, speed and pause, with the slowest-speed rule.
- Research, buildings, production and observatories done by any player reach everyone through the host.
- Flights, the market, contracts and the AI rivals run on the host and reach every player.

The Multiplayer menu and lobby
- A Multiplayer button on the main menu. Host a game from a new game, one of your own saves, or a Multiplayer save.
- A lobby at the menu: every player picks an agency, presses Ready, and the host starts the game. Agencies nobody
  picks play as AI rivals. The host can start alone and let friends join later.
- Steam invites: Invite friends opens Steam's invite window, and any player in the lobby can invite. Friends can also
  join with Join Game in the Steam friends list. "Who can join": Friends (the default), Invite only, or Code only.
- A Steam join code for players who are not your Steam friends, and a LAN code for players on your own
  network. Both are private: share them only with people you trust.
- The host can remove a player (they can come straight back in) or ban them by Steam account, and unban later, from
  the PLAYERS screen on the pause (Esc) menu.
- Multiplayer saves: any player can save; every player keeps a copy, listed under Multiplayer saves, and a save can
  be played alone with the other agencies as AI rivals.
- Steam names: in a Steam game every player shows with their Steam name.

Connections and checks
- Encrypted connections, through Steam's relay over the internet or directly on a home network.
- The join check lists plainly what differs (game build, mod version, other mods) and refuses the join.

Install
- One download for everyone: `install.cmd` picks the Doorstop (no BepInEx) or BepInEx install for you, backs up what
  it replaces, and `uninstall.cmd` puts it back.
- The mod's version reads `0.5.0-beta`. No developer overlay or test files in a normal install.

Known limits
- Supported game build: Steam's `experimental` branch only (0.26.10.9.13 BETA, build 25829824). The `open_beta` branch
  and the default branch are not supported: switch Solar Expanse to `experimental` first.
- Everyone needs the same mod version, the same game build and the same other mods.
- Co-op (several players on one agency) is paused and not in this beta.
- Some buttons are greyed out in a session until they are shared.
- After leaving a session, restart the game before hosting or joining again.
- Steam cannot remove a member from its lobby: a removed or banned player may still show in Steam, but the host's game
  turns them away.
