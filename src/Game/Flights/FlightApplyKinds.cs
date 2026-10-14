using System;
using System.Collections.Generic;
using Company = Game.Company;
using CycleMissionManager = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionManager;
using IdForObjectInfo = Game.Info.IdForObjectInfo;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MissionInfo = Game.Info.MissionInfo;
using MissionPresetData = Game.UI.Windows.Elements.PlanMissionElements.MissionPresetData;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using Spacecraft = CustomUpdate.Spacecraft;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using SpacecraftInfo = Game.Info.SpacecraftInfo;

namespace SolarExpanseMultiplayer
{
    // The flight kinds that apply in one call (cancel, pause, delete, scrap, preset), the client's copy of a created
    // mission and the checks the two sides share. The world calls are the windows' own, for the agency the entry
    // names; the host has checked them a moment ago, so a call that cannot run now is the world changing under the
    // check (host: refused) or a game that parted (client: out of step).
    public sealed partial class FlightApply
    {
        public static bool Handles(int kind)
        {
            return kind >= ActionKind.FlightSchedule && kind <= ActionKind.PresetSave;
        }

        // ---- the kinds that apply at once ----

        public ApplyOutcome ApplyAtOnce(ActionRequest a, bool host)
        {
            try
            {
                switch (a.Kind)
                {
                    case ActionKind.FlightCancel: return Cancel(a, host);
                    case ActionKind.CyclicalSet: return RouteSet(a, host);
                    case ActionKind.CyclicalPause: return Pause(a, host);
                    case ActionKind.CyclicalDelete: return Delete(a, host);
                    case ActionKind.CraftScrap: return Scrap(a, host);
                    case ActionKind.PresetSave: return Preset(a, host);
                    default: return Bad(host);
                }
            }
            catch (Exception e)
            {
                Log.Warn("flight: " + ActionKind.Name(a.Kind) + " " + a.Agency + " could not apply: "
                    + ExceptionText.Chain(e));
                return Bad(host);
            }
        }

        // A host refuses (the world moved under its check); a client that cannot follow is out of step.
        static ApplyOutcome Bad(bool host)
        {
            return new ApplyOutcome { Code = host ? RefuseCodes.BadTarget : RefuseCodes.NotAllowedHere };
        }

        static ApplyOutcome Done(ActionRequest a, params PrintEntry[] touched)
        {
            return new ApplyOutcome { Applied = a, Touched = new List<PrintEntry>(touched) };
        }

        static PrintEntry Missions(string agency)
        {
            return new PrintEntry { Kind = ObjectKind.Missions, Agency = agency, Body = 0 };
        }

        // The abort popup's OK (D 140002-140075): the mission's ship cancels it; a cyclical ship loses its route
        // first; a construct cancels all its missions.
        ApplyOutcome Cancel(ActionRequest a, bool host)
        {
            MissionInfo m = FlightChecks.Mission(a.ItemId);
            if (m == null || m.cancel) return Bad(host);
            var sc = m.spacecraftInfo2 as Spacecraft;
            if (sc != null)
            {
                if (sc.CycleMissionsData != null)
                    MonoBehaviourSingleton<CycleMissionManager>.Instance.RemoveCycleMission(sc.CycleMissionsData);
                sc.CancelMission(m);
            }
            else if (m.spacecraftInfo2 is SpacecraftInfo info && info.spacecraft != null) info.spacecraft.CancelMission(m);
            else if (m.spacecraftInfo2 is SpacecraftConstructData scd) scd.CancelMissions();
            else return Bad(host);
            session.FlightRead.Cancelled(m.id);
            session.FlightWatches.Dirty();
            Log.Info("flight: cancelled " + a.ItemId + " (" + a.Value + ") by " + a.Agency);
            return Done(a, Missions(a.Agency),
                new PrintEntry { Kind = ObjectKind.SiteFacilities, Agency = a.Agency, Body = m.start.id });
        }

