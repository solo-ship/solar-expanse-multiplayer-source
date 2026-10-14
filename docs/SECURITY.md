# Security

> Public source snapshot note: this is the project's working security document. Some passages refer to development
> documents and tools that are not part of this snapshot (DECISIONS.md, TESTING.md, TEST-DEBT.md, DESIGN.md, the
> research notes under docs/research/, the test suite and its tools, the two-device test kit and the test-game
> runner); the test suite is kept private. Milestone names (M1 to M10) are internal stages.

Players are letting other people's data into their game, so the mod has to be safe to join. The rules below are
requirements, not nice-to-haves. (2026-10-09, before any code.)

## What we protect against

- A stranger joining a game they weren't invited to, or taking over someone else's agency.
- A player (or someone pretending to be one) acting for an agency that isn't theirs, or sending impossible actions.
- **Anything from the network running code on a player's PC.** This is the big one: see "Untrusted data".
- Eavesdropping or tampering on the connection.
- Players' IP addresses leaking to each other.
- Crashing or freezing someone's game with malformed or oversized messages.
- Files written outside the mod's own folder.
- The mod sending anything anywhere the player didn't choose (no telemetry, no automatic uploads).

What we can't fully stop: **the host is the authority**, so a host can cheat in their own game (give themselves
money and so on). Joining means trusting the host with the game state, the same as any host-run game. The host can
never run code on, or read files from, anyone else's PC.

## Identity and access

- **Steam games:** friends-only lobbies (or invite-only). The joining player proves their Steam identity with a Steam
  auth session ticket (`GetAuthSessionTicket` / `BeginAuthSession`), checked by the host. An agency claim is tied to
  that Steam id.
- **Direct connections:** the host generates a join code holding the address, a one-time invite secret and the
  host's key fingerprint. Optional lobby password. On first claim each player gets a per-save player key (random,
  256-bit) that proves it's them when they rejoin; stored only in the mod's folder on their PC.
- The host can kick and ban (by Steam id or player key).
- Nothing is accepted from a connection until it has passed the handshake (version, identity); everything after that
  is checked against who sent it.

## The connection

- **Steam:** Steam Networking Sockets are encrypted and authenticated by Steam, and the relay hides IP addresses.
  This is the way to play over the internet. We do not rely on that alone: each Steam connection's flags are checked
  (authenticated, encrypted, relayed), ICE is switched off on every socket and the value is read back (a socket where
  it does not read 0 is closed), and the frames inside are held to the same caps and rate limits as a TLS link. We call
  no Steam sign-in, ticket or credential function today (a source test fails the build on one). A ticket check of the
  other player (SD10) is planned, not built; until then a player's Steam id comes from the connection Steam
  authenticated, never from a message. Steam ids name an account: logs hold only a redacted short form (the log
  blanks any raw 17-digit id), and the device kit's privacy scan removes a raw id or an IP address from every log and
  zip it makes, and stops a zip it cannot clean.
- **Direct:** TLS over TCP (`SslStream`) with the host's self-signed certificate pinned by the fingerprint in the join
  code, so no one in the middle can read or change traffic. Plain unencrypted links are never offered. Direct
  connections show the host's IP to whoever has the join code; the lobby says so. (A VPN such as Tailscale works
  through this too.)

### Direct connection as built (M2, with M4's key rules)

- **TLS 1.2 only.** The client trusts the host's certificate only if its SHA-256 matches the join code (see "Host key
  storage"). The join code holds one 128-bit invite. One invite is open at a time and works once; a version mismatch
  does not spend it.
- **Rejoin keys.** A joined player gets a per-session rejoin key (256-bit, random; every Welcome brings the next).
  The per-save player key comes with the lobby (M8). A presented key is compared with every valid key in constant
  time, as invites and pins are (never a hash lookup, never a comparison that stops at the first difference).
- **A kick takes the keys.** A player the host kicks loses its rejoin keys, so it cannot come back on them this
  session: the failed-join limit, a join-protocol breach (code 23: otherwise a modified client could rejoin, break a
  rule and be kicked without end) and the host's own kick (the dev kick, code 99 by default). A join that merely ended
  (a refusal, a deadline, content that differs) keeps them, so the player may try again.
- **A rejoin replaces.** A valid rejoin key for a player the host still thinks is connected replaces that link: the old
  one is closed with the reason, the game sees one Joined (marked as a replacement), and players are tracked by link, so
  a late Closed from the old link is ignored. A spent, unknown or forged key is refused. Messages that were already
  in flight on the replaced link are dropped, never applied: the host delivers a message only if its link is still the
  player's current one, and the replacement counts as a fresh join for the clock rules.
- **Keys and invites are reserved, then committed (M4: N-1).** `HostGate.Check` spends nothing. It reserves the invite
  (or the key K1 the player presented) and issues the next key K2 with the Welcome. The host commits with `Confirm` on
  the first frame that arrives on the new link, which proves the Welcome arrived: only then is the invite or K1 spent
  and the player's other keys revoked. If the link dies first, `Undo` runs: the invite is open again, and K1 and K2 both
  stay valid until either is used, and using one revokes the other. So a lost Welcome never locks a player out, and
  never lets two copies of the credential in. A player holds at most two valid keys at once; a third replaces the
  oldest one that is not reserved, so the key a client holds after a lost Welcome is never the one dropped.
- **The races around that.** A second Hello with a reserved invite is refused. A second Hello with a key whose
  connection is still reserved takes over (the key proves it is the same player). An invite that was given back keeps
  the player id it had been given, and the key issued with it is its pair, so a loop of lost Welcomes burns no ids. A
  rule-breaker that came in on an invite loses it, and the host publishes a new join code. A Hello that lost a race to
  a newer one for the same key is refused as superseded.
