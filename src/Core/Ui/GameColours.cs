namespace SolarExpanseMultiplayer
{
    // The accent colours every screen takes from the game (DD0: no colour of our own where the game has one), as
    // 0xRRGGBB. The game draws neither on a text object the mod could read at run time: each lives in its own code as
    // a rich-text tag, so the value is copied here with where it comes from. A game update that changes them is a line
    // in docs/GAME-UPDATES.md's part list. UiColours turns them into Unity colours.
    public static class GameColours
    {
        // The game's warning text: MissionPresetController.SetDropdownWarning writes its warning as
        // "<color=#FF6600>" (D 150072); it is the only warning colour in the game's code (its other rich-text colours
        // are white, greys, its teal and plain red/yellow/orange for the cargo gauge, D 54687-54695).
        public const int WarningRgb = 0xFF6600;

        // The game's teal for a highlighted value: the upkeep tooltip's "<color=#00FFE9>" (D 132292).
        public const int AccentRgb = 0x00FFE9;

        // Red, green and blue of a 0xRRGGBB value.
        public static byte[] Channels(int rgb)
        {
            return new[] { (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF) };
        }
    }
}
