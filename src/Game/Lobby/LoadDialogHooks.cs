using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Game.UI.Windows;
using HarmonyLib;
using Manager;
using UnityEngine;
using UnityEngine.UI;

namespace SolarExpanseMultiplayer
{
    // The host setup's "Load a save" (M8 DD7): the game's own load dialog, with all its own checks (the critically old
    // save, the version pop-up, the Demo flag), ending in LoadSaveManager.Load, the one place the dialog, Continue and
    // the import all reach. While the dialog is armed (SetupIntents.DialogArmed), Load's prefix refuses a Demo save
    // with a notice, and otherwise lets the load run and arms the host intent (Track U) or keeps the name and loads
    // nothing (the full track). Load's postfix clears the intent when the load returned false; the dialog's OnHide
    // (OnCloseButtonClick) clears only the dialog's armed state, since it follows every load that runs in the same
    // frame. The decisions are SetupFlow's (Core). Owned by MenuSessions (no static state); main thread only.
    public sealed class HostSetupDialog
    {
        static readonly FieldInfo DialogOfMenu = AccessTools.Field(typeof(MenuSceneUI), "loadSaveDialogWindow");
        static readonly MethodInfo LoadClick = AccessTools.Method(typeof(MenuSceneUI), "OnClickBtnLoad");
        static readonly FieldInfo OkF = AccessTools.Field(typeof(LoadSaveDialogWindow), "btnOk");
        static readonly FieldInfo CancelF = AccessTools.Field(typeof(LoadSaveDialogWindow), "btnCancel");
        static readonly FieldInfo SlotsF = AccessTools.Field(typeof(LoadSaveDialogWindow), "slots");

        // The test seams' saves for K15 (made from the profile's seed save): the Continue pick is made newest.
        static readonly string[] Fixtures =
            { "setup-critical", "setup-demo", "setup-older", "setup-gone", "setup-current" };

        readonly MenuSessions owner;
        MenuSceneUI menu;

        // The full track's kept save name; null until a kept Load.
        public string KeptSave { get; private set; }

        // The kept save for the lobby, once (null when none is waiting).
        public string TakeKeptSave()
        {
            string s = KeptSave;
            KeptSave = null;
            return s;
        }

        public HostSetupDialog(MenuSessions owner)
        {
            this.owner = owner;
            owner.MenuShown += m => menu = m;
        }

        SetupFlow Flow { get { return owner.Setup; } }

        // Opens the game's own load dialog as the host setup. keep: the full track (nothing loads, nothing hosted);
        // otherwise the host's plan is made first (Track U: the session starts once the save has loaded). Null = open;
        // else a UiText key, and nothing is armed.
        public string Open(bool keep)
        {
            if (menu == null) return "menu.notAtMenu";
            if (!keep)
            {
                string busy = M4SessionAdapter.HostBusy();
                if (busy != null) return busy;
                string bind = owner.PrepareHost();
                if (bind != null) return bind;
            }
            // Load's hook is patched now, after the menu's content scan (SetupHookNames.Deferred)
            bool load = GameHooks.EnsureDeferred(SetupHookNames.Load, false);
            string why = Flow.OpenDialog(keep,
                load && SetupHookNames.AllIn(SetupHookNames.ForDialog, GameHooks.IsInstalled));
            if (why != null)
            {
                Log.Error("setup: the load dialog cannot open as the setup (a setup is open, or a hook is missing)");
                return why;
            }
            KeptSave = null;
            try { LoadClick.Invoke(menu, null); }   // the game's own Load game: the shadow, the dialog in Loading mode
            catch (Exception e)
            {
                Log.Error("setup: opening the load dialog failed: " + e);
                Flow.OnDialogHidden();
                return SetupFlow.Unavailable;
            }
            Log.Info("setup: the load dialog opens as the host setup (" +
                     (keep ? "kept: nothing loads" : "Track U: the load runs") + ")");
            return null;
        }

