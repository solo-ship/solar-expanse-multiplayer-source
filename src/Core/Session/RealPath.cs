namespace SolarExpanseMultiplayer
{
    public static class RealPath
    {
        // The ONE switch for a normal install's Multiplayer button and the menus' session (the player build). Open
        // since the beta's switch commit (user, 2026-10-14: "I want to play without that"): a normal install is the
        // player path; test profiles keep their seams (TestSeams). RealPathSourceTests pins the value in every
        // commit, so changing it changes that test's expected value in the same commit.
        public const bool Open = true;

        // The button shows when RealPath.Open, or with a valid test profile (seams open, or the player-path flag
        // together with that profile). The player-path flag alone never shows it by itself.
        public static bool ButtonShown(string validProfile, bool playerPath)
        {
            return Open || TestSeams.Valid(validProfile);
        }
    }

    // The mod's label and status lines drawn over the game (its version, the session's dev status, the drift line):
    // a test launch's (the -semp-profile flag, the player-path profile included), never a normal install's, where
    // the session's own screens, notices and dialogs speak to the player. A normal install shows only a broken
    // install's warning (two copies of the mod), which a player has to act on.
    public static class DevOverlay
    {
        public static bool Shown(bool profileFlagGiven) { return profileFlagGiven; }

        // The one line a normal install draws, or null for nothing.
        public static string PlayerLine(string installProblem)
        {
            return installProblem == null ? null
                : ModInfo.Name + " " + ModInfo.Version + ": WARNING: two installs of the mod, see its log";
        }
    }
}