- **Ids and seats.** A host session has 64 player ids. A player whose rejoin key is lost or revoked comes back on a new
  id. The dev host seats 4 players at once. A refusal because the game is full does not spend the invite (retry when a
  seat frees).
- **Strangers learn nothing.** The invite or rejoin key is checked before the versions, so a stranger never gets the
  host's version or build. A stranger gets nothing beyond the refusal or violation reason (never host data), and no Bye
  on handshake or idle timeouts; a stranger's Bye text is never logged (a fixed reason is used instead).
- **Address.** The host listens on 127.0.0.1 by default, and otherwise on exactly the address the dev flag
  `-semp-bind` gives, never on every interface. Session flags work only in a test profile, and every `-semp-` flag
  (the test profile included) counts only in a TEST COPY: `semp-test-copy.marker` beside the mod's DLL, written by
  the dev tools (`deploy-test.ps1`, `run-test-game.ps1`, the device kit) and never by the release installer; the
  release lint refuses it in the zip. Without it Boot drops every `-semp-` flag and its value (`DevMarker.Args`) and
  logs how many, so Steam starting a player's game with a command line a friend's client chose ("Join Game" on a
  rich-presence `connect` string) gives a plain player build: no test profile, no dev flags, no test seams.
  `-semp-bind` takes this
  PC's loopback (127.x.x.x) or, for the two-device test, a private home-network address (RFC 1918:
  10.x.x.x, 172.16.x.x to 172.31.x.x, 192.168.x.x), written as four plain numbers (`BindAddress`). Everything else
  is refused and means no session: a public address, `0.0.0.0`, link-local (169.254.x.x), multicast, broadcast, IPv6,
  a name, and other spellings of an allowed address (`10.1`, `010.0.0.1`, `167772161`). `LinkHost` refuses the same
  addresses itself, whoever calls it. The join code carries the address the listener is really bound to, and the
  client connects there. `tools\run-test-game.ps1 -Bind` checks the same rule before anything starts.
- **The joiner goes only where a host may listen.** A join code whose address is not loopback or a home-network
  address (the same `BindAddress` rule, IPv6 included) is refused by `JoinCode.Parse`, which both of the game's join
  paths (the start and the dev "join" trigger) call before they connect, so a mistaken or planted code never makes
  the game reach out of the home network. The refusal starts with `JoinCode.NotLocal` ("the join code points
  outside this PC and the home network") and names the address. `LinkClient.Connect` refuses such an address
  itself, before any socket exists, whoever calls it.
- **New exposure with a home-network address.** Any device on the home network can reach the port and attempt a
  connection. It cannot join without the join code's one-time invite and the pinned TLS key: a stranger gets only a
  refusal ("Strangers learn nothing" above), and the handshake timeout, the cap on connections in their handshake,
  the failed-join limit and the size and rate caps all still apply. The join file holds the secret, so it travels by
  USB stick or a shared folder, never email or chat. Windows asks once whether the game may listen on private
  networks.
- **Tests never listen beyond loopback.** Unit tests and mutation runs check the home-network path by parsing and by
  constructing a refused `LinkHost` without starting it, and the joiner's refusal by parsing join files only; every
  socket a test or tool opens or connects is on 127.0.0.x (the client's own refusal is shown with 0.0.0.0, which
  cannot leave the PC). `LinkBindTests` scans the tests' and tools' C# and PowerShell for any other listener (the
  one-argument `TcpListener` forms too, whose address cannot be seen) and checks the bind test's address is
  loopback before it listens; it also scans all of `src` (Core and the BepInEx shim included) for any listener
  besides `LinkHost`'s.
- **Across the internet is Steam's relay** (the Steam join code, below): there is no public-address option and no
  port forward. **Tailscale counts as home** (the fallback when Steam is down): `BindAddress` also takes
  100.64.0.0/10 and fd7a:115c:a1e0::/48 (only the short lower-case IPv6 form), for the host's bind, the joiner's code
  and the device kit alike. A Tailscale address is reachable only by devices the players put on their own Tailscale
  network, so it is no wider than the home network; the invite and the pinned key still guard the join. One caveat:
  some internet providers use 100.64.0.0/10 for carrier-grade NAT, but that address sits on the router, not on a PC,
  so a PC holding one is on Tailscale (or a similar overlay the player chose).

### The Steam join code (M9 Steam-lite)

- **What it holds:** kind byte 2, the host's Steam id (an individual account, checked on both ends), the same 128-bit
  one-time invite as the home-network code (one invite, two codes: whichever is used first spends it, and the host
  writes a fresh pair), the host's protocol and a 4-byte tag of its mod version and game build (so a joiner on another
  version is told before it connects; the gate still checks the versions in full after the invite), and a checksum.
  Never an address, a port or a certificate. `JoinCode.Parse` refuses a Steam code and `SteamCode.TryParse` refuses a
  home-network one, each saying which it is; the joiner's route follows the kind byte (`JoinRoute`).
