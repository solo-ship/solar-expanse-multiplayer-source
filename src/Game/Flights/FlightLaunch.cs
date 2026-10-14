using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MissionInfo = Game.Info.MissionInfo;
using Spacecraft = CustomUpdate.Spacecraft;
using SpacecraftType = Data.ScriptableObject.SpacecraftType;

namespace SolarExpanseMultiplayer
{
    // The state one checked launch carries from its prefix to its finalizer.
    public sealed class LaunchNote
    {
        public MissionInfo Mission;
        public bool Catapult;
    }

    // M6 Task 8, DD-F6 and X7: a launch that reads a facility at its moment (the space elevator's fake launch vehicle
    // without power or switched off, a magnetic catapult with too few units) is one host decision. The host's
    // RocketLauncher.LaunchSpaceCraft.OnLaunch runs as vanilla and its outcome goes out as MissionLaunched (74) or
    // MissionCancelled (71, reason 1 or 2): what really happened, not what the check said, because the catapult's
    // cancel is a no-op after the launch date. A client takes that outcome inside its own launch step, whatever its
    // own facility says: the game's two cancel calls in OnLaunch go through VerdictCancel, which lets the ship go
    // when the host's 74 is being applied, and a 71 cancels as the host's did even when the client's check passed.
    // The nested class is private, so the class names its target in code (HarmonyX has no attribute that takes a
    // private nested type's name) and needs a [HarmonyPatch] of its own: HarmonyX skips a patch class without one.
    [HarmonyPatch]
    public static class FlightLaunch
    {
        [HarmonyTargetMethod]
        static MethodBase Target()
        {
            Type type = AccessTools.TypeByName("RocketLauncher+LaunchSpaceCraft");
            MethodBase m = type != null ? AccessTools.DeclaredMethod(type, "OnLaunch") : null;
            if (m == null)
                throw new InvalidOperationException("RocketLauncher.LaunchSpaceCraft.OnLaunch was not found");
            return m;
        }

        static class LaunchFields
        {
            public static readonly Type Type = AccessTools.TypeByName("RocketLauncher+LaunchSpaceCraft");
            static readonly FieldInfo SpacecraftField = AccessTools.Field(Type, "spacecraft");
            static readonly FieldInfo WasLaunchField = AccessTools.Field(Type, "wasLaunch");
            static readonly FieldInfo CancelField = AccessTools.Field(Type, "cancel");
            static readonly FieldInfo ReadyField = AccessTools.Field(Type, "readyToCountDown");

            public static Spacecraft Ship(object launch) { return SpacecraftField.GetValue(launch) as Spacecraft; }
            public static bool WasLaunch(object launch) { return (bool)WasLaunchField.GetValue(launch); }
            public static bool Cancelled(object launch) { return (bool)CancelField.GetValue(launch); }
            public static bool Ready(object launch) { return (bool)ReadyField.GetValue(launch); }
        }

        static bool IsFacilityVehicle(MissionInfo mi)
        {
            var vehicle = mi.launchVehicleInfo2 as LaunchVehicle;
            return vehicle != null && vehicle.launchVehicleType != null && vehicle.launchVehicleType.FakeForFacility;
        }

        [HarmonyPrefix]
        static void LaunchStart(object __instance, ref LaunchNote __state)
        {
            if (!HumanAgencies.Active) return;
            try
            {
                Spacecraft ship = LaunchFields.Ship(__instance);
                MissionInfo mi = ship != null ? ship.GetMissionInfo() : null;
                if (mi == null) return;
                FlightState f = HumanAgencies.State.Flights;
                if (HumanAgencies.Host == null)
                {
                    // the host's cancel is made here, whatever this game's facility says
                    if (LaunchVerdictRule.ForcesCancel(ClientOutcome(mi.id))) ForceCancel(ship, mi);
                    return;
                }
                if (HumanAgencies.Applying) return;
                // the game's own guard: only a launch that is about to count down is checked
                if (LaunchFields.Cancelled(__instance) || !LaunchFields.Ready(__instance)
                    || LaunchFields.WasLaunch(__instance)) return;
                bool facility = IsFacilityVehicle(mi);
                SpacecraftType type = ship.GetTypeSpaceCraft();
                bool catapult = !facility && type != null && type.MagneticCatapult && mi.start != null;
                if (!facility && !catapult) return;
                __state = new LaunchNote { Mission = mi, Catapult = catapult };
                f.BeginQuiet();
            }
            catch (Exception e)
            {
                Log.Error("flight: could not note a launch (the clients will wait for it): " + e.GetType().Name);
            }
        }