        // Load's prefix, armed: true = the game's own Load runs; false = it does not, with result as its answer.
        internal bool OnLoad(string name, bool demo, ref bool result)
        {
            string shown = TextSanitiser.OneLine(name);
            switch (Flow.OnLoad(demo))
            {
                case LoadStep.RefuseDemo:
                    Log.Warn("setup: the Demo save '" + shown + "' is refused, nothing loads");
                    Notice(SetupFlow.DemoNotice);
                    result = false;
                    return false;
                case LoadStep.Keep:
                    KeptSave = name;
                    Log.Info("setup: the save '" + shown + "' is kept, nothing loads");
                    result = false;
                    return false;
                case LoadStep.Run:
                    Log.Info("setup: the save '" + shown + "' loads as the host's; host once loaded armed");
                    return true;
                default:
                    return true;
            }
        }

        // A notice on the mod's own stack, and the same line in the log.
        internal void Notice(string key)
        {
            try { owner.Actions.Notices.Push(key, null, NoticeQueue.Warning, NetClock.NowMs); }
            catch (Exception e) { Log.Error("setup: the notice could not be shown: " + e.Message); }
            Log.Warn("notice: " + UiText.Get(key));
        }

        // ---- test seams (MenuSessions.DevPoll calls TakeDevTriggers, only while TestSeams.Open) ----

        // "setup-dialog" opens the dialog as the Multiplayer screen's "Load a save" will (Track U); "setup-dialog keep"
        // for the full track's hand-over; "setup-fixtures" makes K15's saves from the seed save in this profile;
        // "setup-drop setup-gone" removes that fixture's world file after the dialog listed it (a Load that returns
        // false).
        internal void TakeDevTriggers()
        {
            string open = DevTriggers.Take("setup-dialog");
            if (open != null)
            {
                string why = Open(open == "keep");
                if (why == null) Log.Info("dev setup-dialog: ok");
                else Log.Warn("dev setup-dialog: refused (" + why + "): " + UiText.Get(why));
            }
            if (DevTriggers.Take("setup-fixtures") != null) MakeFixtures();
            string drop = DevTriggers.Take("setup-drop");
            if (drop != null) Drop(drop);
        }

        void MakeFixtures()
        {
            try
            {
                string dir = LoadSaveManager.FullSaveDirectory();
                string seed = Boot.Seed ?? "multiplayertest";
                string world = Path.Combine(dir, seed + ".json.gz"), info = Path.Combine(dir, seed + ".info.gz");
                DateTime t0 = DateTime.Now.AddMinutes(-10);
                for (int i = 0; i < Fixtures.Length; i++)
                {
                    string name = Fixtures[i];
                    SaveInfo x = BaseSaveGameData<SaveInfo>.Deserialize(File.ReadAllBytes(info));
                    x.saveVersion = Application.version;
                    x.saveVersionCode = 6;
                    x.createdInDemo = name == "setup-demo";
                    if (name == "setup-older") x.saveVersion = "0.25.0.0.0";
                    if (name == "setup-critical") { x.saveVersion = "0.20.0.0.0"; x.saveVersionCode = 5; }
                    string w = Path.Combine(dir, name + ".json.gz"), f = Path.Combine(dir, name + ".info.gz");
                    File.Copy(world, w, true);
                    File.WriteAllBytes(f, x.Serialize());
                    File.SetLastWriteTime(w, t0.AddMinutes(i));
                    File.SetLastWriteTime(f, t0.AddMinutes(i));
                    Log.Info("dev setup-fixtures: " + name + ": saveVersion " + x.saveVersion + ", code " +
                             x.saveVersionCode + ", createdInDemo " + x.createdInDemo);
                }
            }
            catch (Exception e) { Log.Error("dev setup-fixtures: failed: " + e.Message); }
        }

        static void Drop(string name)
        {
            if (Array.IndexOf(Fixtures, name) < 0) { Log.Warn("dev setup-drop: only a setup fixture"); return; }
            try
            {
                string w = Path.Combine(LoadSaveManager.FullSaveDirectory(), name + ".json.gz");
                File.Delete(w);
                Log.Info("dev setup-drop: " + name + "'s world file removed");
            }
            catch (Exception e) { Log.Error("dev setup-drop: failed: " + e.Message); }
        }

