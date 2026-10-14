using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The menu screens' named states for screenshots (test seams only): the dev trigger "screen-menu" holds
    // "<screen> <state>", and the screens open in that state over a test adapter with the capabilities named here.
    // Every state of the mock-up boards 1, 2, 3, 4 and 12 has a name, the ones the early session cannot reach too.
    public sealed class MenuShot
    {
        public const Capability Early = Capability.HostSave | Capability.HostNewGame | Capability.JoinByCode |
                                        Capability.PickAgency | Capability.Players;
        public const Capability Full = Early | Capability.LobbyAtMenu | Capability.Ready | Capability.Start |
                                       Capability.StartAnyway | Capability.Kick | Capability.Ban |
                                       Capability.AgencyStates | Capability.CarryOn | Capability.LeaveNoPause |
                                       Capability.EndSession | Capability.MpSaves | Capability.SoloPlay |
                                       Capability.SaveCopies | Capability.NamesEverywhere | Capability.PingAll |
                                       Capability.HostMpSave;

        public string Screen, State;
        public MenuScreen Opens;
        public Capability Caps;
        public string Refusal;      // the line every action answers with in this state (null: they succeed)
        public bool OwnTab;         // host setup: the "My saves" tab
        public int Pick = -1;       // host setup: the item picked; the saves list: the row picked
        public string Agency;       // the agency picked (host setup's tiles, the solo picker)
        public bool Filled;         // the join screen: a name and a (fake) code entered
        public bool Confirm;        // the saves list: the delete confirmation open
        public bool NoSaves;        // the saves list: the folder is empty

        static readonly List<MenuShot> All = new List<MenuShot>
        {
            S("entry", "normal", MenuScreen.None, Early),
            S("entry", "update", MenuScreen.None, Early),
            S("multiplayer", "early", MenuScreen.Multiplayer, Early),
            S("multiplayer", "full", MenuScreen.Multiplayer, Full),
            S("multiplayer", "joinonly", MenuScreen.Multiplayer, Capability.JoinByCode),
            Picked(S("host", "early-new", MenuScreen.HostSetup, Early), 0, "esa"),
            S("host", "early-noagency", MenuScreen.HostSetup, Early),
            Own(S("host", "early-own", MenuScreen.HostSetup, Early)),
            Picked(S("host", "full-new", MenuScreen.HostSetup, Full), 0, null),
            Picked(S("host", "full-save", MenuScreen.HostSetup, Full), 1, null),
            Own(S("host", "full-own", MenuScreen.HostSetup, Full)),
            Refused(Picked(S("host", "refused", MenuScreen.HostSetup, Early), 0, "esa"), "host.noLocalNetwork"),
            S("join", "empty", MenuScreen.Join, Early),
            Fill(S("join", "filled", MenuScreen.Join, Early)),
            Refused(Fill(S("join", "refused", MenuScreen.Join, Early)), "join.badCode"),
            Picked(S("saves", "list", MenuScreen.MpSaves, Full), 0, "nasa"),
            Picked(S("saves", "noagency", MenuScreen.MpSaves, Full), 0, null),
            Empty(S("saves", "empty", MenuScreen.MpSaves, Full)),
            Ask(Picked(S("saves", "delete", MenuScreen.MpSaves, Full), 0, null)),
            S("loaddialog", "button", MenuScreen.None, Full),
            S("loaddialog", "early", MenuScreen.None, Early),
        };

        static MenuShot S(string screen, string state, MenuScreen opens, Capability caps)
        {
            return new MenuShot { Screen = screen, State = state, Opens = opens, Caps = caps };
        }

        static MenuShot Refused(MenuShot s, string key) { s.Refusal = key; return s; }
        static MenuShot Picked(MenuShot s, int pick, string agency) { s.Pick = pick; s.Agency = agency; return s; }
        static MenuShot Own(MenuShot s) { s.OwnTab = true; s.Pick = 0; return s; }
        static MenuShot Fill(MenuShot s) { s.Filled = true; return s; }
        static MenuShot Ask(MenuShot s) { s.Confirm = true; return s; }
        static MenuShot Empty(MenuShot s) { s.NoSaves = true; return s; }

        public string Id { get { return Screen + " " + State; } }

        // "<screen> <state>" -> the state, or null for anything else (the trigger's text is checked, never trusted).
        public static MenuShot Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 64) return null;
            string[] parts = text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return null;
            foreach (MenuShot s in All)
                if (s.Screen == parts[0] && s.State == parts[1]) return (MenuShot)s.MemberwiseClone();
            return null;
        }

        public static IEnumerable<string> Ids()
        {
            foreach (MenuShot s in All) yield return s.Id;
        }
    }
}
