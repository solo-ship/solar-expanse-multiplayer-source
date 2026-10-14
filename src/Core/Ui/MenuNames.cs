namespace SolarExpanseMultiplayer
{
    // The player's own name as the menu keeps it (NameStore, player-name.txt): what was typed or read back, cleaned
    // like any peer text and cut to the name rule's length; "" when nothing is left (the file is then forgotten).
    public static class MenuNames
    {
        public static string Stored(string raw)
        {
            return TextSanitiser.Clean(raw, PlayerNames.MaxChars, "");
        }
    }
}
