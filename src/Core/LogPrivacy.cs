using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // What a player's own log may say. Players post semp.log for help (the beta has no log collector), so on a player's
    // install (no test profile) it names a network by its kind, never its address, another player by their number in
    // the session, never their Steam name (the screens show the names), and a path relative to the game folder with no
    // Windows user name. Test profiles keep the full lines the dev tools read.
    public static class LogPrivacy
    {
        // "loopback", "Tailscale", "home network", or "other" for an address none of those (never the address itself).
        public static string Kind(IPAddress a)
        {
            if (a == null) return "no";
            if (IPAddress.IsLoopback(a)) return "loopback";
            if (BindAddress.IsTailscale(a)) return "Tailscale";
            if (BindAddress.Allowed(a)) return "home network";
            return "other";
        }

        // Where a link goes: its kind and port for a player, the address too in a test profile.
        public static string Where(IPAddress a, int port, bool player)
        {
            return player ? "a " + Kind(a) + " address, port " + port : a + ":" + port;
        }

        // ---- the lines of the sites that name an address, a player or a path ----

        public static string HostListening(IPAddress a, int port, string pinStart, string build, bool player)
        {
            return "host: listening on " + Where(a, port, player) + " (TLS 1.2; certificate sha256 " + pinStart
                + "...; build " + build + ")" + (!player && BindAddress.IsTailscale(a) ? "; a Tailscale address" : "");
        }

        public static string ClientConnecting(IPAddress a, int port, bool rejoin, bool player)
        {
            return "client: connecting to " + Where(a, port, player) + (rejoin ? " (rejoin)" : "");
        }

        // "player N joined" and "joined as player N" stay as they are: the dev tools wait for them.
        public static string HostJoined(int playerId, string name, bool player)
        {
            return "host: player " + playerId + " joined" + (player ? "" : ": " + name);
        }

        public static string ClientJoined(int playerId, string hostName, bool player)
        {
            return "client: joined as player " + playerId + (player ? "" : " (host " + hostName + ")");
        }

        public static string ViewName(string name, bool player)
        {
            return player ? "" : " '" + TextSanitiser.OneLine(name) + "'";
        }

        static readonly Regex UserFolder = new Regex(@"([A-Za-z]:)?\\Users\\[^\\]+", RegexOptions.IgnoreCase);

        // A path as the log shows it: relative to the game folder when inside it ("<game>\..."), else with any
        // C:\Users\<name> part replaced by "<user folder>", so no Windows user name is written.
        public static string Path(string path, string gameRoot)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                if (!string.IsNullOrEmpty(gameRoot))
                {
                    string root = System.IO.Path.GetFullPath(gameRoot).TrimEnd('\\', '/');
                    string full = System.IO.Path.GetFullPath(path);
                    if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return "<game>";
                    if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                        return "<game>" + full.Substring(root.Length);
                }
            }
            catch (Exception) { }
            return UserFolder.Replace(path, "<user folder>");
        }
    }
}
