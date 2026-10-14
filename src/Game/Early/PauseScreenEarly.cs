using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using BaseScreen = Game.UI.Screens.BaseScreen;
using PauseScreen = Game.UI.Screens.PauseScreen;

namespace SolarExpanseMultiplayer
{
    // The pause (Esc) screen's PLAYERS entry (M8 Track U; the full track's pause-screen hooks take it over): a clone of
    // the screen's own Settings button (the game's look), shown while a session runs and hidden outside one; a click
    // goes to MenuSessions.PlayersClicked (the PLAYERS screen). The screen's pause is not held here: in a session
    // GamePauses drops the setter's speed calls (the open's pause and the close's restore), so this prefix only adds
    // the button and always lets the game's setter run. GameOverScreen, the other BaseScreen, stays vanilla.
    // Installed only on an early-track profile (HookPlan).
    [HarmonyPatch(typeof(BaseScreen), "Visible", MethodType.Setter)]
    public static class PauseScreenEarly
    {
        const string ButtonName = "SEMP Players";
        static readonly FieldInfo SettingsButton = AccessTools.Field(typeof(PauseScreen), "settingsButton");
        static readonly FieldInfo ExitToMenuButton = AccessTools.Field(typeof(PauseScreen), "exitToMenuButton");
        static readonly FieldInfo ChildElements = AccessTools.Field(typeof(Game.UI.CommonControls.MyButton),
            "childElements");

        [HarmonyPrefix]
        static void Prefix(BaseScreen __instance, bool value)
        {
            MenuSessions early = Session.Early;
            var pause = __instance as PauseScreen;
            if (pause == null || early == null || !value) return;
            bool live;
            try { live = Session.Live; }
            catch (Exception e) { early.PauseFailed("the session check failed: " + e.Message); return; }
            ShowPlayers(pause, early, live);
        }

        // The PLAYERS button on or off; built once per pause screen. A failure only costs the button.
        static void ShowPlayers(PauseScreen pause, MenuSessions early, bool on)
        {
            if (early.Saves != null) early.Saves.Buttons.Apply(pause, early);   // the save buttons, in or out of a session
            try
            {
                var settings = SettingsButton != null ? SettingsButton.GetValue(pause) as Button : null;
                if (settings == null) { if (on) early.PauseFailed("no Settings button to copy"); return; }
                Transform parent = settings.transform.parent;
                Transform mine = parent.Find(ButtonName);
                if (mine == null)
                {
                    if (!on) return;
                    mine = Build(pause, settings, early);
                    if (mine == null) return;
                }
                mine.gameObject.SetActive(on);
            }
            catch (Exception e) { early.PauseFailed("the PLAYERS button failed: " + e.Message); }
        }

        // DD0's recipe, cut down to one button: cloned under an inactive holder (so nothing of it runs yet), every
        // component but plain UGUI and TMP parts, the button itself and MyButton removed (the game's translation
        // script would rewrite the text and keep handlers on the game's language events; an EventTrigger, an
        // Animator or another control would act on its own), every event of what is kept replaced by an empty one,
        // the child colours put back to the original's untinted ones (MyButton snapshots them when it wakes), then
        // moved beside the original, which wakes it.
        // TODO(M8 Task 17): replace this with the parts kit's Strip (the vetted allowlist, the reference scan).
        static Transform Build(PauseScreen pause, Button original, MenuSessions early)
        {
            var holder = new GameObject("SEMP holder");
            holder.SetActive(false);
            try
            {
                GameObject clone = UnityEngine.Object.Instantiate(original.gameObject, holder.transform, false);
                clone.name = ButtonName;
                var button = clone.GetComponent<Button>();
                if (button == null) { early.PauseFailed("the copied button has no Button"); return null; }
                int removed = 0;
                foreach (Component c in clone.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || Kept(c, button)) continue;
                    UnityEngine.Object.DestroyImmediate(c);
                    removed++;
                }
                int events = 0;
                foreach (MonoBehaviour m in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    if (m != null) events += FreshEvents(m);
                button.onClick.AddListener(early.OnPlayersClicked);
                UntintedColours(original, clone);
                TMP_Text label = null;
                foreach (TMP_Text t in clone.GetComponentsInChildren<TMP_Text>(true))
                {
                    t.richText = false;
                    t.parseCtrlCharacters = false;
                    if (label == null) label = t;   // the button's own label: the first text under it
                }
                if (label != null) label.text = UiText.Get("hud.players.button");
                Transform parent = original.transform.parent;
                var exit = ExitToMenuButton != null ? ExitToMenuButton.GetValue(pause) as Button : null;
                int at = exit != null && exit.transform.parent == parent ? exit.transform.GetSiblingIndex()
                    : original.transform.GetSiblingIndex() + 1;
                clone.transform.SetParent(parent, false);
                clone.transform.SetSiblingIndex(at);
                Log.Info("ui: built pause.players (stripped " + removed + " components, " + events +
                         " events emptied)");
                return clone.transform;
            }
            finally { UnityEngine.Object.Destroy(holder); }
        }

        // What a clone keeps: its transforms and canvas renderers, the button itself (MyButton or a plain Button), and
        // the plain UGUI and TMP parts that only draw or lay out. Any other control, an EventTrigger, an Animator and
        // every game script goes.
        static bool Kept(Component c, Button button)
        {
            if (c is Transform || c is CanvasRenderer || c is CanvasGroup) return true;
            if (ReferenceEquals(c, button)) return true;
            if (c is Selectable || c is ScrollRect) return false;
            if (!(c is MonoBehaviour)) return false;
            string ns = c.GetType().Namespace;
            return ns == "UnityEngine.UI" || ns == "TMPro";
        }

        // Every UnityEvent field of a kept component (persistent listeners included) replaced by an empty one of the
        // same type, so nothing the original was wired to is reached from the clone. The count, for the log.
        static int FreshEvents(MonoBehaviour m)
        {
            int n = 0;
            for (Type t = m.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(UnityEventBase).IsAssignableFrom(f.FieldType)) continue;
                    object now = f.GetValue(m);
                    Type kind = now != null ? now.GetType() : f.FieldType;
                    if (kind.IsAbstract) continue;   // no event object to carry listeners
                    // a type with no plain constructor throws here: no button at all, rather than one still wired
                    f.SetValue(m, Activator.CreateInstance(kind));
                    n++;
                }
            return n;
        }

        // MyButton keeps each child graphic's untinted colour from when it woke; a live button's children already
        // carry its current tint, so the clone takes the original's untinted colours before it wakes.
        static void UntintedColours(Button original, GameObject clone)
        {
            if (ChildElements == null || !(original is Game.UI.CommonControls.MyButton)) return;
            object elements = ChildElements.GetValue(original);
            if (elements == null) return;
            FieldInfo tints = AccessTools.Field(elements.GetType(), "originalTintColor");
            var map = tints != null ? tints.GetValue(elements) as Dictionary<Graphic, Color> : null;
            if (map == null) return;
            foreach (KeyValuePair<Graphic, Color> kv in map)
            {
                if (kv.Key == null) continue;
                string path = PathUnder(kv.Key.transform, original.transform);
                Transform t = path == null ? null : path.Length == 0 ? clone.transform : clone.transform.Find(path);
                Graphic g = t != null ? t.GetComponent<Graphic>() : null;
                if (g != null) g.color = kv.Value;
            }
        }

        static string PathUnder(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (Transform x = t; x != null; x = x.parent)
            {
                if (x == root) { parts.Reverse(); return string.Join("/", parts.ToArray()); }
                parts.Add(x.name);
            }
            return null;
        }
    }
}