- **The pin is the host's Steam id.** On a Steam link there is no certificate to pin: Steam authenticates each end of
  the connection with a certificate Steam itself issues for the account. The joiner connects only to the id in the
  code, and when Steam says the connection is up, `SteamPipe.CheckUp` compares the connection's verified remote
  identity with that id (and requires the connection's flags to say authenticated and encrypted). Anything else is
  closed before a byte is sent: the joiner's Hello, with the invite, waits in the pipe until that check passes, so a
  wrong peer never sees the secret.
- **The secret is still required.** Steam's identity says who the joiner is, not that the host invited them: a Steam
  connection reaches exactly the same Hello check as a TLS one (`LinkHost.AddSteamLink`: the same pending cap,
  handshake deadline, gate and one-time invite or rejoin key). A Steam Hello without the right invite is refused like
  any other. One exception (B7): a Steam account that still holds a seat in this session (a valid rejoin key, so never
  a removed, banned or barred player) and comes back with a join code, spent or current, gets its own player id back
  without touching the code's invite. Steam proves the account, exactly as for a lobby member's Hello; the invite
  only ever admitted it once, and a stranger with the spent code is still refused.
- **The door before Steam accepts.** The host decides each incoming Steam connection from Steam's verified identity
  before `AcceptConnection` (`SteamDoor`): the session must be open, the account an individual one, the connection
  authenticated and encrypted, the pending cap not reached, and either a Steam join code open or the account already a
  player of this session. Everything else is closed unaccepted. The door's lines name the account only by its short
  form, and are throttled to one a second.
- **One handshake per account (beta, B8).** While a code is open any Steam account reaches the door, so the door book
  (`HostSteamDoor`, `DoorAccounts`, in memory) lets each account have at most ONE connection on its way in, from the
  door's "ask again" or "yes" until its handshake ends (`LinkHost.SteamHandshakeEnded`: joined, or closed before the
  Welcome; at most 20 s if the end is never reported). A second one from the same account is refused
  (`AccountBusy`), so one account holds at most one of the 8 pending slots. An account whose handshake fails (deadline,
  refused Hello, breach, or a final "not invited") 3 times within 60 s is refused for 5 minutes (`CoolingDown`); a
  refusal at the pending cap is the host's state and is not counted. Players already connected are not affected.
- **Rejoin keys are tied to the account.** The gate remembers which peer took each player id (TLS, or the Steam
  account): a rejoin key presented by another Steam account, or carried from Steam to the home network (or back), is
  refused as an unknown key. A Steam link with no verified individual account is refused outright.
- **What we never use:** no Steam auth, session or web API ticket, no credential of any kind, no rich presence or
  stats (`SteamSourceTests` fails the build on any of those calls). The ticket check (SD10) is still planned for later
  and still needs the user's ruling.
