using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine.UI;

namespace SolarExpanseMultiplayer
{
    // The main menu's Multiplayer button: the game's own hidden btnMult (third in the list, the game's own look and
    // hover), shown and re-wired to the mod. A second patch class on MenuSceneUI.Start beside MenuMarker, installed
    // only where the button may show (HookPlan.EarlyTrack: a normal install since the real path opened, or a valid
    // test or player-path profile). It runs after MenuMarker's postfix (lower priority), which
    // makes the early session object at the menu-ready step. A failure leaves the button hidden and logs why; it
    // never quits.
    [HarmonyPatch(typeof(MenuSceneUI), "Start")]
    public static class MenuEntry
    {
        static readonly FieldInfo BtnMult = AccessTools.Field(typeof(MenuSceneUI), "btnMult");

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        static void Postfix(MenuSceneUI __instance)
        {
            try
            {
                if (!RealPath.ButtonShown(Boot.Profile, MenuSessions.PlayerPath)) return;
                MenuSessions early = Session.Early;
                if (early == null) { Log.Warn("menu: no early session object, so no Multiplayer button"); return; }
                Button button = BtnMult != null ? BtnMult.GetValue(__instance) as Button : null;
                if (button == null) { Log.Warn("menu: the game's Multiplayer button was not found"); return; }
                // The game's own listener loads its old loading screen: every listener goes, then only ours.
                button.onClick.RemoveAllListeners();
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(early.OnMultiplayerClicked);
                // The game's own label reads "MULTIPLAYER GAME", and its StaticTranslateText writes that over any
                // other text in its Start (which runs once the hidden button is shown) and on a language change: it
                // goes before the button is shown, so the mod's "MULTIPLAYER" stays.
                foreach (UnityEngine.MonoBehaviour m in button.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true))
                    if (m != null && m.GetType().Name == "StaticTranslateText") UnityEngine.Object.DestroyImmediate(m);
                button.gameObject.SetActive(true);
                foreach (TMPro.TMP_Text t in button.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    t.text = UiText.Get("menu.button");
                    break;
                }
                Log.Info("menu: the Multiplayer button is shown");
                early.OnMenuShown(__instance);
            }
            catch (Exception e) { Log.Error("menu: the Multiplayer button failed: " + e.Message); }
        }
    }
}
