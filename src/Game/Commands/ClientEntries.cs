using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoManager = Manager.ObjectInfoManager;
using ObjectSubType = ScriptableObjectScripts.ObjectSubType;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;

namespace SolarExpanseMultiplayer
{
    // A client's side of the host-decided entries the host's game made itself (DD11, DD14, DD15): the counters are
    // already the host's (CounterSync), and each entry runs the same game call the rules hold on a client, for exactly
    // what it names, under the entry's own dice or spawn replay, then checks it ended where the host's did:
    //   * CraftFinished (53): the held build released (CraftCompletionRule.Release), a probe's dice replayed;
    //   * ObservatoryStep, ObservatoryFound, ObservatoryReplan (54, 55, 57): the held observatory call, then the
    //     observatory's state against the host's (ObsAfter);
    //   * SystemFound (56): the survey crossed with the host's seed, name and object id;
    //   * AsteroidSpawned (58): the game's own creation, given the host's spawn;
    //   * ObjectRemoved (59): an impact runs the client's own landing handler inside the entry (ApplyImpactNow
    //     hands it in); a removal no impact made (none in M5: an impact with no mission is a host fault) removes it.
    // Anything else, or any difference, is out of step (a code; the caller logs why). The player kinds' own client
    // sides, which open their dice or spawn replays themselves, are ActionApply's.
    public static class ClientEntries
    {
        static readonly MethodInfo CreateAsteroid = AccessTools.Method(typeof(ObjectInfoManager),
            "CreateDynamicAsteroid");
        static readonly AccessTools.FieldRef<ObjectInfoManager, List<ObjectSubType>> SubTypes =
            AccessTools.FieldRefAccess<ObjectInfoManager, List<ObjectSubType>>("asteroidSubTypeNeedIfNotSpawn");

        public static ApplyOutcome Apply(ActionRequest a, ApplyNumbers given)
        {
            if (!HumanAgencies.Active) return OutOfStep(a, "the rules are off, so no host-decided entry can apply");
            string why;
            switch (a.Kind)
            {
                case ActionKind.CraftFinished: why = Craft(a, given); break;
                case ActionKind.ObservatoryStep:
                case ActionKind.ObservatoryFound:
                case ActionKind.ObservatoryReplan: why = Observatory(a, given); break;
                case ActionKind.SystemFound: why = System(a, given); break;
                case ActionKind.AsteroidSpawned: why = Spawned(given); break;
                case ActionKind.ObjectRemoved: why = Removed(a, given); break;
                default: why = "a host-decided kind no client applies"; break;
            }
            return why != null ? OutOfStep(a, why) : new ApplyOutcome { Code = 0, Applied = a };
        }

        static ApplyOutcome OutOfStep(ActionRequest a, string why)
        {
            Log.Warn("command: " + ActionKind.Name(a.Kind) + " " + a.Agency + ": " + why);
            return new ApplyOutcome { Code = RefuseCodes.NotAllowedHere };
        }

        // ---- 53 ----

        static string Craft(ActionRequest a, ApplyNumbers given)
        {
            CraftCompletionRule.ReleaseResult result = CraftCompletionRule.ReleaseResult.NotFound;
            string run = Dice(given.Draws, () =>
            {
                result = CraftCompletionRule.Release(a.Agency, a.Body, a.Item, a.ItemId);
                return null;
            });
            if (run != null) return run;
            if (result == CraftCompletionRule.ReleaseResult.NotFound)
                return "no build " + a.Item + " #" + a.ItemId + " at body " + a.Body;
            if (result == CraftCompletionRule.ReleaseResult.AlreadyBuilt)
                return "build " + a.Item + " #" + a.ItemId + " finished here before the host's entry";
            return null;
        }

        // ---- 54, 55, 57 ----

        static string Observatory(ActionRequest a, ApplyNumbers given)
        {
            ObservatoryFacility o = ObservatoryWorld.Find(a);
            if (o == null) return "no such observatory here";
            string run = Dice(given.Draws, () =>
            {
                switch (a.Kind)
                {
                    case ActionKind.ObservatoryStep: return ObservatoryWorld.ApplyStep(o);
                    case ActionKind.ObservatoryFound: return ObservatoryWorld.ApplyFound(o);
                    default: return ObservatoryWorld.ApplyReplan(o, GameAgencies.Body(a.ToBody));
                }
            });
            // the host ran these inside its chunk of the day, which set the fresh work it counts from (DayChunk)
            if (run == null && (a.Kind == ActionKind.ObservatoryStep || a.Kind == ActionKind.ObservatoryFound))
                ObservatoryRule.TookHostChunk(o);
            return run ?? AfterProblem(given.After, ObservatoryWorld.AfterOf(o));
        }

