using System;

namespace SolarExpanseMultiplayer
{
    // Test seams only (the dev triggers "ui-dialog" and "ui-notices"): a stand-in session behind the dialogs and the
    // notices, holding one of DialogStates' named states, so a state the early session cannot reach (a drop, a reload,
    // carry on, the full track's buttons) can be seen and screenshotted. Its actions only log ("dev screen: ..."),
    // apart from carry on, which ends the drop as the real one would. The routine notices are pushed again every few
    // seconds, so they stay on screen for the screenshot instead of fading.
    public sealed class DialogsTestSession : ISessionActions
    {
        const long RepushMs = 4000;

        readonly DialogState state;
        readonly NoticeQueue notices = new NoticeQueue();
        long lastPush = long.MinValue;

        public DialogsTestSession(DialogState state)
        {
            if (!TestSeams.Open(Boot.Profile, MenuSessions.PlayerPath))
                throw new InvalidOperationException("the dialogs' stand-in session is for the test seams only");
            this.state = state;
            Push(true);
        }

        public Capability Capabilities { get { return state.Caps; } }
        public SessionView View { get { return state.View; } }
        public NoticeQueue Notices { get { return notices; } }
        public event Action Changed { add { } remove { } }   // the screens poll the view

        public void Tick()
        {
            if (NetClock.NowMs - lastPush >= RepushMs) Push(false);
        }

        // Every seed the first time; afterwards only the routine ones (a warning stays until dismissed, and one the
        // tester dismissed stays dismissed).
        void Push(bool all)
        {
            lastPush = NetClock.NowMs;
            foreach (NoticeSeed n in state.Notices)
                if (all || n.Kind != NoticeQueue.Warning) notices.Push(n.Key, n.Args, n.Kind, lastPush);
        }

        static string Did(string what)
        {
            Log.Info("dev screen: " + what);
            return null;
        }

        public string HostFromSave(string ownSaveName) { return Did("host a save"); }
        public string HostNewGame(NewGameSetup setup) { return Did("host a new game"); }
        public string HostMpSave(string mpSaveName) { return Did("host a multiplayer save"); }
        public string Join(string joinCodeText, string playerName) { return Did("join"); }
        public string PickAgency(string agencyId) { return Did("pick an agency"); }
        public void SetReady(bool ready) { Did("ready " + ready); }
        public void Start(bool anyway) { Did("start" + (anyway ? " anyway" : "")); }
        public void Kick(int player) { Did("kick " + player); }
        public void Ban(int player) { Did("ban " + player); }
        public void SetAgencyRule(string agencyId, AgencyStanding rule) { Did("agency rule " + rule); }
        public void Leave() { Did("leave"); }
        public void EndSession() { Did("end session"); }
        public string PlaySolo(string mpSaveName, string agencyId) { return Did("play solo"); }
        public string NewCode() { return Did("new code"); }

        public void CarryOnWithout(int player)
        {
            Did("carry on without " + player);
            PlayerRow r = state.View.Row(player);
            if (r != null) r.State = PlayerRow.InGame;
            state.View.Pauses.RemoveAll(p => p != null && p.Player == player);
        }
    }
}
