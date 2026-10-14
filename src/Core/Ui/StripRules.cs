using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    public enum StripAction { Remove = 0, Keep = 1, ReplaceWithScrollRect = 2 }

    // DD0's widget allowlist (docs/research/lobby.md U1b), the one table Strip resolves to Types. Every entry is
    // "<assembly>|<full type name>", so a game update that moves or renames a vetted class, or a mod class that
    // happens to share a short name, never matches: the game side resolves each entry to its Type once and then
    // compares Type identity, and a subclass of a kept type is not kept.
    public static class StripRules
    {
        public const string UnityUi = "UnityEngine.UI", UnityUiModule = "UnityEngine.UIModule",
            UnityCore = "UnityEngine.CoreModule", Tmp = "Unity.TextMeshPro", Game = "Assembly-CSharp";

        // What a clone may keep. Unity's own graphics, layout, masks and controls, TMP, and the four game classes
        // vetted method by method (MyButton, MyToggle, MyDropdown, UIDashedRoundedBorder). Transform and
        // RectTransform are listed because the strip never removes them.
        public static readonly string[] Kept =
        {
            UnityCore + "|UnityEngine.Transform", UnityCore + "|UnityEngine.RectTransform",
            UnityUiModule + "|UnityEngine.CanvasRenderer", UnityUiModule + "|UnityEngine.Canvas",
            UnityUiModule + "|UnityEngine.CanvasGroup",
            UnityUi + "|UnityEngine.UI.Image", UnityUi + "|UnityEngine.UI.RawImage", UnityUi + "|UnityEngine.UI.Mask",
            UnityUi + "|UnityEngine.UI.RectMask2D", UnityUi + "|UnityEngine.UI.Button",
            UnityUi + "|UnityEngine.UI.Toggle",
            UnityUi + "|UnityEngine.UI.ToggleGroup", UnityUi + "|UnityEngine.UI.ScrollRect",
            UnityUi + "|UnityEngine.UI.Scrollbar", UnityUi + "|UnityEngine.UI.Slider",
            UnityUi + "|UnityEngine.UI.LayoutElement", UnityUi + "|UnityEngine.UI.HorizontalLayoutGroup",
            UnityUi + "|UnityEngine.UI.VerticalLayoutGroup", UnityUi + "|UnityEngine.UI.GridLayoutGroup",
            UnityUi + "|UnityEngine.UI.ContentSizeFitter", UnityUi + "|UnityEngine.UI.AspectRatioFitter",
            UnityUi + "|UnityEngine.UI.Outline", UnityUi + "|UnityEngine.UI.Shadow",
            UnityUi + "|UnityEngine.UI.PositionAsUV1",
            UnityUi + "|UnityEngine.UI.GraphicRaycaster",
            Tmp + "|TMPro.TextMeshProUGUI", Tmp + "|TMPro.TMP_InputField", Tmp + "|TMPro.TMP_SubMeshUI",
            Tmp + "|TMPro.TMP_Dropdown", Tmp + "|TMPro.TMP_SelectionCaret",
            Game + "|Game.UI.CommonControls.MyButton", Game + "|Game.UI.CommonControls.MyToggle",
            Game + "|Game.UI.CommonControls.MyDropdown", Game + "|ScriptsAdam.UIDashedRoundedBorder",
        };

        // Sets the game-wide shader value ResearchContentScale in Awake (D 43442): replaced by a plain ScrollRect with
        // the same settings.
        public const string CustomScrollRect = Game + "|CustomScrollRect";
        // [RequireComponent(typeof(Image))]: it must go before its Image whenever that Image goes.
        public const string DashedBorder = Game + "|ScriptsAdam.UIDashedRoundedBorder";
        // Its DoStateTransition dereferences nonInteractableText: a clone keeps it only with that text inside it.
        public const string Dropdown = Game + "|Game.UI.CommonControls.MyDropdown";
        // The tint snapshot (MyControlChildElements) that step 2b copies from the original: on these three only.
        public static readonly string[] Tinted =
        {
            Game + "|Game.UI.CommonControls.MyButton", Game + "|Game.UI.CommonControls.MyToggle", Dropdown,
        };

        // U1b's list of game scripts that reach a singleton, a static event or game state. Never kept (the table
        // above is the only way in); listed so a test pins that none of them slips into Kept.
        public static readonly string[] Refused =
        {
            Game + "|Language.StaticTranslateText", Game + "|ShowToolTip", Game + "|DropDownEnum",
            Game + "|ToolTipWindow", Game + "|Game.UI.MainWindow.NotificationUI", Game + "|Game.UI.TimeControlPanel",
            Game + "|Game.UI.Windows.Windows.UIWindow", Game + "|Game.UI.Menu.GameCustomizationUI.GameCustomizationUI",
            Game + "|Game.UI.Windows.LoadSaveDialogWindow", Game + "|Game.UI.Windows.SettingsWindow",
            Game + "|Game.UI.Windows.Elements.LoadSaveDialogElement",
            Game + "|CorporationSelectUI", Game + "|MenuSceneUI", CustomScrollRect,
        };

        static readonly HashSet<string> keptSet = new HashSet<string>(Kept, StringComparer.Ordinal);

        public static string Key(string assembly, string fullName)
        {
            return (assembly ?? "") + "|" + (fullName ?? "");
        }

        public static StripAction Decide(string assembly, string fullName)
        {
            if (string.IsNullOrEmpty(assembly) || string.IsNullOrEmpty(fullName)) return StripAction.Remove;
            string key = Key(assembly, fullName);
            if (key == CustomScrollRect) return StripAction.ReplaceWithScrollRect;
            return keptSet.Contains(key) ? StripAction.Keep : StripAction.Remove;
        }

        public static bool IsTinted(string assembly, string fullName)
        {
            return Array.IndexOf(Tinted, Key(assembly, fullName)) >= 0;
        }

        // The order DestroyImmediate needs (U1b): scripts first, then the dashed border before its Image, graphics
        // before CanvasRenderer. Lower goes first.
        public static int RemoveRank(string assembly, string fullName, bool isGraphic, bool isCanvasRenderer)
        {
            if (isCanvasRenderer) return 3;
            if (isGraphic) return 2;
            if (Key(assembly, fullName) == DashedBorder) return 1;
            return 0;
        }

        // The removal list in that order; ties keep the order they came in (the deepest objects first, as found).
        public static List<int> Order(IList<int> ranks)
        {
            var idx = new List<int>();
            if (ranks == null) return idx;
            for (int i = 0; i < ranks.Count; i++) idx.Add(i);
            idx.Sort((a, b) => ranks[a] != ranks[b] ? ranks[a].CompareTo(ranks[b]) : a.CompareTo(b));
            return idx;
        }

        // The entry's assembly and type name, for resolving it ("<full name>, <assembly>").
        public static string TypeLoadName(string entry)
        {
            int bar = entry == null ? -1 : entry.IndexOf('|');
            if (bar <= 0 || bar == entry.Length - 1) return null;
            return entry.Substring(bar + 1) + ", " + entry.Substring(0, bar);
        }
    }
}
