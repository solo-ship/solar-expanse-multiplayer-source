using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host's Steam bans for the screens: the running host's door when it listens on Steam (so the door hears the
    // change at once), else the ban file itself (a host without Steam, or no session at all). The Steam id stays in
    // BannedPlayer, in memory: it is never shown or logged.
    public static class BanBook
    {
        public static List<BannedPlayer> List()
        {
            try
            {
                SessionHost h = Session.Host;
                if (h != null && h.SteamDoorOpen) return h.SteamBans();
                return new HostSteamDoor(null, SessionHost.BanFile, null).Bans();
            }
            catch (Exception e)
            {
                Log.Warn("ui: the ban list could not be read (" + e.GetType().Name + ")");
                return new List<BannedPlayer>();
            }
        }

        public static bool Unban(ulong steamId)
        {
            try
            {
                SessionHost h = Session.Host;
                if (h != null && h.SteamDoorOpen) return h.Unban(steamId);
                bool done = new HostSteamDoor(null, SessionHost.BanFile, null).Unban(steamId);
                if (done) Log.Info("ui: a ban was lifted (steam-bans.txt rewritten)");
                return done;
            }
            catch (Exception e)
            {
                Log.Warn("ui: the ban could not be lifted (" + e.GetType().Name + ")");
                return false;
            }
        }
    }
}
