namespace SolarExpanseMultiplayer
{
    // Where the strip under the speed buttons sits across the screen, in screen pixels: its left edge and width are
    // the game's own speed panel's (so its frame lines up with that frame's corner), never narrower than the six
    // buttons, and always inside the screen so its border shows on every side.
    public static class HudPlacement
    {
        public const float MinWidth = 60f;

        // The strip's left and right edges. frame*: the speed panel's frame; buttons*: the first and last speed
        // button; margin: the gap kept to the screen's edges.
        public static void Span(float frameLeft, float frameRight, float buttonsLeft, float buttonsRight,
                                float screenWidth, float margin, out float left, out float right)
        {
            left = System.Math.Min(frameLeft, buttonsLeft);
            right = System.Math.Max(frameRight, buttonsRight);
            float max = screenWidth - margin;
            if (right > max) right = max;      // shrink, so the left edge stays on the frame's
            if (left < margin) left = margin;
            if (right < left + MinWidth) right = System.Math.Min(max, left + MinWidth);
            if (right < left + MinWidth) left = System.Math.Max(0f, right - MinWidth);
        }

        // Whether two screen rectangles (min x, min y, max x, max y) overlap: the panel yields to the game's tooltip
        // only when the tooltip is over it.
        public static bool Overlaps(float aL, float aB, float aR, float aT, float bL, float bB, float bR, float bT)
        {
            return aL < bR && bL < aR && aB < bT && bB < aT;
        }
    }
}
