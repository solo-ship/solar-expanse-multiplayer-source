using System.Collections.Generic;
using ObjectInfo = Game.Info.ObjectInfo;
using Facility = Game.ObjectInfoDataScripts.Facility;
using MissionInfo = Game.Info.MissionInfo;
using Spacecraft = CustomUpdate.Spacecraft;
using EObjectTypes = Data.EObjectTypes;

namespace SolarExpanseMultiplayer
{
    // The state reader's reads for the print rules in Core (ProductionPrint, FleetPrint): the facts the game holds, the
    // decisions are in Core.
    public static partial class StateReader
    {
        static partial class Fx
        {
            public static readonly StateField<Spacecraft, Spacecraft> Carrier =
                new StateField<Spacecraft, Spacecraft>("multiMissionParent");
            public static readonly StateField<Spacecraft, bool> EndChanged =
                new StateField<Spacecraft, bool>("trajectoryObjectEndObjectInfoChangeObject");
            public static readonly StateField<Spacecraft, ObjectInfo> ImpactTo =
                new StateField<Spacecraft, ObjectInfo>("asteroidImpactChangeTo");
            public static readonly StateField<Spacecraft, ObjectInfo> EndCopy =
                new StateField<Spacecraft, ObjectInfo>("trajectoryObjectEndObjectInfoCopy");
        }

        static long IdOf(ObjectInfo o) { return o != null ? (long)o.id : -1L; }

        // By reference: a facility's Equals is not the game's identity.
        static bool Listed(List<Facility> list, Facility f)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], f)) return true;
            return false;
        }

        // Fills LandsOn, CarrierPhase and GoneOnLanding for a ship in Landing or carried by one.
        static void Landing(Spacecraft sc, ref ShipRow r)
        {
            Spacecraft carrier = Fx.Carrier.Get(sc);
            r.CarrierPhase = carrier != null ? (long)(int)carrier.CurrentPhase : -1L;
            r.LandsOn = r.OnObject;
            if (r.Phase == FleetPrint.Landing)
            {
                r.GoneOnLanding = Gone(sc);
                r.LandsOn = IdOf(Settle(sc, EndOf(sc)));
            }
            else if (r.CarrierPhase == FleetPrint.Landing && !Gone(carrier))
                r.LandsOn = IdOf(Settle(sc, Settle(carrier, EndOf(carrier))));   // riders follow the carrier's body
            if (FleetPrint.Settling(r.Phase, r.CarrierPhase)) r.TrajEnds = TrajectoryEnds(sc);
        }

        // The landing frame removes the mission's label, which completes the mission and destroys its trajectory at the
        // frame's end; a trajectory Unity has already destroyed reads as none anyway.
        static bool TrajectoryEnds(Spacecraft sc)
        {
            if (sc.TrajectoryObject == null) return false;
            MissionInfo mi = sc.GetMissionInfo();
            return mi != null && mi.complete;
        }

        // Spacecraft.TrajectoryObjectEndObjectInfo: the body ChangeStage(None) lands a ship on. Vanilla reads the
        // field, so a trajectory Unity destroyed in the landing frame still names the end body.
        static ObjectInfo EndOf(Spacecraft sc)
        {
            if (Fx.EndChanged.Get(sc)) return Fx.ImpactTo.Get(sc);
            return !ReferenceEquals(sc.TrajectoryObject, null) ? sc.TrajectoryObject.EndObjectInfo
                : sc.CurrentlyOnThisObject;
        }

        // Spacecraft.Update in Landing: a ship with no reuse that lands on a body that is not an orbit is removed.
        static bool Gone(Spacecraft sc)
        {
            ObjectInfo at = Fx.EndCopy.Get(sc);
            if (at == null) at = sc.CurrentlyOnThisObject;
            return at != null && at.objectTypes != EObjectTypes.Orbit && at.objectTypes != EObjectTypes.SolarOrbit
                && sc.spacecraftType != null && sc.spacecraftType.DestroyOnLand;
        }

        // Spacecraft.SetCurrentlyOnThisObject's outcome, without running it: the ships it refuses to move stay where
        // they are, and an orbit ship sent to a body goes to that body's low orbit.
        static ObjectInfo Settle(Spacecraft sc, ObjectInfo to)
        {
            ObjectInfo cur = sc.CurrentlyOnThisObject;
            var t = sc.spacecraftType;
            if (t == null || t.LowOrbitContainer || t.MagneticCatapult || to == cur) return cur;
            if (t.IsInterstellarShip && cur != null && cur.parentObjectInfo != null
                && cur.parentObjectInfo.InterstellarConstructionFacilityFinish) return cur;
            if (t.AsteroidPullingShipFromFacility && cur != null) return cur;
            if (t.IsInterstellarShip && to != null && !to.InterstellarConstructionFacility) return cur;
            if (to != null && to.objectTypes != EObjectTypes.Orbit && t.OrbitSC && !to.AsteroidFromPush
                && to.LowOrbitCustom != null) return to.LowOrbitCustom.GetObjectInfo();
            return to;
        }
    }
}