- **The Steam lobby (beta B1).** A host's session is a friends-only Steam lobby by default (invites-only when the host
  turns friends' joining off). Its data holds nothing secret (the host's Steam id, protocol, mod version, game build,
  a random session id, the access mode, players and seats, joinable) and only the owner writes it. A joiner reads it
  before joining (other versions are refused there), and once a member it trusts the host only when the lobby's owner
  (Steam's word) is the account the data names (`LobbyDataKeys.TrustedHost`); it connects to that id through the relay,
  the pin as for a Steam code. The invite dialog (`ActivateGameOverlayInviteDialog`) is opened by the lobby glue only,
  for the host's own lobby. The host's door admits a current lobby member without a code.
- **A player's log (beta, B8).** Players post `semp.log` for help, so on a player's install (no test profile) the
  lines that named an address, a player or a path go through `LogPrivacy`: a network by its kind ("home network",
  "Tailscale", "loopback"; the Steam relay lines name only the host's short id), another player by their number (the
  screens show the names), a path relative to the game folder (`<game>\...`) or with `<user folder>` in place of
  `C:\Users\<name>`. Test profiles keep the full lines their tools read. INSTALL.txt says what the log holds and to
  send only `logs\`.
- **Ids in logs:** only `SteamIdText.Short` ("s:" and 8 hex digits of SHA-256 over a per-install random salt in the
  profile's data folder, `steam-id-salt.bin`); `Log.Redact` blanks any 17-digit run that slips through. The Steam
  code file, `join-code-steam.txt`, holds the secret: it is a test profile's file like `join-code.txt`, never logged,
  and every evidence filter that skips `join-code*` skips it too.
- **Versions after the credential.** The invite or key is checked before the protocol, mod version and game build. Once
  it has passed, a refusal for one of those carries the host's version, so the player can see what to update.
- **The failed-join limit.** A join failure that is the joiner's own (a failed check, a drop in a batch, a missed
  deadline, a rule broken, a wrong date) counts against it: three in a session and the host kicks the player, revokes
  its keys and refuses it at once from then on. This stops a joiner that expires and re-queues from holding every
  batch's pause for ever. A failure that is the host's own (no snapshot, its own check refusing it) never counts.
- **Text from the other side has a byte budget.** A character cap is not a byte cap: 160 CJK characters are 480 bytes.
  Everything peer-derived that goes into a message is cut to a byte budget first, at a character boundary, and report
  lines are budgeted at their JSON size, so no send can throw; `Link.Send` returns false rather than throwing for a
  message that does not fit, and every caller that matters looks at it. The host's own text counts too: the Roster
  checks names (48 bytes) and the save title (96 bytes) by bytes, so `LobbyHost.Roster` cuts every name (the host's,
  the joiners', the last holders') with `LobbyLimits.Name` and the title with `TextBudget.FitBytes` before it is sent;
  otherwise one host name of 17 CJK characters would make every joiner refuse the roster and leave
  (`HostMessagesWorstCaseTests` builds each name- or title-carrying message the host sends from worst-case input).
- **A test client keeps its rejoin key in a file (test profiles only).** In a test profile the joiner writes its current
  rejoin key to `<mod>\data\test-<profile>\rejoin-key.txt` after every Welcome (through a temp file) and, started with
  `-semp-rejoin`, reads it back (with a size cap) so a restarted game can rejoin. It is a plain-text secret, never
  logged, and the test tools never copy it. A real player never gets this file: where real players' keys live (and how
  they are protected, like the host key below) is an M8/M10 item.

### Host key storage

- **Pin only.** A client trusts the host's certificate by its SHA-256 in the join code and nothing else: chain, name and
  validity dates are ignored on purpose (an expiry date would only be a way to lock players out). The client rejects a
  missing certificate, and a pin that is not exactly 64 hex characters (32 bytes), and compares in constant time.
- **M2 (development only):** the host's key is stored unprotected in the test profile's data folder
  (`<mod>\data\test-<profile>\host-cert.p12`). The PKCS12 password in the code is a constant and is not protection. The
  code asks for the file to be restricted to the current Windows user, but the game's Mono does not implement
  `File.SetAccessControl` (it throws `NotImplementedException`): in the game the key file keeps the data folder's
  permissions (on the test copy, ordinary users could read it) and a WARN is logged. Writes go through a temp file. The
  key loads with `DefaultKeySet`, not `Exportable`, and UnityTls serves TLS with that. Mono wrote nothing to
  `%APPDATA%\.mono` or `Microsoft\Crypto\RSA`: checked with `Exportable` in the Task 3 spike, and again with
  `DefaultKeySet` after the Task 8 in-game run (no `.mono` folder; nothing in `Crypto\RSA` newer than 2024). Nobody
  but developers runs a host in M2, and still nobody else in M4 (test profiles only).
- **A player's install (the beta, 0.5.0):** no test profile means the key is kept by `KeyFiles.HostCertificate`
  (`HostKeyStore`): `<mod>\data\player\host-cert.protected`, DPAPI for the current Windows user (crypt32 called
  directly, with the mod's own entropy), so another Windows account, a backup or a zipped `data\` folder gets only a
  blob. The plain `host-cert.p12` an older build wrote (and its `.tmp` / `.bad-*` copies) is deleted once the protected
  file is in place; it is not carried over, so a new identity is made. Without DPAPI there is no home-network hosting
  and never a plain key (Steam relay games do not use this certificate). Proved on the Windows test runner with the
  real DPAPI; the run in the game's Mono is owed (docs/TEST-DEBT.md). Test profiles keep the plain file above.
- **A damaged key file** is moved aside as `host-cert.p12.bad-<time>` (never deleted) and a new identity is made, logged
  at WARN with the old fingerprint; old join codes stop working. A temporary IO error (file in use) never replaces the
  key: the host start fails instead.

## Untrusted data

Everything received is treated as hostile until checked.

- **Messages are plain data with a fixed schema**: each message type has its own small class; unknown types and
  fields are dropped. Never `BinaryFormatter`, never Newtonsoft `TypeNameHandling`, never any "type name in the
  data decides what gets created".
- **The save sent by the host** is the risky part: the game's save format (Odin Serializer) can create objects by
  type name, and Odin's own docs warn it isn't safe for untrusted data. So before the game sees a received save it
  passes the walls in "The snapshot walls (M4)" below: size caps, a hash, a bounded decompression, a strict scan against
  an allowlist of the game's own save data types, a refusing binder, and a deserialise only at the main menu.
- **Sizes are capped**: per message, per attachment, and after decompression (protects against "zip bombs").
  Oversized or malformed messages close the connection with a log line, never a crash.
- **Rate limits** per player, so no one can flood the host. Beyond the per-connection message limit below, the host acts
  on a player's speed-ups at most 4 times a second (the latest is kept, the rest are counted and logged as
  coalesced); a slow-down or pause is acted on at once, but each must lower the speed that player's last acted request
  allows, so at most 5 come between two speed-ups: at most 6 acted in any 250 ms (24 a second). A client sends at
  most 2 speed requests in any 100 ms. `SpeedRequest.Seq` and `Vote.Seq` (the host's echo of the last request it acted
  on) must not be negative; the largest `Clock` with them still fits its 1 KiB cap. The host broadcasts the clock at
  most every 50 ms on a change (and 4 times a second otherwise).
- **Limits in M2** (M4 added the snapshot's caps, below, and the payload caps of its fourteen messages, in
  DESIGN.md's message table). A frame body is at most 64 KiB (a length of 0 or over the cap is refused before anything
  is allocated). Each message type has its own payload cap: 64 bytes (`Ping`, `Pong`), 128 (`SpeedRequest`), 512
  (`Welcome`, `Reject`, `Bye`), 1 KiB (`Hello`, `Clock`). JSON nests at most 8 deep. A connection has 10 s to finish
  its handshake and must be heard from at least every 10 s (each side pings every second); at most 8 connections may
  be in their handshake at once. Each connection may burst 100 messages and 256 KiB, then 50 messages and 64 KiB a
  second. A connection whose writes block for 5 s, or whose send queue passes 512 frames, is dropped. The main
  thread's inbox holds at most 20,000 events (beyond that messages are dropped, never a join or a leave).
- **A broken rule closes that connection with the reason**: a bad frame or message, a wrong sequence number, the rate
  limit, a `Hello` after joining. For a joined player it also cancels that player's rejoin keys, and so does a join
  message out of turn that the host's join service finds (its kick, code 23). Running out of time
  (handshake, idle) or losing the connection (a reset, or a stream that ends in the middle of a frame) closes the
  connection but cancels no key: the peer went away, it broke no rule.
- **A hostile host cannot loop a joiner.** A joiner whose host breaks a rule (a first frame that is not the Welcome
  included) closes the link and never reconnects to it (code 23). The host answers each claim once and asks for the
  content details at most once per join attempt: a `ClaimResult` that answers no claim (an Ok after a refusal, a
  second Ok) or a second `ContentRequest` is such a breach, and the joiner sends nothing more for it. A host that
  answers a reconnect with a Reject ends
  the reconnects too (code 53), and so does a host that says goodbye (its `Bye`: it closed its game, kicked the
  player or took its agency; code 21, or the code the `Bye` carries, 0 to 99, which only picks the joiner's words).
  After a goodbye a joiner that has not loaded goes back to the menu, and a loaded game goes back to the menu too (no
  reconnect, no game left open); so does every other ending of a loaded game (the reconnects used up, a refused
  reconnect, a breach), and the session's hooks come down with it. A goodbye before any join is in progress changes
  nothing. Reconnects are capped at 5 per join (code 52), and
  neither a Welcome, a load nor a quick rejoin resets the count: only a minute in the game, connected, with no drop.
  Both sides count a join's total deadline from its header (the host from sending it, the joiner from receiving
  it), scaled with the snapshot's size.
- **Text from other players** (names, chat, agency names) is length-limited and stripped of TextMeshPro rich-text tags
  (`<link>`, `<sprite>`, `<size>` ...) so it can't fake UI or hide content.
- **No file paths from the network.** Save names are reduced to safe characters; the mod only writes inside its own
  folder (`<game>\SolarExpanseMultiplayer\` data, plus the game's normal save folder for the shared save).

## The snapshot walls (M4)

The host's game reaches a joiner as a gzipped Odin JSON document. It is hostile until it has passed every wall below,
in this order, on the joiner, before the game sees any of it. The host runs the same hash, inflater and scanner on its
own snapshot before sending, so a host fault is caught on the host and never counts against the joiner. A failed wall
sends the host `JoinStage(Failed, code)`, returns the joiner to the main menu with the reason, and loads nothing.

1. **Caps while receiving.** The header's declared sizes must be inside 1 to 16 MiB compressed and 1 to 64 MiB
   uncompressed (real saves: 0.2 to 0.6 MB and 7 to 16 MB), its chunk count must be exactly what the compressed size
   needs, its save version, hash format and player id must be ours, and a save id, when there is one (drift runs), is
   16 hex digits. Only then is the one receive buffer allocated,
   at exactly the declared compressed size. Chunks must arrive in order, each exactly 32 KiB but the last, and add up
   exactly.
2. **SHA-256.** The hash of the received bytes must equal the header's (checked on the last chunk, outside the
   receiver's lock).
3. **Bounded gunzip.** Our own inflater, not `DeflateStream` (which reads ahead and cannot say where the data ended, so
   it cannot refuse trailing data; Mono's and desktop .NET's also differ on members and truncation). The declared size
   is checked against the cap before anything is allocated, and the output buffer is exactly that size and never grows.
   A declared size past what deflate can expand to (1032 to 1) is refused before the buffer exists. It accepts exactly
   one gzip member followed by exactly the 8-byte trailer (a second member, trailing bytes and junk before the trailer
   are refused), checks the trailer's CRC-32 and size, and spends at most a fixed multiple of the output it has written
   on building code tables (16 table entries per output byte plus 1 MiB), so a stream of empty blocks with 15-bit codes
   cannot burn a joiner's CPU. It runs on the background worker, never on Unity's main thread, so the heartbeat and the
   deadlines keep running. Measured: 7 to 34 ms for real saves; a hostile 64 MiB stream about 2.1 s of work on desktop
   .NET. 180,000 differential fuzz cases against zlib and the Framework's `GZipStream` found 0 mismatches, 0 wrongly
   accepted inputs, 0 unexpected exceptions and 0 hangs.
4. **The scanner (`OdinScan`).** It reads the Odin JSON dialect itself, strictly, before Odin or the game sees a byte,
   because Odin's own reader is loose in ways that let one document read two ways. Where the scanner and Odin could
   disagree, the scanner refuses. It accepts only what Odin's writer writes: ASCII only (Odin escapes everything above
   0x7F, so this also covers every byte-order mark and invalid UTF-8); whitespace only between tokens; exactly one comma
   between entries; the escapes Odin writes and no others, and none at all inside a type name, a reference or a key;
   a `\u` escape only above 0x7F, which is all Odin's writer escapes that way (its reader takes the character a `\u`
   decodes to as the last one read, so `\u005C` before a quote keeps Odin's string open where the scanner's closes, and
   the two would read different documents; the other escapes, `\"`, `\\`, `\0`, `\a`, `\b`, `\f`, `\n`, `\r`, `\t`,
   were checked against Odin's reader and end strings in the same place; `\/` and any other are refused);
   canonical decimal for every number that means something structural. Every `$type` anywhere is a node's own type
   (Odin deserialises and registers nodes it "skips"), so every `{` is checked. Every type name must pass a strict
   grammar and then the allowlist (below). On top of that, rules the names alone do not give:
   - **E1** every node carries `$type` right after `{` (or after `{` and `$id`); a `$id` anywhere else is refused;
   - **E2** a `$k`/`$v` node only as an element of a dictionary's content, and a dictionary holds nothing else;
   - **E3** a comparer node is empty (an enum comparer: an empty array);
   - **E4** the elements of an `object[]` are boxed strings, doubles and floats, the two AI event arguments
     (`AI.IdForOffer`, `AI.IdForContract`: what a rival's AI keeps queued for its behaviour tree, scanned as ordinary
     nodes), or references to them; every other type, listed or not, is refused there;
   - **E5** type ids canonical, defined before use and never redefined; one type name under one id;
   - **E6** every `$id` unique, and every `$iref` names an `$id` already seen;
   - **E7** one key per member: an ordinary key appears once in a node (Odin is last-wins, so a repeat could hide a
     value from anyone reading the JSON); a key may not be a `[FormerlySerializedAs]` old name of the node's type or
     its bases (Odin reads such a name into the member of the current one, which would let `constructionEquipment`
     silently override `constructionEquipmentCount`), from a table generated from the beta build and checked for drift;
     a node holds at most a fixed number of keys, and the duplicate-key checking has a work budget in all, so a flood of
     colliding keys cannot cost seconds.

   `$rlength` and `$plength` are held to a ceiling and must equal the elements present; `$eref`, `$guidref` and
   `$strref` are refused; `$fstrref` must be `Class/id` with the class on a list of 12. The scan never throws: any
   failure, including a bug in the scanner, is a refusal. Afterwards every reference must resolve to a definition in
   this game's content (the second net for a mod the joiner lacks).
5. **The refusing binder.** At the strict deserialise, each type name goes first through the same allowlist policy, then
   is bound ONCE as a whole name (Odin binds generic arguments inside its own default binder, never through an
   override, so a per-argument check would never run), and the bound type's name must equal the name exactly (a name
   that binds somewhere else through a type-mapping attribute, or a type found by searching every assembly, is refused).
   **A refusal aborts the deserialise** (the binder throws, and the refusal is also recorded, so the snapshot is
   refused even if something swallowed the exception). It never just returns null: Odin treats a null type as "use the
   slot's declared type" and carries on in silence, in two places, so a null would let the node through.
