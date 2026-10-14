using System;

namespace SolarExpanseMultiplayer
{
    // A part beside its original, pixel by pixel (DD0's look check). A pixel differs when any channel differs by more
    // than Threshold of 255. The outer ring of the part (EdgePx wide) is masked out of the verdict: there the
    // original's own neighbours show through the anti-aliased border, and the clone's do not. The largest difference
    // is always reported, inside and on the edge, so nothing hides behind the mask. A part 4 px or less across (or
    // tall) is all edge: it can never come out DIFFERENT, so a check of such small parts needs another measure.
    public sealed class PixelCompare
    {
        public const int Threshold = 2, EdgePx = 2;

        public int Width, Height, Compared, Differing, EdgeDiffering, Max, MaxInside;
        public bool Same { get { return Compared > 0 && Differing == 0; } }

        // a and b: w*h pixels as 0xRRGGBB, row by row. Different sizes, or nothing to compare, are never Same.
        public static PixelCompare Of(int[] a, int[] b, int w, int h)
        {
            var r = new PixelCompare { Width = w, Height = h };
            if (a == null || b == null || w <= 0 || h <= 0 || a.Length != w * h || b.Length != w * h) return r;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int d = Channel(a[i], b[i]);
                    bool edge = x < EdgePx || y < EdgePx || x >= w - EdgePx || y >= h - EdgePx;
                    r.Compared++;
                    if (d > r.Max) r.Max = d;
                    if (edge)
                    {
                        if (d > Threshold) r.EdgeDiffering++;
                    }
                    else
                    {
                        if (d > r.MaxInside) r.MaxInside = d;
                        if (d > Threshold) r.Differing++;
                    }
                }
            return r;
        }

        static int Channel(int p, int q)
        {
            int dr = Math.Abs(((p >> 16) & 0xFF) - ((q >> 16) & 0xFF));
            int dg = Math.Abs(((p >> 8) & 0xFF) - ((q >> 8) & 0xFF));
            int db = Math.Abs((p & 0xFF) - (q & 0xFF));
            return Math.Max(dr, Math.Max(dg, db));
        }

        public override string ToString()
        {
            return Width + "x" + Height + " px, " + Compared + " compared, differing " + Differing + " (edge " +
                   EdgeDiffering + "), max " + Max + " (inside " + MaxInside + "): " + (Same ? "SAME" : "DIFFERENT");
        }
    }
}
