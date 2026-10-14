using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The on-screen status label (SempRoot): the status, then the first few lines of a refusal's report, each capped
    // and wrapped to the window's width so nothing runs off its edge. A refusal shown as one cut-off line told the
    // player nothing; the full report stays in the log until M8's report screen. The game wraps by its font's own
    // width for each character (GlyphWidths), in pixels; the column form (an East Asian wide character takes two
    // columns) is kept for tools without a font. Pure, so it is tested here rather than in the game.
    // The status is this game's own text (any peer text in it was cleaned where it came in), so it is only made one
    // line: a folder name keeps its backslashes. Report lines come from the host and are cleaned (TextSanitiser).
    public static class ScreenLines
    {
        public const int ReportShown = 4;                                // report lines on screen; the rest counted
        public const int MinWidth = 20;                                  // in widths of 'x'; a narrower window wraps
        public const int MaxStatusChars = 600;                           // the status line
        public const int MaxLineChars = JoinLimits.MaxReportLineChars;   // one report line, after cleaning

        // width in columns: a Latin character takes one, an East Asian wide one two.
        public static List<string> Of(string status, IList<string> report, int width)
        {
            return Of(status, report, width, ColumnsOf);
        }

        // width in the units charWidth gives for a code point (the game: pixels from its label font).
        public static List<string> Of(string status, IList<string> report, double width, Func<int, double> charWidth)
        {
            if (charWidth == null) throw new ArgumentNullException("charWidth");
            double min = MinWidth * Sane(charWidth('x'));
            if (!(width >= min)) width = min;   // NaN too
            var lines = new List<string>();
            Wrap(Cap(TextSanitiser.OneLine(status), MaxStatusChars), width, charWidth, lines);
            if (report == null) return lines;
            int shown = 0, more = 0;
            foreach (string raw in report)
            {
                string l = TextSanitiser.Clean(raw, MaxLineChars, "");
                if (l.Length == 0) continue;
                if (shown < ReportShown) { Wrap(l, width, charWidth, lines); shown++; }
                else more++;
            }
            if (more > 0) lines.Add(JoinText.MoreInLog(more));
            return lines;
        }

        // A measure that makes no sense (NaN, negative, infinite) counts as nothing, so the wrap always ends.
        static double Sane(double w) { return w >= 0 && w < double.PositiveInfinity ? w : 0; }

        static string Cap(string s, int max)
        {
            if (s.Length <= max) return s;
            int cut = max;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;   // never split a surrogate pair
            return s.Substring(0, cut);
        }

        // The East Asian wide and full-width blocks (pairs of first, last): Hangul Jamo, CJK punctuation and
        // radicals, kana, CJK ideographs, Yi, Hangul syllables, compatibility ideographs, vertical and full-width
        // forms, the supplementary ideographs.
        static readonly int[] Wide =
        {
            0x1100, 0x115F, 0x2E80, 0x303E, 0x3041, 0x33FF, 0x3400, 0x4DBF, 0x4E00, 0x9FFF, 0xA000, 0xA4CF,
            0xAC00, 0xD7A3, 0xF900, 0xFAFF, 0xFE30, 0xFE4F, 0xFF00, 0xFF60, 0xFFE0, 0xFFE6, 0x20000, 0x3FFFD,
        };

        static bool Pair(string text, int i)
        {
            return char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
        }

        static int CodePoint(string text, int i)
        {
            return Pair(text, i) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
        }

        // Columns of a code point: two for a wide one, else one.
        static double ColumnsOf(int cp)
        {
            for (int r = 0; r < Wide.Length; r += 2) if (cp >= Wide[r] && cp <= Wide[r + 1]) return 2;
            return 1;
        }

        // The columns a line takes on screen.
        public static int Width(string text)
        {
            int n = 0;
            for (int i = 0; i < text.Length; i += Pair(text, i) ? 2 : 1) n += (int)ColumnsOf(CodePoint(text, i));
            return n;
        }

        // Breaks at the last space that fits; a word wider than the width is cut, never inside a surrogate pair. A
        // character wider than the whole width still takes a line of its own, so the wrap always moves on.
        static void Wrap(string text, double width, Func<int, double> charWidth, List<string> into)
        {
            int at = 0;
            while (at < text.Length)
            {
                while (at < text.Length && text[at] == ' ') at++;
                if (at >= text.Length) break;
                int i = at, space = -1;
                double used = 0;
                while (i < text.Length)
                {
                    if (text[i] == ' ') space = i;
                    double w = Sane(charWidth(CodePoint(text, i)));
                    if (used + w > width) break;
                    used += w;
                    i += Pair(text, i) ? 2 : 1;
                }
                if (i == at) i = at + (Pair(text, at) ? 2 : 1);
                if (i >= text.Length) { into.Add(text.Substring(at)); break; }
                int cut = space > at ? space : i;
                into.Add(text.Substring(at, cut - at).TrimEnd());
                at = cut;
            }
        }
    }
}
