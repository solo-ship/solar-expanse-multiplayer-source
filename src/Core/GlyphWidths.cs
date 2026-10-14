using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The label font's width for each character, measured once through the game's own text measure (GUIStyle.CalcSize,
    // passed in) and kept, for ScreenLines to wrap by. A width estimated from a sample line's average let lines of
    // digits and hex ids (wider than the average letter) run off the window. A measure includes the style's padding,
    // so a character's width is the difference between five of it and one, over four. Owned by SempRoot's instance.
    public sealed class GlyphWidths
    {
        public const int MaxKept = 4096;   // a flood of distinct characters (host report lines) cannot grow it for ever
        readonly Func<string, double> measure;
        readonly Dictionary<int, double> kept = new Dictionary<int, double>();

        public GlyphWidths(Func<string, double> measure)
        {
            if (measure == null) throw new ArgumentNullException("measure");
            this.measure = measure;
        }

        public int Kept { get { return kept.Count; } }

        public double Of(int codePoint)
        {
            double w;
            if (kept.TryGetValue(codePoint, out w)) return w;
            // a lone surrogate (or any value ConvertFromUtf32 refuses) is measured as the single char it is
            string c = codePoint > 0xFFFF && codePoint <= 0x10FFFF ? char.ConvertFromUtf32(codePoint)
                : ((char)codePoint).ToString();
            w = (measure(c + c + c + c + c) - measure(c)) / 4;
            if (!(w >= 0 && w < double.PositiveInfinity)) w = 0;
            if (kept.Count >= MaxKept) kept.Clear();
            kept[codePoint] = w;
            return w;
        }
    }
}