        // The line the flight tools compare: a mission that has launched, once, on every game.
        [HarmonyPostfix]
        static void LaunchLogged(object __instance)
        {
            if (!HumanAgencies.Active) return;
            try
            {
                Spacecraft ship = LaunchFields.Ship(__instance);
                MissionInfo mi = ship != null ? ship.GetMissionInfo() : null;
                if (mi != null && LaunchFields.WasLaunch(__instance) && !LaunchFields.Cancelled(__instance))
                    FlightLog.Launched(mi);
            }
            catch (Exception e) { Log.Error("flight: could not write a launch: " + e.GetType().Name); }
        }

        // A finalizer, so a launch that throws part way still tells the clients, who are holding for it.
        [HarmonyFinalizer]
        static void LaunchOutcomeRaised(object __instance, LaunchNote __state)
        {
            if (!HumanAgencies.Active || __state == null) return;
            KeyValuePair<string, long>[] cancels = HumanAgencies.State.Flights.EndQuiet();
            IHostDecisions host = HumanAgencies.Host;
            if (host == null) return;
            try
            {
                MissionInfo mi = __state.Mission;
                int kind = LaunchOutcome.Kind(true, mi.cancel, LaunchFields.WasLaunch(__instance));
                if (kind == ActionKind.MissionLaunched) FlightDecisions.Launched(host, mi);
                else if (kind == ActionKind.MissionCancelled)
                    FlightDecisions.CancelledAll(host, cancels, LaunchOutcome.Reason(__state.Catapult));
                else Log.Warn("flight: the checked launch of mission " + mi.id + " neither launched nor cancelled");
            }
            catch (Exception e) { Log.Error("flight: could not report a launch: " + e.GetType().Name); }
        }

        // The two places OnLaunch cancels a mission whose facility is off or whose catapult is short.
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> VerdictPoints(IEnumerable<CodeInstruction> code)
        {
            MethodInfo to = AccessTools.Method(typeof(FlightLaunch), nameof(VerdictCancel));
            return Il.Replace(code, "RocketLauncher.LaunchSpaceCraft.OnLaunch", 2,
                (c, i) => Il.Calls(c[i], typeof(Spacecraft), "CancelMission", 1),
                () => new[] { new CodeInstruction(OpCodes.Call, to) });
        }

        // A client applying the host's launch (74) lets the ship go; anything else is the game's own cancel.
        public static void VerdictCancel(Spacecraft ship, MissionInfo mission)
        {
            if (HumanAgencies.Active && HumanAgencies.Host == null && mission != null
                && LaunchVerdictRule.SkipsCancel(ClientOutcome(mission.id))) return;
            ship.CancelMission(mission);
        }

        // The host's outcome this client has applied for the mission's launch (the launch step's own record, kept for
        // a second ship of the mission too), or none.
        static int ClientOutcome(long missionId)
        {
            CommandSession s = Session.Commands;
            CommandClientGlue g = s != null ? s.ClientGlue : null;
            return g != null ? g.Flights.OutcomeFor(missionId) : LaunchVerdictRule.None;
        }

        // The game's own cancel of a checked launch, as the host's did (OnLaunch's two branches).
        static void ForceCancel(Spacecraft ship, MissionInfo mi)
        {
            if (mi.cancel) return;
            if (IsFacilityVehicle(mi))
            {
                var vehicle = (LaunchVehicle)mi.launchVehicleInfo2;
                if (vehicle.spacecraft == null) return;
                mi.cancelFromRocketLauncher = true;
                try { vehicle.spacecraft.CancelMission(mi); }
                finally { mi.cancelFromRocketLauncher = false; }
            }
            else ship.CancelMission(mi);
        }
    }
}
