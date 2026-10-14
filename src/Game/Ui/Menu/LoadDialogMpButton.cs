using System;
using System.Reflection;
using Game.UI.Windows;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SolarExpanseMultiplayer
{
    // Board 12, left: the game's own Load Game dialog gains a "Multiplayer saves" button (DD24), a clone of its own OK
    // button on a row of its own between the save list and OK and Cancel: the row goes into the dialog's own vertical
    // layout, so the dialog grows to hold it and nothing overlaps the list. A postfix on LoadSaveDialogWindow.Open: shown only at the main menu, in Loading
    // mode, while the session offers the Multiplayer saves list and solo play (MenuControls.LoadDialogButton), and
    // never while host setup has the dialog open as its own. The button closes the game's dialog first (it closes on
    // Esc by itself, so no mod dialog may sit over it), then opens the list; the list's Back opens the dialog again.
    // Not in HookPlan yet (its capabilities are off in the early track); the dev trigger applies it directly.
    [HarmonyPatch(typeof(LoadSaveDialogWindow), nameof(LoadSaveDialogWindow.Open))]
    public static class LoadDialogMpButton
    {
        public const string ButtonName = Strip.ObjectPrefix + "button menu.saves.open";
        const float RowW = 440f, RowH = 56f, Width = 236f, Height = 39f;   // the dialog's own units
        static readonly MethodInfo CloseClick = AccessTools.Method(typeof(LoadSaveDialogWindow), "OnCloseButtonClick");
        static bool failed;

        [HarmonyPostfix]
        static void Postfix(LoadSaveDialogWindow __instance, LoadSaveDialogWindow.ELoadSaveDialogMode mode)
        {
            try
            {
                MenuSessions early = Session.Early;
                MenuScreens screens = early != null ? early.Screens : null;
                if (screens == null) return;
                if (mode != LoadSaveDialogWindow.ELoadSaveDialogMode.Loading)
                {
                    Hide(__instance);
                    return;
                }
                Apply(__instance, screens);
            }
            catch (Exception e)
            {
                if (failed) return;
                failed = true;
                Log.Error("menu: the Load Game dialog's Multiplayer saves button failed: " + e.Message);
            }
        }

        // Shows the button on this dialog when the menu offers it (made once per dialog), else hides it.
        public static void Apply(LoadSaveDialogWindow dialog, MenuScreens screens)
        {
            if (dialog == null || screens == null) return;
            Transform existing = dialog.transform.Find(ButtonName);
            if (!screens.OffersSavesButton)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }
            if (existing != null) { existing.gameObject.SetActive(true); return; }
            MenuKit kit = screens.KitForDialogButton();
            if (kit == null) return;
            // the row sits where the dialog's own button row is, which moves down one place
            Transform buttons = OkRow(dialog);
            RectTransform row = UiWidgets.Box(dialog.transform, "mp saves row");
            row.gameObject.name = ButtonName;
            row.sizeDelta = new Vector2(RowW, RowH);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = RowH;
            le.minWidth = le.preferredWidth = RowW;
            if (buttons != null) row.SetSiblingIndex(buttons.GetSiblingIndex());
            Button b = kit.Button(row, "load.mpsaves", "menu.saves.button", (RowW - Width) / 2f, (RowH - Height) / 2f,
                Width, Height, () => Open(dialog, screens));
            if (b == null) { UnityEngine.Object.Destroy(row.gameObject); return; }
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)dialog.transform);
            Log.Info("menu: the Load Game dialog has its Multiplayer saves button");
        }

        // The dialog's own row of OK and Cancel (a direct child of the dialog), or null.
        static Transform OkRow(LoadSaveDialogWindow dialog)
        {
            var ok = GameParts.Field<Button>(dialog, "btnOk");
            for (Transform x = ok != null ? ok.transform : null; x != null; x = x.parent)
                if (x.parent == dialog.transform) return x;
            return null;
        }

        static void Hide(LoadSaveDialogWindow dialog)
        {
            Transform existing = dialog != null ? dialog.transform.Find(ButtonName) : null;
            if (existing != null) existing.gameObject.SetActive(false);
        }

        // The game's own close (its OnHide and the menu's shadow), then the list.
        static void Open(LoadSaveDialogWindow dialog, MenuScreens screens)
        {
            if (!CloseGameDialog(dialog)) return;
            screens.OpenSaves(true);
        }

        // Closes the game's Load Game dialog the way its own close button does; false when it could not.
        internal static bool CloseGameDialog(LoadSaveDialogWindow dialog)
        {
            if (dialog == null || !dialog.gameObject.activeInHierarchy) return false;
            try { if (CloseClick != null) CloseClick.Invoke(dialog, null); return CloseClick != null; }
            catch (Exception e) { Log.Warn("menu: the Load Game dialog did not close: " + e.Message); return false; }
        }
    }
}
