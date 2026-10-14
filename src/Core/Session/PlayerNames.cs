using System;

namespace SolarExpanseMultiplayer
{
    public static class PlayerNames
    {
        public const int MaxChars = 24;
        public const string BuiltInFallback = "Player";

        // M8: the name saved on the Multiplayer screen, else the Steam persona name read locally; M9: the checked
        // Steam name. The game glue replaces it; until then nobody has a name of their own.
        public static Func<string> Default = () => null;

        // What an empty name becomes: the text table's "name.fallback" line (BuiltInFallback if the seam fails).
        public static Func<string> Fallback = () => UiText.Get("name.fallback");

        // The name rule for every name this PC sends or shows as its own: cleaned like any peer text, cut to MaxChars,
        // never empty.
        public static string Clean(string raw)
        {
            string cleaned = TextSanitiser.Clean(raw, MaxChars, "");
            if (cleaned.Length > 0) return cleaned;
            string fallback = null;
            try { fallback = Fallback == null ? null : Fallback(); } catch (Exception) { }
            cleaned = TextSanitiser.Clean(fallback, MaxChars, "");
            return cleaned.Length > 0 ? cleaned : BuiltInFallback;
        }
    }
}