        // The observatory's state after the apply against the host's (DD14's after-check).
        static string AfterProblem(ObsAfter host, ObsAfter mine)
        {
            if (host == null) return null;
            if (!ObservatoryRules.Changed(host, mine)) return null;
            return "the observatory is mode " + mine.Mode + " target " + mine.Target + " group " + mine.Group + " days "
                + mine.Days + ", the host's mode " + host.Mode + " target " + host.Target + " group " + host.Group
                + " days " + host.Days;
        }

        // ---- 56 ----

        static string System(ActionRequest a, ApplyNumbers given)
        {
            Company c = GameAgencies.Find(a.Agency);
            if (c == null) return "no agency " + a.Agency;
            // ItemId is the new system's object id here (the observatory that found it may be gone with its target)
            if (a.ItemId < 0 || a.ItemId > int.MaxValue) return "no object id for the system";
            return ObservatoryWorld.ApplySystemFound(c, a.Count, a.Value, given.SystemName, (int)a.ItemId);
        }

        // ---- 58 ----

        // A spawn with no removal behind it: the game's own creation, which the spawn rule gives the host's values.
        static string Spawned(ApplyNumbers given)
        {
            if (given.Spawns == null || given.Spawns.Length == 0) return "no spawn in the entry";
            ObjectInfoManager m = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (m == null || CreateAsteroid == null) return "no asteroid maker";
            return Spawns(given.Spawns, () =>
            {
                foreach (AsteroidSpawn s in given.Spawns)
                {
                    ObjectInfoGroups group = ObservatoryWorld.GroupAt(s.Group);
                    ObjectSubType kind = SubType(m, s.SubType);
                    if (group == null || kind == null) return "no group " + s.Group + " or kind " + s.SubType + " here";
                    var args = new object[] { group, kind, null, null, -1 };
                    try { CreateAsteroid.Invoke(m, args); }
                    catch (Exception e) { return "the asteroid maker threw " + (e.InnerException ?? e).GetType().Name; }
                }
                return null;
            });
        }

        static ObjectSubType SubType(ObjectInfoManager m, string id)
        {
            List<ObjectSubType> kinds = SubTypes(m);
            for (int i = 0; kinds != null && i < kinds.Count; i++)
                if (kinds[i] != null && kinds[i].ID == id) return kinds[i];
            return null;
        }

        // ---- 59 ----

        static string Removed(ActionRequest a, ApplyNumbers given)
        {
            ObjectInfo o = GameAgencies.Body(a.Body);
            if (given.RunBranch != null)
            {
                // the impact: the client's own landing handler, here inside the entry, with the host's spawns
                string said = null;
                string run = Spawns(given.Spawns, () =>
                    AsteroidSpawnRule.RunImpact(a.ItemId, () => { said = given.RunBranch(); }) ?? said);
                if (run != null) return run;
                return o != null && !o.IsInGameDestroy ? "the impact left asteroid " + a.Body + " in place" : null;
            }
            // an impact is applied inside the client's own landing (ApplyImpactNow) and never here
            if (a.ItemId >= 0) return "an impact's entry outside its landing";
            if (o == null || o.IsInGameDestroy) return "no object " + a.Body + " to remove";
            return Spawns(given.Spawns, () =>
            {
                o.VirtualDestroy(null);
                return null;
            });
        }

        // ---- the replays ----

        // run under the entry's dice: null when it ran and took exactly the host's draws.
        static string Dice(ObsDraw[] draws, Func<string> run)
        {
            ObservatoryDrawState dice = HumanAgencies.Dice;
            if (dice == null) return "no observatory rules are running";
            if (!dice.BeginReplay(draws)) return "an observatory replay is open already";
            string said = null, replay;
            try { said = run(); }
            catch (Exception e)
            {
                Log.Error("command: the game threw in a host entry: " + ExceptionText.Chain(e));
                said = "the game threw " + e.GetType().Name;
            }
            finally { replay = dice.EndReplay(); }
            return said ?? replay;
        }

        // run under the entry's spawns: null when the game made exactly the host's asteroids.
        static string Spawns(AsteroidSpawn[] spawns, Func<string> run)
        {
            AsteroidSpawnState state = HumanAgencies.Spawns;
            if (state == null) return "no asteroid rules are running";
            if (!state.BeginReplay(spawns)) return "an asteroid replay is open already";
            string said = null, replay;
            try { said = run(); }
            catch (Exception e)
            {
                Log.Error("command: the game threw in a host entry: " + ExceptionText.Chain(e));
                said = "the game threw " + e.GetType().Name;
            }
            finally { replay = state.EndReplay(); }
            return said ?? replay;
        }
    }
}
