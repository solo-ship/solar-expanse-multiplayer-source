using System;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // What the screen's font can draw (Design decision 13). The game side answers from the font and its fallbacks
    // with HasCharacters and tryAddCharacter = false, so asking never grows the shared font atlas.
    public interface IGlyphs
    {
        bool Has(char c);
        float Width(string s);
    }

    // Text from another player, already through TextSanitiser, made safe to put on screen: a character the font
    // cannot draw becomes '?' and the drawn width is capped with an ellipsis.
    public static class PeerText
    {
        const int MaxInput = 4096;   // bounds the work on hostile input

        public static string ForScreen(string cleaned, IGlyphs glyphs, float maxWidth)
        {
            if (string.IsNullOrEmpty(cleaned)) return "";
            if (!(maxWidth > 0)) return "";
            if (cleaned.Length > MaxInput) cleaned = cleaned.Substring(0, MaxInput);
            string drawn = Replace(cleaned, glyphs);
            if (glyphs == null || Fits(glyphs, drawn, maxWidth)) return drawn;

            string ellipsis = glyphs.Has('\u2026') ? "\u2026" : "...";
            // the longest prefix that still fits with the ellipsis after it (widths only grow with length)
            int lo = 0, hi = drawn.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Fits(glyphs, drawn.Substring(0, mid) + ellipsis, maxWidth)) lo = mid; else hi = mid - 1;
            }
            string kept = drawn.Substring(0, lo).TrimEnd();
            string result = kept + ellipsis;
            return Fits(glyphs, result, maxWidth) ? result : "";
        }

        static bool Fits(IGlyphs glyphs, string s, float maxWidth)
        {
            float w = glyphs.Width(s);
            return w <= maxWidth;   // NaN is not "fits"
        }

        // Every character the font has stays; every other one, and every control character, is one '?'. A character
        // outside the basic plane is two chars and cannot be asked about, so it is one '?' as well.
        static string Replace(string s, IGlyphs glyphs)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append('?');
                    i++;
                }
                else if (char.IsControl(c) || char.IsSurrogate(c) || (glyphs == null ? c > '~' : !glyphs.Has(c)))
                    sb.Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
