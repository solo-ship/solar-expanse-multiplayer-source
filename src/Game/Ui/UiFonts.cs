using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace SolarExpanseMultiplayer
{
    // PeerText's font (IGlyphs) for a TMP text. A character counts as drawable only if drawing it can never grow a
    // font atlas: GlyphWalk follows TMP's own lookup order, and at a dynamic font that lacks the character it asks
    // the font's FILE (the glyph-index lookup TMP itself uses, which adds nothing) whether TMP would add it there.
    // A file that has it means '?' (drawing would add it); one that lacks it lets the walk go on to the next font,
    // so a static font further down (Binggrae's Hangul) still counts. The mod never adds a glyph.
    public sealed class TmpGlyphs : IGlyphs
    {
        readonly TMP_Text text;

        public TmpGlyphs(TMP_Text text) { this.text = text; }

        public bool Has(char c)
        {
            if (text == null || text.font == null || char.IsSurrogate(c) || char.IsControl(c)) return false;
            uint u = GlyphCase.Drawn(c, (int)text.fontStyle);
            try { return UiFonts.Drawable(text, u); }
            catch (Exception) { return false; }
        }

        // Only called by PeerText on text already filtered by Has (plus '?' and the ellipsis it checked).
        public float Width(string s)
        {
            if (text == null || string.IsNullOrEmpty(s)) return 0f;
            return text.GetPreferredValues(s).x;
        }
    }

    public static class UiFonts
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo MissingFromFile =
            typeof(TMP_FontAsset).GetField("m_MissingUnicodesFromFontFile", Private);

        // Puts peer text (already through TextSanitiser) on a mod text: never read as markup, characters the font
        // cannot draw as '?', cut with an ellipsis to the text's own width (or maxWidth when given).
        public static string SetPeerText(TMP_Text text, string cleaned, float maxWidth = -1f)
        {
            if (text == null) return "";
            text.richText = false;
            text.parseCtrlCharacters = false;
            // a text laid out by a layout group gets its width at the next layout pass: run it now
            if (!(maxWidth > 0f)) Canvas.ForceUpdateCanvases();
            float width = maxWidth > 0f ? maxWidth : text.rectTransform.rect.width;
            string shown = PeerText.ForScreen(cleaned, new TmpGlyphs(text), width);
            text.text = shown;
            LogGlyphs(text, cleaned, width, shown);
            return shown;
        }

        const int GlyphLogLimit = 40;
        static int glyphLogs;

        // Test seams only, at most GlyphLogLimit lines a run: for each character past ASCII (up to eight), which
        // font decided and whether it is drawn or shown as '?'.
        static void LogGlyphs(TMP_Text text, string cleaned, float width, string shown)
        {
            if (glyphLogs >= GlyphLogLimit || string.IsNullOrEmpty(cleaned) || !PartsBench.SeamsOpen()) return;
            var parts = new List<string>();
            var done = new HashSet<char>();
            foreach (char c in cleaned)
            {
                if (c < 0x80 || char.IsSurrogate(c) || !done.Add(c)) continue;
                if (parts.Count == 8) { parts.Add("..."); break; }
                uint u = GlyphCase.Drawn(c, (int)text.fontStyle);
                string a;
                try { a = Answer(text, u).ToString(); } catch (Exception e) { a = "? (" + e.GetType().Name + ")"; }
                parts.Add("U+" + ((int)c).ToString("X4") + " " + a);
            }
            if (parts.Count == 0) return;
            glyphLogs++;
            Log.Info("ui: peer text glyphs in '" + text.name + "' (width " + width.ToString("0.0") + ", shown " +
                     shown.Length + " chars" + (glyphLogs == GlyphLogLimit ? "; the last such line this run" : "") +
                     "): " + string.Join("; ", parts.ToArray()));
        }

        // TMP's own lookup order for one character (GlyphWalk); false at the first font where drawing would add it.
        internal static bool Drawable(TMP_Text text, uint u) { return Answer(text, u).Drawable; }

        internal static GlyphAnswer Answer(TMP_Text text, uint u)
        {
            bool bold = (text.fontStyle & FontStyles.Bold) == FontStyles.Bold;
            FontWeight weight = bold ? FontWeight.Bold : text.fontWeight;
            bool italic = (text.fontStyle & FontStyles.Italic) == FontStyles.Italic;
            var settings = new List<IGlyphFont>();
            if (TMP_Settings.fallbackFontAssets != null)
                foreach (TMP_FontAsset f in TMP_Settings.fallbackFontAssets)
                    settings.Add(TmpGlyphFont.Of(f, weight, italic));
            return GlyphWalk.Answer(u, TmpGlyphFont.Of(text.font, weight, italic), settings,
                                    TmpGlyphFont.Of(TMP_Settings.defaultFontAsset, weight, italic));
        }

        // Would TMP add u to this dynamic font when asked (TMP_FontAsset.TryAddCharacterInternal)? It gives up at
        // once for a character its file was already found to lack, and when the font's file does not load; otherwise
        // it adds whatever the file has (a no-break space and the two hyphens through their stand-ins). The file is
        // asked with FontEngine.TryGetGlyphIndex, which reads the loaded face and adds nothing; the face is loaded as
        // TMP loads it before each lookup of its own. A lookup that fails answers "would add", the safe side.
        internal static bool WouldAdd(TMP_FontAsset f, uint u)
        {
            var missing = MissingFromFile != null ? MissingFromFile.GetValue(f) as HashSet<uint> : null;
            if (missing != null && missing.Contains(u)) return false;
            try
            {
                Font source = f.sourceFontFile;
                if (source == null) return false;
                if (FontEngine.LoadFontFace(source, f.faceInfo.pointSize) != FontEngineError.Success) return false;
                uint index;
                if (FontEngine.TryGetGlyphIndex(u, out index) && index != 0) return true;
                uint stand = u == 160u ? 32u : u == 173u || u == 8209u ? 45u : 0u;
                return stand != 0u && FontEngine.TryGetGlyphIndex(stand, out index) && index != 0;
            }
            catch (Exception) { return true; }
        }

        internal static TMP_FontAsset Typeface(TMP_FontAsset font, FontWeight weight, bool italic)
        {
            TMP_FontWeightPair[] table = font.fontWeightTable;
            int i = (int)weight / 100;
            if (table == null || i < 0 || i >= table.Length) return null;
            return italic ? table[i].italicTypeface : table[i].regularTypeface;
        }

        // Every font a text can draw from, with its population mode and counts, so a check before and after can
        // prove an atlas did not grow (the default font and TMP Settings' fallbacks included).
        public static string AtlasState(TMP_FontAsset font)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<int>();
            AtlasOf(font, sb, seen);
            if (TMP_Settings.fallbackFontAssets != null)
                foreach (TMP_FontAsset f in TMP_Settings.fallbackFontAssets) AtlasOf(f, sb, seen);
            AtlasOf(TMP_Settings.defaultFontAsset, sb, seen);
            return sb.ToString();
        }

        static void AtlasOf(TMP_FontAsset f, StringBuilder sb, HashSet<int> seen)
        {
            if (f == null || !seen.Add(f.GetInstanceID())) return;
            sb.Append(f.name).Append('[').Append(f.atlasPopulationMode).Append(" tex ")
              .Append(f.atlasTextures != null ? f.atlasTextures.Length : 0)
              .Append(" glyphs ").Append(f.glyphTable.Count)
              .Append(" chars ").Append(f.characterTable.Count).Append("] ");
            if (f.fallbackFontAssetTable != null)
                foreach (TMP_FontAsset g in f.fallbackFontAssetTable) AtlasOf(g, sb, seen);
        }
    }

    // One TMP font asset as GlyphWalk sees it, for one text's weight and italic (TMP tries that face first).
    sealed class TmpGlyphFont : IGlyphFont
    {
        readonly TMP_FontAsset font;
        readonly FontWeight weight;
        readonly bool italic, styled;

        TmpGlyphFont(TMP_FontAsset font, FontWeight weight, bool italic, bool styled)
        {
            this.font = font; this.weight = weight; this.italic = italic; this.styled = styled;
        }

        public static TmpGlyphFont Of(TMP_FontAsset font, FontWeight weight, bool italic)
        {
            return font == null ? null : new TmpGlyphFont(font, weight, italic, true);
        }

        public int Id { get { return font.GetInstanceID(); } }
        public string Name { get { return font.name; } }
        public bool Dynamic { get { return font.atlasPopulationMode == AtlasPopulationMode.Dynamic; } }

        public bool InAtlas(uint u)
        {
            Dictionary<uint, TMP_Character> table = font.characterLookupTable;
            return table != null && table.ContainsKey(u);
        }

        public bool WouldAdd(uint u) { return UiFonts.WouldAdd(font, u); }

        public IGlyphFont Typeface
        {
            get
            {
                if (!styled || !(italic || weight != FontWeight.Regular)) return null;
                TMP_FontAsset face = UiFonts.Typeface(font, weight, italic);
                return face == null ? null : new TmpGlyphFont(face, weight, italic, false);
            }
        }

        public IList<IGlyphFont> Fallbacks
        {
            get
            {
                var list = new List<IGlyphFont>();
                if (font.fallbackFontAssetTable != null)
                    foreach (TMP_FontAsset g in font.fallbackFontAssetTable) list.Add(Of(g, weight, italic));
                return list;
            }
        }
    }
}
