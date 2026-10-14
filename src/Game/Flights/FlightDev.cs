using System;
using System.Collections.Generic;
using System.IO;
using Company = Game.Company;
using EResourceTypeType = Game.ObjectInfoDataScripts.EResourceTypeType;
using ILaunchVehicleInfo = CustomUpdate.ILaunchVehicleInfo;
using ISpacecraftInfo = Game.Info.ISpacecraftInfo;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;

namespace SolarExpanseMultiplayer
{
    // The test profile's `fly <n>` trigger (plan "Dev flags, triggers and faults"): line n of the -semp-flights file is
    // planned on a hidden planner for this game's own agency, stopped before its schedule step, turned into a request
    // with FlightRequests and sent through CommandSession.Click, the way the window capture will send a real click.
    // The fastest cell is taken; the line's depart offset is not used. Test profiles only; one instance per session.
    public sealed class FlightDev
    {
        readonly CommandSession session;
        FlightLine[] lines;
        bool loaded;

        public FlightDev(CommandSession session) { this.session = session; }

        static Manager.GameManager GM { get { return MonoBehaviourSingleton<Manager.GameManager>.Instance; } }

        FlightLine[] Lines()
        {
            if (loaded) return lines;
            loaded = true;
            string file = Boot.Dev != null ? Boot.Dev.FlightsFile : null;
            if (file == null) { Log.Warn("flight: fly needs -semp-flights"); return null; }
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > FlightScript.MaxBytes)
                {
                    Log.Error("flight: the flight script is missing or over " + FlightScript.MaxBytes + " bytes");
                    return null;
                }
                string error;
                lines = FlightScript.Parse(File.ReadAllText(file), out error);
                if (lines == null) Log.Error("flight: the flight script is refused: " + TextSanitiser.OneLine(error));
                else Log.Info("flight: flight script of " + lines.Length + " flights");
            }
            catch (Exception e)
            {
                Log.Error("flight: the flight script could not be read: " + TextSanitiser.OneLine(e.Message));
            }
            return lines;
        }

        public void Fly(string text)
        {
            int n;
            if (!int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out n))
            {
                Log.Warn("flight: fly needs a line number");
                return;
            }
            FlightLine[] script = Lines();
            if (script == null) return;
            if (n < 1 || n > script.Length)
            {
                Log.Warn("flight: fly " + n + ": the script has " + script.Length + " flights");
                return;
            }
            try { Plan(script[n - 1], n); }
            catch (Exception e) { Log.Warn("flight: fly " + n + " failed: " + ExceptionText.Chain(e)); }
        }

        // ---- the small clicks, without a window (test profiles): the requests the captures would send ----

        // `abort <mission>`: the abort of one of this game's missions (61), as the mission window's button sends it:
        // the ship branch, or the construct branch while its craft is still being built.
        public void Abort(string text)
        {
            long id;
            if (!DevTriggerText.TryLong(text, out id)) { Log.Warn("flight: abort needs a mission id"); return; }
            MissionInfo mi = OwnMission(id);
            if (mi == null) { Log.Warn("flight: abort " + id + ": no such mission of this agency"); return; }
            var ship = mi.spacecraftInfo2 as CustomUpdate.Spacecraft;
            int branch;
            int first = -1;
            if (!ReferenceEquals(ship, null)) { first = ship.ID; branch = FlightClickRequests.AbortShip; }
            else if (mi.spacecraftInfo2 is Data.SpacecraftConstructData) branch = FlightClickRequests.AbortConstruct;
            else { Log.Warn("flight: abort " + id + ": its craft is of no kind the window aborts"); return; }
            Send("abort " + id + " branch " + branch, FlightClickRequests.Cancel(id, first, branch));
        }

        // `scrap <ship>`: the scrap button for one of this game's ships standing at a body (66).
        public void Scrap(string text)
        {
            long id;
            if (!DevTriggerText.TryLong(text, out id)) { Log.Warn("flight: scrap needs a ship id"); return; }
            foreach (CustomUpdate.Spacecraft s in ShipManager.Instance.ListAllSpaceShip)
            {
                if (ReferenceEquals(s, null) || s.ID != id || s.GetCompany() != GM.Player) continue;
                if (s.CurrentlyOnThisObject == null)
                {
                    Log.Warn("flight: scrap " + id + ": the ship is not at a body");
                    return;
                }
                Send("scrap " + id + " at " + s.CurrentlyOnThisObject.id,
                    FlightClickRequests.ScrapShip(s.CurrentlyOnThisObject.id, s.ID));
                return;
            }
            Log.Warn("flight: scrap " + id + ": no such ship of this agency");
        }

        // `route-pause <ship> <0|1>` and `route-delete <ship>`: the routes list's buttons (63, 64).
        public void RoutePause(string text)
        {
            string[] parts = (text ?? "").Split(' ');
            long ship, on;
            if (parts.Length != 2 || !DevTriggerText.TryLong(parts[0], out ship)
                || !DevTriggerText.TryLong(parts[1], out on) || on > 1 || ship > int.MaxValue)
            {
                Log.Warn("flight: route-pause needs a ship id and 0 or 1");
                return;
            }
            Send("route-pause " + ship + " " + on, FlightClickRequests.Pause((int)ship, on == 1));
        }

        public void RouteDelete(string text)
        {
            long ship;
            if (!DevTriggerText.TryLong(text, out ship) || ship > int.MaxValue)
            {
                Log.Warn("flight: route-delete needs a ship id");
                return;
            }
            Send("route-delete " + ship, FlightClickRequests.Delete((int)ship));
        }

        void Send(string what, ActionRequest a)
        {
            a.Agency = GameAgencies.Wire(GM.Player);
            Log.Info("flight: dev " + what + " sent");
            session.Click(a);
        }

        static MissionInfo OwnMission(long id)
        {
            Manager.MissionInfoManager m = MonoBehaviourSingleton<Manager.MissionInfoManager>.Instance;
            if (m == null || m.ListMissionInfo == null) return null;
            foreach (MissionInfo mi in m.ListMissionInfo)
                if (mi != null && mi.id == id && mi.company == GM.Player && !mi.cancel) return mi;
            return null;
        }

        void Plan(FlightLine line, int n)
        {
            Company c = GM.Player;
            ObjectInfo from = GameAgencies.Body(line.Start), to = GameAgencies.Body(line.Target);
            if (from == null || to == null) throw new InvalidOperationException("no such body");
            ISpacecraftInfo ship = FlightMap.FindShip(c, new CraftRef { Kind = CraftKinds.Ship, Id = line.Ship }, from);
            if (ship == null) throw new InvalidOperationException("no ship " + line.Ship);
            var ships = new List<ISpacecraftInfo> { ship };
            var rockets = new List<ILaunchVehicleInfo>();
            if (line.Rocket >= 0)
            {
                ILaunchVehicleInfo lv = FlightMap.FindRocket(c, new CraftRef { Kind = CraftKinds.Rocket,
                    Id = line.Rocket }, from);
                if (lv == null) throw new InvalidOperationException("no launch vehicle " + line.Rocket);
                rockets.Add(lv);
            }
            var cargo = Game.ObjectInfoDataScripts.CargoAll.CreateCargoEmpty();
            cargo.cargoFuel.objectInfo = from;
            cargo.cargoFuel.cargoMass = 0.0;
            cargo.cargoFuel.resourceType = ship.GetTypeSpaceCraft().GetFuelType();
            foreach (CargoRow row in line.Cargo)
            {
                var cg = new Game.ObjectInfoDataScripts.Cargo(cargo);
                cargo.listCargo.Add(cg);
                cg.resourceTypeType = EResourceTypeType.resorces;
                cg.resourceType = FlightMap.ResourceOf(row.Item);
                cg.objectInfo = from;
                cg.cargoMass = row.Amount;
            }
            var pmp = new PMMissionParameter();
            pmp.ChangeMissionName("fly " + n).SetCompany(c).SetTabSC(ships, ships.Count).SetTabCargo(cargo)
                .SetTabDestination(from, to).SetMissionOrigin(MissionInfo.EMissionCreator.Manual);
            pmp.SetTabLV(rockets, rockets.Count);
            pmp.ChangeStage(PlanMissionWindow.EStageWindow.Schedule);
            pmp.Fast = false;
            pmp.RandomBestOption = false;
            pmp.SetBurst(true);
            pmp.TryFastAsPossible = true;
            pmp.ClickFastestButton = true;
            PlanMissionWindow pmw = FlightApply.WindowFor(pmp);
            GM.SetPMParameterForCodeJobSystem(pmp, delegate
            {
                try
                {
                    pmw.PmTabSchedule.ButtonFastestClickButton();
                    var check = pmp.CheckCanPlanMission();
                    if (check.planMissionResult != PMMissionParameter.EPlanMissionResult.AllOk)
                    {
                        Log.Warn("flight: fly " + n + ": CheckCanPlanMission said " + check.planMissionResult);
                        return;
                    }
                    string problem;
                    ActionRequest req = FlightRequests.Schedule(pmp, out problem);
                    if (req == null) { Log.Warn("flight: fly " + n + ": " + problem); return; }
                    Log.Info("flight: fly " + n + " planned " + req.Mission.Start + "->" + req.Mission.Target);
                    session.Click(req);
                }
                catch (Exception e) { Log.Warn("flight: fly " + n + " threw " + ExceptionText.Chain(e)); }
                finally { pmw.ClearVariableForCode(); }
            });
        }
    }
}
