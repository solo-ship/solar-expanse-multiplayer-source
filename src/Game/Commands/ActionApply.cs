using System;
using System.Collections.Generic;
using HarmonyLib;
using Company = Game.Company;
using GameManager = Manager.GameManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfoManager = Manager.ObjectInfoManager;

namespace SolarExpanseMultiplayer
{
    // The world calls of every entry (plan "Where the host applies"), on the host with no numbers given and on a
    // client with the host's. A player's kind runs the window's own game calls for the acting agency; the
    // host-decided kinds are the session's seats and stops (DD5, DD9), applied the same way everywhere, and the
    // completions and observatory and asteroid outcomes the host's game has already made (DD11, DD14, DD15), which
    // the host only publishes with the numbers its rules recorded.
    // This file is the only writer of the session's HumanSet (a source test pins it): every game changes its human
    // agencies at the same point of the stream.
    public static partial class ActionApply
    {
        static readonly AccessTools.FieldRef<ObjectInfoData, DateTime?> LifeSupportNotice =
            AccessTools.FieldRefAccess<ObjectInfoData, DateTime?>("lastNotificationLifeSupportFinish");

        public static ApplyOutcome Apply(ActionRequest a, ApplyNumbers given, CommandSession session)
        {
            if (a == null) throw new ArgumentNullException("a");
            bool host = given == null;
            if (ResearchCommand.IsResearch(a.Kind)) return Research(a, host);
            if (FlightApply.Handles(a.Kind)) return host ? session.Flights.Apply(a) : ClientFlight(a, given, session);
            switch (a.Kind)
            {
                case ActionKind.SeatClaimed: SeatClaimed(a, session.Humans); return Done(a, null, null);
                case ActionKind.SeatReleased: SeatReleased(a, session.Humans, host); return Done(a, null, null);
                case ActionKind.AgencyBankrupt: Bankrupt(a); return Done(a, null, null);
            }
            if (a.Kind == ActionKind.NukeAsteroid) return ApplyNuke(a, given);
            // the shared world's kinds that act now (M7): a player's market, contract and stock kinds and the host's 92/95
            if (host && WorldApply.Handles(a.Kind)) return WorldApply.Apply(a);
            // a client applies the host's numbers: the ops, a rival's steps, the 92's chain, the end-game ship
            if (!host && WorldApply.HandlesGiven(a.Kind)) return WorldApply.ApplyGiven(a, given);
            if (ActionKind.HostOnly(a.Kind))
            {
                // the client's side of these (the counters set first by the client glue, the held call released
                // under the entry's replay); the host-decided flights have their own (M6)
                if (!host && FlightClientEntries.Handles(a.Kind)) return FlightClientEntries.Apply(a, given, session);
                if (!host) return ClientEntries.Apply(a, given);
                ApplyNumbers numbers = session.TakeDecided(a);
                if (numbers == null)
                    throw new InvalidOperationException(ActionKind.Name(a.Kind) + " came without its recorded numbers");
                return Done(a, numbers, Touched(a, numbers));
            }
            if (ProductionCommand.Handles(a.Kind)) return ApplyProduction(a, given);
            if (ObservatoryCommand.Handles(a.Kind)) return ApplyObservatory(a, given);
            if (a.Kind == ActionKind.ForgetObject) return ApplyForget(a, given);
            if (BuildingChecks.Handles(a.Kind)) return BuildingApply.Apply(a, given);
            // a kind whose checks are not built refuses on the host; a client never gets one
            return Refused(RefuseCodes.NotAllowedHere);
        }

        // A player's flight kind on a client: the copy of a mission goes over frames (Deferred, the client glue
        // polls); the other kinds apply at once.
        static ApplyOutcome ClientFlight(ActionRequest a, ApplyNumbers given, CommandSession session)
        {
            if (a.Kind == ActionKind.FlightSchedule || a.Kind == ActionKind.SupplyFlight)
                return session.FlightApply.StartClient(a, given);
            return session.FlightApply.ApplyAtOnce(a, false);
        }

        static ApplyOutcome Done(ActionRequest applied, ApplyNumbers numbers, IList<PrintEntry> touched)
        {
            return new ApplyOutcome { Code = 0, Applied = applied, Numbers = numbers, Touched = touched };
        }

        static ApplyOutcome Refused(int code) { return new ApplyOutcome { Code = code }; }

        // A payment the game would not take (R19-3): the one place that decides what a shortfall means, so M7 routes
        // it through its world tolerance here and nowhere else. The host's checks passed a moment ago, so on the host
        // the world changed under them and the request is refused; on a client the host paid and this game could not,
        // which is out of step.
        internal static ApplyOutcome PaymentShortfall(bool host)
        {
            if (host) return Refused(RefuseCodes.CannotAfford);
            return new ApplyOutcome { Code = RefuseCodes.CannotAfford, PaymentFailed = true };
        }

        // ---- research (R1-R5) ----

