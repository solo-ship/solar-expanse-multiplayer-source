using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Wraps a line that may hold another player's text into lines that each fit a width, every one through
    // PeerText.ForScreen (a character the font cannot draw is '?'; nothing is ever read as markup). A screen puts each
    // line on its own text, or joins them with its own line breaks: PeerText turns a line break inside peer text into
    // '?', so a break can only come from here. Past maxLines the last line ends with PeerText's ellipsis.
    public static class PeerWrap
    {
        public static List<string> Lines(string cleaned, IGlyphs glyphs, float width, int maxLines)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(cleaned) || !(width > 0) || maxLines < 1) return lines;
            // the characters as they will be drawn, before any measuring; a font that cannot measure fits nothing
            string drawn = PeerText.ForScreen(cleaned, glyphs, float.MaxValue);
            if (drawn.Length == 0) return lines;
            if (glyphs == null) { lines.Add(drawn); return lines; }
            int pos = 0;
            while (pos < drawn.Length && lines.Count < maxLines)
            {
                while (pos < drawn.Length && drawn[pos] == ' ') pos++;
                if (pos >= drawn.Length) break;
                if (lines.Count == maxLines - 1)
                {
                    string last = PeerText.ForScreen(drawn.Substring(pos), glyphs, width);
                    if (last.Length > 0) lines.Add(last);
                    break;
                }
                int end = Fit(drawn, pos, glyphs, width);
                if (end <= pos) break;   // not even one character fits
                lines.Add(drawn.Substring(pos, end - pos).TrimEnd());
                pos = end;
            }
            return lines;
        }

        // Where the line that starts at pos ends: after the last whole word that fits, or, when not even the first
        // word fits, after the most characters of it that do.
        static int Fit(string s, int pos, IGlyphs glyphs, float width)
        {
            if (Fits(glyphs, s.Substring(pos), width)) return s.Length;
            int lastBreak = -1;
            for (int i = pos + 1; i < s.Length; i++)
            {
                if (s[i] != ' ') continue;
                if (!Fits(glyphs, s.Substring(pos, i - pos), width)) break;
                lastBreak = i;
            }
            if (lastBreak > pos) return lastBreak;
            int lo = pos, hi = s.Length;   // the longest piece of the word that fits (widths only grow with length)
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Fits(glyphs, s.Substring(pos, mid - pos), width)) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        static bool Fits(IGlyphs glyphs, string s, float width)
        {
            float w = glyphs.Width(s);
            return w <= width;   // NaN is not "fits"
        }
    }
}
