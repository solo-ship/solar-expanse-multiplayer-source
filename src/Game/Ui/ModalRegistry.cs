using System;
using HarmonyLib;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The mod's open dialogs (DD28). While one is open the game must act as if one of its own pop-ups were up:
    // UIManager.Update then ignores Esc (no pause menu behind the dialog, D 131891), the pause screen keeps itself
    // open on Esc (D 171577), and the hotkeys stand still (InputManager.UpdateKeyboard, D 81759). The dialog handles
    // Esc itself (ModDialog). An entry counts only while its object is alive and shown, so a dialog lost with its scene
    // never leaves the game deaf to Esc. The stack is a static here only because Harmony's postfixes are static; it
    // holds nothing that outlives the dialogs' objects.
    // Two game windows close on Esc by themselves, whatever pop-up is up: LoadSaveDialogWindow.Update (D 132744) and
    // SettingsWindow.Update (D 133112). A mod dialog over either would be closed by the same Esc as the window under
    // it, so the screens never stack a mod dialog over those two.
    public static class ModalRegistry
    {
        // HookPlan's name for ModalPopups.
        public const string HookName = HookPlan.ModalPopups;

        // Ranked as drawn: by the sorting order of the canvas it sits in (the screens' layers, UiLayers), so the dialog
        // Esc belongs to is the one on top. Within a canvas the newest is drawn last (Open moves it there).
        static readonly ModalStack<GameObject> stack =
            new ModalStack<GameObject>(g => g != null && g.activeInHierarchy, DrawnOrder);

        public static bool AnyOpen { get { return stack.AnyOpen; } }
        public static GameObject Top { get { return stack.Top; } }

        public static void Open(GameObject dialog)
        {
            stack.Open(dialog);
            // the newest of its canvas is drawn over the others there, as it is the newest of its rank here
            Transform t = dialog != null ? dialog.transform : null;
            if (t != null && t.parent != null && t.parent.GetComponent<Canvas>() != null) t.SetAsLastSibling();
            Log.Info("ui: dialog open " + (dialog != null ? dialog.name : "(none)") + " (" + stack.Count + " open)");
        }

        public static void Close(GameObject dialog)
        {
            stack.Close(dialog);
        }

        static int DrawnOrder(GameObject g)
        {
            Canvas c = g.GetComponentInParent<Canvas>();
            return c != null ? c.sortingOrder : int.MinValue;
        }

    }

    // UIManager.IsPopUpWindowVisible and IsAnnyPopUpWindowVisible (D 131316-131318) also answer true while a mod
    // dialog is open; otherwise they are untouched. Two getters: HookPlan.ExpectedMethods must say 2.
    // In the menu scene the game's own IsAnnyPopUpWindowVisible throws (its popupWindowYesNoBeforeTutorial is unset
    // there), so this postfix never runs there. That is harmless: the game's only caller is
    // InputManager.UpdateKeyboard (D 81759), and InputManager lives only in the game scene (level4, not kept across
    // scenes); the mod calls it only from its test bench. The menu's Esc asks IsPopUpWindowVisible, which works.
    [HarmonyPatch(typeof(Game.UI.UIManager))]
    public static class ModalPopups
    {
        static bool failed;

        [HarmonyPatch(typeof(Game.UI.UIManager), nameof(Game.UI.UIManager.IsPopUpWindowVisible), MethodType.Getter)]
        [HarmonyPostfix]
        static void PopUpVisible(ref bool __result) { if (!__result) __result = Open(); }

        [HarmonyPatch(typeof(Game.UI.UIManager), nameof(Game.UI.UIManager.IsAnnyPopUpWindowVisible), MethodType.Getter)]
        [HarmonyPostfix]
        static void AnyPopUpVisible(ref bool __result) { if (!__result) __result = Open(); }

        // Never throws into the game's Update; a failure answers "none open" (the game as vanilla), logged once.
        static bool Open()
        {
            try { return ModalRegistry.AnyOpen; }
            catch (Exception e)
            {
                if (!failed) { failed = true; Log.Error("ui: the dialog check failed: " + e.Message); }
                return false;
            }
        }
    }
}