        ApplyOutcome Pause(ActionRequest a, bool host)
        {
            Spacecraft s = FlightChecks.Ship(a.Ship);
            if (s == null || s.CycleMissionsData == null) return Bad(host);
            s.CycleMissionsData.Pause = a.Value == 1;
            Log.Info("cyclical: " + a.Agency + " ship " + a.Ship + (a.Value == 1 ? " paused" : " resumed"));
            return Done(a);
        }

        ApplyOutcome Delete(ActionRequest a, bool host)
        {
            Spacecraft s = FlightChecks.Ship(a.Ship);
            if (s == null || s.CycleMissionsData == null) return Bad(host);
            MonoBehaviourSingleton<CycleMissionManager>.Instance.RemoveCycleMission(s.CycleMissionsData);
            session.FlightWatches.Dirty();
            Log.Info("cyclical: " + a.Agency + " ship " + a.Ship + " deleted");
            return Done(a, Missions(a.Agency));
        }

        ApplyOutcome Scrap(ActionRequest a, bool host)
        {
            if (a.Ship >= 0)
            {
                Spacecraft s = FlightChecks.Ship(a.Ship);
                if (s == null) return Bad(host);
                s.Scrap();
                return Done(a, new PrintEntry { Kind = ObjectKind.SiteBuilds, Agency = a.Agency, Body = a.Body });
            }
            ObjectInfo body = GameAgencies.Body(a.Body);
            if (body == null || body.ListLaunchVehicle == null) return Bad(host);
            foreach (LaunchVehicle lv in body.ListLaunchVehicle)
                if (lv != null && lv.ID == a.ItemId)
                {
                    lv.Scrap();
                    return Done(a, new PrintEntry { Kind = ObjectKind.SiteBuilds, Agency = a.Agency, Body = a.Body });
                }
            return Bad(host);
        }

        // DD-F11: a fresh preset built from the validated fields, appended in stream order on every game.
        ApplyOutcome Preset(ActionRequest a, bool host)
        {
            Company c = GameAgencies.Find(a.Agency);
            PresetData p = a.Preset;
            if (c == null || p == null) return Bad(host);
            if (c.MissionPresets.Count >= ActionLimits.MaxPresetsPerAgency && host) return Bad(host);
            var d = new MissionPresetData
            {
                presetName = p.Name, target = new IdForObjectInfo(FlightMap.Body(p.Target)),
                scTypeID = p.ScType ?? "", scCount = p.ScCount, scIsLowOrbitContainer = p.ScIsLowOrbitContainer,
                scIsFromFacility = p.ScIsFromFacility, scTypeIDFacility = p.ScTypeFacility ?? "",
                lvTypeID = p.LvType ?? "", lvCount = Math.Max(1, p.LvCount),
                cargoAllData = FlightMap.PresetCargo(p.Cargo),
                costType = (MissionInfo.ECostType)p.CostType, forCyclical = p.ForCyclical
            };
            c.MissionPresets.Add(d);
            Log.Info("flight: preset " + c.MissionPresets.Count + " \"" + p.Name + "\" saved for " + a.Agency);
            return Done(a);
        }

        // ---- a client's copy of an entry's mission ----

        // The client side of a 60, 65 or 70: the entry's removals first (the host took them from its stock at
        // acceptance), then the mission through the load path over frames. Deferred: Poll answers when it is made.
        public ApplyOutcome StartClient(ActionRequest a, ApplyNumbers given)
        {
            Company c = GameAgencies.Find(a.Agency);
            if (c == null || a.Mission == null || clientCreation != null) return Bad(false);
            string problem = TakeRemovals(c, a.Mission.Removals);
            if (problem != null)
            {
                Log.Warn("flight: " + a.Agency + ": " + problem);
                return ActionApply.PaymentShortfall(false);
            }
            var cr = new FlightCreation { Company = c, Entry = a, Mission = a.Mission, Host = false };
            foreach (CounterRow row in given != null && given.Counters != null ? given.Counters : new CounterRow[0])
                if (row.Which == CounterKind.MissionId) cr.MissionIdAfter = row.After;
            clientCreation = cr;
            Begin(cr);
            Log.Info("flight: applying " + ActionKind.Name(a.Kind) + " mission " + a.Mission.MissionId + " for " + a.Agency);
            return new ApplyOutcome { Deferred = true, Applied = a };
        }

