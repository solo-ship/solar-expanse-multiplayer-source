using System;
using System.Reflection;
using HarmonyLib;

namespace SolarExpanseMultiplayer
{
    // Every speed or pause change in the game ends in TimeController.SetTimescale (docs/research/clock.md). Patches:
    //  - SetTimescale prefix + postfix: every call the game makes for its player (clicks, hotkeys, popups, the tutorial,
    //    the countdown) is that player's speed REQUEST, whether or not the value changed. Not requests: calls the game
    //    blocks (the same checks as SetTimescale itself: end-game impact; without force, the launch countdown and the
    //    planner windows), silent calls (autosave, the game's own corrections) and the mod's own (Applying).
    //    A client's request is still counted as asked, but the call is rewritten down to what the session allows (ClampLocal).
    //    Whether the request is the player's own choice ("player": a click or hotkey) or the game's ("game": a popup)
    //    decides whether it can end a drop pause. In a session the pauses the game makes for its own menus, tutorial
    //    steps and warnings (GamePauses) are dropped before any of this: never applied, never a request.
    //    Three rare game-code SetTime1 callers still read as "player" (known deviation, M2; handled in M8).
    //  - PauseGame2 / UnPauseGame2: the space bar, which sets no click flag; its calls are the player's all the same
    //    (ClockHooks.FromPlayer), so a space-bar unpause can end a drop pause like a click.
    //  - PauseGamePlanMission / UnPauseGamePlanMission: the mission planner. In a session the mod does the planner's
    //    pause itself: opening it pauses everyone "planning a mission", closing it takes only that pause away.
    // Patched only in a test session (HookPlan.ClockSync); main thread only, like the game's clock.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a class without one (Task 10, S10).
    [HarmonyPatch(typeof(Manager.TimeController))]
    public static class ClockSync
    {
        public static bool Applying;
        // The session's hooks (ClockHooks says what each is); ClockHost / ClockClient set them, EndSession takes them
        // down.
        public static readonly ClockHooks Hooks = new ClockHooks();
        // This player's speed request: the speed, and whether the player made it (click, hotkey) rather than the game.
        public static Action<float, bool> LocalChange
        {
            get { return Hooks.LocalChange; }
            set { Hooks.LocalChange = value; }
        }
        // This player's planner opened (true) / closed (false).
        public static Action<bool> LocalPlanner
        {
            get { return Hooks.LocalPlanner; }
            set { Hooks.LocalPlanner = value; }
        }
        // A client in a session: the speed this player may actually run at right now for what they just asked for
        // (never above what the session allows). The call is rewritten to it before the game applies anything.
        public static Func<float, float> ClampLocal
        {
            get { return Hooks.ClampLocal; }
            set { Hooks.ClampLocal = value; }
        }
        // This player's planner flag, dropped when the planner windows go with the world (ClockHooks.NewWorld).
        public static Action PlannerGone
        {
            get { return Hooks.PlannerGone; }
            set { Hooks.PlannerGone = value; }
        }
        public static int Requests;
        static PlannerCount planner { get { return Hooks.Planner; } }

        // The session is over and the player is at the menu: the game's own clock calls are the game's again.
        public static void EndSession() { Hooks.End(); }
        // A save loaded or the game is at the menu (Session): the planner windows went with the old world.
        public static void NewWorld() { Hooks.NewWorld(); }
        static long lastPatchErrorMs = -60000;
        static long lastDropLogMs = -60000;
        static readonly FieldInfo NextDay = AccessTools.Field(typeof(Manager.TimeController), "nextDay");

        // __state: the speed originally asked for when this call is a request, otherwise -1.
        // False (the call is skipped): a game pause source's call in a session (GamePauses), so the game keeps the
        // session's speed and this player asks nothing of the host.
        [HarmonyPatch(typeof(Manager.TimeController), nameof(Manager.TimeController.SetTimescale))]
        [HarmonyPrefix]
        static bool SetTimescalePrefix(ref float timescale, bool force, bool silent, out float __state)
        {
            __state = -1f;
            if (Applying) return true;
            // Before the session check: a host's start pop-up can open before its clock is attached (DropsCall).
            if (Hooks.DropsCall(force, silent))
            {
                long now = NetClock.NowMs;
                if (now - lastDropLogMs >= 1000)
                {
                    lastDropLogMs = now;
                    Log.Info("clock: the game's own speed call " + timescale + " (" + GamePauses.Source
                        + ") dropped: not this player's request");
                }
                return false;
            }
            if (LocalChange == null) return true;
            if (silent)
            {
                // The autosave's brief silent pause (and its restore afterwards) is the game's, not a request.
                Hooks.Silent(timescale, NetClock.NowMs);
                return true;
            }
            try
            {
                if (Blocked(force)) return true;
                __state = timescale;
            }
            catch (Exception e) { PatchError("block check failed: " + e.Message); }
            try
            {
                // Counted as asked for, but never applied faster than the session allows (not even for one frame).
                // A clamp that throws holds the call at the speed already running (ClockHooks.Clamp fails closed).
                string failed;
                float allowed = Hooks.Clamp(timescale, Scale, out failed);
                if (failed != null) PatchError("the clamp failed, the call is held at " + allowed + ": " + failed);
                if (allowed != timescale)
                {
                    Log.Info("clock: request " + timescale + " held to " + allowed + " until the host answers");
                    timescale = allowed;
                }
            }
            catch (Exception e)
            {
                PatchError("clamp failed: " + e.Message);
                if (ClampLocal != null) timescale = 0f;   // a client's call: held, never let through unchecked
            }
            return true;
        }

