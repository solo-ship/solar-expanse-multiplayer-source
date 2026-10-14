using System;

namespace SolarExpanseMultiplayer
{
    // Where a card's text lines sit: the lines that have words, stacked and centred up and down in the card, so a card
    // with two lines does not hang from its top edge. A line with no words takes no room.
    public static class CardLayout
    {
        // Top of each line from the card's top; lines with no words get -1. heights[i] is that line's height.
        public static float[] Tops(float cardHeight, float[] heights, bool[] hasText)
        {
            var tops = new float[heights.Length];
            float total = 0f;
            for (int i = 0; i < heights.Length; i++) if (hasText[i]) total += heights[i];
            float y = Math.Max(0f, (cardHeight - total) / 2f);
            for (int i = 0; i < heights.Length; i++)
            {
                if (!hasText[i]) { tops[i] = -1f; continue; }
                tops[i] = y;
                y += heights[i];
            }
            return tops;
        }
    }
}
