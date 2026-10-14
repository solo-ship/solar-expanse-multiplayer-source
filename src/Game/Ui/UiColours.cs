using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The game's own accent colours (GameColours, with where each comes from) as Unity colours, for every screen: the
    // warning colour of MissionPresetController.SetDropdownWarning (D 150072) and the upkeep tooltip's teal.
    public static class UiColours
    {
        public static readonly Color Warning = Of(GameColours.WarningRgb);
        public static readonly Color Accent = Of(GameColours.AccentRgb);

        static Color32 Of(int rgb)
        {
            byte[] c = GameColours.Channels(rgb);
            return new Color32(c[0], c[1], c[2], 0xFF);
        }
    }
}