        [HarmonyPatch(typeof(Manager.TimeController), nameof(Manager.TimeController.SetTimescale))]
        [HarmonyPostfix]
        static void SetTimescalePostfix(Manager.TimeController __instance, float __state)
        {
            if (__state < 0f || LocalChange == null) return;
            try
            {
                // curentScaleTimeChange2bool is true while the buttons, hotkeys and SetTime1..5 make the call (and in
                // three rare game-code SetTime1 callers); the tutorial sets it too, but in a session its calls never
                // get here (GamePauses).
                // The space bar sets no flag: its own scope says it is the player's (ClockHooks.FromPlayer).
                bool fromPlayer = Hooks.FromPlayer(__instance.curentScaleTimeChange2bool);
                Requests++;
                Log.Info("clock: this player's request " + __state + " (" + (fromPlayer ? "player" : "game") + ")");
                LocalChange(__state, fromPlayer);
            }
            catch (Exception e) { PatchError("local change failed: " + e.Message); }
        }

        // A patch must never break the game's own method: errors are logged (at most one a minute) and the game carries on.
        static void PatchError(string what)
        {
            long now = NetClock.NowMs;
            if (now - lastPatchErrorMs < 60000) return;
            lastPatchErrorMs = now;
            Log.Error("clock: " + what);
        }

        // The checks at the top of SetTimescale, in the same order.
        static bool Blocked(bool force)
        {
            var gm = MonoBehaviourSingleton<Manager.GameManager>.Instance;
            if (gm != null && gm.EndGameAsteroidImpact) return true;
            if (force) return false;
            var ui = SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance;
            if (ui == null) return false;
            if (ui.CountDownOn) return true;
            return PlannerWindowOpen(ui);
        }

        // A mission planner window (either kind) is open.
        static bool PlannerWindowOpen(Game.UI.UIManager ui)
        {
            if (ui == null) return false;
            var plan = ui.GetWindow<Game.UI.Windows.Windows.PlanMissionWindow>();
            if (plan != null && plan.Open) return true;
            var cyclical = ui.GetWindow<PlanCyclicalMissionWindow>();
            return cyclical != null && cyclical.Open;
        }

        [HarmonyPatch(typeof(Manager.TimeController), nameof(Manager.TimeController.PauseGamePlanMission))]
        [HarmonyPrefix]
        static bool PlannerOpenPrefix()
        {
            try
            {
                if (LocalPlanner == null) return true;   // no session: the game's own behaviour
                // Each open adds one. A close takes one while a planner window is still open, and clears them all once
                // none is (PlannerCount: an unbalanced pair from the game must never leave everyone paused).
                if (planner.Open())
                {
                    Log.Info("clock: mission planner opened - everyone pauses (planning a mission)");
                    LocalPlanner(true);
                }
                return false;   // the mod pauses, as a request with a reason
            }
            catch (Exception e) { PatchError("planner open failed, the game pauses itself: " + e.Message); return true; }
        }

        [HarmonyPatch(typeof(Manager.TimeController), nameof(Manager.TimeController.UnPauseGamePlanMission))]
        [HarmonyPrefix]
        static bool PlannerClosePrefix()
        {
            try
            {
                if (LocalPlanner == null) return true;
                // The game hides the window before it unpauses, so a real close normally sees no planner window open.
                bool windowOpen = PlannerWindowOpen(SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance);
                int opens = planner.Count;
                if (planner.Close(windowOpen))
                {
                    Log.Info("clock: mission planner closed" + (opens > 1 ? " (no planner window open: all " + opens + " opens cleared)" : ""));
                    LocalPlanner(false);
                }
                return false;
            }
            catch (Exception e) { PatchError("planner close failed, the game resumes itself: " + e.Message); return true; }
        }

        // The space bar's pause and unpause (InputManager's pause key, their only caller): the call they make is the
        // player's own, like a speed button's. A finalizer closes the scope even when the method throws.
        [HarmonyPatch(typeof(Manager.TimeController), nameof(Manager.TimeController.PauseGame2))]
        [HarmonyPrefix]
        static void SpacePausePrefix() { Hooks.PlayerKeyBegin(); }

