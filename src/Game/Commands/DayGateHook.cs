using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using Facility = Game.ObjectInfoDataScripts.Facility;
using MissionInfo = Game.Info.MissionInfo;
using MissionInfoManager = Manager.MissionInfoManager;
using ObjectInfo = Game.Info.ObjectInfo;
using Spacecraft = CustomUpdate.Spacecraft;
using SpaceraftCustom3dview = CustomUpdate.SpaceraftCustom3dview;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // The day gate and the impact holds inside the fixed step (DD4, DD15; plan Task 10b). TimeController.FixedUpdate
    // runs TimeStep and then DoThePeriodicEvents; a prefix on it decides how far the step may go (StepGate):
    //   * a client applies what is due for its day, then ends its step short of nextDay while the gate says no, and
    //     short of each watched impact's arrival until the host's entry for it is in (the Short form: the engine's
    //     own step error makes the step the wanted length; a step with nothing left is dropped with its real time);
    //   * the host never waits: it ends one step a day 60 game seconds short of nextDay and one step at each watched
    //     impact's hold point, so a landing and a day event rarely share a step and its impacts land in arrival order.
    // A prefix on DoThePeriodicEvents is the client's last look before its day event (BeforeDayEvent). A prefix on the
    // ship's private landing handler applies the host's impact entry inside the client's own landing step. Every
    // patch is a no-op outside a session; none of them is a speed vote. The class needs a [HarmonyPatch] of its own:
    // HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class DayGateHook
    {
        [HarmonyPatch(typeof(TimeController), "FixedUpdate")]
        [HarmonyPrefix]
        static bool StepPrefix(TimeController __instance)
        {
            CommandSession s = Session.Commands;
            if (s == null) return true;
            FlightLog.StepStarted();
            try { return s.Steps.Step(__instance); }
            catch (Exception e) { return s.Steps.StepFailed(e); }
        }

        [HarmonyPatch(typeof(TimeController), "DoThePeriodicEvents")]
        [HarmonyPrefix]
        static void DayPrefix(TimeController __instance)
        {
            CommandSession s = Session.Commands;
            if (s == null || s.IsHost) return;
            long day;
            try { if (s.Steps.DayEventDue(__instance, out day)) BeforeDayEvent(day); }
            catch (Exception e) { s.Steps.Failed(e); }
        }

        // The end of the day event: the line the flight tools compare (FlightLog). DoThePeriodicEvents runs every
        // fixed step; its day event ran only when it moved nextDay on.
        [HarmonyPatch(typeof(TimeController), "DoThePeriodicEvents")]
        [HarmonyPrefix]
        static void DayBefore(TimeController __instance, ref long __state)
        {
            __state = Session.Commands != null ? FlightLog.NextDayTicks(__instance) : 0;
        }

        [HarmonyPatch(typeof(TimeController), "DoThePeriodicEvents")]
        [HarmonyPostfix]
        static void DayPostfix(TimeController __instance, long __state)
        {
            try { FlightLog.DayEventDone(__instance, __state); }
            catch (Exception e) { Log.Error("flight: could not write the step end: " + e.GetType().Name); }
            // a build the day event started (a queue's next item) counts from the boundary on every game
            if (__state != 0 && HumanAgencies.Active && FlightLog.NextDayTicks(__instance) != __state)
                BuildStartGlue.Note(null, __state);
        }

        // The client's crossing step, before its day event for `day` (the client twin of the host's DayClosing): the
        // gate must have let this day go, and the day's hold is written to the log. M7 hooks its own work in here.
        public static void BeforeDayEvent(long day)
        {
            CommandSession s = Session.Commands;
            if (s != null && !s.IsHost) s.Steps.ClientDayEvent(day);
        }

        // The line the flight tools compare: a mission's landing, once. The ship's own landing step (the one call that
        // ends its Landing phase), since the 3D view's handler runs only for a ship that has a view.
        [HarmonyPatch(typeof(Spacecraft), "RocketLandDoStuff")]
        [HarmonyPrefix]
        static void LandingLogged(Spacecraft __instance)
        {
            try { FlightLog.Landed(__instance); }
            catch (Exception e) { Log.Error("flight: could not write a landing: " + e.GetType().Name); }
        }

        // The landing event reaches this private handler (the public wrapper is never called). On a client, a watched
        // impact is applied here, inside the game's own call, with this handler as the branch the entry runs; while an
        // entry is being applied (that branch itself) the handler runs as the game has it.
        [HarmonyPatch(typeof(SpaceraftCustom3dview), "SpaceCraftOnrocketLandNormal")]
        [HarmonyPrefix]
        static bool LandingPrefix(SpaceraftCustom3dview __instance, Spacecraft __0, ObjectInfo __1, ObjectInfo __2)
        {
            CommandSession s = Session.Commands;
            if (s == null || s.IsHost || s.Applying) return true;
            try { return s.Steps.Landing(__instance, __0, __1, __2); }
            catch (Exception e)
            {
                s.Steps.Failed(e);
                return true;
            }
        }

        // A ship's landing builds the facilities its type carries, one item id each (productionCountID++). On a
        // client, a landing the host ran ahead of an entry this game applied earlier takes the ids the host gave it
        // (ItemIdGaps), and the counter is put back after, the host's again; any other landing's items are noted, so
        // an entry the host applied before its own landing can move them on. __state: null outside a client's own.
        [HarmonyPatch(typeof(Spacecraft), "CreateFacility")]
        [HarmonyPrefix]
        static void LandingIdsBefore(Spacecraft __instance, ref LandingIdState __state)
        {
            __state = null;
            CommandSession s = Session.Commands;
            if (s == null || s.IsHost || s.Applying) return;
            try { __state = s.Steps.LandingIds(__instance); }
            catch (Exception e) { s.Steps.Failed(e); }
        }

        // A finalizer, so a build that throws still gives the counter back.
        [HarmonyPatch(typeof(Spacecraft), "CreateFacility")]
        [HarmonyFinalizer]
        static void LandingIdsAfter(Spacecraft __instance, LandingIdState __state)
        {
            if (__state == null) return;
            CommandSession s = Session.Commands;
            try
            {
                if (s != null) s.Steps.LandedIds(__instance, __state);
                else if (__state.Restore >= 0) __instance.GetCompany().productionCountID = __state.Restore;
            }
            catch (Exception e) { if (s != null) s.Steps.Failed(e); }
        }
    }

    // A client's landing between the two halves of DayGateHook's CreateFacility patch: the ids it starts from, and
    // the counter to put back when they were the host's (-1: they were this game's own).
    public sealed class LandingIdState
    {
        public string Agency;
        public int From;
        public int Restore = -1;
    }

    // The fixed step's decisions for one session (DayGateHook), host and client: the watched impacts, the host's daily
    // cut, the client's holds and their log lines. Owned by CommandSession; each load starts it again (Loaded).
    public sealed class StepGate
    {
        static readonly AccessTools.FieldRef<TimeController, DateTime> NextDayOf =
            AccessTools.FieldRefAccess<TimeController, DateTime>("nextDay");
        static readonly AccessTools.FieldRef<GravityEngine, double> StepError =
            AccessTools.FieldRefAccess<GravityEngine, double>("physTimeError");
        static readonly AccessTools.FieldRef<GravityEngine, float> LastStep =
            AccessTools.FieldRefAccess<GravityEngine, float>("previousPhyLoopGameTime");
        static readonly MethodInfo LandingHandler = AccessTools.Method(typeof(SpaceraftCustom3dview),
            "SpaceCraftOnrocketLandNormal");

        const int SummaryEveryDays = 30;

        readonly CommandSession session;
        // the landings (impacts and orbit pulls) this game watches, and those it has landed in this world
        public readonly ImpactWatch Watch = new ImpactWatch();
        public readonly HashSet<long> Landed = new HashSet<long>();
        // the checked launches and the plain cuts (M6): FlightWatches' lists, rebuilt from this game's own missions
        // after every flight entry, so every game, host or client, has the same events
        public MissionWatch Launches { get { return session.FlightWatches.Launches; } }
        public MissionWatch PlainCuts { get { return session.FlightWatches.Plain; } }
        readonly ClientCut clientCut = new ClientCut();
        long dayCutFor = long.MinValue, researchTakenFor = long.MinValue;
        readonly StepCarry carry = new StepCarry();
        long heldDay = -1, heldBoundary;
        // the client's last step was not taken: an entry waiting for the host's tick applies in the next (HostTick)
        bool clientStanding;
        // when the client began to hold for the event it waits for, and which event (the log lines of the cut)
        long eventHeldSince = -1, eventHeldId = -1;
        int errors;

        public StepGate(CommandSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            this.session = session;
        }

        // A load (the host's, or a client's snapshot): nothing watched or cut carries over.
        public void Loaded()
        {
            Watch.Clear();
            Watch.ForgetCuts();
            Watch.Dirty = true;
            session.FlightWatches.Loaded();
            Landed.Clear();
            clientCut.Reset();
            dayCutFor = long.MinValue;
            researchTakenFor = long.MinValue;
            carry.Forget();
            heldDay = -1;
            eventHeldSince = -1;
            clientStanding = false;
        }

        static readonly AccessTools.FieldRef<Spacecraft, List<Facility>> ShipFacilities =
            AccessTools.FieldRefAccess<Spacecraft, List<Facility>>("facilityOnShip");

        // A client's landing about to build its ship's facilities: when the host spent item ids on it ahead of an entry
        // this game has applied, the counter is set to the first of them. The ids it starts from, or null.
        public LandingIdState LandingIds(Spacecraft ship)
        {
            Company c = ship != null ? ship.GetCompany() : null;
            Data.ScriptableObject.SpacecraftType type = ship != null ? ship.spacecraftType : null;
            CommandClientGlue glue = session.ClientGlue;
            TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
            if (c == null || type == null || type.FacilityOnSC == null || glue == null || tc == null) return null;
            string agency = GameAgencies.Wire(c);
            var state = new LandingIdState { Agency = agency, From = c.productionCountID };
            MissionInfo mi = ship.GetMissionInfo();
            long from;
            if (!glue.Counters.Gaps.Take(agency, type.FacilityOnSC.Length, mi != null ? mi.DateArrive.Ticks
                : long.MinValue, tc.CurrentTime.Ticks, out from)) return state;
            state.Restore = c.productionCountID;
            state.From = (int)from;
            c.productionCountID = (int)from;
            Log.Info("command: ship " + ship.ID + " of " + agency + " lands with item ids from " + from
                + ", the host's ahead of an entry applied here earlier; the counter goes back to " + state.Restore);
            return state;
        }

        // The landing's facilities are built: the host's ids give the counter back; this game's own are noted.
        public void LandedIds(Spacecraft ship, LandingIdState state)
        {
            Company c = ship.GetCompany();
            if (c == null) return;
            if (state.Restore >= 0)
            {
                c.productionCountID = state.Restore;
                return;
            }
            var items = new List<object>();
            List<Facility> made = ShipFacilities(ship);
            if (made != null)
                foreach (Facility f in made)
                    if (f != null && f.ID >= state.From && f.ID < c.productionCountID) items.Add(f);
            items.Sort((a, b) => ((Facility)a).ID.CompareTo(((Facility)b).ID));
            CommandClientGlue glue = session.ClientGlue;
            if (items.Count == 0 || glue == null) return;
            glue.Counters.Gaps.Landed(state.Agency, ((Facility)items[0]).ID, items);
            Log.Info("command: ship " + ship.ID + " of " + state.Agency + " lands with this game's item ids from "
                + ((Facility)items[0]).ID + " (" + items.Count + ")");
        }

        // How long the client held for that event's entry (ms), once, or -1 when it did not hold for it.
        public long TakeEventHold(long id)
        {
            if (eventHeldSince < 0 || eventHeldId != id) return -1;
            long ms = NetClock.NowMs - eventHeldSince;
            eventHeldSince = -1;
            return ms;
        }

        // A throw in the gate: the host logs it and runs on (it never waits); a client cannot tell what it may cross,
        // so it is out of step (the full rejoin follows).
        public void Failed(Exception e)
        {
            errors++;
            if (errors <= 5 || errors % 1000 == 0)
                Log.Error("command: the day gate failed (" + errors + "): " + ExceptionText.Chain(e));
            CommandClientGlue g = session.ClientGlue;
            if (g != null) g.Client.OutOfStepNow("the day gate threw " + e.GetType().Name);
        }

        // The same in the fixed step, which a client then does not take.
        public bool StepFailed(Exception e)
        {
            Failed(e);
            GravityEngine ge = GravityEngine.instance;
            return session.IsHost || ge == null || Drop(ge);
        }

        // ---- the step ----

        // False skips the step (a client holding with nothing left to take).
        public bool Step(TimeController tc)
        {
            GravityEngine ge = GravityEngine.instance;
            if (tc == null || ge == null) return true;
            // the time an earlier cut took off, run now (never while paused)
            double back = carry.Repay(tc.CurrentTimeScale > 0f);
            if (back > 0) StepError(ge) -= back;
            if (session.IsHost && HostHolds(tc, ge)) return Drop(ge);
            return session.IsHost ? HostStep(tc, ge) : ClientStep(tc, ge);
        }

        bool HostStep(TimeController tc, GravityEngine ge)
        {
            if (!session.Active) return true;
            long now, end;
            double dPhys = Span(tc, ge, out now, out end);
            long nextDay = NextDayOf(tc).Ticks;
            if (Watch.Dirty)
            {
                Rebuild(now);
                StepCut.PassedOver(now, nextDay, Watch);
            }
            // the day's research prints are taken where the clients compare theirs (CommandHost)
            if (researchTakenFor != nextDay && StepCut.AtDayPoint(now, nextDay, Watch))
            {
                researchTakenFor = nextDay;
                session.World.TakeResearchAtPoint(nextDay);
            }
            CutReason why;
            long impact;
            long target = StepCut.Host(now, end, nextDay, dayCutFor, Watch, out why, out impact);
            // M6: a checked launch or an earlier landing is cut at once, with no wait, if the step reaches it first;
            // then the day's or the impact's cut was not reached, and is taken by a later step
            long flight = session.FlightWatches.HostCut(now, target == StepCut.None ? end : target, nextDay);
            if (flight < target)
            {
                target = flight;
                why = CutReason.Landing;
            }
            if (why == CutReason.Day) dayCutFor = nextDay;
            if (why == CutReason.Impact) Watch.MarkCut(impact);
            if (target == StepCut.None || target <= now) return true;
            Cut(ge, dPhys, To(tc, ge, target), true);
            return true;
        }

        // The host's plan hold (DD-F3): while a plan is pending, the step that would pass the day cut waits for it, up
        // to PlanHoldMaxMs, when the plans still unmade are given up. The wait is a dropped step, like a client's hold.
        bool HostHolds(TimeController tc, GravityEngine ge)
        {
            if (!session.Active) return false;
            long now, end;
            Span(tc, ge, out now, out end);
            return PlanHoldWaits(now, NextDayOf(tc).Ticks);
        }

        bool PlanHoldWaits(long now, long nextDay)
        {
            CommandService service = session.Service;
            if (service == null) return false;
            PlanHold hold = session.PlanHold;
            PlanHoldState state = hold.Check(now, StepCut.DayPoint(nextDay, Watch), nextDay, service.PlansPending,
                NetClock.NowMs);
            if (state == PlanHoldState.Ended)
                Log.Info("flight: plan hold " + hold.HeldMs + " ms day " + (nextDay / TimeSpan.TicksPerDay));
            else if (state == PlanHoldState.Expired)
            {
                Log.Warn("flight: plan hold " + hold.HeldMs + " ms day " + (nextDay / TimeSpan.TicksPerDay)
                    + " ran out; the plans still pending are given up");
                session.Flights.AbandonPending();
            }
            return state == PlanHoldState.Hold;
        }

        bool ClientStep(TimeController tc, GravityEngine ge)
        {
            CommandClientGlue g = session.ClientGlue;
            if (g == null || !g.Loaded)
            {
                if (g != null) g.Holding = false;
                return true;
            }
            CommandClient c = g.Client;
            long day;
            // no day key, or out of step: nothing more moves until the full rejoin
            if (!g.LocalDay(out day) || c.OutOfStep) return Hold(g, ge);
            long now, end;
            double dPhys = Span(tc, ge, out now, out end);
            // a step not taken last time (a hold, a parked or an empty step): nothing waits for the clock (HostTick)
            c.Standing = clientStanding;
            clientStanding = false;
            c.Update(day);
            // after the applies, so a seat entry applied just now already counts
            if (Watch.Dirty) Rebuild(now);
            if (session.FlightWatches.IsDirty) session.FlightWatches.Rebuild(now);
            g.Flights.AdmitHead(c, now);   // DD-F6: a late checked launch heading the stream is the host's to decide
            if (c.OutOfStep) return Hold(g, ge);
            long nextDay = NextDayOf(tc).Ticks;
            // a paused host ahead: the step runs no further than its date (PausedLanding), and standing there it is
            // dropped; ClientCut decides on that end, so a stop short of the day point is no approach to it
            long paused = c.CatchingUp ? 0 : c.PausedLandingTo();
            if (PausedLanding.Parked(now, paused))
            {
                g.Holding = false;
                clientStanding = true;
                return Drop(ge);
            }
            long stepEnd = PausedLanding.StepEnd(end, paused);
            // the aligned cut, the cut at nextDay, the watched landings and launches, the plain cuts, the gate's
            // comparison while held (ClientCut): the same points on every game
            CutReason why;
            bool hold;
            long target = clientCut.Decide(c, day, now, stepEnd, nextDay, NetClock.NowMs, Watch, Launches, PlainCuts,
                out why, out hold);
            if (hold || c.OutOfStep) return Hold(g, ge);
            if (target == StepCut.None && stepEnd < end)
            {
                target = stepEnd;
                why = CutReason.None;
            }
            if (why == CutReason.Impact)
            {
                if (eventHeldSince < 0)
                {
                    eventHeldSince = NetClock.NowMs;
                    eventHeldId = clientCut.WaitedId;
                }
            }
            // catching up after a drop: the step ends exactly at the host's paused date, then waits for the release
            // (M7)
            if (c.CatchingUp)
            {
                bool reached;
                long stop = StepCut.CatchUp(now, end, c.CatchUpTo, target, out reached);
                if (reached) return Hold(g, ge);
                if (stop != target) { target = stop; why = CutReason.None; }
            }
            if (why == CutReason.Day && heldDay != day)
            {
                heldDay = day;
                heldBoundary = nextDay;
            }
            if (target == StepCut.None)
            {
                g.Holding = false;
                return true;
            }
            g.Holding = why == CutReason.Day || why == CutReason.Impact;
            // a target the engine already stands on (its clock can read a tick short of it) is stood at: the step
            // run whole would pass it, and the day gate with it
            double to = To(tc, ge, target);
            clientStanding = EngineTime.Stands(target, now, to);
            if (clientStanding) return Drop(ge);
            // a stop at the day point or before a landing is no hold: the time it takes off is run in the next step
            Cut(ge, dPhys, to, why == CutReason.Approach || why == CutReason.Landing);
            return true;
        }

        bool Hold(CommandClientGlue g, GravityEngine ge)
        {
            g.Holding = true;
            clientStanding = true;
            return Drop(ge);
        }

        // The step's present and its end, in game ticks, and its length in the engine's own time.
        static double Span(TimeController tc, GravityEngine ge, out long now, out long end)
        {
            double dPhys = ge.CurrentPhysicsDeltaTime;
            double cur = ge.GetTimeWorldSeconds();
            double to = GravityScaler.GetWorldTimeSeconds(ge.GetPhysicalTimeDouble() + Math.Max(dPhys, 0.0));
            long start = tc.StartDateTime.Ticks;
            now = EngineTime.Ticks(start, cur);
            end = EngineTime.Ticks(start, to);
            return dPhys;
        }

        // The world seconds from the engine's time to `target`.
        static double To(TimeController tc, GravityEngine ge, long target)
        {
            return EngineTime.SecondsTo(tc.StartDateTime.Ticks, target, ge.GetTimeWorldSeconds());
        }

        // The Short form: the step ends `want` world seconds on through the engine's own step error (what is cut off
        // is lost, not carried, so a client never runs on into what it was held from).
        void Cut(GravityEngine ge, double dPhys, double want, bool owe)
        {
            if (!(want > 0)) return;
            StepError(ge) += carry.Cut(dPhys, GravityScaler.WorldSecsToPhysTime(want), owe);
        }

        // A step with nothing left to take is dropped with its real time, as a zero-length step would lose it.
        static bool Drop(GravityEngine ge)
        {
            LastStep(ge) = UnityEngine.Time.timeSinceLevelLoad;
            return false;
        }

        // ---- the client's day event ----

        // The day event is due in this step: its day, from this world's nextDay.
        public bool DayEventDue(TimeController tc, out long day)
        {
            day = 0;
            CommandClientGlue g = session.ClientGlue;
            if (g == null || !g.Loaded || GravityEngine.instance == null) return false;
            if (tc.CurrentTime.Ticks < NextDayOf(tc).Ticks) return false;
            return g.LocalDay(out day);
        }

        public void ClientDayEvent(long day)
        {
            CommandClientGlue g = session.ClientGlue;
            if (g == null) return;
            CommandClient c = g.Client;
            // the step that got here was let through by the gate; a day event it never allowed would part the games
            if (!c.OutOfStep && !c.MayCross(day))
                c.OutOfStepNow("day " + day + "'s event came before the host closed the day");
            // this game's world print against the host's (M7): the day-event point, before the event changes anything
            c.BeforeDayEvent(day);
            // the length the gate itself counted for the summary, so the two lines agree
            long heldMs = g.Gate.TakeHold(day);
            if (heldDay == day)
            {
                if (heldMs >= 0)
                    Log.Info("command: day gate: held day " + day + " boundary " + heldBoundary + " for " + heldMs
                        + " ms");
                heldDay = -1;
            }
            if (day % SummaryEveryDays == 0)
                Log.Info("command: day gate: " + g.Gate.Summary(true).Replace(" holds, ", " holds in "
                    + SummaryEveryDays + " days, "));
        }

        // ---- the client's landing ----

        // A watched impact landing on this client: the host's entry for it is applied here, with the game's handler
        // run inside it. False: the handler must not run again on its own (it ran inside the entry, or this client may
        // not run it at all); true: not an impact the session decides.
        public bool Landing(SpaceraftCustom3dview view, Spacecraft ship, ObjectInfo target, ObjectInfo from)
        {
            CommandClientGlue g = session.ClientGlue;
            if (g == null || !g.Loaded || !HumanAgencies.Active || ship == null) return true;
            if (ship.CargoAll == null || !ship.CargoAll.entireAsteroid) return true;
            if (target == null) return true;
            if (!HumanAgencies.IsHuman(ship.GetCompany())) return true;
            MissionInfo mission = ship.GetMissionInfo();
            if (mission == null)
            {
                // no client can land such an impact (the host starts a new stream for it): this world has parted now
                g.Client.OutOfStepNow("an impact with no mission landed here");
                return false;
            }
            long id = mission.id;
            // the client's day as it is now: a landing in the crossing step, after the day event, is for the next day
            long day;
            if (!g.LocalDay(out day)) return false;
            string why = g.Client.ApplyImpactNow(id, day, () => RunHandler(view, ship, target, from));
            Landed.Add(id);
            Watch.Remove(id);
            long held = TakeEventHold(id);
            if (why != null) Log.Warn("command: the impact of mission " + id + " did not apply: " + why);
            else
                Log.Info("flight: landing cut " + id + " " + Math.Max(0, held) + " ms "
                    + (InLastMinuteOfItsDay(mission.DateArrive.Ticks, g.Origin) ? "last-minute" : "normal"));
            return false;
        }

        // The aligned last minute of the day the arrival falls in (the day's boundary is a whole number of days from the
        // host's origin).
        static bool InLastMinuteOfItsDay(long arrival, long origin)
        {
            long span = arrival - origin;
            if (span < 0) return false;
            long next = origin + (span / TimeSpan.TicksPerDay + 1) * TimeSpan.TicksPerDay;
            return AlignedBoundary.InLastMinute(arrival, next);
        }

        // The game's own handler, through its patched entry: this prefix lets it run while the entry is applied.
        static string RunHandler(SpaceraftCustom3dview view, Spacecraft ship, ObjectInfo target, ObjectInfo from)
        {
            if (LandingHandler == null) return "the game has no landing handler";
            try
            {
                LandingHandler.Invoke(view, new object[] { ship, target, from });
                return null;
            }
            catch (TargetInvocationException e)
            {
                return "the landing handler threw " + (e.InnerException ?? e).GetType().Name;
            }
        }

        // ---- the watch ----

        // Every human agency's mission still flying to hit an asteroid or pull one into an orbit, from this game's own
        // missions. Only while the rules run: without them nobody announces an impact, so nothing is held for one. A
        // watched mission whose arrival has passed stays until it lands here (its entry may still be on the way); one
        // already past at a load is not taken up, since no step of this world can reach its landing.
        void Rebuild(long now)
        {
            Watch.Dirty = false;
            HashSet<long> passed = null;
            for (int i = 0; i < Watch.Count; i++)
            {
                if (Watch.ArrivalAt(i) > now) continue;
                if (passed == null) passed = new HashSet<long>();
                passed.Add(Watch.IdAt(i));
            }
            Watch.Clear();
            if (!HumanAgencies.Active) return;
            MissionInfoManager m = MonoBehaviourSingleton<MissionInfoManager>.Instance;
            if (m == null || m.ListMissionInfo == null) return;
            foreach (MissionInfo mi in m.ListMissionInfo)
            {
                if (mi == null || mi.cancel || mi.complete || mi.cargoAll == null || !mi.cargoAll.entireAsteroid)
                    continue;
                ObjectInfo target = mi.trajectoryObject != null ? mi.target : null;
                if (target == null) continue;
                if (!HumanAgencies.IsHuman(mi.company) || Landed.Contains(mi.id)) continue;
                long arrival = mi.DateArrive.Ticks;
                if (arrival <= now && (passed == null || !passed.Contains(mi.id))) continue;
                if (!Watch.Add(mi.id, arrival))
                {
                    WatchFull();
                    return;
                }
            }
        }

        void WatchFull()
        {
            string why = "more than " + ImpactWatch.MaxWatched + " impact missions in flight";
            CommandClientGlue g = session.ClientGlue;
            if (g != null) g.Client.OutOfStepNow(why);
            else session.HostFault(why);
        }
    }
}
