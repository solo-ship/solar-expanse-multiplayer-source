using System;
using System.Collections.Generic;
using MissionInfo = Game.Info.MissionInfo;

namespace SolarExpanseMultiplayer
{
    // What the host raises into the stream when its own game launches, cancels, pulls or lands (plan "Game seams":
    // IHostDecisions gains MissionLaunched, MissionCancelled, LandingCraft and FlightPlanned). They are built here on
    // the interface M5 already has, Decided, so the interface and the session that implements it stay as they are.
    // Every one is a request the host's game has already made the change for: its apply changes nothing on the host.
    public static class FlightDecisions
    {
        public static void Launched(IHostDecisions host, MissionInfo mission)
        {
            Raise(host, new ActionRequest { Kind = ActionKind.MissionLaunched, Agency = Agency(mission),
                ItemId = mission.id }, null);
        }

        public static void Cancelled(IHostDecisions host, string agency, long missionId, int reason)
        {
            Raise(host, new ActionRequest { Kind = ActionKind.MissionCancelled, Agency = agency, ItemId = missionId,
                Value = reason }, null);
            Log.Info("flight: cancel raised for mission " + missionId + " (" + CancelReasons.Name(reason) + ")");
        }

        // Each cancel a quiet host call made (agency wire id, mission id), in the order the game made them.
        public static void CancelledAll(IHostDecisions host, KeyValuePair<string, long>[] list, int reason)
        {
            if (host == null || list == null) return;
            foreach (KeyValuePair<string, long> c in list) Cancelled(host, c.Key, c.Value, reason);
        }

        // An orbit pull: the asteroid moved into the orbit, with the draws and the orbit id the host made. count is
        // how many entries directly after it belong to its apply (normally none).
        public static void Pulled(IHostDecisions host, string agency, int asteroid, int orbit, long missionId,
            AsteroidPull pull, int count)
        {
            var a = new ActionRequest { Kind = ActionKind.AsteroidPulled, Agency = agency, Body = asteroid,
                ToBody = orbit, ItemId = missionId, Count = count };
            Raise(host, a, new ApplyNumbers { Pull = pull });
            Log.Info("flight: pull " + asteroid + " into " + orbit + " by mission " + missionId);
        }

        // A ship the host's landing built: its game-wide counters before and after, which every client sets before
        // it makes the ship itself.
        public static void LandingCraft(IHostDecisions host, string agency, int body, string shipType, long missionId,
            long spacecraftBefore, long spacecraftAfter, long vehicleBefore, long vehicleAfter)
        {
            var a = new ActionRequest { Kind = ActionKind.LandingCraft, Agency = agency, Body = body, Item = shipType,
                ItemId = missionId };
            var n = new ApplyNumbers
            {
                Counters = new[]
                {
                    new CounterRow { Which = CounterKind.SpacecraftCount, Before = spacecraftBefore,
                        After = spacecraftAfter },
                    new CounterRow { Which = CounterKind.LaunchVehicleCount, Before = vehicleBefore,
                        After = vehicleAfter }
                }
            };
            Raise(host, a, n);
        }

        // A plan the host's own game made (a route's leg, a rival's flight, the end-game flight), read after its
        // CreateFly by Task 9's reader.
        public static void Planned(IHostDecisions host, ActionRequest planned, ApplyNumbers numbers)
        {
            Raise(host, planned, numbers);
        }

        static string Agency(MissionInfo mission)
        {
            return mission != null && mission.company != null ? GameAgencies.Wire(mission.company) : null;
        }

        static void Raise(IHostDecisions host, ActionRequest a, ApplyNumbers n)
        {
            if (host == null) return;
            try { host.Decided(a, n ?? new ApplyNumbers()); }
            catch (Exception e)
            {
                Log.Error("flight: could not raise " + ActionKind.Name(a.Kind) + ": " + e.GetType().Name);
            }
        }
    }
}
