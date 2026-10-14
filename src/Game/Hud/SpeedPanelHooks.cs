using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using TimeControlPanel = Game.UI.TimeControlPanel;

namespace SolarExpanseMultiplayer
{
    // The game's own speed panel shows the session's real speed (DD10). The panel lights its selected images only in
    // its refresh, which the game runs when the engine's speed changes (TimeController.curentScaleTimeChange): in a
    // session this postfix lights them again from PanelSpeed (the host's speed, or what the session holds a client
    // at), and SpeedPanel asks the panel to refresh whenever that speed changes, so a client held at 0 shows the pause
    // button lit and never a running speed. Outside a session the postfix does nothing and the panel is the game's.
    // Installed only on an early-track profile (HookPlan); the full track keeps it.
    [HarmonyPatch(typeof(TimeControlPanel), "TimeControllerOncurentScaleTimeChange")]
    public static class SpeedPanelHooks
    {
        static readonly FieldInfo Label = AccessTools.Field(typeof(TimeControlPanel), "timeScaleLabel");

        [HarmonyPostfix]
        static void Postfix(TimeControlPanel __instance)
        {
            SpeedPanel panel = Session.Early != null ? Session.Early.Panel : null;
            if (panel == null) return;
            try
            {
                panel.Seen(__instance);
                float shown;
                if (!panel.Shown(out shown)) return;
                Light(__instance, shown);
            }
            catch (Exception e) { panel.Failed("the panel's marks failed: " + e.Message); }
        }

        // The six selected images, as the game's own refresh lights them, for the session's speed instead of the
        // engine's: none lit for a speed that has no button.
        static void Light(TimeControlPanel p, float shown)
        {
            var eco = MonoBehaviourSingleton<Manager.GameManager>.Instance.Economic;
            float b = eco.baseTimeScale;
            int mark = PanelSpeed.Mark((int)shown, (int)(b * eco.multiTime2), (int)(b * eco.multiTime3),
                (int)(b * eco.multiTime4), (int)(b * eco.multiTime5), (int)(b * eco.multiTime6));
            p.selectedTimeScale0X.gameObject.SetActive(mark == 0);
            p.selectedTimeScale1kX.gameObject.SetActive(mark == 1);
            p.selectedTimeScale100KX.gameObject.SetActive(mark == 2);
            p.selectedTimeScale1MX.gameObject.SetActive(mark == 3);
            p.selectedTimeScale5MX.gameObject.SetActive(mark == 4);
            p.selectedTimeScale10MX.gameObject.SetActive(mark == 5);
            var label = Label != null ? Label.GetValue(p) as TMPro.TMP_Text : null;
            // Known gap: the game's ButtonOnClicSetTime writes the clicked speed into this label after SetTimescale, so
            // on a client whose click is held the label shows the asked speed while the marks show the held one (the
            // shown speed did not change, so no refresh follows). It matters only if the label is visible in game.
            if (label != null) label.text = shown.ToString(CultureInfo.InvariantCulture);
        }
    }

    // The speed panel's side of the session (one instance, owned by MenuSessions): which panel is up (the postfix sees
    // it at the panel's first refresh), what speed it shows, and when to ask it to refresh. Its Clock comes from the
    // client's own link: the time each arrived decides whether the host's clock has stopped.
    public sealed class SpeedPanel
    {
        static readonly MethodInfo Refresh = AccessTools.Method(typeof(TimeControlPanel),
            "TimeControllerOncurentScaleTimeChange");
        readonly PanelRefresh refresh = new PanelRefresh();
        TimeControlPanel panel;
        SessionClient wired;
        Clock lastClock;
        long lastClockMs;
        int problems;

        // The panel the postfix just refreshed: the one this world shows.
        public void Seen(TimeControlPanel p)
        {
            if (ReferenceEquals(p, panel)) return;
            panel = p;
            refresh.Reset();
        }

        // In a session: true, and the speed the panel shows. Outside one: false (the game's own marks).
        public bool Shown(out float speed)
        {
            speed = 0f;
            if (!Session.Live) return false;
            // The host shows its engine's speed: during the yearly autosave's silent pause (up to about 10 s) that is
            // 0, so the pause button lights where vanilla (which ignores silent calls) would not. It is the real
            // state: the host's Clock says 0 to every player then too.
            if (Session.Host != null) { speed = PanelSpeed.Host(ClockSync.Scale); return true; }
            SessionClient c = Session.Client;
            if (c == null) return false;
            Clock last = ReferenceEquals(c, wired) ? lastClock : null;
            speed = PanelSpeed.Client(c.Connected, c.Flow.InGame, last != null, last != null ? last.Scale : 0f,
                NetClock.NowMs - lastClockMs);
            return true;
        }

        // Every frame (MenuSessions.Update). Nothing is allocated unless the shown speed changes.
        public void Update()
        {
            WireUp();
            TimeControlPanel p = panel;
            if (ReferenceEquals(p, null)) return;
            if (p == null) { panel = null; refresh.Reset(); return; }   // destroyed with its world
            float shown;
            bool inSession = Shown(out shown);
            if (!refresh.Step(inSession, shown) || Refresh == null) return;
            try { Refresh.Invoke(p, null); }
            catch (Exception e) { Failed("the panel's refresh failed: " + (e.InnerException ?? e).Message); }
        }

        // A save loaded or the menu came back: the panel goes with the old world.
        public void NewWorld()
        {
            panel = null;
            refresh.Reset();
        }

        // The client's Clock, with the time it came. One listener per link (M4 makes one client per process at most);
        // a plain assignment, so it can never throw into the link's other listeners.
        void WireUp()
        {
            SessionClient c = Session.Client;
            if (c == null || ReferenceEquals(c, wired)) return;
            wired = c;
            lastClock = null;
            c.Received += Heard;
        }

        void Heard(Message m)
        {
            var clock = m as Clock;
            if (clock == null) return;
            lastClock = clock;
            lastClockMs = NetClock.NowMs;
        }

        public void Failed(string what)
        {
            problems++;
            if (problems <= 5) Log.Error("speed panel: " + what);
        }
    }
}
