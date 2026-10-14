SOLAR EXPANSE MULTIPLAYER {VERSION}  -  READ THIS FIRST
=====================================================

THIS IS A BETA. It is early, unfinished and will have bugs. Expect rough edges, and expect to lose a session now and
then. Keep your own saves backed up (copy the game's save folder somewhere safe before you try it). Please report
problems on the mod's Nexus page.

What it is
----------
A mod that lets up to 5 people play Solar Expanse together. One player hosts; the others join. Each player runs their
own agency in the same shared world: the same date, speed and pause, with research, buildings, production, flights,
the market, and the AI rivals shared through the host.
Host from a new game or a save, invite your Steam friends to the lobby (or
share a join code), pick agencies, and start. Steam's relay carries the game over the internet; players on one home
network can also connect directly.

What does not work yet
----------------------
- Co-op (several players running ONE agency together) is not in this beta, but I plan to add it.
- After you leave a session, restart the game before you host or join again.
- Bugs. Games can drift out of step. If that happens, leave and rejoin.

What you need
-------------
- Solar Expanse on Steam, on the supported build ONLY: {GAMEBUILD}.
  Switch the game to Steam's "experimental" branch first (Steam: right-click Solar Expanse, Properties, Betas). The
  "open_beta" branch and the default branch are not supported: on any other build the mod will not start a session
  and says "This game version isn't supported by this version of the mod yet." After a game update, the mod waits for
  an update of its own.
- Windows 10 or 11.
- Everyone in a session needs the SAME mod version and the same game build. Everyone needs the same other mods too
  (code mods and BepInEx plugins), or the join is refused with a list of what differs.

How to install
--------------
Run install.cmd. See INSTALL.txt. Do NOT just extract the zip over your game folder: if you use BepInEx, that could
overwrite its files. install.cmd checks first and picks the right files for you.

Connecting has risks
--------------------
Joining a game means letting another person's computer send your game data. The mod is built to be careful (the
connection is encrypted, a join code is needed, the host's game is checked before it loads, and nothing from the
network can run code on your PC), but no software is risk free, and this is a beta.
- Only join people you trust, and only host for people you trust.
- A LAN code shows your network address to whoever has it. Share every join code privately. Steam
  connections go through Steam's relay and show no address.
- Any player in your lobby can invite others. The host can remove or ban a player from the PLAYERS screen (Esc).
- The host decides what happens in the game, as in any host-run game. A host can give themselves anything.
- The mod uses Unity Doorstop (a small winhttp.dll beside the game). Some antivirus programs distrust any file named
  like that; it is the same file BepInEx and many other mods use. You can check its SHA-256 on the mod's Nexus page.

Privacy
-------
The mod sends nothing anywhere you did not choose: no telemetry, no automatic uploads, no accounts. It writes only
inside its own folder (a log, the host's key if you host, your ban list and the names you typed) and, for Multiplayer
saves, in the game's own save folder. Players you play with see your Steam name (or the name you typed on a home
network) and, on a direct home-network connection, your network address.

Removing it
-----------
Run uninstall.cmd. It takes the mod out and puts back anything the install replaced. Your saves stay; see INSTALL.txt.
You can also delete the mod's files by hand (INSTALL.txt lists them). After installing, the zip and the extracted
folder can be deleted; the game does not need them.

Credits and licences
--------------------
Built by solo-ship-it. See LICENSES.txt for the third-party parts (Harmony and Unity Doorstop). This mod was
built with the assistance of AI coding tools. It is not made by or endorsed by the makers of Solar Expanse.
