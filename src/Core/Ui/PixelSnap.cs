using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    public enum SnapFix { None = 0, SetOwnCanvas = 1, AddNestedCanvas = 2 }

    // Unity's pixel snapping for a UI graphic: the nearest canvas above it that overrides the setting decides,
    // otherwise the root canvas does. The game draws its load list on a nested canvas that does not snap, so at a UI
    // scale that is not whole a copy drawn on a snapping canvas lands a pixel away from the game's own.
    public static class PixelSnap
    {
        // The canvases from the nearest to the root: whether each overrides the setting, and its own setting.
        public static bool Snaps(IList<bool> overrides, IList<bool> pixelPerfect)
        {
            if (overrides == null || pixelPerfect == null) return false;
            if (overrides.Count == 0 || overrides.Count != pixelPerfect.Count) return false;
            for (int i = 0; i < overrides.Count; i++)
                if (overrides[i]) return pixelPerfect[i];
            return pixelPerfect[pixelPerfect.Count - 1];
        }

        // What a clone needs to snap as the game draws its original: a canvas of its own is told to (whatever it
        // inherited from the original's parents no longer applies); otherwise a nested canvas only where the two
        // differ.
        public static SnapFix Fix(bool want, bool have, bool ownCanvas)
        {
            if (ownCanvas) return SnapFix.SetOwnCanvas;
            return want == have ? SnapFix.None : SnapFix.AddNestedCanvas;
        }
    }
}
