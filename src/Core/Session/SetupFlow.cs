using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The setup hooks' names. HookPlan's constants for them are these strings and GameHooks maps each to its class; the
    // setup opens only when every hook it needs is installed (fail closed: a missing Prev, Esc or OnHide hook would
    // leave an armed state nothing clears).
    public static class SetupHookNames
    {
        public const string Pages = "SetupPages", Epochs = "SetupEpochs", HandOver = "SetupHandOver",
            Load = "SetupLoad", DialogHide = "SetupDialogHide";

        public static readonly string[] ForPages = { Pages, Epochs, HandOver };
        public static readonly string[] ForDialog = { Load, DialogHide };
        public static readonly string[] All = { Pages, Epochs, HandOver, Load, DialogHide };

        // Patched only when the setup opens the dialog (HookPlan.Deferred, GameHooks.EnsureDeferred): under BepInEx,
        // HarmonyX patching Load (an instance method returning bool) loads an in-memory MonoMod helper assembly, which
        // the menu's content scan would count; patched after the scan it is never counted.
        public static readonly string[] Deferred = { Load };
        public static readonly string[] AtStartUp = { Pages, Epochs, HandOver, DialogHide };

        // How many game methods each class patches: SetupPages the panel's Start, SetUserCompany and
        // OnPrevButtonClick; the others one each (SetupLoad's prefix and postfix are on the one Load).
        public static int Methods(string name)
        {
            if (name == Pages) return 3;
            return Array.IndexOf(All, name) >= 0 ? 1 : 0;
        }

        // True when every name is installed (the caller's GameHooks.IsInstalled).
        public static bool AllIn(string[] names, Func<string, bool> installed)
        {
            if (names == null || installed == null) return false;
            foreach (string n in names) if (!installed(n)) return false;
            return true;
        }
    }

    // What the LoadScene prefix does with a load (DD6 "The hand-over").
    public enum SceneStep
    {
        Vanilla,   // not the armed pages' last Next: untouched
        Run,       // Track U: the new game loads and the host intent is armed
        Keep,      // the full track: a copy is kept, nothing loads, the setup is left
        Refuse,    // the configuration broke the setup's rules: nothing loads, the setup is left
    }

    // What the LoadSaveManager.Load prefix does (DD7).
    public enum LoadStep
    {
        Vanilla,      // the dialog is not the setup's: untouched (a solo Load, Continue, the import)
        Run,          // Track U: the save loads and the host intent is armed
        Keep,         // the full track: the name is kept, nothing loads
        RefuseDemo,   // a Demo save: nothing loads, the host sees a notice
    }

    // The setup hooks' decisions (M8 DD6, DD7), each one feeding its SetupIntents trigger (DD26 "The setup's
    // intents"). Pure, so every hook's effect on the intents has a test (HostIntentTests); the hooks themselves are
    // thin game glue around these calls. One instance per MenuSessions, over its Intents. Main thread only.
    public sealed class SetupFlow
    {
        public const string LoadingScene = "LoadingScreen";
        public const int FirstShownPage = 1;   // the star system page: the agency page (0) is skipped
        public const string Unavailable = "host.couldNotStart", DemoNotice = "host.demoSave";

        readonly SetupIntents intents;

        public SetupFlow(SetupIntents intents)
        {
            if (intents == null) throw new ArgumentNullException("intents");
            this.intents = intents;
        }

        public SetupIntents Intents { get { return intents; } }
        public bool PagesKeep { get; private set; }    // the full track's last Next: kept, nothing loads
        public bool DialogKeep { get; private set; }   // the full track's Load: the name kept, nothing loads

        // The setup opens the pages. installed: every hook the pages need is in. Null = armed; else a UiText key.
        // One setup at a time: the pages and the dialog are never armed together.
        public string OpenPages(bool keep, bool installed)
        {
            if (!installed || intents.PagesArmed || intents.DialogArmed) return Unavailable;
            PagesKeep = keep;
            intents.PagesOpened();
            return null;
        }

        public string OpenDialog(bool keep, bool installed)
        {
            if (!installed || intents.PagesArmed || intents.DialogArmed) return Unavailable;
            DialogKeep = keep;
            intents.DialogOpened();
            return null;
        }

        // ---- the pages' hooks ----

        // Prev on the first shown page leaves the setup (never to the skipped agency page).
        public bool PrevLeaves(int currentPage)
        {
            return intents.PagesArmed && currentPage <= FirstShownPage;
        }

        // The setup's own Esc: only while its pages are on screen, and never while a game pop-up or the Settings
        // window is (or was in the last frame) up: then the key is theirs.
        public bool EscLeaves(bool pagesOpen, bool gameWindowUp)
        {
            return intents.PagesArmed && pagesOpen && !gameWindowUp;
        }

        // Whether Esc this frame belongs to a game window over the pages: one up now, or seen in the last frame (a
        // window whose own Update closes it on this Esc may run before the setup's).
        public static bool EscIsTheWindows(bool upNow, int frame, int lastUpFrame)
        {
            return upNow || frame - lastUpFrame <= 1;
        }

        // Prev, Esc, a refused start or a broken page: the setup is left and nothing is hosted.
        public void PagesLeft()
        {
            intents.PagesLeft();
            PagesKeep = false;
        }

        // A leave asked for from inside a game method that carries on afterwards (the epoch page's Setup inside a
        // page fade, SetUserCompany inside the setup's own page switch): the pages are disarmed at once, so no hook
        // acts again, and the panel is put back on the next frame (TakePendingLeave), never mid-call.
        public void LeaveLater(string why)
        {
            PagesLeft();
            if (PendingLeave == null) PendingLeave = why ?? "";
        }

        public string PendingLeave { get; private set; }

        // The leave due this frame, once; null when none is.
        public string TakePendingLeave()
        {
            string why = PendingLeave;
            PendingLeave = null;
            return why;
        }

        // The SceneManager.LoadScene prefix. fromPages: the configuration being loaded is the panel's own (its last
        // Next built it; a save's Load sets another). allowed: it passed the setup's rules.
        public SceneStep OnLoadScene(string scene, bool fromPages, bool allowed)
        {
            if (!intents.PagesArmed || scene != LoadingScene || !fromPages) return SceneStep.Vanilla;
            if (!allowed)
            {
                PagesLeft();
                return SceneStep.Refuse;
            }
            bool keep = PagesKeep;
            PagesKeep = false;
            intents.PagesHandedOver(!keep);
            return keep ? SceneStep.Keep : SceneStep.Run;
        }

        // ---- the dialog's hooks ----

        // The LoadSaveManager.Load prefix.
        public LoadStep OnLoad(bool demo)
        {
            if (!intents.DialogArmed) return LoadStep.Vanilla;
            if (demo) return LoadStep.RefuseDemo;
            if (DialogKeep) return LoadStep.Keep;
            intents.LoadRuns();
            return LoadStep.Run;
        }

        // The Load postfix, for a call the armed prefix saw: a load that returned false hosts nothing.
        public void OnLoadReturned(bool ok) { intents.LoadReturned(ok); }

        // The dialog's OnHide (OnCloseButtonClick): it follows every load that runs in the same frame, so it clears
        // the dialog's armed state only, never the host intent.
        public void OnDialogHidden()
        {
            if (!intents.DialogArmed) return;
            intents.DialogHidden();
            DialogKeep = false;
        }

        // The Menu scene came back (MenuSessions clears the intents themselves).
        public void OnMenuShown()
        {
            PagesKeep = false;
            DialogKeep = false;
            PendingLeave = null;   // the old menu's panel is gone with it
        }
    }

    // One epoch's row on the setup's epoch page.
    public struct EpochOffer
    {
        public bool Offered;
        public string TooltipKey;   // the game's own tooltip line for a locked row
        public string Why;          // for the log
    }

    // The setup's rules over the game's choices (DD6), pure so each is tested with a bad input.
    public static class SetupRules
    {
        public const string TooltipFallback = "Tooltip.NAIAV", TooltipNotForAll = "Tooltip.NAIAV3";

        // The game's own epoch rule (StartGameEpochSettingsUI.Setup), over every playable agency instead of the one
        // user company: any agency may be claimed in a session, so an epoch is offered only if it is open on this PC
        // and the override is on or its possible companies hold every playable agency. No playable agency at all
        // offers nothing (the list was not read).
        public static EpochOffer Epoch(bool isLocked, bool unlockedHere, bool overridePossible,
                                       ICollection<string> possible, IList<string> playable, string lockedKey,
                                       bool lockedKeyTranslated)
        {
            string key = lockedKeyTranslated && !string.IsNullOrEmpty(lockedKey) ? lockedKey : TooltipFallback;
            if (isLocked && !unlockedHere)
                return new EpochOffer { Offered = false, TooltipKey = key, Why = "locked on this PC" };
            if (playable == null || playable.Count == 0)
                return new EpochOffer { Offered = false, TooltipKey = TooltipNotForAll, Why = "no playable agency" };
            if (!overridePossible)
            {
                var missing = new List<string>();
                foreach (string p in playable) if (possible == null || !possible.Contains(p)) missing.Add(p);
                if (missing.Count > 0)
                    return new EpochOffer
                    {
                        Offered = false, TooltipKey = TooltipNotForAll,
                        Why = "not for " + string.Join("+", missing.ToArray()),
                    };
            }
            return new EpochOffer { Offered = true, TooltipKey = key, Why = "open" };
        }

        // A star system only a local file makes (a custom map, interstellar start data from an ended game) is never
        // played in a session: a joiner may not have that file. Why, or null.
        public static string StarSystem(bool pageRead, bool customMap, string interstellarFile)
        {
            if (!pageRead) return "the star system page could not be read";
            if (customMap) return "a custom map is not played in a session";
            if (!string.IsNullOrEmpty(interstellarFile)) return "interstellar start data is not played in a session";
            return null;
        }

        // The row the page selects: the one already selected if it is offered, else the first offered; -1 when none
        // is (the page's Next must then be off).
        public static int PickEpoch(int selected, IList<bool> offered)
        {
            if (offered == null) return -1;
            if (selected >= 0 && selected < offered.Count && offered[selected]) return selected;
            for (int i = 0; i < offered.Count; i++) if (offered[i]) return i;
            return -1;
        }

        // Track U's rule (DD22): every agency but the host's is an AI rival. toggles: every agency the rival toggles
        // list; built: the configuration's rivals. True only when built is exactly toggles without the company.
        public static bool TrackURivals(IList<string> toggles, string company, IList<string> built)
        {
            if (toggles == null || built == null || string.IsNullOrEmpty(company) || !toggles.Contains(company))
                return false;
            var want = new HashSet<string>(StringComparer.Ordinal);
            foreach (string t in toggles) if (t != company) want.Add(t);
            var got = new HashSet<string>(StringComparer.Ordinal);
            foreach (string b in built) if (b == null || !got.Add(b)) return false;
            return want.SetEquals(got);
        }

        // Whether an agency's rival toggle is shown on the difficulty page. The game hides the toggle of the agency the
        // player plays: it is never a rival (SetUserCompany, D 171765-171771; the built list leaves it out too). In
        // Track U the host's agency is known before the pages, so its toggle stays hidden as in the game. In the full
        // track the player is chosen later in the lobby, so every toggle shows there.
        public static bool RivalToggleShown(bool fullTrack, string agency, string host)
        {
            return fullTrack || string.IsNullOrEmpty(host) || agency != host;
        }

        // The full track's choices, read from the toggles (never the built list, which leaves out the reference agency
        // even when its box is ticked): ticked = AI rival, unticked = not in the game, for every agency.
        public static void FromToggles(IList<KeyValuePair<string, bool>> toggles, List<string> ai, List<string> off)
        {
            if (toggles == null) return;
            foreach (KeyValuePair<string, bool> t in toggles)
            {
                if (t.Key == null) continue;
                if (t.Value) { if (ai != null) ai.Add(t.Key); }
                else if (off != null) off.Add(t.Key);
            }
        }
    }
}