6. **Deserialise only at the main menu, with the menu check.** The strict deserialise runs only when the active scene
   is the menu, no `GameManager` exists and the `LoadSaveManager` does. Immediately before it, the menu premise is
   checked again by looking for a `GameManager` and an `ObjectInfoManager` anywhere, active or not; if either exists
   the snapshot is refused. This check is REQUIRED, not optional (see "The binder cannot block a cast" below). Odin runs
   under `ThrowOnWarningsAndErrors` and anything it logs anyway refuses the snapshot. The game's own flag that keeps id
   lookups off while Odin builds objects is raised for the deserialise and always lowered again, and the context's
   binder is reset afterwards.
7. **Sanity.** The deserialised save's date equals the header's within 1 ms; its game speed is 0 (the host snapshots a
   paused game); it is not a new-game save; its save version is the header's and this game's; it is not already over
   (the end-game impact); the header's configuration names the agency this joiner claimed, and that is its own seat;
   and every agency the header names is both in the snapshot and in this game's content.
8. **The summary.** The joiner shows the date, the agencies and its own agency for 5 s before it loads. In M4 only a
   dev trigger can decline; the real choice screen is M8's. This is where "only join people you trust" stays true: a
   host can always send a hostile but valid game.

Then the load itself has four failure signals (the extract's result, the save-loaded event, the Unity error count
beyond a short pinned list of harmless lines, and being cancelled), any one of which fails it, and the host's content
hashes after its own load are compared with the joiner's after the load.

