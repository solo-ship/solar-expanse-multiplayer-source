using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The named states of the dialog and notice boards (mock-up boards 9, 10, 11, 13, 15 and 16), for the dev
    // triggers behind the test seams: each is a SessionView, the capabilities behind it, the notices to push and the
    // dialog to ask for, so one in-game run can screenshot every state, the ones the early session cannot reach too.
    public sealed class DialogState
    {
        public string Name;
        public SessionView View = new SessionView();
        public Capability Caps;
        public DialogKind Ask = DialogKind.None;   // a dialog a click asks for (carry on, leave, end); None: the view's
        public int Player = -1;                    // the player Ask names (carry on)
        public readonly List<NoticeSeed> Notices = new List<NoticeSeed>();
    }

    public sealed class NoticeSeed { public string Key; public object[] Args; public int Kind; }

    public static class DialogStates
    {
        // What M4's session offers (M4SessionAdapter), and every capability on (the full track).
        public const Capability Early = Capability.HostSave | Capability.HostNewGame | Capability.JoinByCode |
                                        Capability.PickAgency | Capability.Players;
        public const Capability Full = (Capability)((int)Capability.HostMpSave * 2 - 1);

        public static readonly string[] Dialogs =
        {
            "mismatch", "version", "agency", "refused", "ended", "ended-early", "kicked", "dropped", "dropped-early",
            "reloading", "carryon", "leave", "end", "end-nocopy",
        };
        public static readonly string[] NoticeStates = { "notices", "notices-early", "notices-many", "notices-back" };

        // The state by name, or null for a name that is not one.
        public static DialogState Make(string name)
        {
            var s = new DialogState { Name = name, Caps = Full };
            SessionView v = s.View;
            switch (name)
            {
                case "mismatch":
                    Ended(v, "join.refused." + JoinCodes.PluginsDiffer, "Alex", new[]
                    {
                        UiText.Get("join.report.versions", "Plugin", "City Expansions", "0.6.0", "0.4.1"),
                        UiText.Get("join.report.onlyHost", "Plugin", "Antimatter Drive",
                                   UiText.Get("join.report.versionPart", "1.0.0")),
                        UiText.Get("join.report.onlyJoiner", "Plugin", "GameStateConnector",
                                   UiText.Get("join.report.versionPart", "0.20.90")),
                        UiText.Get("join.report.missing", "Research", 3, ""),
                    });
                    break;
                case "version":
                    Ended(v, "join.refused." + JoinCodes.BuildDiffers, "Alex",
                          new[] { UiText.Get("join.version.game", "0.26.10.9.13", "0.26.4.10.18") });
                    break;
                case "agency":
                    Client(v, "Sam", SessionPhase.Starting);
                    v.Status = StatusLine.Make("join.refused." + JoinCodes.AgencyTaken);
                    break;
                case "refused":
                    Ended(v, "join.refused." + JoinCodes.JoinerTimeout, "Alex", null);
                    break;
                case "ended":
                case "ended-early":
                    Ended(v, "join.ended.hostLeft", "Alex", null);
                    if (name == "ended-early") s.Caps = Early;
                    break;
                case "kicked":
                    Ended(v, "join.ended.kicked", "Alex", null);
                    s.Caps = Early;
                    break;
                case "dropped":
                case "dropped-early":
                    Client(v, "Sam", SessionPhase.InGame);
                    v.Status = StatusLine.Make(StatusLine.Dropped);
                    if (name == "dropped-early") s.Caps = Early;
                    break;
                case "reloading":
                    Client(v, "Sam", SessionPhase.Loading);
                    v.Status = StatusLine.Make(StatusLine.OutOfStep);
                    v.Row(2).JoinPct = 70;
                    v.Row(2).JoinSecsLeft = 10;
                    break;
                case "carryon":
                    Host(v);
                    s.Ask = DialogKind.CarryOn;
                    s.Player = 3;
                    break;
                case "leave":
                    Client(v, "Sam", SessionPhase.InGame);
                    s.Ask = DialogKind.ConfirmLeave;
                    break;
                case "end":
                case "end-nocopy":
                    Host(v);
                    s.Ask = DialogKind.ConfirmEnd;
                    if (name == "end-nocopy") s.Caps = Early | Capability.EndSession;
                    break;
                case "notices":
                case "notices-early":
                    Host(v);
                    Seed(s, "join.notice.joined", NoticeQueue.Routine, "Tom");
                    Seed(s, "join.notice.joining", NoticeQueue.Routine, "Sam");
                    Seed(s, NoticeCards.DroppedKey, NoticeQueue.Warning, "Jordan", 3);
                    Seed(s, "join.notice.couldNotJoin", NoticeQueue.Warning, "Kim");
                    if (name == "notices-early") s.Caps = Early;
                    break;
                case "notices-many":
                    Host(v);
                    Seed(s, "join.notice.couldNotJoin", NoticeQueue.Warning, "Zoë 한국 <b>x</b>");
                    for (int i = 1; i <= 13; i++)
                        Seed(s, "join.notice.couldNotJoin", NoticeQueue.Warning, "Player " + (10 + i));
                    break;
                case "notices-back":
                    Host(v);
                    Seed(s, "join.notice.rejoined", NoticeQueue.Routine, "Sam");
                    break;
                default:
                    return null;
            }
            return s;
        }

        static void Ended(SessionView v, string why, string host, string[] report)
        {
            v.Phase = SessionPhase.Idle;
            v.EndedWhy = why;
            v.EndedHost = host;
            v.EndedReport = report;
        }

        static void Client(SessionView v, string me, SessionPhase phase)
        {
            v.Phase = phase;
            v.MyId = 2;
            v.Players = SessionView.GroupByAgency(new List<PlayerRow>
            {
                new PlayerRow { Id = 2, Name = me, Agency = "esa", Me = true, State = PlayerRow.InGame },
                new PlayerRow { Id = 0, Name = "Alex", Agency = "nasa", Host = true, State = PlayerRow.InGame },
            });
        }

        // The host Alex with Tom (in game), Sam (joining) and the CNSA player (dropped, the clock holding a drop pause).
        static void Host(SessionView v)
        {
            v.Phase = SessionPhase.InGame;
            v.IsHost = true;
            v.MyId = 0;
            v.Players = SessionView.GroupByAgency(new List<PlayerRow>
            {
                new PlayerRow
                {
                    Id = 0, Name = "Alex", Agency = "nasa", Host = true, Me = true, State = PlayerRow.InGame,
                },
                new PlayerRow { Id = 1, Name = "Tom", Agency = "roscosmos", State = PlayerRow.InGame },
                new PlayerRow { Id = 2, Name = "Sam", Agency = "esa", State = PlayerRow.Joining, JoinPct = 45 },
                new PlayerRow { Id = 3, Name = "Jordan", Agency = "cnsa", State = PlayerRow.Dropped },
            });
            v.Pauses = new List<PauseLine> { new PauseLine { Player = 3, Why = PauseWhy.Dropped } };
        }

        static void Seed(DialogState s, string key, int kind, params object[] args)
        {
            s.Notices.Add(new NoticeSeed { Key = key, Args = args, Kind = kind });
        }
    }
}
