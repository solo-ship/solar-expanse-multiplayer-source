using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Cargo = Game.ObjectInfoDataScripts.Cargo;
using MissionInfo = Game.Info.MissionInfo;
using MissionInfoManager = Manager.MissionInfoManager;
using ObjectInfo = Game.Info.ObjectInfo;
using Spacecraft = CustomUpdate.Spacecraft;
using SpaceraftCustom3dview = CustomUpdate.SpaceraftCustom3dview;
using TrajectoryObject = Game.VisualizationScripts.TrajectoryObject;

namespace SolarExpanseMultiplayer
{
    // DD-F12: the flights an asteroid's destruction cancels, read from the game (the decision is Core's TargetGone)
    // and cancelled the vanilla nuke's way, with the three gaps vanilla leaves (X8): in-flight crew come back, a ship
    // with a queued next flight is freed, a carried asteroid is put back on its orbit. The host calls it from inside
    // an impact; a client calls the same from the apply of the host's reason-5 71, so both games end alike.
    public static class FlightCancels
    {
        static readonly MethodInfo ChangeStage =
            AccessTools.Method(typeof(Spacecraft), "ChangeStage", new[] { typeof(Spacecraft.EPhase) });
        static readonly AccessTools.FieldRef<Spacecraft, bool> QueuedAfterChange =
            AccessTools.FieldRefAccess<Spacecraft, bool>("plannMissionAfterStageChange");
        static readonly AccessTools.FieldRef<Spacecraft, TrajectoryObject> HelpTrajectory =
            AccessTools.FieldRefAccess<Spacecraft, TrajectoryObject>("helpTrajectoryObject");
        static readonly AccessTools.FieldRef<Spacecraft, CustomUpdate.ILaunchVehicleInfo> HelpVehicle =
            AccessTools.FieldRefAccess<Spacecraft, CustomUpdate.ILaunchVehicleInfo>("helpLV");
        static readonly AccessTools.FieldRef<Spacecraft, Game.Info.ISpacecraftInfo> HelpShip =
            AccessTools.FieldRefAccess<Spacecraft, Game.Info.ISpacecraftInfo>("helpSC");
        static readonly AccessTools.FieldRef<Spacecraft, SpaceraftCustom3dview> ShipView =
            AccessTools.FieldRefAccess<Spacecraft, SpaceraftCustom3dview>("spaceraftCustom3dView");
        static readonly AccessTools.FieldRef<SpaceraftCustom3dview, UnityEngine.Transform> ParentBeforeFlight =
            AccessTools.FieldRefAccess<SpaceraftCustom3dview, UnityEngine.Transform>("beforeParent");

        // The body a mission's end names: an orbit stands for the body it circles, so a flight to an asteroid's orbit
        // goes with the asteroid.
        static int BodyOf(ObjectInfo o)
        {
            if (o == null) return 0;
            bool orbit = o.objectTypes == Data.EObjectTypes.Orbit && o.parentObjectInfo != null;
            return orbit ? o.parentObjectInfo.id : o.id;
        }

        static bool Gone(ObjectInfo o)
        {
            ObjectInfo body = o != null && o.objectTypes == Data.EObjectTypes.Orbit && o.parentObjectInfo != null
                ? o.parentObjectInfo : o;
            return body != null && body.IsInGameDestroy;
        }

        // Every live mission, as DD-F12's set reads it.
        public static List<GoneRow> Rows()
        {
            var rows = new List<GoneRow>();
            MissionInfoManager missions = MonoBehaviourSingleton<MissionInfoManager>.Instance;
            if (missions == null) return rows;
            foreach (MissionInfo mi in missions.ListMissionInfo)
            {
                if (mi == null || mi.cancel || mi.trajectoryObject == null) continue;
                ObjectInfo start = mi.start, target = mi.target;
                if (start == null || target == null) continue;
                rows.Add(new GoneRow
                {
                    MissionId = mi.id, Agency = GameAgencies.Wire(mi.company), Start = BodyOf(start),
                    Target = BodyOf(target), DepartTicks = mi.DateLaunch.Ticks, ArriveTicks = mi.DateArrive.Ticks,
                    Carried = mi.cargoAll != null && mi.cargoAll.entireAsteroid ? BodyOf(start) : 0,
                    StartDestroyed = Gone(start)
                });
            }
            return rows;
        }

        // The missions an impact or a nuke of the asteroid cancels, in order; raises nothing.
        public static List<TargetCancel> Cancels(int asteroid, long atTicks)
        {
            return TargetGone.Cancels(Rows(), asteroid, atTicks);
        }

