namespace SolarExpanseMultiplayer
{
    // The mod's screens draw in three layers of one canvas per scene, bottom to top: the HUD and the notices, then the
    // screens (PLAYERS, the lobby and the agency picker, the menu's pages), then the session's dialogs. Each layer is
    // a nested canvas with its own sorting order, so the game draws and hit-tests them in this order whatever was
    // built first, and ModalRegistry ranks an open dialog by the same order (the one drawn on top owns Esc).
    public enum UiLayer { Hud = 0, Screens = 1, Dialogs = 2 }

    public static class UiLayers
    {
        public static readonly UiLayer[] BottomToTop = { UiLayer.Hud, UiLayer.Screens, UiLayer.Dialogs };

        // A layer's sorting order, over the mod canvas's own: one step per layer, so the three stay right above the
        // game's canvas, as the screens' own canvases were.
        public static int Order(int canvasOrder, UiLayer layer) { return canvasOrder + (int)layer; }
    }
}