        // nowMs when the running client creation's plan left the game's queue; -1 while queued
        public long ClientStartedMs { get { return clientCreation == null ? -1 : clientCreation.StartedMs; } }

        // null while the creation runs; then its outcome. A mission that is not what the entry says is out of step.
        public ApplyOutcome PollClient()
        {
            FlightCreation cr = clientCreation;
            if (cr == null) return new ApplyOutcome { Code = RefuseCodes.NotAllowedHere };
            if (!cr.Done) return null;
            clientCreation = null;
            if (cr.Code != 0 || cr.Result == null)
                return new ApplyOutcome { Code = RefuseCodes.NotAllowedHere };
            string why = AfterCheck(cr);
            if (why != null)
            {
                Log.Warn("flight: after-check: " + why);
                return new ApplyOutcome { Code = RefuseCodes.NotAllowedHere };
            }
            if (cr.MissionIdAfter >= 0) LastMissionId = (int)cr.MissionIdAfter;
            Finished(cr);
            FlightRouteCount.AfterClientLeg(cr.Entry);   // the held route controller's trip count, as the host's
            return new ApplyOutcome { Applied = cr.Entry, Touched = TouchedBy(cr) };
        }

        // The creation and the entry must agree on the mission's row (ids, bodies, dates, crafts, the leftover fuel).
        public static string AfterCheck(FlightCreation cr)
        {
            return MissionPrint.AfterCheck(FlightMap.RowOf(cr.Result), cr.Mission);
        }

        // The creation is in the world: the watches are told, and the actor's id counter follows. Its "flight:
        // created" line is FlightSources.CreatedFly's, written once for every CreateFly on every game.
        public void Finished(FlightCreation cr)
        {
            session.FlightWatches.Dirty();
            session.Steps.Watch.Dirty = true;
            // F10's row keeps the flight that was made for it, on every game (it greys the row's send button)
            if (cr.Entry.Kind == ActionKind.SupplyFlight) FlightSupply.Link(cr.Entry, cr.Result);
            PlannerWait.Applied(cr.Entry);   // Task 11: the player's planner waits for exactly this
        }

        static IList<PrintEntry> TouchedBy(FlightCreation cr)
        {
            var list = new List<PrintEntry> { Missions(cr.Entry.Agency) };
            foreach (CargoRow r in cr.Mission.Cargo ?? new CargoRow[0])
                if (r.Kind == CargoKinds.Module || r.Kind == CargoKinds.Crew)
                {
                    list.Add(new PrintEntry { Kind = ObjectKind.SiteFacilities, Agency = cr.Entry.Agency,
                        Body = cr.Mission.Start });
                    break;
                }
            return list;
        }

        public static IList<PrintEntry> Touched(FlightCreation cr) { return TouchedBy(cr); }

        // The entry's removals, taken from the acting agency as the host's step took them. A resource row the stock
        // cannot cover goes through Shortfall; money goes off whatever the purse holds (the host's check passed).
        string TakeRemovals(Company c, RemovalRow[] rows)
        {
            if (rows == null) return null;
            bool human = session.Humans.Contains(GameAgencies.Wire(c));
            foreach (RemovalRow r in rows)
            {
                Company who = GameAgencies.Find(r.Agency);
                if (who == null) return "no agency " + r.Agency;
                if (r.What == RemovalKinds.Money)
                {
                    who.MoneyController.RemoveMoney(r.Amount);
                    continue;
                }
                ObjectInfoData site = FlightMap.Body(r.Body).GetObjectInfoData(who);
                var rd = FlightMap.ResourceOf(r.Resource);
                double onHand = site.CheckResources(rd);
                double take = Shortfall(r.Agency, r.Amount, onHand, human);
                if (take < 0) return "short of " + r.Resource + " at body " + r.Body;
                if (take > 0)
                {
                    ObjectInfoData.TransactionRemoveResource ignore;
                    site.RemoveResource(rd, take, out ignore);
                }
            }
            return null;
        }
    }
}
