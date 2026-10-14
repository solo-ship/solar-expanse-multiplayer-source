namespace SolarExpanseMultiplayer
{
    // The host's dev trigger "reload [save name]" (test sessions only): the host loads a save mid-session, as its
    // player would from the game's load window, so the host-load path (the stream reset, every client's full rejoin)
    // can be driven without a click. The name is one of the game's own save names, never a path.
    public static class HostReload
    {
        public const int MaxName = 60;

        // The save to load: "" for the game's newest save, the name otherwise; null when the text is refused (why
        // says why).
        public static string SaveName(string text, out string why)
        {
            why = null;
            string name = text == null ? "" : text.Trim();
            if (name.Length > MaxName)
            {
                why = "the save name is over " + MaxName + " characters";
                return null;
            }
            foreach (char c in name)
            {
                if (c < ' ' || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<'
                    || c == '>' || c == '|')
                {
                    why = "a save name, not a path: no separators, wildcards or control characters";
                    return null;
                }
            }
            if (name == "." || name == ".." || name.StartsWith("..", System.StringComparison.Ordinal))
            {
                why = "a save name, not a path";
                return null;
            }
            return name;
        }
    }
}