        static ApplyOutcome Research(ActionRequest a, bool host)
        {
            Company c = GameAgencies.Find(a.Agency);
            if (c == null || c.ResearchManager == null) return Refused(RefuseCodes.BadTarget);
            var game = new ResearchGame(c);
            ActionRequest applied = a;
            if (host)
            {
                // the same decision Check made a moment ago, now with the list every game applies
                int code = ResearchCommand.Decide(a, game, out applied);
                if (code != 0) return Refused(code);
            }
            string problem = ResearchCommand.Apply(applied, game);
            if (problem != null)
            {
                // the host's checks passed, so the game refusing now is the world changing under them: a fault
                if (host) throw new InvalidOperationException(problem);
                return Refused(RefuseCodes.NotAllowedHere);
            }
            return Done(applied, null, new List<PrintEntry> { Print(ObjectKind.Research, a.Agency, 0) });
        }

        // ---- the seats (DD5) and the stop (DD9) ----

        static Company Need(string agency)
        {
            Company c = GameAgencies.Find(agency);
            if (c == null) throw new InvalidOperationException("no agency " + agency);
            return c;
        }

        // The agency becomes a player's on every game: its AI off, its money the host's, the crew-death timer that
        // only a game that played it on has set back to unset everywhere (an unsaved field a joiner's fresh copy
        // lacks), and only then into the human set, so a throw part way leaves the seat unclaimed and the next batch
        // claims it again.
        static void SeatClaimed(ActionRequest a, HumanSet humans)
        {
            Company c = Need(a.Agency);
            WorldSwitches.AiOffOne(c);
            Seat(c, a.Amount, true);
            humans.Add(a.Agency);
        }

        // A human agency's money and crew-death notices as its claim sets them: on a claim, and on the host again for
        // every human agency after a load mid-session (CommandSession.Reseat). setMoney false keeps the money a save
        // already holds as a real amount.
        internal static void Seat(Company c, double money, bool setMoney)
        {
            if (setMoney) c.MoneyController.ForCheatSetCurrentMony(money);
            ClearLifeSupportNotices(c);
        }

        // After a host load: the set follows the ledger's seats (HumanReconcile). An agency no player holds goes back
        // to the computer as a released seat does; one a player holds joins the set with its AI off.
        internal static void FollowSeats(HumanSet humans, HumanReconcile plan)
        {
            foreach (string agency in plan.Drop)
            {
                try { SeatReleased(agency, humans, true); }
                catch (Exception e)
                {
                    Log.Error("command: releasing " + agency + " after the load failed: " + e.GetType().Name);
                    humans.Remove(agency);
                }
            }
            foreach (string agency in plan.Add)
            {
                Company c = GameAgencies.Find(agency);
                if (c != null) WorldSwitches.AiOffOne(c);
                humans.Add(agency);
            }
        }

        // Back to a rival: out of the human set, the infinite money every non-player rival has, and on the host its
        // AI exactly as the game's own setup would have it (SeatRules); a client keeps every AI off through
        // Company.BlockEnableAI. Switching it on mirrors the setup rather than restoring a recorded state, which would
        // have to survive the host's loads.
        static void SeatReleased(ActionRequest a, HumanSet humans, bool host) { SeatReleased(a.Agency, humans, host); }

        internal static void SeatReleased(string agency, HumanSet humans, bool host)
        {
            Company c = Need(agency);
            humans.Remove(agency);
            c.MoneyController.ForCheatSetCurrentMony(double.PositiveInfinity);
            if (!host) return;
            GameManager.StartGameConfiguration cfg = GameAgencies.Manager().StartGameConfig;
            bool rival = cfg.enabledRivalCorporations != null && c.Definition != null
                && cfg.enabledRivalCorporations.Contains(c.Definition);
            bool government = c.Definition != null && c.Definition.IsWorldGovernment;
            if (SeatRules.AiOnAfterRelease(cfg.setupAIs, rival, government, Company.BlockEnableAI))
                c.EnableDisableAI(true);
        }

        // The game's own branch for an agency that is not the player: switched off (D 80669-80681). Its AI is off
        // already, as a human agency's always is.
        static void Bankrupt(ActionRequest a)
        {
            Company c = Need(a.Agency);
            c.EnableDisableAI(false);
            c.enabled = false;
        }

        static void ClearLifeSupportNotices(Company c)
        {
            ObjectInfoManager m = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (m == null) return;
            foreach (ObjectInfo o in m.allObjectInfos)
            {
                if (o == null || o.ObjectsInfoData == null) continue;
                foreach (ObjectInfoData d in o.ObjectsInfoData)
                    if (d != null && ReferenceEquals(d.company, c)) LifeSupportNotice(d) = null;
            }
        }

        // ---- what an entry made the day's prints cover ----

        static PrintEntry Print(int kind, string agency, int body)
        {
            return new PrintEntry { Kind = kind, Agency = agency, Body = body };
        }

        // A finished build changes the site's builds, and a finished facility its facilities; an examine step can
        // select a deposit on its target (DD14).
        static IList<PrintEntry> Touched(ActionRequest a, ApplyNumbers n)
        {
            var list = new List<PrintEntry>();
            if (a.Kind == ActionKind.CraftFinished && a.Body > 0)
            {
                list.Add(Print(ObjectKind.SiteBuilds, a.Agency, a.Body));
                list.Add(Print(ObjectKind.SiteFacilities, a.Agency, a.Body));
            }
            if (a.Kind == ActionKind.ObservatoryStep && n != null && n.After != null && n.After.Target > 0)
                list.Add(Print(ObjectKind.SiteFacilities, a.Agency, n.After.Target));
            return list;
        }
    }
}