        // The dialog's controls in client pixels (the "setup-where" trigger).
        internal void Where()
        {
            var d = menu != null ? DialogOfMenu.GetValue(menu) as LoadSaveDialogWindow : null;
            if (d == null || !d.gameObject.activeInHierarchy) { Log.Info("setup: where: no load dialog"); return; }
            HostSetupPages.WhereOf("dialog.ok", OkF.GetValue(d) as Button);
            HostSetupPages.WhereOf("dialog.cancel", CancelF.GetValue(d) as Button);
            foreach (object el in (IList)SlotsF.GetValue(d))
            {
                var c = el as Component;
                string save = c != null ? AccessTools.Field(el.GetType(), "saveName").GetValue(el) as string : null;
                if (save == null) continue;
                var id = new System.Text.StringBuilder("dialog.row.");
                foreach (char ch in save.ToLowerInvariant())
                    id.Append(ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' ? ch : '_');
                HostSetupPages.WhereOf(id.ToString(), c);
            }
        }
    }

    // DD7's hand-over on Load, acting only while the setup's dialog is armed. __state: the prefix acted, so the
    // postfix answers only for that call.
    [HarmonyPatch(typeof(LoadSaveManager))]
    public static class SetupLoad
    {
        [HarmonyPatch(nameof(LoadSaveManager.Load), new[] { typeof(string), typeof(bool) })]
        [HarmonyPrefix]
        static bool Prefix(object[] __args, ref bool __result, out bool __state)
        {
            __state = false;
            MenuSessions e = Session.Early;
            if (e == null || !e.Intents.DialogArmed) return true;
            __state = true;
            try
            {
                string name = __args[0] as string;
                bool demo = (bool)__args[1];
                return e.Dialog.OnLoad(name, demo, ref __result);
            }
            catch (Exception ex)
            {
                Log.Error("setup: the load hand-over failed, nothing loads: " + ex);
                e.Dialog.Notice(SetupFlow.Unavailable);
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(nameof(LoadSaveManager.Load), new[] { typeof(string), typeof(bool) })]
        [HarmonyPostfix]
        static void Postfix(bool __result, bool __state)
        {
            if (__state) Returned(__result);
        }

        // A Load that threw never reaches its postfix: it loaded nothing, so it hosts nothing.
        [HarmonyPatch(nameof(LoadSaveManager.Load), new[] { typeof(string), typeof(bool) })]
        [HarmonyFinalizer]
        static void Finalizer(Exception __exception, bool __state)
        {
            if (__state && __exception != null) Returned(false);
        }

        static void Returned(bool ok)
        {
            MenuSessions e = Session.Early;
            if (e == null) return;
            try
            {
                if (!ok && e.Intents.HostIntent) Log.Info("menu: host intent cleared (the load returned false)");
                e.Setup.OnLoadReturned(ok);
            }
            catch (Exception ex) { Log.Error("setup: the load's result could not be read: " + ex.Message); }
        }
    }

    // The dialog's OnHide: every way it closes (OK after a load, the version pop-up's Yes, Esc, Cancel, Close, the
    // critically old save's pop-up) goes through OnCloseButtonClick. A finalizer, so a throwing OnHide listener still
    // disarms the dialog (a dialog left armed would host the next solo Load).
    [HarmonyPatch(typeof(LoadSaveDialogWindow))]
    public static class SetupDialogHide
    {
        [HarmonyPatch("OnCloseButtonClick")]
        [HarmonyFinalizer]
        static void Finalizer()
        {
            MenuSessions e = Session.Early;
            if (e == null || !e.Intents.DialogArmed) return;
            try
            {
                e.Setup.OnDialogHidden();
                Log.Info("setup: the load dialog closed; host intent " +
                         (e.Intents.HostIntent ? "armed" : "not armed"));
            }
            catch (Exception ex) { Log.Error("setup: the dialog's close could not be read: " + ex.Message); }
        }
    }
}
