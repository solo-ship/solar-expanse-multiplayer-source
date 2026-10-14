using System;
using System.Collections.Generic;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // The events a game cuts its fixed step for besides the day and the impacts (plan "The one step cut"): the
    // checked launches (a facility's launch is judged at L, so every game must stand just before it) and the plain
    // cuts (a landing at an asteroid that a pending impact or pull is about to remove, which must land first).
    // Rebuilt from the game's own missions after every change (Dirty), so every game, host or client, finds the same
    // events; the host cuts at each once with no wait, a client holds (Task 10). One instance per session.
    public sealed class FlightWatches
    {
        public readonly MissionWatch Launches = new MissionWatch();
        public readonly MissionWatch Plain = new MissionWatch();
        readonly HashSet<long> cutDone = new HashSet<long>();
        bool dirty = true;
        // DD-F6, the late launch: the checked launches this game holds for the host's verdict (mission id -> the game
        // time it was put on the watch at). A rebuild keeps them; the glue drops them when the verdict is applied.
        readonly Dictionary<long, long> late = new Dictionary<long, long>();
        int shipCount = -1;

        // Dirty also when the number of ships changed: a craft built (or gone) moves a checked launch on or off the
        // watch (an unbuilt craft has no launch to judge yet).
        public bool IsDirty { get { return dirty || ShipsChanged(); } }
        public void Dirty() { dirty = true; }

        bool ShipsChanged()
        {
            ShipManager ships = ShipManager.Instance;
            return ships != null && ships.ListAllSpaceShip != null && ships.ListAllSpaceShip.Count != shipCount;
        }

        public bool IsLate(long missionId) { return late.ContainsKey(missionId); }

        // Puts a late checked launch on the watch at `now` (never at its old date: nothing waits from then).
        public void AdmitLate(long missionId, long now)
        {
            late[missionId] = now;
            Launches.Add(missionId, now);
        }

        public void DropLate(long missionId)
        {
            late.Remove(missionId);
            Launches.Remove(missionId);
        }

        // A load: nothing watched or cut carries over.
        public void Loaded()
        {
            Launches.Clear();
            Plain.Clear();
            cutDone.Clear();
            late.Clear();
            dirty = true;
        }

        // The missions the cut inputs come from. `now` is the game time: a launch already past is not watched.
        public void Rebuild(long now)
        {
            dirty = false;
            ShipManager shipManager = ShipManager.Instance;
            shipCount = shipManager != null && shipManager.ListAllSpaceShip != null ? shipManager.ListAllSpaceShip.Count : -1;
            Launches.Clear();
            Plain.Clear();
            Manager.MissionInfoManager m = MonoBehaviourSingleton<Manager.MissionInfoManager>.Instance;
            if (m == null || m.ListMissionInfo == null || !HumanAgencies.Active) return;
            // the arrivals a pending impact or pull will remove an asteroid at, by target
            var landsAt = new Dictionary<int, long>();
            foreach (MissionInfo mi in m.ListMissionInfo)
            {
                if (mi == null || mi.cancel || mi.complete || mi.trajectoryObject == null) continue;
                if (mi.cargoAll == null || !mi.cargoAll.entireAsteroid || !HumanAgencies.IsHuman(mi.company)) continue;
                ObjectInfo target = mi.target;
                if (target == null) continue;
                long arrival = mi.DateArrive.Ticks;
                long was;
                if (!landsAt.TryGetValue(target.id, out was) || arrival < was) landsAt[target.id] = arrival;
            }
            foreach (MissionInfo mi in m.ListMissionInfo)
            {
                if (mi == null || mi.cancel || mi.complete || mi.trajectoryObject == null || cutDone.Contains(mi.id))
                    continue;
                // a craft not built yet has no launch to judge: if it is built after its date the launch is late and
                // the host's verdict is waited for then (DD-F6), not at a date nothing happens on
                if (mi.DateLaunch.Ticks > now && IsChecked(mi) && IsBuilt(mi)) Launches.Add(mi.id, mi.DateLaunch.Ticks);
                long at;
                ObjectInfo to = mi.target;
                long arrive = mi.DateArrive.Ticks;
                bool carriesTheAsteroid = mi.cargoAll != null && mi.cargoAll.entireAsteroid;
                if (to != null && !carriesTheAsteroid && arrive > now && landsAt.TryGetValue(to.id, out at) && arrive < at)
                    Plain.Add(mi.id, arrive);
            }
            KeepLate(m);
        }

        // The late launches still on the watch (their mission is live).
        void KeepLate(Manager.MissionInfoManager m)
        {
            var gone = new List<long>();
            foreach (KeyValuePair<long, long> e in late)
            {
                bool live = false;
                foreach (MissionInfo mi in m.ListMissionInfo)
                    if (mi != null && mi.id == e.Key) { live = !mi.cancel && !mi.complete; break; }
                if (live) Launches.Add(e.Key, e.Value); else gone.Add(e.Key);
            }
            foreach (long id in gone) late.Remove(id);
        }

        public static bool IsBuilt(MissionInfo mi) { return mi.spacecraftInfo2 is Spacecraft; }

        // A launch the game judges at the moment it happens: a facility's craft (a space elevator's fake launch
        // vehicle, a catapult's ship).
        public static bool IsChecked(MissionInfo mi)
        {
            var sc = mi.spacecraftInfo2 as Spacecraft;
            if (sc != null && sc.scFromFacility) return true;
            var lv = mi.launchVehicleInfo2 as LaunchVehicle;
            if (lv == null || lv.objectInfo == null || mi.company == null) return false;
            var data = lv.objectInfo.GetObjectInfoData(mi.company);
            return data != null && data.GetFakeLVFromFacilityReverse(lv) != null;
        }

        // The host's next cut at one of these events in (now, end], or StepCut.None. Taking it marks that mission
        // cut, so it is cut for once.
        public long HostCut(long now, long end, long nextDay)
        {
            if (dirty) Rebuild(now);
            long launch, plain;
            long a = StepCut.HostEvent(now, end, nextDay, Launches, out launch);
            long b = StepCut.HostEvent(now, end, nextDay, Plain, out plain);
            if (a == StepCut.None && b == StepCut.None) return StepCut.None;
            if (a <= b)
            {
                Launches.Remove(launch);
                cutDone.Add(launch);
                return a;
            }
            Plain.Remove(plain);
            cutDone.Add(plain);
            return b;
        }
    }
}
