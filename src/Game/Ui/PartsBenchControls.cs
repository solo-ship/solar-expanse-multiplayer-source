using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // A mod class that IS a Button: the allowlist goes by exact type, so a clone must not keep it.
    public sealed class BenchButton : Button { }

    // The bench's negative controls: every fail-closed check of the kit is fed a bad clone through the real recipe
    // and must refuse it (or, for the census and the pixel check, must see the fault), plus the dialog's Esc and
    // hotkey test.
    public sealed partial class PartsBench
    {
        IEnumerator Controls()
        {
            GameParts parts = GameParts.InMenu();
            if (parts.Missing != null) { checks.Fail("menu part missing: " + parts.Missing); yield break; }
            GameObject button = ((Button)parts.Original(GameParts.PMenuButton)).gameObject;
            Graphic outside = button.GetComponentInChildren<TMPro.TMP_Text>(true);
            Canvas canvas = BenchCanvas(parts.GameCanvas);
            Type translate = GameType("Language.StaticTranslateText");
            string problem;

            RectTransform r = Strip.Clone(button, canvas.transform, "control script-left", out problem, null,
                                          new StripControl { KeepType = translate });
            Track(r != null ? r.gameObject : null);
            checks.Control("script-left", r == null && Has(problem, "game scripts left"), problem ?? "built");

            r = Strip.Clone(button, canvas.transform, "control outside", out problem, null,
                            new StripControl { PlantOutside = outside });
            Track(r != null ? r.gameObject : null);
            checks.Control("outside-reference", r == null && Has(problem, "outside the clone"), problem ?? "built");

            var broken = new StripTable(StripRules.Kept.Concat(new[] { StripRules.Game + "|Game.UI.NoSuchClass" }));
            r = Strip.Clone(button, canvas.transform, "control table", out problem, null,
                            new StripControl { Table = broken });
            Track(r != null ? r.gameObject : null);
            checks.Control("allowlist-unresolved", r == null && Has(problem, "could not be resolved"),
                           problem ?? "built");

            r = Strip.Clone(button, canvas.transform, "control subclass", out problem, null, new StripControl
            {
                Plant = g =>
                {
                    var child = new GameObject(Strip.ObjectPrefix + "control child", typeof(RectTransform));
                    child.transform.SetParent(g.transform, false);
                    child.AddComponent<BenchButton>();
                }
            });
            Track(r != null ? r.gameObject : null);
            bool left = r != null && r.GetComponentInChildren<BenchButton>(true) != null;
            checks.Control("subclass-not-kept", r != null && !left, r == null ? problem : "BenchButton left " + left);

            RectTransform hidden = UiWidgets.Box(canvas.transform, "control hidden parent");
            Track(hidden.gameObject);
            hidden.gameObject.SetActive(false);
            r = Strip.Clone(button, hidden, "control hidden", out problem);
            checks.Control("inactive-parent", r == null && Has(problem, "not active"), problem ?? "built");

            var corp = (CorporationSelectUI)parts.Original(GameParts.PAgencies);
            object item = ((IList)GameParts.Field<IList>(corp, "corporationItems"))[0];
            var agency = GameParts.Field<Toggle>(item, "toggle");
            r = Strip.Clone(agency.gameObject, canvas.transform, "control tint-key", out problem, null,
                            new StripControl { PlantTintKey = outside });
            Track(r != null ? r.gameObject : null);
            checks.Control("tint-key-outside", r == null && Has(problem, "tint snapshot"), problem ?? "built");

            // step 2b refuses a tint class it does not know, and a tinted control with no original to copy from
            r = Strip.Clone(agency.gameObject, canvas.transform, "control tint-class", out problem, null,
                            new StripControl { TintClassGone = true });
            Track(r != null ? r.gameObject : null);
            checks.Control("tint-class-gone", r == null && Has(problem, "MyControlChildElements changed"),
                           problem ?? "built");
            r = Strip.Clone(button, canvas.transform, "control tint-orphan", out problem, null,
                            new StripControl { Plant = g => AddChild(g, typeof(Game.UI.CommonControls.MyButton)) });
            Track(r != null ? r.gameObject : null);
            checks.Control("tint-no-original", r == null && Has(problem, "tint: no original"), problem ?? "built");

            // a MyDropdown is kept only with its own nonInteractableText
            r = Strip.Clone(button, canvas.transform, "control dropdown", out problem, null,
                            new StripControl { Plant = g => AddChild(g, typeof(Game.UI.CommonControls.MyDropdown)) });
            Track(r != null ? r.gameObject : null);
            checks.Control("dropdown-without-text", r == null && Has(problem, "MyDropdown without"),
                           problem ?? "built");

            // the registry lets go of an entry once it is hidden; a mod dialog hidden and shown again holds again
            RectTransform entry = UiWidgets.Box(canvas.transform, "control modal entry");
            Track(entry.gameObject);
            ModalRegistry.Open(entry.gameObject);
            bool held = ModalRegistry.AnyOpen;
            entry.gameObject.SetActive(false);
            bool released = !ModalRegistry.AnyOpen;
            ModalRegistry.Close(entry.gameObject);
            checks.Control("hidden-entry-releases", held && released, "held " + held + ", released " + released);
            ModDialog dialog = ModDialog.Open(parts, canvas, "name.fallback", null);
            if (dialog == null) checks.Control("dialog-shown-again-holds", false, "the dialog was not built");
            else
            {
                Track(dialog.gameObject);
                dialog.gameObject.SetActive(false);
                bool off = !ModalRegistry.AnyOpen;
                dialog.gameObject.SetActive(true);
                bool on = ModalRegistry.AnyOpen && ModalRegistry.Top == dialog.gameObject;
                dialog.Close();
                checks.Control("dialog-shown-again-holds", off && on,
                               "hidden: released " + off + "; shown: holds " + on);
            }

            GameParts missing = GameParts.InMenu("NoSuchPart (control)");
            Button refused = missing.MenuButton(canvas.transform, "name.fallback");
            Track(refused != null ? refused.gameObject : null);
            checks.Control("missing-part", missing.Missing == "NoSuchPart (control)" && refused == null,
                           "Missing " + (missing.Missing ?? "none") + "; button " +
                           (refused == null ? "refused" : "BUILT"));

            yield return CensusControl(button, canvas);
            yield return TintControl(parts);
        }

        // The census must see a real leak: a clone left holding StaticTranslateText subscribes to the game's language
        // events in its Start, and the count must fail; once it is destroyed the count must pass again.
        IEnumerator CensusControl(GameObject button, Canvas canvas)
        {
            HandlerCensus c0 = checks.Census();
            string problem;
            var leak = new StripControl { KeepType = GameType("Language.StaticTranslateText"), BuildAnyway = true };
            RectTransform r = Strip.Clone(button, canvas.transform, "control leak", out problem, null, leak);
            if (r == null)
            {
                checks.Control("census-leak", false, "the leak clone was refused: " + problem);
                yield break;
            }
            Track(r.gameObject);
            yield return null;
            yield return null;
            HandlerCensus c1 = checks.Census();
            bool failed = HandlerCensus.Verdict(c0, c1, true) == HandlerCensus.Fail;
            checks.Control("census-leak", failed && c1.Ours.Count > 0,
                           "ours " + c1.Ours.Count + (c1.Ours.Count > 0 ? " [" + c1.Ours[0] + "]" : "") + "; " +
                           HandlerCensus.Differences(c0, c1).Count + " differ");
            DestroyImmediate(r.gameObject);
            yield return null;
            HandlerCensus c2 = checks.Census();
            checks.Control("census-leak-gone", HandlerCensus.Verdict(c0, c2, true) == HandlerCensus.Pass,
                           "ours " + c2.Ours.Count + "; " + HandlerCensus.Differences(c0, c2).Count + " differ");
        }

        // Step 2b on a live, tinted save row: with it the clone matches in every state; without it at least one state
        // must differ (a pixel check that cannot see the fault would be no check).
        IEnumerator TintControl(GameParts parts)
        {
            var menu = (MenuSceneUI)parts.Original(GameParts.PMenu);
            Click(GameParts.Field<Button>(menu, "btnLoad").gameObject);
            yield return Wait(0.8f);
            var list = (RectTransform)parts.Original(GameParts.PList);
            List<Toggle> rows = RowsOf(list);
            if (rows.Count == 0) { checks.Control("tint-skipped", false, "no save row to test on"); yield break; }
            Toggle row = rows.Count > 1 ? rows[1] : rows[0];
            Pair with = MakePair("control-tint-on", row.gameObject, Side.Right);
            if (with != null) { yield return States(with); Hide(with); }
            var menuText = ((Button)parts.Original(GameParts.PMenuButton)).GetComponentInChildren<TMPro.TMP_Text>(true);
            LiveTintControls(row, BenchCanvas(UiWidgets.RootCanvas(row.transform)), menuText);

            int differBefore = checks.Different;
            Canvas canvas = BenchCanvas(UiWidgets.RootCanvas(row.transform));
            string problem;
            RectTransform part = Strip.Clone(row.gameObject, canvas.transform, "control tint-off", out problem, null,
                                             new StripControl { SkipTint = true });
            if (part == null) { checks.Control("tint-skipped", false, "refused: " + problem); yield break; }
            Track(part.gameObject);
            RectTransform orig = (RectTransform)row.transform;
            UiWidgets.Place(part, orig, Offset(orig, Side.Right));
            var off = new Pair { Name = "control-tint-off", Orig = orig, Part = part };
            Lift(off);
            yield return States(off, false);
            Hide(off);
            int differ = checks.Different - differBefore;
            checks.Control("tint-skipped", differ > 0, differ + " state(s) differ without step 2b");
            Click(((Button)parts.Original(GameParts.PCancel)).gameObject);
            yield return Wait(0.5f);
        }

        // Step 2b's refusals that need an original that woke (a live row): a snapshot naming a graphic outside the
        // original (planted in the live row's snapshot for one clone and taken out again), and a snapshot graphic the
        // clone does not have (removed from the clone before the strip).
        void LiveTintControls(Toggle row, Canvas canvas, Graphic outside)
        {
            string problem;
            Dictionary<Graphic, Color> snap = Strip.TintSnapshotOf(row);
            if (snap == null || snap.Count == 0)
            {
                checks.Control("tint-original-outside", false, "the live row has no tint snapshot");
                return;
            }
            Graphic key = snap.Keys.FirstOrDefault(g => g != null && g.transform.IsChildOf(row.transform) &&
                                                       g.GetComponents<Component>().All(c => c == null ||
                                                           c.GetType().Name != "UIDashedRoundedBorder"));
            RectTransform r;
            snap[outside] = outside.color;
            try { r = Strip.Clone(row.gameObject, canvas.transform, "control tint-original", out problem); }
            finally { snap.Remove(outside); }
            Track(r != null ? r.gameObject : null);
            checks.Control("tint-original-outside", r == null && Has(problem, "names a graphic outside it"),
                           problem ?? "built");
            if (key == null) { checks.Control("tint-graphic-missing", false, "no snapshot graphic to remove"); return; }
            Transform rowT = row.transform;
            r = Strip.Clone(row.gameObject, canvas.transform, "control tint-graphic", out problem, null,
                            new StripControl
            {
                Plant = clone =>
                {
                    Transform t = Strip.FindSame(clone.transform, rowT, key.transform);
                    Component g = t != null ? t.GetComponent(key.GetType()) : null;
                    if (g != null) DestroyImmediate(g);
                }
            });
            Track(r != null ? r.gameObject : null);
            checks.Control("tint-graphic-missing", r == null && Has(problem, "no clone graphic"), problem ?? "built");
        }

        static void AddChild(GameObject clone, Type component)
        {
            var child = new GameObject(Strip.ObjectPrefix + "control child", typeof(RectTransform));
            child.transform.SetParent(clone.transform, false);
            child.AddComponent(component);
        }

        // ---- the dialog: Esc and the hotkeys stop at it ----

        IEnumerator Modal()
        {
            bool menuScene = Object.FindObjectOfType<MenuSceneUI>() != null;
            GameParts parts = menuScene ? GameParts.InMenu() : GameParts.InGame();
            if (parts.Missing != null) { checks.Fail("part missing: " + parts.Missing); yield break; }
            bool hooked = GameHooks.IsInstalled(ModalRegistry.HookName);
            Log.Info("ui: bench modal: the pop-up postfix is " + (hooked ? "installed" : "NOT installed (HookPlan " +
                     "has no " + ModalRegistry.HookName + " yet): the getters keep the game's own answer"));
            Log.Info("ui: bench modal before: " + Getters());
            Canvas canvas = BenchCanvas(parts.GameCanvas);
            bool closed = false;
            ModDialog d = ModDialog.Open(parts, canvas, "name.fallback", () => closed = true);
            if (d == null) { checks.Fail("the dialog was not built"); yield break; }
            Track(d.gameObject);
            yield return Wait(0.3f);
            string with = Getters();
            Log.Info("ui: bench modal with the dialog open: " + with + "; registry open " + ModalRegistry.AnyOpen);
            if (hooked) GettersControl("getters-with-dialog", menuScene, true);
            yield return Natural("modal-dialog");

            string speed0 = Speed();
            Log.Info("ui: bench modal: waiting 30 s for keys (a speed hotkey first, then Esc); speed " + speed0);
            float until = Time.realtimeSinceStartup + 30f;
            while (!closed && Time.realtimeSinceStartup < until) yield return null;
            string speed1 = Speed();
            bool pause = PauseVisible();
            Log.Info("ui: bench modal: dialog closed by Esc " + closed + "; pause screen shown " + pause + "; speed " +
                     speed0 + " -> " + speed1);
            if (closed)
            {
                checks.Control("esc-stops-at-dialog", !pause, "pause screen shown " + pause);
                checks.Control("hotkeys-stop-at-dialog", speed0 == speed1, "speed " + speed0 + " -> " + speed1);
            }
            else { checks.Fail("no Esc reached the dialog in 30 s"); d.Close(); }
            yield return Wait(0.3f);
            string after = Getters();
            Log.Info("ui: bench modal after close: " + after);
            if (hooked) GettersControl("getters-after-close", menuScene, false);

            RectTransform plain = UiWidgets.Box(canvas.transform, "unregistered panel");
            Track(plain.gameObject);
            if (hooked) GettersControl("getters-unregistered", menuScene, false);

            if (!menuScene) yield return KeysReachGame();
        }

        // The control for the dialog's key test: with no dialog open, Esc must reach the game and open the pause
        // screen (the bench then closes it). Needs no hook.
        IEnumerator KeysReachGame()
        {
            if (Object.FindObjectOfType<Game.UI.TimeControlPanel>() == null)
            {
                checks.Fail("keys: not in a game");
                yield break;
            }
            if (PauseVisible()) { checks.Fail("keys: the pause screen is already shown"); yield break; }
            // Esc closes an open game window before it opens the pause screen (a loaded save opens Contracts):
            // close any first, as Esc would
            Game.UI.UIManager ui = SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance;
            for (int i = 0; i < 3 && ui != null && (Open(ui.Current) || Open(ui.Current2)); i++)
            {
                Log.Info("ui: bench keys: closing the game's open window " +
                         (Open(ui.Current) ? ui.Current.name : ui.Current2.name));
                ui.Close();
                yield return Wait(0.5f);
            }
            if (ui != null && (Open(ui.Current) || Open(ui.Current2)))
            {
                checks.Fail("keys: a game window stays open");
                yield break;
            }
            Log.Info("ui: bench keys: waiting 20 s for Esc with no dialog (the pause screen must open)");
            float until = Time.realtimeSinceStartup + 20f;
            while (!PauseVisible() && Time.realtimeSinceStartup < until) yield return null;
            checks.Control("keys-reach-game", PauseVisible(), "pause screen shown " + PauseVisible());
            var screen = Object.FindObjectsOfType<Game.UI.Screens.PauseScreen>(true).FirstOrDefault();
            if (screen != null && screen.Visible) Click(GameParts.Field<Button>(screen, "closeButton").gameObject);
            yield return Wait(0.5f);
        }

        static string Getters() { return "popup " + Getter(false) + ", any " + Getter(true); }

        // One getter's answer, or why it gave none (each asked on its own, so one that throws does not hide the
        // other).
        static string Getter(bool any)
        {
            try
            {
                Game.UI.UIManager ui = SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance;
                if (ui == null) return "no UIManager";
                return (any ? ui.IsAnnyPopUpWindowVisible : ui.IsPopUpWindowVisible).ToString();
            }
            catch (Exception e) { return "?(" + e.GetType().Name + ")"; }
        }

        // The getters' control. In game both getters; at the menu only IsPopUpWindowVisible (the one the menu's Esc
        // asks, D 131891): the game's own IsAnnyPopUpWindowVisible throws there (popupWindowYesNoBeforeTutorial is
        // unset in the menu scene) before any postfix runs, and nothing calls it there (ModalPopups).
        void GettersControl(string name, bool menuScene, bool open)
        {
            string want = open ? "True" : "False";
            if (menuScene)
            {
                string popup = Getter(false);
                Log.Info("ui: bench modal " + name + " at the menu: IsAnnyPopUpWindowVisible not asked (the " +
                         "game's own getter throws there and nothing calls it there); IsPopUpWindowVisible " + popup);
                checks.Control(name, popup == want, "popup " + popup);
                return;
            }
            string both = Getters();
            checks.Control(name, both == "popup " + want + ", any " + want, both);
        }

        static string Speed()
        {
            string c = BenchChecks.Clock();
            int at = c.IndexOf('@');
            return at > 0 ? c.Substring(0, at) : c;
        }

        static bool Open(Game.UI.Windows.Windows.UIWindow w) { return w != null && w.Open; }

        static bool PauseVisible()
        {
            var p = Object.FindObjectsOfType<Game.UI.Screens.PauseScreen>(true).FirstOrDefault();
            return p != null && p.Visible;
        }

        static bool Has(string s, string part) { return s != null && s.IndexOf(part, StringComparison.Ordinal) >= 0; }

        static Type GameType(string fullName)
        {
            return typeof(MenuSceneUI).Assembly.GetType(fullName, false);
        }
    }
}
