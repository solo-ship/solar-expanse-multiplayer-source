using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // What the player can do. The screens call only this; M4SessionAdapter (Track U) and SessionRoot (full track)
    // implement it. A screen shows a control only when its capability is in Capabilities (hidden or disabled as the
    // mock-up's state says), so turning a capability on later changes no screen.
    [Flags]
    public enum Capability
    {
        None = 0, HostSave = 1, HostNewGame = 2, JoinByCode = 4, PickAgency = 8, LobbyAtMenu = 16, Ready = 32,
        Start = 64, StartAnyway = 128, Kick = 256, Ban = 512, AgencyStates = 1024, CarryOn = 2048, LeaveNoPause = 4096,
        EndSession = 8192, MpSaves = 16384, SoloPlay = 32768, SaveCopies = 65536, NamesEverywhere = 131072,
        PingAll = 262144, Players = 524288,      // PingAll: every row's ping (protocol 7); Players: the PLAYERS screen
        HostMpSave = 1048576                     // host setup lists the Multiplayer saves to host again
    }

    public interface ISessionActions
    {
        Capability Capabilities { get; }
        SessionView View { get; }                                    // rebuilt on each Roster and Clock
        event Action Changed;                                        // raised on the main thread after View is rebuilt
        NoticeQueue Notices { get; }                                 // the mod's own notice stack
        string HostFromSave(string ownSaveName);                     // null = started; else a UiText key
        string HostNewGame(NewGameSetup setup);
        string HostMpSave(string mpSaveName);
        string Join(string joinCodeText, string playerName);
        string PickAgency(string agencyId);                          // the claim (M4: AgencyClaim; full: LobbyClaim)
        void SetReady(bool ready); void Start(bool anyway); void Kick(int player); void Ban(int player);
        void SetAgencyRule(string agencyId, AgencyStanding rule); void CarryOnWithout(int player);
        void Leave(); void EndSession();
        string PlaySolo(string mpSaveName, string agencyId);         // DD24
        string NewCode();                                            // the host's "New code" (DD4): null = ok (see
                                                                     // SessionView.JoinCode), else a UiText key
    }

    public sealed class NewGameSetup
    {
        public string StarSystem, Epoch, Difficulty; public bool AiRivals;
        public Dictionary<string, AgencyStanding> Rules;
        public int MaxPlayers;              // the lobby's player limit; 0 = the default (five)
        public bool NoLateJoin;             // true = no new player once the game runs (a returning player still may)
    }

    // The one answer to "does the screen show this control?": every bit asked for is on. None is never on, so a
    // control wired to no capability stays hidden rather than appearing by accident.
    public static class CapabilityRules
    {
        public static bool Has(Capability have, Capability need)
        {
            return need != Capability.None && (have & need) == need;
        }
    }
}
