using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // Text from other players (names now, chat later) before it is shown or logged: no TextMeshPro rich-text tags
    // (<link>, <sprite>, <size>, ...), no '<', '>' or '\' (TextMeshPro can turn "<" into '<'), no control,
    // invisible or direction-changing characters (including the "letters" that draw nothing: the Hangul fillers, the braille
    // blank and the combining grapheme joiner), single spaces, a length cap. Never returns null or empty, and returns the
    // fallback when no letter, digit, punctuation or symbol is left (a name of only combining marks draws nothing either).
    // At most four combining marks (real Tibetan and pointed Hebrew stack three) stay on one base character.
    public static class TextSanitiser
    {
        static readonly Regex Tag = new Regex("<[^<>]*>");
        const int MaxInput = 4096;   // bounds the work on hostile input

        const int MaxMarksPerBase = 4;   // "Zalgo" names: more combining marks than this on one base character are dropped

        // Code points that draw nothing or only change how their neighbours look, dropped outright. These are PINNED here (pairs
        // of first, last), not left to the category tables: the tests run on .NET Framework and the game on Mono, whose Unicode
        // data can differ. The first group is "letters" and marks that are not Format characters (cloud review C-2):
        // the Hangul fillers, the braille blank, the combining grapheme joiner, the soft hyphen and the Khmer inherent vowels.
        static readonly int[] DroppedRanges =
        {
            0x00AD, 0x00AD, 0x034F, 0x034F, 0x061C, 0x061C, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x180B, 0x180E,
            0x200B, 0x200F, 0x202A, 0x202E, 0x2060, 0x206F, 0x2800, 0x2800, 0x3164, 0x3164, 0xFEFF, 0xFEFF,
            0xFFA0, 0xFFA0, 0xFFF9, 0xFFFB,
            0xE0000, 0xE0FFF,                                 // tag characters and the variation selectors supplement
            0x1D159, 0x1D159,                                 // musical null notehead: blank in the fonts that have it
            0x1D165, 0x1D169, 0x1D16D, 0x1D182, 0x1D185, 0x1D18B, 0x1D1AA, 0x1D1AD,   // musical combining marks and format controls
        };

        // Code points that are visible whatever the category tables say: ASCII, Latin-1 (without the soft hyphen) and the spacing
        // modifier letters. So '_' (ConnectorPunctuation), the superscript two (OtherNumber) and the modifier h (ModifierLetter) pin.
        static readonly int[] VisibleRanges = { 0x21, 0x7E, 0xA1, 0xAC, 0xAE, 0xFF, 0x2B0, 0x2FF };

        static bool InRanges(int[] ranges, int cp)
        {
            for (int i = 0; i < ranges.Length; i += 2) if (cp >= ranges[i] && cp <= ranges[i + 1]) return true;
            return false;
        }

        static UnicodeCategory CategoryOf(int cp)
        {
            return cp < 0x10000 ? char.GetUnicodeCategory((char)cp) : char.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);
        }

        static bool IsMark(UnicodeCategory cat)
        {
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark || cat == UnicodeCategory.EnclosingMark;
        }

        static bool IsVisibleCategory(UnicodeCategory cat)
        {
            switch (cat)
            {
                case UnicodeCategory.UppercaseLetter: case UnicodeCategory.LowercaseLetter: case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter: case UnicodeCategory.OtherLetter:
                case UnicodeCategory.DecimalDigitNumber: case UnicodeCategory.LetterNumber: case UnicodeCategory.OtherNumber:
                case UnicodeCategory.ConnectorPunctuation: case UnicodeCategory.DashPunctuation: case UnicodeCategory.OpenPunctuation:
                case UnicodeCategory.ClosePunctuation: case UnicodeCategory.InitialQuotePunctuation: case UnicodeCategory.FinalQuotePunctuation:
                case UnicodeCategory.OtherPunctuation:
                case UnicodeCategory.MathSymbol: case UnicodeCategory.CurrencySymbol: case UnicodeCategory.ModifierSymbol: case UnicodeCategory.OtherSymbol:
                    return true;
            }
            return false;
        }

        // True when the text holds a letter, digit, punctuation or symbol. A combining mark (Mn, Mc, Me) is never visible by
        // itself, and neither is anything in DroppedRanges.
        static bool HasVisible(string r)
        {
            for (int i = 0; i < r.Length; i++)
            {
                int cp = r[i];
                if (char.IsHighSurrogate(r[i]) && i + 1 < r.Length && char.IsLowSurrogate(r[i + 1])) { cp = char.ConvertToUtf32(r[i], r[i + 1]); i++; }
                else if (char.IsSurrogate(r[i])) continue;
                if (InRanges(DroppedRanges, cp)) continue;
                if (InRanges(VisibleRanges, cp) || IsVisibleCategory(CategoryOf(cp))) return true;
            }
            return false;
        }

        public static string Clean(string s, int maxLength, string fallback)
        {
            if (s == null) return fallback;
            if (s.Length > MaxInput) s = s.Substring(0, MaxInput);
            s = Tag.Replace(s, "");
            var sb = new StringBuilder(s.Length);
            bool space = false;
            int marks = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                int cp = c;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) continue;   // a lone surrogate is dropped
                    cp = char.ConvertToUtf32(c, s[i + 1]);
                    i++;
                }
                else if (char.IsLowSurrogate(c)) continue;
                if (cp == '<' || cp == '>' || cp == '\\' || InRanges(DroppedRanges, cp)) continue;
                UnicodeCategory cat = CategoryOf(cp);
                if (cat == UnicodeCategory.Format || cat == UnicodeCategory.PrivateUse || cat == UnicodeCategory.OtherNotAssigned || cat == UnicodeCategory.Surrogate) continue;
                if (cp < 0x10000 && (char.IsWhiteSpace(c) || char.IsControl(c)))
                {
                    marks = 0;
                    if (sb.Length > 0) space = true;
                    continue;
                }
                if (IsMark(cat))
                {
                    if (marks >= MaxMarksPerBase) continue;      // a tower of accents would overflow the lines around it
                    marks++;
                }
                else marks = 0;
                Flush(sb, ref space);
                if (cp < 0x10000) sb.Append(c); else sb.Append(char.ConvertFromUtf32(cp));
            }
            string r = sb.ToString();
            if (r.Length > maxLength)
            {
                int cut = maxLength;
                if (cut > 0 && char.IsHighSurrogate(r[cut - 1])) cut--;   // never split a surrogate pair
                r = r.Substring(0, cut).TrimEnd();
            }
            return r.Length == 0 || !HasVisible(r) ? fallback : r;
        }

        // For a log line made of text we did not write (exception messages, a peer's close reason): every control character
        // (CR, LF, NUL, ESC, ...) and Unicode line/paragraph separator becomes a space, so the log stays one plain-text
        // line per entry and greppable. Trailing blanks go (Mono's socket errors end in NULs). Never null.
        public static string OneLine(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsControl(c) || IsSeparator(c) ? ' ' : c);
            return sb.ToString().TrimEnd();
        }

        static bool IsSeparator(char c)
        {
            UnicodeCategory cat = char.GetUnicodeCategory(c);
            return cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator;
        }

        static void Flush(StringBuilder sb, ref bool space)
        {
            if (!space) return;
            sb.Append(' ');
            space = false;
        }
    }
}
