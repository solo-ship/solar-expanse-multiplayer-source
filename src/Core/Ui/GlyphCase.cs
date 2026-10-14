namespace SolarExpanseMultiplayer
{
    // The character a TMP text actually draws for c under its font style, worked out exactly as TMP does it when it
    // lays out a character (Unity.TextMeshPro, TMP_Text: UpperCase, then LowerCase, then SmallCaps, each with the
    // CURRENT culture's char.ToUpper/ToLower). The glyph check must ask about this character: an upper-case style
    // draws a small e with diaeresis as the capital one, and a font can hold one without the other.
    public static class GlyphCase
    {
        // TMPro.FontStyles' values.
        public const int LowerCase = 0x8, UpperCase = 0x10, SmallCaps = 0x20;

        public static char Drawn(char c, int fontStyle)
        {
            if ((fontStyle & UpperCase) == UpperCase) return char.IsLower(c) ? char.ToUpper(c) : c;
            if ((fontStyle & LowerCase) == LowerCase) return char.IsUpper(c) ? char.ToLower(c) : c;
            if ((fontStyle & SmallCaps) == SmallCaps && char.IsLower(c)) return char.ToUpper(c);
            return c;
        }
    }
}
