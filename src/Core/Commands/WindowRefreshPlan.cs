namespace SolarExpanseMultiplayer
{
    // In a session the click becomes a request, so the window's own redraw after its world call never runs: the
    // entry applies later, from the stream. These are the redraws the game's click handlers make, by kind, for the
    // windows still open when the entry applies (WindowRefresh runs them on every game, host and clients).
    public static class WindowRefreshPlan
    {
        public const int None = 0;
        // B1: ObjectInfoWindow.InvokeOnAddFacilityModule (D 136839-136840)
        public const int FacilityAdded = 1;
        // B2: the queue popup's OK, RefreshFacilityList (D 136873)
        public const int FacilityQueued = 2;
        // B6, P5: the row's window SetData of its site (UIRowFacility D 165237, UIRowRocket D 167446)
        public const int SiteRedraw = 3;
        // B3: the facility card closes when nothing is left to build, else SetData (D 134777-134781, 134800-134811)
        public const int CardCancel = 4;
        // B4: the facility card closes when no unit is left, else SetData (D 134698-134699, 134716-134723, 134736)
        public const int CardScrap = 5;
        // B8: the card's SetData after the priority step (D 134627); B7: after the units-on slider sets Enabled
        // (D 134617). SetData moves the slider without notifying, so the redraw sends nothing back
        public const int CardRedraw = 6;
        // F4: the planner's preset list rebuilt and the new preset selected (MissionPresetController.OnSaveClicked)
        public const int PresetList = 7;

        public static int For(int kind)
        {
            switch (kind)
            {
                case ActionKind.Build: return FacilityAdded;
                case ActionKind.BuildQueue: return FacilityQueued;
                case ActionKind.BuyAndBuild: case ActionKind.CraftBuyAndBuild: return SiteRedraw;
                case ActionKind.BuildCancel: return CardCancel;
                case ActionKind.Scrap: return CardScrap;
                case ActionKind.FacilityPriority: case ActionKind.FacilityEnabled: return CardRedraw;
                case ActionKind.PresetSave: return PresetList;
                default: return None;
            }
        }

        // The card of a scrapped or cancelled facility: closed once nothing of it is left, as the game closes it.
        public static bool CloseCard(long left) { return left <= 0; }
    }
}
