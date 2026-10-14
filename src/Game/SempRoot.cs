using System;
using System.Collections.Generic;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The mod's own object, kept across scenes.
    public class SempRoot : MonoBehaviour
    {
        public static SempRoot Instance;
        GUIStyle style;
        // The parts bench's captures (test seams only): the label is drawn over every canvas, the bench's included.
        public bool LabelHidden;
        float autoLoadAt = -1f;
        string label;
        // A draw that throws is tried again next frame and logged once per distinct error (GuiFailures).
        readonly GuiFailures guiFailures = new GuiFailures();
        // The label font's width per character (made with the style), and the status label's wrapped lines, made again
        // only when the status, the report or the width changes (OnGUI runs several times a frame).
        GlyphWidths glyphs;
        List<string> lines;
        string linesFor;
        string[] reportFor;
        float widthFor = -1f;
        // Steam (M9): readiness, the transports and the one-PC self-test; torn down on quit and on destroy.
        public readonly SteamNet Steam = new SteamNet();
        // A test profile's window: -semp-window's size (or 1280x720), then whatever the `window` dev trigger asked for,
        // kept so a load that applies the saved screen settings again gets this size back.
        int windowW, windowH;
        float nextWindowPoll;

        void Awake()
        {
            Instance = this;
            label = ModInfo.Name + " " + ModInfo.Version + " - " + Boot.Mode + (Boot.Profile != null ? " - test profile " + Boot.Profile : "") +
                    (Boot.InstallProblem != null ? " - WARNING: two installs, see log" : "");
            Manager.LoadSaveManager.StaticOnSaveStateLoaded += OnSaveLoaded;
            if (Boot.AutoLoad != null && Boot.Profile != null) autoLoadAt = Time.realtimeSinceStartup + 3f;   // dev only, test profiles only
            if (Boot.Dev != null) Boot.Dev.TestWindow(out windowW, out windowH);
            else { windowW = DevFlags.DefaultTestWidth; windowH = DevFlags.DefaultTestHeight; }
            if (Boot.Profile != null) TestWindow();
            ConsoleGuard.Hook();   // the game's developer console: a joiner's world-changing command is refused
            // the parts kit's bench, test seams only
            if (PartsBench.SeamsOpen()) gameObject.AddComponent<PartsBench>();
        }

        // Test profiles only: a fixed window (1280x720 unless -semp-window or the `window` trigger says otherwise), so
        // two games fit on one screen and screenshots have known sizes. Unity saves screen settings in the registry, so
        // this only runs when the copy has its own identity (SEMPTest, tools\isolate-test-copy.ps1): otherwise it would
        // change the real game's settings. The size is set from inside the game: a window resized from outside once
        // asked the game for a render texture it could not make, and it crashed.
        void TestWindow()
        {
            try
            {
                string data = Application.persistentDataPath;
                if (!TestIdentity.InTestDataFolder(data))
                {
                    Log.Warn("test window: skipped, this copy shares the real game's identity (data folder " + data + ")");
                    return;
                }
                Screen.SetResolution(windowW, windowH, FullScreenMode.Windowed);
                Log.Info("test window: " + windowW + "x" + windowH + " (data folder " + data + ")");
            }
            catch (Exception e) { Log.Error("test window failed: " + e.Message); }
        }

        // The `window` dev trigger (test seams only): "<W>x<H>", by -semp-window's rule.
        void WindowTrigger()
        {
            if (Boot.Profile == null || !DevTriggers.Every(ref nextWindowPoll, Time.realtimeSinceStartup)) return;
            string text = DevTriggers.Take("window");
            if (text == null) return;
            int w, h;
            if (!DevFlags.TryWindowSize(text, out w, out h))
            {
                Log.Warn("dev window: refused '" + text + "': give <width>x<height>, from 640x360 to 7680x4320");
                return;
            }
            windowW = w;
            windowH = h;
            TestWindow();
        }

        void OnDestroy()
        {
            Manager.LoadSaveManager.StaticOnSaveStateLoaded -= OnSaveLoaded;
            StopSteam();
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            WindowTrigger();
            if (autoLoadAt >= 0f && Time.realtimeSinceStartup >= autoLoadAt)
            {
                autoLoadAt = -1f;
                try
                {
                    Log.Info("autoload: " + Boot.AutoLoad);
                    bool ok = SerializedMonoBehaviourSingleton<Manager.LoadSaveManager>.Instance.Load(Boot.AutoLoad);
                    if (!ok) Log.Error("autoload: the game refused to load " + Boot.AutoLoad);
                }
                catch (Exception e) { Log.Error("autoload failed: " + e); }
            }
            ConsoleGuard.Poll();
            Session.Update();   // does nothing outside a test session
            try { Steam.Update(); }   // nothing Steam is called unless something asked for it
            catch (Exception e) { Log.Error("steam: update failed: " + e.Message); }
            Drift.Update();     // does nothing without a test profile and an M3 flag
        }

        // After every Update this frame (Drift.Update's stop hold and the game's own included): the host's Clock goes out with
        // the speed the frame ended with.
        void LateUpdate() { Session.LateUpdate(); }

        // Before the game's Steam manager shuts Steam down (Unity runs every OnApplicationQuit before any OnDestroy):
        // the session's goodbyes are queued first, then our Steam objects are closed while Steam can still close them.
        void OnApplicationQuit() { Session.Stop(); StopSteam(); Drift.OnQuit(); }

        void StopSteam()
        {
            try { Steam.Stop(); }
            catch (Exception e) { Log.Error("steam: stop failed: " + e.Message); }
        }

        // The game's save-loaded event, raised INSIDE ExtractAllFromSaveData with its success flag: a failed load says
        // false there (and nowhere else the joiner can see), so the flag is passed on. Nothing may throw out of here:
        // the game's other listeners and the rest of its load come after this one.
        void OnSaveLoaded(bool success)
        {
            try
            {
                string date = "?";
                try { date = MonoBehaviourSingleton<Manager.TimeController>.Instance.CurrentTime.ToString(); }
                catch (Exception) { }
                Log.Info("save loaded; game date " + date + (success ? "" : "; the game says the load FAILED"));
                if (Boot.Profile != null) TestWindow();   // loading may apply the saved screen settings again
                Session.OnSaveLoaded(success);   // a host's session starts here; a joiner applies its join first
            }
            catch (Exception e) { Log.Error("save loaded: the session's step failed: " + e); }
            // M3: the load time and the state reader's day-0 read; nothing without a test profile and an M3 flag
            try { Drift.OnSaveLoaded(); }
            catch (Exception e) { Log.Error("save loaded: the drift step failed: " + e); }
        }

        // The status and the refusal's first report lines, wrapped to the window (a refusal once ran off its edge):
        // by the label font's own width for each character, within the window less the 10 px margins and the
        // style's padding (CalcSize counts the padding, GlyphWidths takes it out).
        List<string> StatusLines(string status)
        {
            if (glyphs == null) glyphs = new GlyphWidths(s => style.CalcSize(new GUIContent(s)).x);
            float width = Screen.width - 20 - style.padding.horizontal;
            string[] report = Session.StatusReport;
            if (lines == null || status != linesFor || report != reportFor || width != widthFor)
            {
                lines = ScreenLines.Of(status, Session.StatusReport, width, glyphs.Of);
                linesFor = status; reportFor = report; widthFor = width;
            }
            return lines;
        }

        void OnGUI()
        {
            if (guiFailures.Skip(Time.frameCount)) return;
            if (LabelHidden) return;
            try
            {
                if (style == null)
                {
                    // richText off: the status is drawn as written (it keeps its own '<' and backslashes now that only
                    // peer text is cleaned), never read as markup. wordWrap off: StatusLines wraps, and Unity's own
                    // wrap would hide lines in the one-line rect.
                    style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = false, wordWrap = false };
                    style.normal.textColor = Color.white;
                }
                // A normal install draws no dev label or status line, only a broken install's warning (DevOverlay).
                if (!DevOverlay.Shown(Boot.ProfileFlagGiven))
                {
                    string warn = DevOverlay.PlayerLine(Boot.InstallProblem);
                    if (warn != null) GUI.Label(new Rect(10, 98, 800, 24), warn, style);
                    return;
                }
                // Below the agency logo (top left, about 100 x 95 px at 1280x720), which hides the start of a line at y 6.
                GUI.Label(new Rect(10, 98, 800, 24), label, style);
                string status = Session.Status;
                float y = 118;
                if (status != null)
                    foreach (string line in StatusLines(status))
                    {
                        GUI.Label(new Rect(10, y, Screen.width - 20, 24), line, style);
                        y += 20;
                    }
                string drift = Drift.Status;   // test profiles with an M3 flag only; null otherwise
                if (drift != null) GUI.Label(new Rect(10, y, 1200, 24), drift, style);
            }
            catch (Exception e)
            {
                string l = guiFailures.Failed(Time.frameCount, Time.realtimeSinceStartup, e.GetType().Name, e.Message);
                if (l != null) Log.Error(l);
            }
        }
    }
}