        [HarmonyPatch(typeof(Manager.TimeController), nameof(Manager.TimeController.PauseGame2))]
        [HarmonyFinalizer]
        static void SpacePauseFinalizer() { Hooks.PlayerKeyEnd(); }

        [HarmonyPatch(typeof(Manager.TimeController), nameof(Manager.TimeController.UnPauseGame2))]
        [HarmonyPrefix]
        static void SpaceUnpausePrefix() { Hooks.PlayerKeyBegin(); }

        [HarmonyPatch(typeof(Manager.TimeController), nameof(Manager.TimeController.UnPauseGame2))]
        [HarmonyFinalizer]
        static void SpaceUnpauseFinalizer() { Hooks.PlayerKeyEnd(); }

        static Manager.TimeController Tc { get { return MonoBehaviourSingleton<Manager.TimeController>.Instance; } }

        // CurrentTime reads GravityEngine.instance, which exists only in a loaded game.
        public static bool Ready { get { return Tc != null && GravityEngine.instance != null; } }
        public static double GameSeconds { get { return Tc.CurrentTime.Ticks / 1e7; } }
        public static double StartSeconds { get { return Tc.StartDateTime.Ticks / 1e7; } }
        // During a load the scene has no TimeController (Instance finds none): that reads as stopped, never a throw, so
        // the status label (ClockHost.Status) keeps drawing through a host load.
        public static float Scale { get { var tc = Tc; return tc != null ? tc.CurrentTimeScale : 0f; } }

        // Day events (TimeController.nextDay) that are due but not yet run: the backlog that builds at very high speed.
        public static int DaysBehind
        {
            get
            {
                try
                {
                    if (NextDay == null) return -1;
                    var next = (DateTime)NextDay.GetValue(Tc);
                    double d = (Tc.CurrentTime - next).TotalDays;
                    return d < 0 ? 0 : (int)Math.Min(d + 1, 1000000);
                }
                catch (Exception) { return -1; }
            }
        }

        // End-game impact: SetTimescale refuses every change, even forced ones.
        public static bool Locked
        {
            get { var gm = MonoBehaviourSingleton<Manager.GameManager>.Instance; return gm != null && gm.EndGameAsteroidImpact; }
        }

        // The launch countdown (UIManager.CountDown) sets its own speeds; nobody else changes speed while it runs.
        public static bool CountDown
        {
            get { var ui = SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance; return ui != null && ui.CountDownOn; }
        }

        // The autosave (LoadSaveManager.OnEachYearCheckAutoSave) saves the speed, silently sets 0, waits, saves, then
        // silently sets the saved speed back. While that is on (at most 10 s) the clock leaves the speed alone, and
        // afterwards puts back whatever the session's slowest request says.
        public static bool GameSilentPause { get { return Hooks.SilentPause(NetClock.NowMs); } }

        // force: true gets past the planner and countdown blocks. silent: no speed-change events, so the speed panel,
        // camera and orbit views don't react (used for brief catch-up and hold corrections).
        // The speed is read back afterwards: if the engine rounds or refuses an in-between speed (catch-up asks for
        // continuous ones, e.g. 30000), the next listed speed up is used instead (logged once, the first time only).
        // Returns the speed the clock ended up at, so the caller can remember it as the target it reached.
        static bool snapLogged;
        public static int Snapped;

        public static float Apply(float scale, bool silent)
        {
            Applying = true;
            try
            {
                Tc.SetTimescale(scale, true, silent);
                float got = Tc.CurrentTimeScale;
                if (scale > 0f && !ClockRules.Close(got, scale) && !Locked)
                {
                    float snap = ClockRules.SnapUp(scale);
                    if (!snapLogged) { snapLogged = true; Log.Warn("clock: the engine gave speed " + got + " for " + scale + "; using listed speeds (" + snap + ")"); }
                    Snapped++;
                    Tc.SetTimescale(snap, true, silent);
                    got = Tc.CurrentTimeScale;
                }
                return got;
            }
            finally { Applying = false; }
        }

        // As if the player had clicked (SetTime1..5 set the click flag around the call): the dev "speed" trigger.
        public static void PlayerSets(float scale)
        {
            Tc.curentScaleTimeChange2bool = true;
            try { Tc.SetTimescale(scale, false, false); }
            finally { Tc.curentScaleTimeChange2bool = false; }
        }

        // The dev "planner" trigger: the game's own two methods, which our patches intercept.
        public static void DevPlanner(bool open)
        {
            if (open) Tc.PauseGamePlanMission(); else Tc.UnPauseGamePlanMission();
        }
    }
}
