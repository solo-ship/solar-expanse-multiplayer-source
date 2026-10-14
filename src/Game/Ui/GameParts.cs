using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // Title: the dialog's title. Body: the game's own pop-up body text (white, sentence case), for every paragraph and
    // name. Hint: the same text in the dialog label's grey, for the line under a paragraph. Small: the dialog's input
    // label (small grey capitals), for column heads and labels. Button: the menu button's text. Value: a figure.
    public enum TextRole { Title, Body, Button, Small, Value, Hint }

    // The pieces of a cloned window (the game's load dialog), found in the clone at the original's places.
    public sealed class WindowParts
    {
        public RectTransform Root, List;
        public TMP_Text Title;
        public Button Ok, Cancel, Close;
        public GameObject InputSection;
        public TMP_InputField Input;
    }

    // The pieces of a cloned save row.
    public sealed class RowParts
    {
        public RectTransform Root;
        public Toggle Toggle;
        public TMP_Text Name, Date, Company, Difficulty, System, Epoch;
        public GameObject NewSaveText, Details, TrashIcon;
        public Button Trash;

        // How wide the name may draw, in its own units: up to the trash icon or its button, whichever shown starts
        // first, less a small gap (the name's text area spans the whole row, under them). For PeerText's fit.
        public float NameWidth()
        {
            if (Name == null) return -1f;
            RectTransform nr = Name.rectTransform;
            Rect r = nr.rect;
            float stop = r.xMax;
            var corners = new Vector3[4];
            foreach (Component c in new Component[] { TrashIcon != null ? TrashIcon.transform : null, Trash })
            {
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                ((RectTransform)c.transform).GetWorldCorners(corners);
                stop = Mathf.Min(stop, nr.InverseTransformPoint(corners[0]).x);
            }
            return Mathf.Max(0f, stop - r.xMin - 8f);
        }
    }

    // The pieces of a cloned notification card.
    public sealed class NoticeParts
    {
        public RectTransform Root;
        public Image Image;
        public TMP_Text Text;
        public Button Button;
    }

    // The game's own UI parts for one scene (DD0), found once, and cloned by DD0's recipe (Strip) on request. If any
    // part this scene should have is missing (a game update moved it), Missing names the first one, every builder
    // returns null, and nothing half-built is ever shown. The finder reads the game's objects and never changes them.
    public sealed class GameParts
    {
        // part names, as the log and Missing give them
        public const string PMenu = "menu", PMenuButton = "menu button", PWindow = "load dialog",
            PTitle = "dialog title", PInput = "dialog input", PInputSection = "dialog input section",
            PList = "dialog list",
            POk = "dialog OK", PCancel = "dialog Cancel", PClose = "dialog close", PRow = "save row",
            PShadow = "dialog shadow", PAgencies = "agency list", PPanel = "speed panel", PMark = "speed mark ",
            PSpeedButton = "speed button ", PYear = "year text", PNotice = "notification card", PPause = "pause screen",
            PLinks = "menu links text", PVersion = "version text", PInputLabel = "dialog input label",
            PNoticeList = "notification list", PBodyText = "pop-up body text";

        static readonly string[] MarkFields =
        {
            "selectedTimeScale0X", "selectedTimeScale1kX", "selectedTimeScale100KX", "selectedTimeScale1MX",
            "selectedTimeScale5MX", "selectedTimeScale10MX",
        };
        static readonly string[] SpeedButtonFields =
        {
            "buttonTimeScale0X", "buttonTimeScale1kX", "buttonTimeScale100KX", "buttonTimeScale1MX",
            "buttonTimeScale5MX", "buttonTimeScale10MX",
        };

        readonly Dictionary<string, Object> parts = new Dictionary<string, Object>();
        readonly List<string> missing = new List<string>();
        int built;

        public string Scene { get; private set; }
        public string Missing { get { return missing.Count > 0 ? missing[0] : null; } }
        public IList<string> AllMissing { get { return missing.AsReadOnly(); } }
        public Canvas GameCanvas { get; private set; }

        GameParts(string scene) { Scene = scene; }

        public static GameParts InMenu() { return InMenu(null); }

        // extraPart: one more part to look for, which does not exist (the fail-closed control only).
        internal static GameParts InMenu(string extraPart)
        {
            var p = new GameParts("menu");
            try
            {
                MenuSceneUI menu = FindLive<MenuSceneUI>();
                p.Need(PMenu, menu);
                p.Need(PMenuButton, Field<Button>(menu, "btnSingle"));
                p.Need(PShadow, Field<GameObject>(menu, "shadowUnderLoadSaveDialogWindow"));
                p.Need(PLinks, FirstText(menu != null ? menu.transform.Find("Links") : null));
                p.Need(PVersion, Field<TMP_Text>(FindLive<global::Version>(), "text"));
                p.Dialog(Field<Component>(menu, "loadSaveDialogWindow"));
                p.Need(PBodyText, BodyText());
                CorporationSelectUI corp = FindLive<CorporationSelectUI>();
                p.Need(PAgencies, corp != null && AgencyItems(corp).Count > 0 ? corp : null);
                if (extraPart != null) p.Need(extraPart, null);
                p.GameCanvas = RootCanvas(menu != null ? menu.transform : null);
                p.Need("menu canvas", p.GameCanvas);
            }
            catch (Exception e) { p.Need("menu parts (" + e.GetType().Name + ": " + e.Message + ")", null); }
            p.Report();
            return p;
        }

        // Whether the scene's own root object is gone: the scene is closing (the game is loading over the menu). Its
        // parts cannot be found then and are not a game update's doing, so nothing should be logged or latched.
        public static bool SceneGone(bool inGame)
        {
            return inGame ? FindLive<Game.UI.TimeControlPanel>() == null : FindLive<MenuSceneUI>() == null;
        }

        public static GameParts InGame()
        {
            var p = new GameParts("game");
            try
            {
                Game.UI.TimeControlPanel panel = FindLive<Game.UI.TimeControlPanel>();
                p.Need(PPanel, panel);
                for (int i = 0; i < MarkFields.Length; i++)
                {
                    p.Need(PMark + i, Field<Image>(panel, MarkFields[i]));
                    p.Need(PSpeedButton + i, Field<Button>(panel, SpeedButtonFields[i]));
                }
                p.Need(PYear, Field<TMP_Text>(panel, "yearText"));
                Manager.NotificationManager nm = MonoBehaviourSingleton<Manager.NotificationManager>.Instance;
                p.Need(PNotice, Field<Component>(nm, "notificationUIPrefab"));
                p.Need(PNoticeList, Field<Transform>(nm, "notificationParent"));
                Game.UI.Screens.PauseScreen pause = FindLive<Game.UI.Screens.PauseScreen>();
                p.Need(PPause, pause);
                p.Need(PMenuButton, Field<Button>(pause, "saveGameButton"));
                p.Dialog(Field<Component>(pause, "loadSaveDialogWindow"));
                p.Need(PBodyText, BodyText());
                p.GameCanvas = RootCanvas(panel != null ? panel.transform : null);
                p.Need("game canvas", p.GameCanvas);
            }
            catch (Exception e) { p.Need("game parts (" + e.GetType().Name + ": " + e.Message + ")", null); }
            p.Report();
            return p;
        }

        void Dialog(Component window)
        {
            Need(PWindow, window);
            Need(PTitle, Field<TMP_Text>(window, "txtTitle"));
            Need(PInput, Field<TMP_InputField>(window, "inputField"));
            Need(PInputSection, Field<GameObject>(window, "inputSection"));
            Need(PList, Field<RectTransform>(window, "elementsParent"));
            Need(POk, Field<Button>(window, "btnOk"));
            Need(PCancel, Field<Button>(window, "btnCancel"));
            Need(PClose, Field<Button>(window, "btnClose"));
            Need(PRow, Field<Component>(window, "saveDialogElementPrefab"));
            // the label above the name field (shown in the save dialog)
            var section = Field<GameObject>(window, "inputSection");
            var field = Field<TMP_InputField>(window, "inputField");
            TMP_Text label = null;
            if (section != null && field != null)
                foreach (TMP_Text t in section.GetComponentsInChildren<TMP_Text>(true))
                    if (!t.transform.IsChildOf(field.transform)) { label = t; break; }
            Need(PInputLabel, label);
        }

        // The game's yes-or-no pop-up's own message text (each scene has one): the body text of every mod dialog.
        static TMP_Text BodyText()
        {
            foreach (PopUpWindowYesNo p in Object.FindObjectsOfType<PopUpWindowYesNo>(true))
            {
                if (p == null || !p.gameObject.scene.IsValid() || Strip.IsMod(p.transform)) continue;
                var t = Field<TMP_Text>(p, "mainText");
                if (t != null) return t;
            }
            return null;
        }

        static TMP_Text FirstText(Transform root)
        {
            return root != null ? root.GetComponentInChildren<TMP_Text>(true) : null;
        }

        void Need(string name, Object o)
        {
            if (o != null) { parts[name] = o; return; }
            if (!missing.Contains(name)) missing.Add(name);
        }

        void Report()
        {
            foreach (string m in missing) Log.Warn("ui: part missing: " + m + " (" + Scene + ")");
            Log.Info("ui: parts for the " + Scene + ": " + parts.Count + " found, " + missing.Count + " missing" +
                     (missing.Count > 0 ? "; every builder refuses (nothing half-built is shown)" : ""));
        }

        // The game's original of a part (read only), or null.
        public Object Original(string part)
        {
            Object o;
            return parts.TryGetValue(part, out o) ? o : null;
        }

        GameObject Go(string part)
        {
            Object o = Original(part);
            return o is GameObject ? (GameObject)o : o is Component ? ((Component)o).gameObject : null;
        }

        bool Usable(string what, params string[] need)
        {
            if (Missing != null)
            {
                Log.Warn("ui: " + what + " not built: part missing: " + Missing);
                return false;
            }
            foreach (string n in need)
                if (Original(n) == null)
                {
                    Log.Warn("ui: " + what + " not built: no " + n + " in the " + Scene);
                    return false;
                }
            return true;
        }

        // drawnLike: where the game draws copies of a prefab part (its pixel snapping, Strip.Clone).
        RectTransform Build(string part, Transform parent, string name, Action<GameObject> prepare = null,
                            Transform drawnLike = null)
        {
            return Build(Go(part), parent, name, prepare, drawnLike);
        }

        RectTransform Build(GameObject original, Transform parent, string name, Action<GameObject> prepare = null,
                            Transform drawnLike = null)
        {
            string problem;
            RectTransform rt = Strip.Clone(original, parent, name + " #" + (++built), out problem, prepare, null,
                                           drawnLike);
            if (rt != null) Fit(rt, ((RectTransform)original.transform).rect.size);
            return rt;
        }

        // A part keeps its original's size but none of its original's anchoring: centred in its new parent, at the
        // size the game draws it (a stretched original, such as a speed mark, would otherwise fill its new parent).
        // The screens place it from there.
        static void Fit(RectTransform rt, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = Vector2.zero;
        }

        // Sets a cloned toggle the way the game does (with its notification), so MyToggle's own refresh runs and its
        // tint is the last one written over the controls inside it, as in the game's own list. A toggle that is
        // already there is passed through the other state, which has the same effect. At build time the clone's
        // events hold only that refresh.
        public static void SetOn(Toggle t, bool on)
        {
            if (t == null) return;
            if (t.isOn == on) t.isOn = !on;
            t.isOn = on;
        }

        // ---- the parts ----

        // The main menu's own button (in game, the pause screen's), its text from the table.
        public Button MenuButton(Transform parent, string key)
        {
            if (!Usable("menu button", PMenuButton)) return null;
            RectTransform rt = Build(PMenuButton, parent, "menu button");
            if (rt == null) return null;
            SetText(rt, UiText.Get(key));
            return rt.GetComponent<Button>();
        }

        // The load dialog's own OK button, the push button of every mod dialog, with our words; it starts clickable
        // (the game's own OK is off until a save is picked) and a screen turns it off.
        public Button DialogButton(Transform parent, string text)
        {
            if (!Usable("dialog button", POk)) return null;
            RectTransform rt = Build(POk, parent, "dialog button");
            if (rt == null) return null;
            SetText(rt, text);
            Button b = rt.GetComponent<Button>();
            b.interactable = true;
            return b;
        }

        // The load dialog's frame: its rows gone, its name field hidden, the title from the table.
        public RectTransform Window(Transform parent, string titleKey)
        {
            if (!Usable("window", PWindow, PList, PTitle, PInputSection)) return null;
            Transform orig = Go(PWindow).transform, list = ((RectTransform)Original(PList));
            // the rows are hidden before the strip (so they never wake) and destroyed after it: removing them first
            // would move the sibling paths the tint step goes by
            RectTransform rt = Build(PWindow, parent, "window", clone =>
            {
                Transform cl = Strip.FindSame(clone.transform, orig, list);
                if (cl != null) foreach (Transform row in cl) row.gameObject.SetActive(false);
                // the mod's own row in the game's Load Game dialog (LoadDialogMpButton) is no part of a mod window;
                // hidden, not removed, so the pieces after it keep their places
                foreach (Transform ch in clone.transform)
                    if (ch.name.StartsWith(Strip.ObjectPrefix, StringComparison.Ordinal)) ch.gameObject.SetActive(false);
            });
            if (rt == null) return null;
            WindowParts w = PartsOfWindow(rt);
            if (w.List != null)
                for (int i = w.List.childCount - 1; i >= 0; i--) Object.DestroyImmediate(w.List.GetChild(i).gameObject);
            if (w.InputSection != null) w.InputSection.SetActive(false);
            if (w.Title != null) SetText(w.Title, UiText.Get(titleKey));
            return rt;
        }

        public WindowParts PartsOfWindow(RectTransform window)
        {
            var w = new WindowParts { Root = window };
            Transform orig = Go(PWindow) != null ? Go(PWindow).transform : null;
            if (window == null || orig == null) return w;
            w.List = Same<RectTransform>(window, orig, PList);
            w.Title = Same<TMP_Text>(window, orig, PTitle);
            w.Ok = Same<Button>(window, orig, POk);
            w.Cancel = Same<Button>(window, orig, PCancel);
            w.Close = Same<Button>(window, orig, PClose);
            w.Input = Same<TMP_InputField>(window, orig, PInput);
            Transform section = Go(PInputSection) != null
                ? Strip.FindSame(window, orig, Go(PInputSection).transform) : null;
            w.InputSection = section != null ? section.gameObject : null;
            return w;
        }

        // richText and parseCtrlCharacters off, empty.
        public TMP_Text Text(Transform parent, TextRole role)
        {
            string source = RoleSource(role);
            int dot = source.IndexOf('.');
            if (!Usable("text " + role, dot > 0 ? source.Substring(0, dot) : source)) return null;
            Transform t = TextOf(source);
            if (t == null) { Log.Warn("ui: text " + role + " not built: no text in " + source); return null; }
            // a row's text is drawn where the rows are
            Transform drawnLike = dot > 0 ? ((RectTransform)Original(PList)) : null;
            RectTransform rt = Build(t.gameObject, parent, "text " + role, null, drawnLike);
            if (rt == null) return null;
            TMP_Text text = rt.GetComponent<TMP_Text>();
            SetText(text, "");
            // the pop-up's text keeps wide margins for its own window; a screen gives each text its own box. It is
            // drawn a size up: the screens' paragraphs are their main content, as the boards draw them
            if (source == PBodyText)
            {
                text.margin = Vector4.zero;
                text.fontSize = Mathf.Round(text.fontSize * BodyScale);
            }
            if (role == TextRole.Hint) AsHint(text);
            return text;
        }

        // A hint: the body text, a size smaller, in the colour the game gives its dialog's input label.
        void AsHint(TMP_Text text)
        {
            var label = Original(PInputLabel) as TMP_Text;
            if (label != null) text.color = label.color;
            text.fontSize = Mathf.Round(text.fontSize * HintScale);
        }

        public const float BodyScale = 16f / 14f, HintScale = 14f / 16f;

        // Which game text each role copies (logged by the bench beside the copy).
        public string RoleSource(TextRole role)
        {
            switch (role)
            {
                case TextRole.Title: return PTitle;
                case TextRole.Button: return PMenuButton;
                case TextRole.Value: return Scene == "game" ? PYear : PVersion;
                case TextRole.Small: return PInputLabel;
                default: return PBodyText;
            }
        }

        // The game text a role source names: a part that is a text, the first text of a part, or a row field.
        public Transform TextOf(string source)
        {
            int dot = source.IndexOf('.');
            if (dot > 0)
            {
                var row = Original(source.Substring(0, dot));
                var t = Field<TMP_Text>(row, source.Substring(dot + 1));
                return t != null ? t.transform : null;
            }
            Object o = Original(source);
            if (o is TMP_Text) return ((TMP_Text)o).transform;
            GameObject go = Go(source);
            TMP_Text first = go != null ? go.GetComponentInChildren<TMP_Text>(true) : null;
            return first != null ? first.transform : null;
        }

        // The dialog's name field; a password field shows only its mask characters.
        public TMP_InputField Input(Transform parent, int maxChars, bool password)
        {
            if (!Usable("input", PInput)) return null;
            RectTransform rt = Build(PInput, parent, "input");
            if (rt == null) return null;
            TMP_InputField f = rt.GetComponent<TMP_InputField>();
            f.richText = false;
            if (f.textComponent != null)
            {
                f.textComponent.richText = false;
                f.textComponent.parseCtrlCharacters = false;
            }
            f.lineType = TMP_InputField.LineType.SingleLine;
            f.contentType = password ? TMP_InputField.ContentType.Password : TMP_InputField.ContentType.Standard;
            f.characterLimit = Math.Max(0, maxChars);
            f.text = "";
            var placeholder = f.placeholder as TMP_Text;
            if (placeholder != null) SetText(placeholder, "");
            return f;
        }

        // The new-game page's toggle for this agency (its own logo), off, its group cleared by the strip. agencyId is
        // the wire id (the game's CompanyDefinition.ID in lower case, AgencyWire), as the screens hold it.
        public Toggle AgencyToggle(Transform parent, string agencyId)
        {
            if (!Usable("agency toggle", PAgencies)) return null;
            Toggle orig = null;
            var ids = new List<string>();
            var toggles = new List<Toggle>();
            foreach (object item in AgencyItems(Original(PAgencies)))
            {
                // CompanyDefinition hides the base class's ID with its own: read it as that type
                var def = Field<ScriptableObjectScripts.CompanyDefinition>(item, "companyDefinition");
                ids.Add(def != null ? def.ID : null);
                toggles.Add(Field<Toggle>(item, "toggle"));
            }
            string gameId = AgencyWire.Resolve(AgencyWire.From(agencyId), ids);
            if (gameId != null) orig = toggles[ids.IndexOf(gameId)];
            if (orig == null)
            {
                Log.Warn("ui: agency toggle not built: no toggle for agency " + agencyId + " (the page has " +
                         string.Join(", ", ids.ConvertAll(i => i ?? "(none)").ToArray()) + ")");
                return null;
            }
            RectTransform rt = Build(orig.gameObject, parent, "agency " + agencyId);
            if (rt == null) return null;
            Toggle t = rt.GetComponent<Toggle>();
            SetOn(t, false);
            return t;
        }

        // The load list's row (from the game's prefab, so its colours are untinted), shown as the game shows a save:
        // details, the trash icon and its delete button on (a screen hides what it does not offer), texts empty.
        public RectTransform ListRow(Transform parent)
        {
            if (!Usable("list row", PRow)) return null;
            RectTransform rt = Build(PRow, parent, "row", null, (RectTransform)Original(PList));
            if (rt == null) return null;
            RowParts r = PartsOfRow(rt);
            if (r.NewSaveText != null) r.NewSaveText.SetActive(false);
            if (r.Details != null) r.Details.SetActive(true);
            if (r.TrashIcon != null) r.TrashIcon.SetActive(true);
            if (r.Trash != null) r.Trash.gameObject.SetActive(true);
            foreach (TMP_Text t in new[] { r.Name, r.Date, r.Company, r.Difficulty, r.System, r.Epoch })
                if (t != null) SetText(t, "");
            // the trash icon is stretched with the row and sized by a fixed inset, so a row made wider than the
            // game's would draw it wide: pinned to the row's right end at its own size, beside its delete button
            if (r.TrashIcon != null) PinRight((RectTransform)r.TrashIcon.transform);
            SetOn(r.Toggle, false);
            return rt;
        }

        // Keeps a child at its present size and its distance from its parent's right edge whatever the parent's
        // width becomes.
        static void PinRight(RectTransform rt)
        {
            var parent = rt.parent as RectTransform;
            if (parent == null) return;
            Rect pr = parent.rect, r = rt.rect;
            Vector3 c = parent.InverseTransformPoint(rt.TransformPoint(r.center));
            float right = parent.InverseTransformPoint(rt.TransformPoint(new Vector3(r.xMax, 0f, 0f))).x - pr.xMax;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = r.size;
            rt.anchoredPosition = new Vector2(right, c.y - pr.center.y);
        }

        public RowParts PartsOfRow(RectTransform row)
        {
            var r = new RowParts { Root = row };
            Component prefab = Original(PRow) as Component;
            if (row == null || prefab == null) return r;
            Transform p = prefab.transform;
            r.Toggle = row.GetComponent<Toggle>();
            r.Name = SameField<TMP_Text>(row, p, prefab, "txtName");
            r.Date = SameField<TMP_Text>(row, p, prefab, "txtGameDate");
            r.Company = SameField<TMP_Text>(row, p, prefab, "txtCompany");
            r.Difficulty = SameField<TMP_Text>(row, p, prefab, "txtDifficulty");
            r.System = SameField<TMP_Text>(row, p, prefab, "txtPlanetarySystem");
            r.Epoch = SameField<TMP_Text>(row, p, prefab, "txtStartGameEpoch");
            r.Trash = SameField<Button>(row, p, prefab, "trashButton");
            r.NewSaveText = SameGo(row, p, Field<GameObject>(prefab, "newSaveText"));
            r.Details = SameGo(row, p, Field<GameObject>(prefab, "saveDetails"));
            r.TrashIcon = SameGo(row, p, Field<GameObject>(prefab, "trashIcon"));
            return r;
        }

        // The notification card from the game's prefab, laid out as it ends its opening animation (the strip then
        // removes the Animation, so it never plays).
        public RectTransform Notice(Transform parent)
        {
            if (!Usable("notice", PNotice)) return null;
            RectTransform rt = Build(PNotice, parent, "notice", OpenPose, (Transform)Original(PNoticeList));
            if (rt == null) return null;
            OpenHeight(rt);
            // the card's layout group sizes its text; run it now, so a screen that fits text to it sees its width
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            NoticeParts n = PartsOfNotice(rt);
            if (n.Text != null)
                Log.Info("ui: notice text box " + n.Text.rectTransform.rect.size + " in a card of " + rt.rect.size);
            if (n.Text != null) SetText(n.Text, "");
            return rt;
        }

        // The prefab is saved closed (1 unit tall) and its animation opens it; the strip removes that animation. The
        // card takes the height an open card of the game's has, or else its own layout's.
        static void OpenHeight(RectTransform rt)
        {
            if (rt.rect.height >= 2f) { Log.Info("ui: notice size used " + rt.rect.size); return; }
            float h = 0f;
            var cards = Object.FindObjectsOfType<Game.UI.MainWindow.NotificationUI>(false);
            foreach (Game.UI.MainWindow.NotificationUI live in cards)
                if (live != null && !Strip.IsMod(live.transform))
                    h = Math.Max(h, ((RectTransform)live.transform).rect.height);
            string from = "an open card of the game's";
            if (h < 2f)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
                h = LayoutUtility.GetPreferredHeight(rt);
                from = "its own layout";
            }
            if (h < 2f) { Log.Warn("ui: notice: no open height found; left at " + rt.rect.height); return; }
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, h);
            Log.Info("ui: notice opened to " + h.ToString("0.00") + " units tall, from " + from + "; size used " +
                     rt.rect.size);
        }

        static void OpenPose(GameObject clone)
        {
            var anim = clone.GetComponent<Animation>();
            AnimationClip clip = anim != null ? anim.clip : null;
            if (clip == null) return;
            Vector2 before = ((RectTransform)clone.transform).sizeDelta;
            clip.SampleAnimation(clone, clip.length);
            Vector2 after = ((RectTransform)clone.transform).sizeDelta;
            if (after != before)
                Log.Info("ui: notice laid out at the end of '" + clip.name + "': size " + before + " -> " + after);
        }

        public NoticeParts PartsOfNotice(RectTransform notice)
        {
            var n = new NoticeParts { Root = notice };
            Component prefab = Original(PNotice) as Component;
            if (notice == null || prefab == null) return n;
            n.Image = SameField<Image>(notice, prefab.transform, prefab, "image");
            n.Text = SameField<TMP_Text>(notice, prefab.transform, prefab, "text");
            n.Button = SameField<Button>(notice, prefab.transform, prefab, "button");
            return n;
        }

        // The speed panel's lit image (the x1 button's; index 0-5 picks a button's own).
        public Image SpeedMark(Transform parent) { return SpeedMark(parent, 1); }

        public Image SpeedMark(Transform parent, int index)
        {
            if (index < 0 || index >= MarkFields.Length) return null;
            if (!Usable("speed mark", PMark + index)) return null;
            RectTransform rt = Build(PMark + index, parent, "speed mark " + index);
            return rt != null ? rt.GetComponent<Image>() : null;
        }

        // A speed button's dark face (its Background child), for the unlit signal bars and the strip's chips.
        public Image SpeedButtonFace(Transform parent, int index)
        {
            if (index < 0 || index >= SpeedButtonFields.Length) return null;
            if (!Usable("button face", PSpeedButton + index)) return null;
            Transform face = Go(PSpeedButton + index).transform.Find("Background");
            if (face == null)
            {
                Log.Warn("ui: button face not built: no Background under speed button " + index);
                return null;
            }
            RectTransform rt = Build(face.gameObject, parent, "button face " + index);
            return rt != null ? rt.GetComponent<Image>() : null;
        }

        // Behind a modal dialog: the menu's own shadow under its load dialog; in game, where the pause screen is the
        // backdrop, a clear full-screen catcher (it shows nothing, it only keeps clicks off the game).
        public RectTransform Blocker(Transform parent)
        {
            if (Missing != null) { Log.Warn("ui: blocker not built: part missing: " + Missing); return null; }
            RectTransform rt;
            if (Original(PShadow) != null) rt = Build(PShadow, parent, "blocker");
            else
            {
                var go = new GameObject(Strip.ObjectPrefix + "blocker #" + (++built), typeof(RectTransform));
                go.transform.SetParent(parent, false);
                var img = go.AddComponent<Image>();
                img.color = new Color(0f, 0f, 0f, 0f);
                rt = (RectTransform)go.transform;
            }
            if (rt == null) return null;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            Image catcher = rt.GetComponent<Image>();
            if (catcher != null) catcher.raycastTarget = true;
            return rt;
        }

        // ---- helpers ----

        static void SetText(Component root, string text)
        {
            foreach (TMP_Text t in root.GetComponentsInChildren<TMP_Text>(true))
            {
                t.richText = false;
                t.parseCtrlCharacters = false;
                t.text = text ?? "";
            }
        }

        T Same<T>(Transform clone, Transform orig, string part) where T : Component
        {
            GameObject g = Go(part);
            Transform t = g != null ? Strip.FindSame(clone, orig, g.transform) : null;
            return t != null ? t.GetComponent<T>() : null;
        }

        static T SameField<T>(Transform clone, Transform orig, object owner, string field) where T : Component
        {
            var c = Field<Component>(owner, field);
            Transform t = c != null ? Strip.FindSame(clone, orig, c.transform) : null;
            return t != null ? t.GetComponent<T>() : null;
        }

        static GameObject SameGo(Transform clone, Transform orig, GameObject inOrig)
        {
            Transform t = inOrig != null ? Strip.FindSame(clone, orig, inOrig.transform) : null;
            return t != null ? t.gameObject : null;
        }

        static IList AgencyItems(Object corp)
        {
            return Field<IList>(corp, "corporationItems") ?? new object[0];
        }

        // The game's object of this type in the loaded scenes (inactive ones too), never one of the mod's.
        static T FindLive<T>() where T : Component
        {
            foreach (T t in Object.FindObjectsOfType<T>(true))
                if (t != null && t.gameObject.scene.IsValid() && !Strip.IsMod(t.transform)) return t;
            return null;
        }

        static Canvas RootCanvas(Transform t)
        {
            Canvas found = null;
            for (Transform x = t; x != null; x = x.parent)
            {
                var c = x.GetComponent<Canvas>();
                if (c != null) found = c;
            }
            return found != null ? found.rootCanvas : null;
        }

        internal static T Field<T>(object o, string name) where T : class
        {
            if (o == null || (o is Object && (Object)o == null)) return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    object v = f.GetValue(o);
                    if (v is Object && (Object)v == null) return null;
                    return v as T;
                }
            }
            return null;
        }
    }
}