        // A client's apply of the host's reason-5 71: the same decision for that one mission, from the end of it the
        // impact destroyed (the landing applied just before it in the impact's group). Null when it is not one.
        public static TargetCancel ForMission(long missionId, long atTicks)
        {
            MissionInfo mi = Find(missionId);
            if (mi == null || mi.cancel) return null;
            foreach (ObjectInfo end in new[] { mi.target, mi.start })
            {
                if (end == null || !Gone(end)) continue;
                foreach (TargetCancel c in Cancels(BodyOf(end), atTicks))
                    if (c.MissionId == missionId) return c;
            }
            return null;
        }

        static MissionInfo Find(long id)
        {
            MissionInfoManager missions = MonoBehaviourSingleton<MissionInfoManager>.Instance;
            if (missions == null) return null;
            foreach (MissionInfo mi in missions.ListMissionInfo)
                if (mi != null && mi.id == id) return mi;
            return null;
        }

        // Cancels one mission of the set. Null = done (a mission already gone counts as done); else why not.
        public static string Cancel(TargetCancel c)
        {
            MissionInfo mi = Find(c.MissionId);
            if (mi == null || mi.cancel) return null;
            var ship = mi.spacecraftInfo2 as Spacecraft;
            if (ship == null)
            {
                // a craft still under construction: the stalled-build cancel's way
                mi.flagCancelAfterLaunchSCNoBuildOnLaunch = true;
                try { mi.Cancel(); } finally { mi.flagCancelAfterLaunchSCNoBuildOnLaunch = false; }
                return mi.cancel ? null : "the mission of a craft not yet built did not cancel";
            }
            bool flying = ship.CurrentPhase == Spacecraft.EPhase.Launch || ship.CurrentPhase == Spacecraft.EPhase.Fly
                || ship.CurrentPhase == Spacecraft.EPhase.Landing;
            if (!flying)
            {
                ship.CancelMission(mi);
                return mi.cancel ? null : "the planned mission did not cancel";
            }
            int crew = c.Lost ? 0 : CrewOf(mi);
            mi.asteroidImpactCancelMissionFlag = true;
            try
            {
                ship.AsteroidImpactCancelMission();
                // a ship with a queued next flight: CancelMission returned early, leaving this one running
                if (!mi.cancel)
                {
                    mi.Cancel();
                    QueuedAfterChange(ship) = false;
                    HelpTrajectory(ship) = null;
                    HelpVehicle(ship) = null;
                    HelpShip(ship) = null;
                    if (ChangeStage != null) ChangeStage.Invoke(ship, new object[] { Spacecraft.EPhase.None });
                }
            }
            finally { mi.asteroidImpactCancelMissionFlag = false; }
            if (!mi.cancel) return "the flying mission did not cancel";
            // vanilla's refund credits crew only for a mission that never launched
            if (crew > 0) GiveCrewBack(mi, ship, crew);
            if (c.RestoresCarried != 0) RestoreCarried(ship, c.RestoresCarried);
            return null;
        }

        static int CrewOf(MissionInfo mi)
        {
            int crew = 0;
            if (mi.cargoAll == null || mi.cargoAll.listCargo == null) return 0;
            foreach (Cargo cargo in mi.cargoAll.listCargo)
                if (cargo != null && cargo.crew && cargo.crewValue > 0) crew += cargo.crewValue;
            return crew;
        }

        static void GiveCrewBack(MissionInfo mi, Spacecraft ship, int crew)
        {
            ObjectInfo place = ship.GetObjectInfoPlan() ?? mi.start;
            Game.ObjectInfoDataScripts.ObjectInfoData data =
                place != null ? place.GetObjectInfoData(mi.company) : null;
            if (data == null || data.CrewResource == null) return;
            data.CrewResource.Value += crew;
        }

        // The carried asteroid's body is switched off for the flight and its view moved under the ship (done at the
        // launch on every game); a cancel puts both back, so it stays where it was on its own orbit.
        static void RestoreCarried(Spacecraft ship, int asteroidId)
        {
            ObjectInfo asteroid = ship.CurrentlyOnThisObject;
            if (asteroid == null || asteroid.id != asteroidId) asteroid = GameAgencies.Body(asteroidId);
            if (asteroid == null || asteroid.IsInGameDestroy) return;
            if (asteroid.NBody != null && asteroid.NBody.GetObjectInfo() == asteroid)
                asteroid.NBody.gameObject.SetActive(true);
            SpaceraftCustom3dview view = ShipView(ship);
            UnityEngine.Transform parent = view != null ? ParentBeforeFlight(view) : null;
            if (parent != null && asteroid.view != null) asteroid.view.transform.SetParent(parent);
            var spin = asteroid.gameObject.GetComponent<FakeObjectRotateAsRealObject>();
            if (spin != null) spin.enabled = true;
        }
    }
}
