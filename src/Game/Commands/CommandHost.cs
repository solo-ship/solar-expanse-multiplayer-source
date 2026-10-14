using System;
using System.Collections.Generic;
using HarmonyLib;
using Company = Game.Company;
using GameManager = Manager.GameManager;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfoManager = Manager.ObjectInfoManager;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // The agencies and sites a command names, found the one safe way: an agency by its wire id (the game's id in lower
    // case, AgencyWire) among the game's companies; a site by matching ObjectsInfoData[i].company, never through
    // GetObjectInfoData(company), whose fallback hands back another agency's data (docs/research/commands.md,
    // section 4).
    // Stateless: every call reads the game afresh.
    public static class GameAgencies
    {
        // null when no company or more than one has this wire id.
        public static Company Find(string wire)
        {
            if (string.IsNullOrEmpty(wire)) return null;
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            if (gm == null || gm.Companies == null) return null;
            Company found = null;
            foreach (Company c in gm.Companies)
            {
                if (c == null || c.ID == null || AgencyWire.From(c.ID) != wire) continue;
                if (found != null && !ReferenceEquals(found, c)) return null;
                found = c;
            }
            return found;
        }

        public static string Wire(Company c) { return c != null ? AgencyWire.From(c.ID) : null; }

        public static ObjectInfo Body(int id)
        {
            ObjectInfoManager m = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            return m == null || id < 1 ? null : m.GetByID(id);
        }

        // The agency's own data at the body, or null.
        public static ObjectInfoData Site(ObjectInfo body, Company c)
        {
            if (body == null || c == null || body.ObjectsInfoData == null) return null;
            foreach (ObjectInfoData d in body.ObjectsInfoData)
                if (d != null && ReferenceEquals(d.company, c)) return d;
            return null;
        }

        public static GameManager Manager()
        {
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            if (gm == null) throw new InvalidOperationException("no GameManager");
            return gm;
        }
    }

    // The host's game as its command driver sees it (IActionWorld; plan "Where the host applies"): the clock from the
    // session's HostTime, the windows' checks (ActionChecks), the applies (ActionApply), the prints and copies
    // (ObjectRead) and DD8's money. Owned by CommandSession; main thread. Every call may throw: the driver turns a
    // throw into a refusal of that one request (OwnerOf, Check, GameOver) or into a new stream (the rest).
    public sealed class CommandHost : IActionWorld
    {
        readonly CommandSession session;
        readonly HostTime time;
        readonly Func<bool> gameOver;

        public CommandHost(CommandSession session, HostTime time, Func<bool> gameOver)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (time == null) throw new ArgumentNullException("time");
            if (gameOver == null) throw new ArgumentNullException("gameOver");
            this.session = session;
            this.time = time;
            this.gameOver = gameOver;
        }

        // No origin until the session's first load: the day key then fails, which is a new stream, never a guess.
        public long OriginTicks { get { return time.OriginTicks; } }
        public long NextDayTicks { get { return time.NextDay; } }
        public long GameTicks { get { return time.NowTicks; } }
        public bool GameOver { get { return session.PlantedGameOver || gameOver(); } }

        public string OwnerOf(ActionRequest a) { return ActionChecks.OwnerOf(a); }

        public int Check(ActionRequest a) { return ActionChecks.Check(a); }

        // DD8: the agency's own money when it is a real amount, else its opening money.
        public double MoneyFor(string agency)
        {
            Company c = GameAgencies.Find(agency);
            if (c == null || c.MoneyController == null) throw new InvalidOperationException("no agency " + agency);
            double current = c.MoneyController.CurrentMoney;
            // an agency already human has the host's money since its claim: anything else means the rule that keeps
            // it (S7, AfterLoadState) failed, and the games have parted. With the rules on that is a new stream; the
            // negative control (rules off) only says so.
            if (session.Humans.Contains(agency) && !HumanMoney.IsReal(current))
            {
                string why = "money: " + agency + " is " + StreamLines.Money(current) + " though it is human";
                if (HumanAgencies.Active) throw new InvalidOperationException(why);
                Log.Error(why + " (the rules are off); its opening money is sent");
            }
            bool opened;
            double money = HumanMoney.For(current, () => OpeningMoney(c), out opened);
            if (opened) Log.Info("money: " + agency + " " + StreamLines.Money(money) + " (opening)");
            return money;
        }

        // The start data's money for the agency times the difficulty's multiplier: the formula a new game applies
        // (D 81086-81097), read from the loaded game's own start configuration (K9).
        internal static double OpeningMoney(Company c)
        {
            GameManager gm = GameAgencies.Manager();
            var data = gm.StartGameConfig.StartGameData;
            if (data == null || data.SaveGameData == null || data.SaveGameData.companyDataSave == null)
                throw new InvalidOperationException("no start data");
            foreach (var save in data.SaveGameData.companyDataSave)
                if (save != null && save.companyID != null && save.companyID.id == c.ID)
                    return HumanMoney.Opening(save.money, gm.DifficultyBaseMultipliers.StartMoneyMultiplier);
            throw new InvalidOperationException("the start data has no money for " + c.ID);
        }

        public ApplyOutcome Apply(ActionRequest a, ApplyNumbers given)
        {
            // a build this entry starts counts from now, the entry's tick on every game (BuildStarts)
            BuildStartGlue.NoteNow(a.Agency);
            return ActionApply.Apply(a, given, session);
        }

        public string PrintOf(int kind, string agency, int body)
        {
            Research p;
            if (kind == ObjectKind.Missions) return session.FlightRead.Print(agency);
            if (kind == ObjectKind.Research && AtPoint(agency, out p))
                return ObjectPrint.Of(kind, agency, body, ObjectRows.Research(p.State.Running, p.State.Queue,
                    p.State.Paused, p.Done));
            return ObjectRead.Print(kind, agency, body);
        }

        public ObjectData Read(int kind, string agency, int body)
        {
            Research p;
            if (kind == ObjectKind.Research && AtPoint(agency, out p))
                return new ObjectData { Code = ObjectCodes.Ok, Kind = kind, Agency = agency, Body = body, Piece = 0,
                    Pieces = 1, Research = p.State };
            return ObjectRead.Read(kind, agency, body);
        }

        // ---- the research the day's prints show ----

        // A research topic finishes on whichever frame crosses its end. The client compares its prints standing at
        // its day point, and the host closes the day a whole step later, so a topic finishing in between would make
        // the two prints differ and the restore refuse a finished topic (a needless full rejoin). So the host takes
        // each human agency's research as it stands at the same day point (StepGate, StepCut.AtDayPoint) and the day's
        // research prints and copies are that. An entry that changes an agency's research after the point drops its
        // copy (the print is then taken at the close, as every other print is), so a print never misses an entry.
        sealed class Research { public ResearchState State; public List<string> Done; }

        readonly PointCopies<Research> atPoint = new PointCopies<Research>();

        public void TakeResearchAtPoint(long nextDay)
        {
            atPoint.Begin(nextDay);
            foreach (string agency in session.Humans.All())
            {
                Company c = GameAgencies.Find(agency);
                if (c == null || c.ResearchManager == null) continue;
                atPoint.Put(agency, new Research { State = ObjectRead.ResearchOf(c), Done = ObjectRead.CompletedOf(c) });
            }
        }

        public void ResearchChanged(string agency) { atPoint.Drop(agency); }

        public void ForgetPoint() { atPoint.Forget(); }

        bool AtPoint(string agency, out Research p)
        {
            return atPoint.TryGet(agency, time.NextDay, out p);
        }

        // The host's copy is the truth: it is never restored from anyone.
        public string Restore(ObjectData d) { return "the host restores nothing"; }

        // AgencyState's counters (DD7): each human agency's item counter and the game-wide craft counters. The name
        // counters (CountSN, which the save drops) join them with the craft commands, keyed by content id (N7).
        public CounterRow[] StateCounters(string[] agencies)
        {
            var rows = new List<CounterRow>();
            foreach (string agency in agencies ?? new string[0])
            {
                Company c = GameAgencies.Find(agency);
                if (c == null) throw new InvalidOperationException("no agency " + agency);
                rows.Add(new CounterRow { Which = CounterKind.ProductionCountId, Agency = agency,
                    Before = c.productionCountID, After = c.productionCountID });
                rows.AddRange(NameCounters.StateRows(c, agency));
            }
            // a rival's item counter too: a joiner's load moves it (a probe facility a ship carries is made again)
            var all = new List<string>();
            foreach (Company c in GameAgencies.Manager().Companies) if (c != null) all.Add(GameAgencies.Wire(c));
            int left;
            foreach (string rival in CounterRules.Rivals(all, agencies, out left))
            {
                Company c = GameAgencies.Find(rival);
                if (c == null) continue;   // an id two companies share is no agency the stream can name
                rows.Add(new CounterRow { Which = CounterKind.ProductionCountId, Agency = rival,
                    Before = c.productionCountID, After = c.productionCountID });
            }
            if (left > 0)
                Log.Warn("command: " + left + " more rivals than AgencyState carries; their next build sets them");
            rows.Add(new CounterRow { Which = CounterKind.MissionId, Before = FlightApply.LastMissionId,
                After = FlightApply.LastMissionId });
            rows.Add(new CounterRow { Which = CounterKind.SpacecraftCount, Before = Spacecraft.count,
                After = Spacecraft.count });
            rows.Add(new CounterRow { Which = CounterKind.LaunchVehicleCount, Before = LaunchVehicle.count,
                After = LaunchVehicle.count });
            return rows.ToArray();
        }

        static readonly AccessTools.FieldRef<ObjectInfoData, DateTime?> LifeSupportNotice =
            AccessTools.FieldRefAccess<ObjectInfoData, DateTime?>("lastNotificationLifeSupportFinish");

        // AgencyState's crew-death timers: each human agency's sites whose timer runs (the game does not save it, so a
        // joiner's fresh copy would start counting again), by body id, so every state lists them in one order.
        public LifeSupportRow[] StateLifeSupport(string[] agencies)
        {
            var rows = new List<LifeSupportRow>();
            ObjectInfoManager m = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (m == null) throw new InvalidOperationException("no ObjectInfoManager");
            foreach (string agency in agencies ?? new string[0])
            {
                Company c = GameAgencies.Find(agency);
                if (c == null) throw new InvalidOperationException("no agency " + agency);
                var mine = new List<LifeSupportRow>();
                foreach (ObjectInfo o in m.allObjectInfos)
                {
                    ObjectInfoData d = GameAgencies.Site(o, c);
                    DateTime? since = d != null ? LifeSupportNotice(d) : null;
                    if (since.HasValue)
                        mine.Add(new LifeSupportRow { Agency = agency, Body = o.id, Ticks = since.Value.Ticks });
                }
                mine.Sort((x, y) => x.Body.CompareTo(y.Body));
                rows.AddRange(mine);
            }
            return rows.ToArray();
        }
    }
}
