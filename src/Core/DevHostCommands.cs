using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // The text of the host's dev triggers "kick" and "reload" (test profiles only: DevTriggers reads no file without
    // one). Anything that does not parse is refused, and the trigger does nothing.
    public static class DevHostCommands
    {
        // No JoinCodes value, so nothing reads a meaning of its own into it: a loaded player is told the host
        // removed them. Its rejoin keys go with the kick (HostKick.RevokesKeys), so it cannot come back on them.
        public const int KickCode = 99;

        // "<player>" or "<player> <code>": a joiner's id 1..4 and a goodbye code 1..99.
        public static bool Kick(string text, out int player, out int code)
        {
            player = code = 0;
            Match m = text == null ? Match.Empty : Regex.Match(text, @"^\s*([1-9])(?:\s+([1-9][0-9]?))?\s*\z");
            if (!m.Success) return false;
            int p = m.Groups[1].Value[0] - '0';
            if (p >= JoinLimits.MaxPlayers) return false;
            player = p;
            code = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : KickCode;
            return true;
        }

        // The save to load: an empty text is the save the game last loaded or saved (current); otherwise a save name
        // of letters, digits, spaces, '_' and '-' only (no path can be named), at most 64. Null, with why, otherwise.
        public static string ReloadSave(string text, string current, out string why)
        {
            why = null;
            if (text == null) { why = "no trigger text"; return null; }
            if (text.Length == 0)
            {
                if (string.IsNullOrEmpty(current) || current.Trim().Length == 0)
                {
                    why = "no save loaded or saved yet, so there is no current save";
                    return null;
                }
                return current;
            }
            if (!Regex.IsMatch(text, @"^[A-Za-z0-9 _-]{1,64}\z"))
            {
                why = "not a save name (letters, digits, spaces, '_' and '-', at most 64)";
                return null;
            }
            return text;
        }

        // The dev trigger "load-save <name>" (any test-profile game, not only a host): a solo load through the game's
        // own load, to prove a game after a session behaves like vanilla. Only at the main menu and outside a session;
        // the name is as for a reload, with no current save to fall back on, so an empty text is refused. Null, with
        // why, otherwise.
        public static string LoadSave(string text, bool atMenu, bool inSession, out string why)
        {
            why = null;
            if (inSession) { why = "a session is on (a solo load is for after it)"; return null; }
            if (!atMenu) { why = "not at the main menu"; return null; }
            return ReloadSave(text, null, out why);
        }

        // The dev trigger "speed" in a solo game (in a session the clock's own handlers take it): a number 0..max, as a
        // click would set it, once a save is loaded and no session is on. False, with why, otherwise.
        public static bool SoloSpeed(string text, bool atMenu, bool inSession, float max, out float speed,
            out string why)
        {
            speed = 0f; why = null;
            if (inSession) { why = "a session is on (its clock takes the trigger)"; return false; }
            if (atMenu) { why = "at the main menu (no game is loaded)"; return false; }
            if (text == null || !float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out speed) || !(speed >= 0f && speed <= max))
            {
                speed = 0f;
                why = "not a speed between 0 and " + max;
                return false;
            }
            return true;
        }
    }
}
