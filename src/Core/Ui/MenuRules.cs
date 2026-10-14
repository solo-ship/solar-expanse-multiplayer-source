using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The main menu's multiplayer screens (Task 18): the Multiplayer screen, host setup, join by code and the
    // Multiplayer saves list. Their decisions live here, so they are tested without the game.
    public enum MenuScreen { None = 0, Multiplayer = 1, HostSetup = 2, Join = 3, MpSaves = 4 }

    // What host setup's Next does with the current pick.
    public enum HostPick { None = 0, NewGame = 1, MpSave = 2, OwnSave = 3 }

    // Which controls the screens show, from the session's capabilities alone (a control whose capability is off is
    // still built, then hidden or disabled, so turning the capability on later changes no screen).
    public sealed class MenuControls
    {
        public bool Host, Join;            // the Multiplayer screen's two buttons
        public bool NewGameItem;           // host setup: "New game" in the first tab
        public bool MpSaveItems;           // host setup: the Multiplayer saves in the first tab, to host again
        public bool MpSaveList;            // the Multiplayer saves list itself (the host's session saves live there)
        public bool OwnSavesTab;           // host setup: "My saves" (the game's own load dialog)
        public bool AgencyTiles;           // host setup, early track: the host picks its agency before the pages
        public bool Options;               // host setup: the lobby's options (with the lobby at the menu)
        public bool LoadDialogButton;      // the game's Load Game dialog gains "Multiplayer saves"
        public bool SoloPicker;            // the Multiplayer saves list: pick an agency and play alone
        public bool DeleteSaves;           // the Multiplayer saves list: the rows' delete

        public static MenuControls For(Capability c)
        {
            Func<Capability, bool> has = need => CapabilityRules.Has(c, need);
            var m = new MenuControls
            {
                NewGameItem = has(Capability.HostNewGame),
                MpSaveItems = has(Capability.MpSaves) && has(Capability.HostMpSave),
                MpSaveList = has(Capability.MpSaves),
                OwnSavesTab = has(Capability.HostSave),
                Join = has(Capability.JoinByCode),
                Options = has(Capability.LobbyAtMenu),
                SoloPicker = has(Capability.SoloPlay),
                DeleteSaves = has(Capability.MpSaves),
            };
            m.Host = m.NewGameItem || m.MpSaveItems || m.OwnSavesTab;
            // in the full track every player, the host included, picks an agency in the lobby (DD6)
            m.AgencyTiles = m.NewGameItem && !m.Options;
            m.LoadDialogButton = m.MpSaveList && m.SoloPicker;
            return m;
        }

        // The first tab's label: it holds the Multiplayer saves when there are any to hold, else only "New game".
        public string FirstTabKey { get { return MpSaveItems ? "host.setup.tab.mp" : "host.setup.tab.new"; } }

        public bool FirstTab { get { return NewGameItem || MpSaveItems; } }

        // Host setup's options pane: there as soon as the screen opens, whatever is picked (the options do not
        // depend on the pick).
        public bool OptionsShown(HostPick pick) { return Options; }
    }

    // Host setup's pick: a tab and an item in it. Item 0 of the first tab is "New game" when it is offered; the
    // Multiplayer saves follow. The second tab has one item, the game's own load dialog.
    public sealed class HostSetupState
    {
        public bool OwnTab;
        public int Item;
        public string Agency;              // early track: the host's own agency (a wire id), or null

        // The tab the screen opens on: the first when it has anything, else "My saves".
        public static HostSetupState Opening(MenuControls m)
        {
            return new HostSetupState { OwnTab = !m.FirstTab && m.OwnSavesTab };
        }

        // How many items the shown tab lists.
        public int Count(MenuControls m, int mpSaves)
        {
            if (OwnTab) return m.OwnSavesTab ? 1 : 0;
            return (m.NewGameItem ? 1 : 0) + (m.MpSaveItems ? Math.Max(0, mpSaves) : 0);
        }

        public HostPick Pick(MenuControls m, int mpSaves)
        {
            if (Item < 0 || Item >= Count(m, mpSaves)) return HostPick.None;
            if (OwnTab) return HostPick.OwnSave;
            if (m.NewGameItem && Item == 0) return HostPick.NewGame;
            return HostPick.MpSave;
        }

        // The Multiplayer save picked (an index into the list), or -1.
        public int MpSaveIndex(MenuControls m, int mpSaves)
        {
            if (Pick(m, mpSaves) != HostPick.MpSave) return -1;
            return Item - (m.NewGameItem ? 1 : 0);
        }

        // Why Next is off (a UiText key), or null when it is on.
        public string NextBlockedWhy(MenuControls m, int mpSaves, ICollection<string> claimable)
        {
            HostPick p = Pick(m, mpSaves);
            if (p == HostPick.None) return "host.setup.pickOne";
            if (p == HostPick.NewGame && m.AgencyTiles &&
                (Agency == null || claimable == null || !claimable.Contains(Agency)))
                return "host.setup.pickAgency";
            return null;
        }

        public void SelectTab(bool own)
        {
            if (own == OwnTab) return;
            OwnTab = own;
            Item = 0;
        }
    }

    // Host setup's player limit (the lobby's option, DD20): one to five players without co-op, the host included.
    public static class HostOptions
    {
        public const int DefaultPlayers = SessionLimits.PlayerLimitNoCoop;

        public static int Step(int players, int delta)
        {
            int v = players + delta;
            if (v < 1) return 1;
            return v > SessionLimits.PlayerLimitNoCoop ? SessionLimits.PlayerLimitNoCoop : v;
        }
    }

    // Where each screen goes back to. The Multiplayer saves list opened from the game's Load Game dialog goes back
    // there (the dialog is opened again); from anywhere else it goes back to the Multiplayer screen.
    public static class MenuFlow
    {
        public static MenuScreen Back(MenuScreen from, bool savesFromLoadDialog)
        {
            switch (from)
            {
                case MenuScreen.HostSetup:
                case MenuScreen.Join:
                    return MenuScreen.Multiplayer;
                case MenuScreen.MpSaves:
                    return savesFromLoadDialog ? MenuScreen.None : MenuScreen.Multiplayer;
                default:
                    return MenuScreen.None;
            }
        }

        // The screen shows only when the capability behind it is on: an off one is never opened, even by a stray call.
        public static bool MayOpen(MenuScreen s, MenuControls m)
        {
            switch (s)
            {
                case MenuScreen.Multiplayer: return m.Host || m.Join;
                case MenuScreen.HostSetup: return m.Host;
                case MenuScreen.Join: return m.Join;
                case MenuScreen.MpSaves: return m.SoloPicker && m.MpSaveList;
                default: return false;
            }
        }
    }
}
