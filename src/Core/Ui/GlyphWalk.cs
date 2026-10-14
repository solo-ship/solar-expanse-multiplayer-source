using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // One font as TMP's character lookup sees it (the game side wraps a TMP_FontAsset).
    public interface IGlyphFont
    {
        int Id { get; }
        string Name { get; }
        bool Dynamic { get; }
        // Its character table already holds u: drawing it changes nothing.
        bool InAtlas(uint u);
        // Asked of a dynamic font only: would drawing u add it here? True when the font's own file has the glyph
        // (or the font cannot tell), false when the file lacks it (TMP then moves on, adding nothing).
        bool WouldAdd(uint u);
        // The weight or italic face TMP tries first for this text, or null.
        IGlyphFont Typeface { get; }
        IList<IGlyphFont> Fallbacks { get; }
    }

    // What the walk found for one character: drawable or not, and the font that decided (null: no font has it).
    public struct GlyphAnswer
    {
        public bool Drawable;
        public IGlyphFont By;

        public override string ToString()
        {
            if (By == null) return "? (no font has it)";
            return Drawable ? "drawn by " + By.Name : "? (" + By.Name + " would add it)";
        }
    }

    // Whether a TMP text can draw a character without growing any font atlas (PeerText's IGlyphs). It walks TMP's own
    // lookup order (TMP_FontAssetUtilities.GetCharacterFromFontAsset_Internal): at each font the weight/italic face
    // first, then the font's own table; a dynamic font missing the character is then asked to ADD it, and only a font
    // whose file lacks it lets the walk go on; then the fallbacks, depth first, skipping an empty slot as TMP does;
    // then TMP Settings' fallbacks and the default font. The answer is true only where the character is already in a
    // table, false at the first font that would add it ('?' on screen), and false when no font has it.
    public static class GlyphWalk
    {
        public static bool Drawable(uint u, IGlyphFont font, IList<IGlyphFont> settingsFallbacks,
                                    IGlyphFont defaultFont)
        {
            return Answer(u, font, settingsFallbacks, defaultFont).Drawable;
        }

        public static GlyphAnswer Answer(uint u, IGlyphFont font, IList<IGlyphFont> settingsFallbacks,
                                         IGlyphFont defaultFont)
        {
            var seen = new HashSet<int>();
            var a = new GlyphAnswer();
            bool? r = Walk(font, u, seen, ref a);
            if (r.HasValue) return a;
            if (settingsFallbacks != null)
                foreach (IGlyphFont f in settingsFallbacks)
                {
                    r = Walk(f, u, seen, ref a);
                    if (r.HasValue) return a;
                }
            Walk(defaultFont, u, seen, ref a);
            return a;
        }

        // true: drawn from a table as it is; false: drawing would add it; null: none of these fonts has it. The
        // deciding font goes into the answer.
        static bool? Walk(IGlyphFont f, uint u, HashSet<int> seen, ref GlyphAnswer a)
        {
            if (f == null || !seen.Add(f.Id)) return null;
            if (f.Typeface != null)
            {
                bool? t = Step(f.Typeface, u, ref a);
                if (t.HasValue) return t;
            }
            bool? s = Step(f, u, ref a);
            if (s.HasValue) return s;
            if (f.Fallbacks != null)
                foreach (IGlyphFont g in f.Fallbacks)
                {
                    if (g == null) continue;   // TMP skips an empty fallback slot and goes on
                    bool? r = Walk(g, u, seen, ref a);
                    if (r.HasValue) return r;
                }
            return null;
        }

        static bool? Step(IGlyphFont f, uint u, ref GlyphAnswer a)
        {
            if (f.InAtlas(u)) { a.Drawable = true; a.By = f; return true; }
            if (f.Dynamic && f.WouldAdd(u)) { a.Drawable = false; a.By = f; return false; }
            return null;
        }
    }
}
