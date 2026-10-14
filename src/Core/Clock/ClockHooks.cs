using System;

namespace SolarExpanseMultiplayer
{
    // The hooks the game's clock calls reach while a session runs (ClockSync's patches read them): this player's speed
    // request, the mission planner's open and close, and the clamp that keeps a client at what the session allows.
    // Without a session none is set and the game's own behaviour stands. A session that ends (the player is back at
    // the menu) takes them all down and restarts the planner count, so a solo game started afterwards is not held
    // paused or clamped by a session that is gone.
    public sealed class ClockHooks
    {
        public const long SilentPauseMs = 10000;
        public Action<float, bool> LocalChange;   // the speed asked for, and whether the player (not the game) did
        public Action<bool> LocalPlanner;         // a planner window opened (true) or closed (false)
        public Func<float, float> ClampLocal;     // the speed a call may run at right now, for the one asked for
        public Action PlannerGone;                // the planner windows went with the world: drop this player's flag
        public readonly PlannerCount Planner = new PlannerCount();
        long silentSinceMs = -1;
        int gamePauses, playerKeys;

        public bool InSession { get { return LocalChange != null; } }

        // The game pauses by itself for its own windows, menus, tutorial steps and warnings (the GamePauses
        // sources: the Esc menu, a contract's tutorial step, a trivia window, the advisor's warnings). In a session
        // such a pause would become this player's vote and stop every player, so while one of those sources runs
        // its speed calls are dropped: the game keeps the session's speed and nothing is asked of the host. The
        // player's own speed buttons and hotkeys, the planner, the countdown (forced) and the autosave (silent)
        // never run inside one.
        public void GamePauseBegin() { gamePauses++; }
        public void GamePauseEnd() { if (gamePauses > 0) gamePauses--; }
        public bool DropsCall(bool force, bool silent)
        {
            return (InSession || StartingNow()) && gamePauses > 0 && !force && !silent;
        }

        // A host's session about to start from the menus: its start pop-up ("Welcome, Director") opens on the host's
        // first frame, before the session is live, and its pause would stand once the session starts. Set once by
        // the menus' glue; kept across sessions (End leaves it). A throw reads as "not starting".
        public Func<bool> Starting;

        bool StartingNow()
        {
            Func<bool> starting = Starting;
            if (starting == null) return false;
            try { return starting(); }
            catch (Exception) { return false; }
        }

        // The space bar's pause and unpause (TimeController.PauseGame2 / UnPauseGame2, called only by the keyboard)
        // set no click flag of the game's, so their calls read as the game's and an unpause by space could not end a
        // drop hold. Inside one of them a call is the player's, as a speed button's is (FromPlayer).
        public void PlayerKeyBegin() { playerKeys++; }
        public void PlayerKeyEnd() { if (playerKeys > 0) playerKeys--; }
        public bool FromPlayer(bool clickFlag) { return clickFlag || playerKeys > 0; }

        // What a speed call runs at: the clamp's answer, or the speed asked for when no session clamps. A clamp that
        // throws fails closed: the call is held at the speed already running (a slow-down or a pause still goes
        // through), so a broken clamp never lets a client run ahead of the session. error: why it failed, else null.
        public float Clamp(float asked, float running, out string error)
        {
            error = null;
            Func<float, float> clamp = ClampLocal;
            if (clamp == null) return asked;
            try { return clamp(asked); }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return asked <= running ? asked : running >= 0f ? running : 0f;
            }
        }

        // The game's silent speed calls (the autosave's: it saves the speed, silently pauses, saves, then silently
        // puts the speed back). Its pause is the game's, not a request, and lasts at most SilentPauseMs.
        public void Silent(float scale, long nowMs) { silentSinceMs = scale == 0f ? nowMs : -1; }
        public bool SilentPause(long nowMs) { long s = silentSinceMs; return s >= 0 && nowMs - s < SilentPauseMs; }

        // A save loaded, or the game is at the menu: the old world's planner windows are gone, destroyed without the
        // close the game makes when one hides (the only place it unpauses), and so is any autosave pause in it. The
        // count restarts and the session drops its planner flag; kept, a full rejoin or a host load would pause the
        // whole session as "planning a mission" with no planner open.
        public void NewWorld()
        {
            silentSinceMs = -1;
            gamePauses = 0;
            playerKeys = 0;
            Planner.Reset();
            Action gone = PlannerGone;
            if (gone != null) gone();
        }

        public void End()
        {
            LocalChange = null;
            LocalPlanner = null;
            ClampLocal = null;
            PlannerGone = null;
            Planner.Reset();
            silentSinceMs = -1;
            gamePauses = 0;
            playerKeys = 0;
        }
    }
}
