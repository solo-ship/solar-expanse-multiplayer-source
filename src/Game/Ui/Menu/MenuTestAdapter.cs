using System;
using System.Collections.Generic;
using System.Net;

namespace SolarExpanseMultiplayer
{
    // Test seams only: a fake session for the menu screens' screenshots (MenuShot), so every state of the boards can
    // be shown, the ones the early session cannot reach included. It offers the shot's capabilities, three made-up
    // Multiplayer saves, and answers every action with the shot's refusal (or "done") without doing anything. Made
    // only by MenuScreens' dev trigger, which acts only while TestSeams.Open.
    public sealed class MenuTestAdapter : ISessionActions, IMpSaves
    {
        public readonly MenuShot Shot;
        readonly SessionView view = new SessionView();
        readonly NoticeQueue notices = new NoticeQueue();
        readonly List<MpSaveRow> saves = new List<MpSaveRow>();

        public event Action Changed { add { } remove { } }

        public MenuTestAdapter(MenuShot shot)
        {
            Shot = shot;
            if (!shot.NoSaves) MakeSaves();
        }

        public Capability Capabilities { get { return Shot.Caps; } }
        public SessionView View { get { return view; } }
        public NoticeQueue Notices { get { return notices; } }
        public IList<MpSaveRow> MpSaves { get { return saves; } }

        // Host setup opens on the shot's tab and pick.
        public void Prepare(HostSetupState state)
        {
            state.SelectTab(Shot.OwnTab);
            if (Shot.Pick >= 0) state.Item = Shot.Pick;
            state.Agency = Shot.Agency;
        }

        string Answer(string what)
        {
            Log.Info("test adapter: " + what + (Shot.Refusal != null ? " refused (" + Shot.Refusal + ")" : " done"));
            return Shot.Refusal;
        }

        public string HostFromSave(string ownSaveName) { return Answer("host a save"); }
        public string HostNewGame(NewGameSetup setup) { return Answer("host a new game"); }
        public string HostMpSave(string mpSaveName) { return Answer("host a multiplayer save"); }
        public string Join(string joinCodeText, string playerName) { return Answer("join"); }   // never the code
        public string PickAgency(string agencyId) { return Shot.Refusal; }
        public string PlaySolo(string mpSaveName, string agencyId) { return Answer("play alone"); }
        public string NewCode() { return Answer("new code"); }
        public string DeleteMpSave(string name) { return Answer("delete a save"); }
        public void SetReady(bool ready) { }
        public void Start(bool anyway) { }
        public void Kick(int player) { }
        public void Ban(int player) { }
        public void SetAgencyRule(string agencyId, AgencyStanding rule) { }
        public void CarryOnWithout(int player) { }
        public void Leave() { }
        public void EndSession() { }

        void MakeSaves()
        {
            long now = DateTime.UtcNow.Ticks;
            saves.Add(Save("friday-crew", "Friday crew", "13 Mar 2022", "Alex", now - TimeSpan.FromDays(2).Ticks,
                A("nasa", AgencyStanding.Human), A("esa", AgencyStanding.Human), A("cnsa", AgencyStanding.Human),
                A("solex", AgencyStanding.AiRival), A("roscosmos", AgencyStanding.NotInGame)));
            saves.Add(Save("mars-race", "Mars race", "2 Sep 2031", "Sam", now - TimeSpan.FromDays(21).Ticks,
                A("esa", AgencyStanding.Human), A("roscosmos", AgencyStanding.Human), A("nasa", AgencyStanding.Idle)));
            saves.Add(Save("alex-and-sam", "Alex and Sam", "1 Jan 2024", "Alex", now - TimeSpan.FromDays(60).Ticks,
                A("nasa", AgencyStanding.Human), A("esa", AgencyStanding.Human), A("cnsa", AgencyStanding.AiRival),
                A("roscosmos", AgencyStanding.AiRival)));
        }

        static MpSaveRow Save(string name, string title, string date, string host, long lastPlayed,
                              params AgencyRow[] agencies)
        {
            return new MpSaveRow
            {
                Name = name, Title = title, GameDate = date, HostName = host, Difficulty = "Pioneer",
                LastPlayedUtcTicks = lastPlayed, Agencies = new List<AgencyRow>(agencies),
            };
        }

        static AgencyRow A(string id, AgencyStanding state)
        {
            return new AgencyRow
            {
                Id = id, State = state, Playable = true, Claimable = state != AgencyStanding.NotInGame,
            };
        }

        // A well-formed join code for the "filled" state: loopback, a made-up certificate and invite. It connects to
        // nothing (the adapter never connects) and the field shows only its dots.
        public static string FakeCode()
        {
            return new JoinCode
            {
                Address = IPAddress.Loopback, Port = 47000, Fingerprint = new byte[32], Invite = new byte[16],
            }.Encode();
        }
    }
}