### Caps and ceilings

| What | Limit |
|---|---|
| Compressed snapshot / uncompressed | 16 MiB / 64 MiB |
| Chunk | 32 KiB; 16 in flight; an ack every 8 chunks |
| Frame body / one message | 64 KiB / its own payload cap (DESIGN.md's message table) |
| Content summary | 60 KiB; at most 256 plugins, 256 assemblies, 16 categories |
| Content detail | at most 8 parts of 1024 items per category, 128 KiB per check |
| Refusal report | 40 lines of 160 characters, 7 KiB of UTF-8 |
| Seats / rivals in a header | 5 / 16 |
| Players per session / claims per link | 5 / 5 |
| Scanner ceilings | 4 times the maximum measured over 22 saves (below), never above the absolute bound |

Scanner ceilings, the corpus maximum times four, with the absolute bound after it: depth 56 (128), array length 4,408
(4,000,000), elements in all 138,952 (64,000,000), string 960 characters (65,536), distinct types 884 (1,024), distinct
references 1,592 (65,536), `$id` values 139,636 (1,000,000), keys in one node 124 (256), key-checking work 612,296
(64,000,000). A legitimate save has room; a hostile one cannot ask for more.

### The type audit

`docs/research/save-allowlist.md` is the audit and is normative: the code's lists are generated from it, and a test
fails if they differ. The list is **181 closed types, 13 generic definitions, 7 argument-only types, 1 closed generic
and 12 reference classes**: the game's own save graph (329 types reachable from `SaveGameData`), checked against real
saves, minus 58 refused types. Argument-only types (`System.Object`, `SharedVariable`, `ProductionItem` and four
content classes) may be a generic argument or an array element, never a node's own type. The one closed generic is
`ClonableStack` of `SpaceComponentCount` (the runtime type of `Hull.utility` in some saves), allowed as that exact
name only. Names are
matched exactly (no case folding, no version, no prefix). Every member Odin can call on a listed type was read in the
decompile: none writes game state, files, the network, statics or Unity objects. Of the game's 77 cast operators Odin
can reach 26 under the list; only `GetHashCode` and `Equals` run as key methods, and they compare stored values. The
list is audited only for known game builds (`KnownBuilds`).

### The binder cannot block a cast between two allowed types

Odin calls a type conversion (`op_Implicit` or `op_Explicit`) when a node of one allowed type is stored in a slot of
another. The binder sees only type names, so it cannot see or stop these. Of the 26 reachable casts, 16 build a new
object from the source and store it, 2 look content up, 3 return null in the deserialising context, 1 always throws, and
**4 can read the game's live state** through `GameManager` and `ObjectInfoManager`. What covers those four is the menu
premise: at the main menu neither singleton exists, so each such cast throws and the deserialise aborts whenever it
would read. That is why wall 6 exists and why it checks before every deserialise. **Any deserialise outside the menu
is a security regression**, and a future change that needs one must go back through this audit.

### The bulk sink and its arming

The snapshot's bytes never wait for the main thread. The joiner's `SnapshotReceiver` is called by the link's reader
thread, which acks by itself, so a long main-thread frame can neither starve the window nor race the header. It is armed
only by a `ClaimResult(Ok)` that answers a claim this joiner actually sent and that has had no answer yet: every
`ClaimResult`, Ok or not, answers the claim that is out, so an Ok after a refusal, or a second Ok, arms nothing (a
second Ok disarms). It is disarmed by `JoinRefused`, a refused claim or `Bye`. While unarmed it refuses a header and
every chunk, and the link closes as a violation. It takes ONE header; a second, or one that breaks the caps or the
joiner's own header rules, is refused. A chunk frame skips the rate-limit buckets (it would otherwise exceed them) only
while the sink is armed, the header passed, nothing has gone wrong, chunks are still owed and the frame is no bigger
than a chunk frame can be; every other frame goes through the buckets, and a chunk the sink does not want (one after the
last, say) closes the link. A chunk with no receiver at all is a violation. Each new attempt on a link starts from a
reset sink and needs a fresh `ClaimResult(Ok)` to arm it.

### The hostile-snapshot list

Each of these is refused by the wall named, and the joiner stays at the menu. The fake client runs the list as a hostile
host against its fake joiner on every build (`host-selftest`, TESTING.md), and it was run against a real joiner game
in M4 (`fakehost --joiner-dev`): every case was refused with its code, and the joiner stayed at the menu.
- an oversized declaration, sizes that lie, a chunk count that does not match (wall 1);
- chunks out of order, a wrong-sized chunk, a chunk before the header or while unarmed, a second header, a header
  after an Ok that answers no claim (wall 1); a second content request (the join protocol);
- a bad hash (wall 2);
- a decompression bomb, a second gzip member, bytes after the trailer, a forged trailer, an empty-block flood (wall 3);
- a truncated stream (walls 1 and 3);
- a disallowed type at the top level, nested, inside a generic argument, as an array element, under a wrong
  assembly name, with whitespace or an escape in the name (walls 4 and 5);
- a `\u` escape below 0x80 (`\u005C` before a quote: two strings to the scanner, one to Odin) (wall 4);
- a repeated key, an old-name alias, a flood of colliding keys, a duplicate `$id`, an `$iref` to nothing, a node with no
  `$type`, a comparer with contents, a non-primitive in an `object[]`, a length that does not match (wall 4);
- a reference to content the joiner lacks (wall 4, the second net);
- a save that is already over, an unknown agency, an agency the joiner did not claim, a date that is not the header's
  (wall 7);
- a deserialise attempted anywhere but the menu (wall 6).

Not refused, and said so in "Known limits": a node with a member name that is no member of its type, which Odin skips.

## Commands

- The host checks every request: the sender owns that agency, the agency can afford it, the game allows it (the same
  checks the game's UI makes), and the target exists. Anything else is refused with a reason.
- A dropped player's agency is locked: nobody can act for it until its owner is back.
- Sequence numbers stop replayed or reordered requests.

## Supply chain and releases

- Dependencies are few and pinned: Harmony (MIT), Unity Doorstop (LGPL-2.1, only for installs without BepInEx),
  and LiteNetLib only if we need it (MIT). Their licences ship with the mod.
- Releases are built from this repo; each release lists SHA-256 hashes of its files.
- No telemetry. Bug reports are a local zip the player can look at and send themselves.
- **The device kit's evidence zip from a lent laptop** (the laptop is often someone else's).
  `collect.ps1` says first what it gathers and removes and that nothing is sent anywhere. Before zipping it replaces,
  in every text file and entry name, the Windows user name (also any folder after `X:\Users\`), the PC's name, every
  IPv4/IPv6 address except the host's (from the kept join file, which the logs need) and loopback (a version
  number after a version word stays, the laptop's own address never), MAC addresses in any form, a work OneDrive's
  organisation, and VPN-like or renamed adapter names with placeholders that stay the same across the zip
  (`<laptop-user>`, `<laptop-pc>`, `<addr-n>`, `<mac-n>`, `<org>`, `<vpn-adapter-n>`). A binary file holding any
  of them cannot be cleaned like text: a drift record or dump keeps its format with each value overwritten in place
  by `*` of the same length (a ship the player named after themselves included), and is kept; what cannot be masked,
  and a screenshot holding a value (or compressed text past 4 MB), is left out. Both are said in the console
  (`kit-privacy.ps1`). The
  zip is built in a temp folder and scanned again (text entries as Latin-1, UTF-8 and UTF-16, other entries by
  their format, plus the join-code scan); any hit means no zip at all (fail closed, exit 3). Its name holds only
  the date and time. Hardware, Windows build, time zone, drive letters and times stay, and the checklist and
  collect say so.

## Known limits at the end of M2

- There is no cap yet on new connections per second. `MaxPending` (8) bounds the threads in their handshake and the 10 s
  deadline frees them; revisit with M4's wider exposure.
- A player holding a valid invite can pause everyone by dropping and rejoining (the drop rule), and M2 has no kick or
  ban. From M8 only a player who has loaded into the running game pauses it by dropping, and the host can kick and ban.

## Known limits at the end of M4

- **Host key storage** was a release blocker; fixed for the beta (see "Host key storage": a player's key is DPAPI
  protected). Real players' rejoin keys live in memory only (the test-only `rejoin-key.txt` is not their home).
- **Slow handshakes.** The threads of connections still in their handshake are bounded by `MaxPending` (8) and the 10 s
  handshake deadline. A read timeout per frame (silence inside one frame) would free those threads sooner; it is an M10
  item. There is still no cap on new connections per second.
- **Odin skips unknown members.** On Mono, Odin silently skips an ordinary key that names no member of the node's type.
  A legitimate snapshot comes from the host's live game of the same build and never has one, but the scanner does not
  refuse it today, so a planted unknown member loads (and does nothing). A scanner rule **E8** is planned: refuse any
  ordinary key that is not a serialised member of the node's type chain, from a generated per-type member table next to
  the alias table, with the same pin and drift check. It replaces the plan's "a planted unknown member refuses with the
  strict deserialise", which cannot hold.
- **Save copies are not in the beta (B8).** Message kinds 38-41 decode, but nothing sends or acts on them, and the
  session's capabilities offer no copy (no "copied to" or "your copy" lines) and no hosting of a Multiplayer save. When
  copies are wired, these are a release blocker first: accept a `SaveCopyHeader` only when a copy is expected, and
  rate-limit headers; keep at most about 3 held copies within a byte budget; allocate as chunks arrive (not 16 MiB per
  header); run the join's "every reference resolves in this game's content" check on install; cut held holder names
  with `LobbyLimits.Name` before they reach a roster (the security review's L1).
- **Supported builds only.** The type allowlist and the alias table are audited and pinned to the beta build in
  `KnownBuilds`. On another build no session starts, host or joiner ("This game version isn't supported by this
  version of the mod yet."); the next game update needs `docs/GAME-UPDATES.md` before anyone hosts.
- **The cast audit rests on the menu premise.** If a future game build lets a `GameManager` or `ObjectInfoManager` exist
  at the menu, the four live-state casts stop throwing. Wall 6 checks for exactly that before every deserialise.
- **A host can send a hostile but valid game.** The walls decide whether a snapshot is the game's own data; they cannot
  tell whether a host cheated in it. Only join people you trust.
- **The quick-rejoin rule does not cover** value differences that already existed at the drop (the pause-edge offset,
  which M3 found state-safe) or state outside M3's 14 areas (open windows, notifications, the camera). In an unseeded
  session it never applies after the first year event (a full rejoin instead, which is safe).
- **No commands yet.** The joiner's agency has `+Infinity` money (vanilla gives every non-player agency that at load)
  until M5 restores it, and each game simulates the other humans' agencies as non-players.
- **Direct connections only on one home network.** The first two-device session (2026-10-11) joined a laptop over
  home Wi-Fi to a wired host, rejoined after a real Wi-Fi drop and followed every speed. Across the internet waits for
  M9's Steam relay; there is no public-address option and no port forward until then.
- **The game's consoles still work in a session.** The Gravity Engine console (it changes the clock) and the developer
  console (it adds money) bypass the host. Turning both off in a session, with any console command the host runs shown
  to every player, is decided (DECISIONS.md, 2026-10-11) but not built yet. Debug keys, other mods' hotkeys and config
  are further ways around the host; the clock and M7's checksums are what catch those.
- **The device kit's collector and privacy.** The two-device kit's `collect.ps1` never takes a join code, key or
  certificate, but a lent laptop's user name, machine name and network addresses can appear in what it gathers.
  Removing those, and telling the person before it runs, is in progress.

## Testing the security

Each rule above gets a test before release: a fake client that sends unknown types, oversized and malformed
messages, other agencies' actions, replayed sequence numbers, a save with a disallowed type, and rich-text names.

M2: the fake client's hostile list passes against a game host with a real client joined (Task 10: 52 cases plus a
case-count check, 53 PASS lines, `ALL PASSED`; the host kept serving, and the Windows SChannel client and the game's
UnityTls interoperate). M2's final list was 54 cases (two more format cases); M4 added six for its key rules, so the
list is now 60 cases and passes in-process (`selftest`, 61 PASS lines, then `ALL PASSED`). The cases cover:
- frames and sizes: plain TCP instead of TLS, zero, negative, oversize and 65537-byte frames, an oversize `Hello`, deep
  JSON, trailing garbage, huge and non-finite numbers, speeds out of range, a type name in the data (ignored), a flood;
- strangers: malformed, short, long (also exactly 33 and 65 characters, one over the format rule) and non-hex invites
  and rejoin keys; stranger-learns-nothing (a bad invite plus a
  wrong version gets the invite refusal, with no version leak); a wrong pin; a wrong version (the invite is kept);
  a spent invite; an invite race;
- slow-loris before and after joining, TLS then silence, and a joined player who goes silent;
- order: an unknown type, a message in the wrong direction, a bad or repeated sequence number, a second `Hello`;
- rejoin: works once, spent and forged keys refused, a violator's rejoin key revoked, a rejoin that replaces the old
  link (which is told why);
- the key rules (M4): a lost Welcome on an invite and on each of the two rejoin keys, the host's versions shown only
  after the credential, a reserved invite surviving another player's confirm, a violation before the first frame
  spending the invite;
- rich-text names, the player cap, and the host still serving at the end.

M4 adds the hostile-snapshot list above and the join cases (a mismatch per category, a stalling joiner, a taken agency,
a sixth player, a speed request before loading). Each is fed through the real link and code and must end in a refusal;
a check nothing can trigger counts as missing. The code-level tests and their mutation sets are in TESTING.md. The fake
client runs them on every build (`five-selftest`, 31 cases; `host-selftest`, 62: the 54 hostile-host cases, then the
reconnect cap, a handshake breach, a refused reconnect, a quick-rejoin loop and the host leaving at each stage). In game
(M4, TESTING.md "Results"): every content category refused with the host still serving, a stalling joiner dropped, an
interrupted transfer, the host leaving during a load (cancelled before the extract), the hostile list against a real
joiner, a speed request before loading ignored, and a join code pointing outside the home network refused before any
connection.

Steam and commands come later.
