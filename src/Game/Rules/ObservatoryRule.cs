using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Company = Game.Company;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfoManager = Manager.ObjectInfoManager;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;
using SystemDiscoveryData = Data.SystemDiscoveryData;

namespace SolarExpanseMultiplayer
{
    // What the host's wrapper of an observatory call remembers between the call's start and end.
    public sealed class ObsWrap
    {
        public ActionRequest Request;
        public ObsAfter Before;
        public bool OnlyIfChanged;
    }

    // What the host's wrapper of a survey call remembers.
    public sealed class SurveyWrap
    {
        public int Count;
    }

    // DD14: observatory outcomes are host-decided, for every agency (docs/research/observatories.md).
    //   * The dice: the five places the game draws for an observatory go through ObservatoryRule.Draw, which takes the
    //     value from the mod's generator on the host (recording it into the entry's record), from the entry on a
    //     client, and from UnityEngine.Random only outside a session. No game touches the shared generator for an
    //     observatory, so observatories cannot split it.
    //   * The holds: a client never finishes a step, finds an asteroid, re-plans or crosses a survey line by itself;
    //     the host's entry makes it run the game's same method (ObservatoryWorld.Apply*). A re-plan a removal causes
    //     is held on every game and becomes the host's own entry.
    //   * The host's wrappers: each of those calls runs as the game has it inside a record, and what it did becomes
    //     the entry (a step 54, a find 55, a new system 56, a re-plan 57) with its dice and the state after.
    //   * New systems: generated from a seed the host draws, put in the game's own list under a restored generator;
    //     a system that already exists is credited again but never made twice (Q-O4).
    // Every patch is a no-op outside a session. A hook that cannot patch (the transpilers insist on the five call
    // sites) stops the session; there is no vanilla fallback.
    [HarmonyPatch]
    public static class ObservatoryRule
    {
        // Harmony runs this before it patches: the game lookups ObservatoryWorld keeps (fields, private methods) are
        // resolved here, so a renamed one refuses the hook at the start of the session instead of throwing a
        // TypeInitializationException in the middle of it.
        [HarmonyPrepare]
        static bool Prepare()
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ObservatoryWorld).TypeHandle);
            return true;
        }

        // ---- the dice (R1 to R5) ----

        // The seam: UnityEngine.Random.Range(min, max) with max exclusive, for observatory site `site`.
        public static int Draw(int min, int max, int site, int reference)
        {
            if (!HumanAgencies.Active) return UnityEngine.Random.Range(min, max);
            ObservatoryDrawState dice = HumanAgencies.Dice;
            dice.Loading = Manager.LoadSaveManager.OnExtractAllFromSaveData;
            return dice.Range(min, max, site, reference);
        }

        // R1: which unexplored deposit of the target. The checked id is the whole candidate list, each deposit by its
        // resource and its position among the target's rows (two deposits of one resource differ by position).
        public static int DrawDeposit(int min, int max, List<RowExploredResourcesData> rows, ObservatoryFacility obs)
        {
            if (!HumanAgencies.Active) return UnityEngine.Random.Range(min, max);
            var ids = new List<string>();
            foreach (RowExploredResourcesData r in rows)
                ids.Add((r != null && r.ResourceType != null ? r.ResourceType.ID : "")
                    + "@" + ObservatoryWorld.RowPosition(obs, r));
            return Draw(min, max, ObservatoryDraws.SiteDeposit, ObservatoryRules.RefOfIds(ids));
        }

        // R3: which body of a group (the game's PickRandom over its candidate list).
        public static ObjectInfoData PickGroupBody(IList<ObjectInfoData> list)
        {
            if (!HumanAgencies.Active) return list[UnityEngine.Random.Range(0, list.Count)];
            int reference = ObservatoryRules.RefOfBodies(Bodies(list));
            return list[Draw(0, list.Count, ObservatoryDraws.SiteGroupBody, reference)];
        }

        // R4: which asteroid to look for.
        public static int DrawAsteroid(int min, int max, List<ObjectInfoData> list)
        {
            if (!HumanAgencies.Active) return UnityEngine.Random.Range(min, max);
            return Draw(min, max, ObservatoryDraws.SiteAsteroid, ObservatoryRules.RefOfBodies(Bodies(list)));
        }

        static List<int> Bodies(IList<ObjectInfoData> list)
        {
            var ids = new List<int>();
            foreach (ObjectInfoData d in list) ids.Add(d != null && d.ObjectInfo != null ? d.ObjectInfo.id : 0);
            return ids;
        }

        static bool IsIntRange(CodeInstruction i)
        {
            if (!Il.Calls(i, typeof(UnityEngine.Random), "Range", 2)) return false;
            ParameterInfo[] p = ((MethodBase)i.operand).GetParameters();
            return p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(int);
        }

        static CodeInstruction[] SiteCall(int site)
        {
            return new[]
            {
                new CodeInstruction(OpCodes.Ldc_I4, site), new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ObservatoryRule), nameof(Draw)))
            };
        }

        static bool IsCount(CodeInstruction i)
        {
            var m = i.operand as MethodBase;
            return (i.opcode == OpCodes.Callvirt || i.opcode == OpCodes.Call) && m != null && m.Name == "get_Count"
                && m.GetParameters().Length == 0;
        }

        // `list[Random.Range(0, list.Count)]`: the call's own arguments end `ldloc list; callvirt get_Count`, so the
        // list is read again for the seam, which hashes it. `found` keeps that load between the two lambdas.
        static Func<List<CodeInstruction>, int, bool> ListDraw(CodeInstruction[] found)
        {
            return (c, i) =>
            {
                bool hit = IsIntRange(c[i]) && i >= 2 && IsCount(c[i - 1]) && c[i - 2].IsLdloc();
                if (hit) found[0] = c[i - 2];
                return hit;
            };
        }

        // `withThis`: the deposit seam also takes the observatory (ldarg.0) for the rows' positions.
        static CodeInstruction[] ListSeam(CodeInstruction[] found, string seam, bool withThis)
        {
            var seamCall = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ObservatoryRule), seam));
            if (!withThis) return new[] { new CodeInstruction(found[0]), seamCall };
            return new[] { new CodeInstruction(found[0]), new CodeInstruction(OpCodes.Ldarg_0), seamCall };
        }

        [HarmonyPatch(typeof(ObservatoryFacility), "RandomResourceToExplore")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> DepositSite(IEnumerable<CodeInstruction> code)
        {
            var found = new CodeInstruction[1];
            return Il.Replace(code, "ObservatoryFacility.RandomResourceToExplore", 1, ListDraw(found),
                () => ListSeam(found, nameof(DrawDeposit), true));
        }

        [HarmonyPatch(typeof(ObservatoryFacility), "ScheduleNextExplorationStep")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> StepDaysSite(IEnumerable<CodeInstruction> code)
        {
            return Il.Replace(code, "ObservatoryFacility.ScheduleNextExplorationStep", 1, (c, i) => IsIntRange(c[i]),
                () => SiteCall(ObservatoryDraws.SiteStepDays));
        }

        [HarmonyPatch(typeof(ObservatoryFacility), "FindNextObjectForExplorationFromObjectInfoGroups")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> GroupBodySite(IEnumerable<CodeInstruction> code)
        {
            return Il.Replace(code, "ObservatoryFacility.FindNextObjectForExplorationFromObjectInfoGroups", 1,
                (c, i) => Il.Calls(c[i], typeof(Extensions.MyExtensions), "PickRandom", 1),
                () => new[] { new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(ObservatoryRule), nameof(PickGroupBody))) });
        }

        [HarmonyPatch(typeof(ObservatoryFacility), "RandomizeNextObjectToDiscover")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> AsteroidSite(IEnumerable<CodeInstruction> code)
        {
            var found = new CodeInstruction[1];
            return Il.Replace(code, "ObservatoryFacility.RandomizeNextObjectToDiscover", 1, ListDraw(found),
                () => ListSeam(found, nameof(DrawAsteroid), false));
        }

        [HarmonyPatch(typeof(ObservatoryFacility), "FindNextObjectForDiscovery")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> DiscoveryDaysSite(IEnumerable<CodeInstruction> code)
        {
            return Il.Replace(code, "ObservatoryFacility.FindNextObjectForDiscovery", 1, (c, i) => IsIntRange(c[i]),
                () => SiteCall(ObservatoryDraws.SiteDiscoveryDays));
        }

        // ---- the host's wrappers and the clients' holds ----

        // A client runs a held call only inside the host's entry for that observatory (ObservatoryWorld.Apply*). The
        // host runs it as the game has it, in a record, unless an entry's apply or its own re-plan run is going.
        // Returns false to skip the game's method.
        static bool Begin(ObservatoryFacility obs, int kind, bool onlyIfChanged, Action<ObsWrap> detail,
            ref ObsWrap state)
        {
            RuleState st = HumanAgencies.State;
            if (HumanAgencies.Host == null) return st.Replanning == obs;
            if (HumanAgencies.Applying || st.Replanning != null) return true;
            ActionRequest named = ObservatoryWorld.NameOf(obs, kind);
            if (named == null)
            {
                Fault("a " + ActionKind.Name(kind) + " for an observatory that cannot be named");
                return true;
            }
            var wrap = new ObsWrap { Request = named, OnlyIfChanged = onlyIfChanged };
            if (detail != null) detail(wrap);
            if (!st.Dice.BeginRecord())
            {
                Fault("a " + ActionKind.Name(kind) + " while another record is open");
                return true;
            }
            state = wrap;
            return true;
        }

        // What the host cannot tell the clients stops the stream: they rejoin fully (and the log says why).
        static void Fault(string why)
        {
            Log.Error("observatory: " + why + "; the stream is reset");
            IHostDecisions host = HumanAgencies.Host;
            if (host == null) return;
            try { host.HostFault("observatory: " + why); }
            catch (Exception e) { Log.Error("observatory: could not raise a host fault: " + e.GetType().Name); }
        }

        static void End(ObservatoryFacility obs, ObsWrap state)
        {
            if (state == null) return;
            RuleState st = HumanAgencies.State;
            ObsDraw[] draws = st.Dice.EndRecord();
            ObsAfter after = ObservatoryWorld.AfterOf(obs);
            if (!ObservatoryRules.RaisesReplan(state.OnlyIfChanged, draws, state.Before, after)) return;
            Raise(state.Request, draws, after);
            RunListenerNotes();
        }

        static void Raise(ActionRequest request, ObsDraw[] draws, ObsAfter after)
        {
            IHostDecisions host = HumanAgencies.Host;
            if (host == null) return;
            try { host.Decided(request, new ApplyNumbers { Draws = draws, After = after }); }
            catch (Exception e) { Log.Error("observatory: could not raise " + ActionKind.Name(request.Kind) + ": "
                + e.GetType().Name); }
        }

        // 54: an examine step done.
        [HarmonyPatch(typeof(ObservatoryFacility), "OnScheduledTimeOfResourceExploration")]
        [HarmonyPrefix]
        static bool StepPrefix(ObservatoryFacility __instance, ref ObsWrap __state)
        {
            if (!HumanAgencies.Active) return true;
            return Begin(__instance, ActionKind.ObservatoryStep, false, null, ref __state);
        }

        [HarmonyPatch(typeof(ObservatoryFacility), "OnScheduledTimeOfResourceExploration")]
        [HarmonyFinalizer]
        static void StepFinalizer(ObservatoryFacility __instance, ObsWrap __state)
        {
            if (!HumanAgencies.Active) return;
            End(__instance, __state);
        }

        // 55: an asteroid found.
        [HarmonyPatch(typeof(ObservatoryFacility), "OnObjectDiscovered")]
        [HarmonyPrefix]
        static bool FoundPrefix(ObservatoryFacility __instance, ref ObsWrap __state)
        {
            if (!HumanAgencies.Active) return true;
            return Begin(__instance, ActionKind.ObservatoryFound, false, null, ref __state);
        }

        [HarmonyPatch(typeof(ObservatoryFacility), "OnObjectDiscovered")]
        [HarmonyFinalizer]
        static void FoundFinalizer(ObservatoryFacility __instance, ObsWrap __state)
        {
            if (!HumanAgencies.Active) return;
            End(__instance, __state);
        }

        // 57, the listener path: another source explored the target's last deposit, and the game re-plans at once.
        // The host raises it when the re-plan changed anything; a step running at the same moment (its own deposit
        // change reaches this listener) hands it over to run on its own record right after that step's entry.
        [HarmonyPatch(typeof(ObservatoryFacility), "AnyResourceExploredByOtherProbeOrObservatory")]
        [HarmonyPrefix]
        static bool ListenerPrefix(ObservatoryFacility __instance, RowExploredResourcesData rowExploredResourcesData,
            ref ObsWrap __state)
        {
            if (!HumanAgencies.Active) return true;
            RuleState st = HumanAgencies.State;
            if (HumanAgencies.Host == null)
            {
                if (st.Replanning == __instance) return true;
                if (rowExploredResourcesData != null && rowExploredResourcesData.ExploredFullyOrPreliminary)
                    ObservatoryWorld.Unsubscribe(__instance, rowExploredResourcesData);
                return false;
            }
            if (HumanAgencies.Applying || st.Replanning != null) return true;
            ObjectInfo target = ObservatoryWorld.TargetBody(__instance);
            if (st.Dice.Recording)
            {
                // the game's handler acts only for an explored row that leaves this observatory nothing to examine
                bool explored = rowExploredResourcesData != null && rowExploredResourcesData.ExploredFullyOrPreliminary;
                int left = ObservatoryWorld.UnexploredCount(__instance);
                if (target != null && st.Notes.Count < NoteCap && st.Listeners.Note(__instance, explored, left))
                    st.Notes.Add(new ReplanNote { Observatory = __instance, Row = rowExploredResourcesData,
                        ToBody = target.id });
                return !(explored && left == 0);
            }
            bool run = Begin(__instance, ActionKind.ObservatoryReplan, true, w =>
            {
                w.Before = ObservatoryWorld.AfterOf(__instance);
                if (target != null) w.Request.ToBody = target.id;
            }, ref __state);
            if (__state != null && __state.Request.ToBody < 1) Drop(ref __state);
            return run;
        }

        [HarmonyPatch(typeof(ObservatoryFacility), "AnyResourceExploredByOtherProbeOrObservatory")]
        [HarmonyFinalizer]
        static void ListenerFinalizer(ObservatoryFacility __instance, ObsWrap __state)
        {
            if (!HumanAgencies.Active) return;
            End(__instance, __state);
        }

        // A re-plan with no examined body cannot be named (ToBody is required): close its record, raise nothing.
        static void Drop(ref ObsWrap state)
        {
            HumanAgencies.Dice.EndRecord();
            Fault("a re-plan with no target to name");
            state = null;
        }

        // 57, the destroyed target: held on every game (it re-plans and draws inside VirtualDestroy, where no entry
        // is open). The host notes it for the removal's own entries to run; a client's apply of the 57 runs it.
        [HarmonyPatch(typeof(ObservatoryFacility), "ObjectInfoOnvirtualDestroy")]
        [HarmonyPrefix]
        static bool DestroyPrefix(ObservatoryFacility __instance, ObjectInfo obj)
        {
            if (!HumanAgencies.Active) return true;
            RuleState st = HumanAgencies.State;
            if (st.Replanning == __instance) return true;
            if (HumanAgencies.Host == null || obj == null) return false;
            ObjectInfo target = ObservatoryWorld.TargetBody(__instance);
            int mode = ObservatoryWorld.Mode(__instance);
            // the game's handler acts only for its own target while it works on it
            if (target == null || obj != target || (mode != ObsMode.Exploration && mode != ObsMode.Discovery))
                return false;
            if (st.Notes.Count < NoteCap)
                st.Notes.Add(new ReplanNote { Observatory = __instance, Destroyed = obj, ToBody = obj.id });
            return false;
        }

        // more than this is a runaway, not a world: nothing is noted past it
        const int NoteCap = 4096;

        // The survey's line: a client's own work never crosses it (the cost stays just above zero); the host's
        // crossing becomes a SystemFound the clients apply with the seed, name and object id.
        [HarmonyPatch(typeof(Company), "DecreaseExoplanetSurveyCost")]
        [HarmonyPrefix]
        static bool SurveyPrefix(Company __instance, double power, ref bool __result, ref SurveyWrap __state)
        {
            if (!HumanAgencies.Active) return true;
            RuleState st = HumanAgencies.State;
            if (HumanAgencies.Host == null)
            {
                if (st.Pending != null && st.Pending.Given) return true;
                ObservatoryWorld.SetSurveyCost(__instance,
                    ObservatoryRules.HeldSurveyCost(ObservatoryWorld.SurveyCostOf(__instance), power));
                __result = true;
                return false;
            }
            st.Pending = new PendingSystem { Index = __instance.DiscoveredSystemsCount };
            __state = new SurveyWrap { Count = __instance.DiscoveredSystemsCount };
            return true;
        }

        [HarmonyPatch(typeof(Company), "DecreaseExoplanetSurveyCost")]
        [HarmonyFinalizer]
        static void SurveyFinalizer(Company __instance, SurveyWrap __state)
        {
            if (!HumanAgencies.Active) return;
            if (__state == null || HumanAgencies.Host == null) return;
            RuleState st = HumanAgencies.State;
            PendingSystem p = st.Pending;
            st.Pending = null;
            if (__instance.DiscoveredSystemsCount <= __state.Count) return;   // no crossing
            ObservatoryFacility obs = st.Stepping ?? ObservatoryWorld.SurveyingObservatory(__instance);
            ActionRequest a = obs != null ? ObservatoryWorld.NameOf(obs, ActionKind.SystemFound) : null;
            if (a == null || a.Ship >= 0 || p == null)
            {
                Fault(__instance.ID + " found a system that no observatory can announce");
                return;
            }
            a.ItemId = p.ObjectId;
            a.Count = p.Index;
            a.Value = p.Generated ? p.Seed : 0;
            RaiseSystem(a, p.Generated ? p.Name : null);
        }

        static void RaiseSystem(ActionRequest a, string systemName)
        {
            IHostDecisions host = HumanAgencies.Host;
            if (host == null) return;
            try { host.Decided(a, new ApplyNumbers { SystemName = systemName }); }
            catch (Exception e) { Log.Error("observatory: could not raise SystemFound: " + e.GetType().Name); }
        }

        // MyUpdate marks the observatory that is stepping, so a survey crossing inside it can be named.
        [HarmonyPatch(typeof(ObservatoryFacility), nameof(ObservatoryFacility.MyUpdate))]
        [HarmonyPrefix]
        static void SteppingStart(ObservatoryFacility __instance)
        {
            if (!HumanAgencies.Active) return;
            if (HumanAgencies.Host != null) HumanAgencies.State.Stepping = __instance;
        }

        [HarmonyPatch(typeof(ObservatoryFacility), nameof(ObservatoryFacility.MyUpdate))]
        [HarmonyFinalizer]
        static void SteppingEnd()
        {
            if (!HumanAgencies.Active) return;
            if (HumanAgencies.Host != null) HumanAgencies.State.Stepping = null;
        }

        static readonly AccessTools.FieldRef<ObservatoryFacility, DateTime> LastChunk =
            AccessTools.FieldRefAccess<ObservatoryFacility, DateTime>("prevTime");
        static readonly AccessTools.FieldRef<Manager.TimeController, DateTime> NextDay =
            AccessTools.FieldRefAccess<Manager.TimeController, DateTime>("nextDay");

        // Every game ends an observatory's work chunks on the day boundaries (DayChunk), host and clients alike. A new
        // observatory (its prevTime never set) counts from its build day's boundary.
        [HarmonyPatch(typeof(ObservatoryFacility), nameof(ObservatoryFacility.MyUpdate))]
        [HarmonyPrefix]
        static void ChunkStart(ObservatoryFacility __instance, ref long __state)
        {
            if (!HumanAgencies.Active) return;
            __state = long.MinValue;
            Manager.TimeController tc = MonoBehaviourSingleton<Manager.TimeController>.Instance;
            if (tc == null || GravityEngine.instance == null) return;
            long after;
            long handed = DayChunk.Before(LastChunk(__instance).Ticks, tc.CurrentTime.Ticks, NextDay(tc).Ticks,
                tc.StartDateTime.Ticks, out after);
            LastChunk(__instance) = InRange(handed);
            __state = after;
        }

        [HarmonyPatch(typeof(ObservatoryFacility), nameof(ObservatoryFacility.MyUpdate))]
        [HarmonyFinalizer]
        static void ChunkEnd(ObservatoryFacility __instance, long __state)
        {
            if (!HumanAgencies.Active) return;
            if (__state > 0) LastChunk(__instance) = InRange(__state);
        }

        // A client's apply of a step or a find the host ran inside its own chunk: that chunk counts as taken here too.
        public static void TookHostChunk(ObservatoryFacility o)
        {
            Manager.TimeController tc = MonoBehaviourSingleton<Manager.TimeController>.Instance;
            if (o == null || tc == null || GravityEngine.instance == null) return;
            LastChunk(o) = InRange(DayChunk.AfterHostChunk(LastChunk(o).Ticks, tc.CurrentTime.Ticks,
                NextDay(tc).Ticks));
        }

        // DayChunk keeps its times inside the clock's range; a time outside it must not throw inside MyUpdate (the
        // throw skips the rest of that body's update on one game only).
        static DateTime InRange(long ticks)
        {
            return new DateTime(Math.Max(0, Math.Min(ticks, DateTime.MaxValue.Ticks)));
        }

        // ---- new star systems (R6, R7) ----

        // The game generates a system when the survey reaches a new index, from the shared generator. The host
        // draws a seed and generates under it with the shared state put back; a client generates only inside the
        // host's SystemFound, from that entry's seed, and takes its name.
        [HarmonyPatch(typeof(ObjectInfoManager), "GetSystemToDiscover")]
        [HarmonyPrefix]
        static bool SystemPrefix(ObjectInfoManager __instance, int index, ref SystemDiscoveryData __result)
        {
            if (!HumanAgencies.Active) return true;
            List<SystemDiscoveryData> list = __instance.SystemsToDiscover;
            if (index < list.Count) return true;   // already there (a predefined one, or another agency's)
            RuleState st = HumanAgencies.State;
            PendingSystem p = st.Pending;
            if (HumanAgencies.Host != null)
            {
                if (index != list.Count)
                {
                    string why = "observatory: system " + index + " asked for with " + list.Count + " made";
                    Log.Error(why);
                    HumanAgencies.Host.HostFault(why);
                    __result = null;
                    return false;
                }
                if (p == null || p.Index != index) p = st.Pending = new PendingSystem { Index = index };
                p.Seed = st.SystemRng.NextSeed();
                p.Generated = true;
            }
            else if (p == null || !p.Given || p.Index != index || index != list.Count)
            {
                Log.Error("observatory: a client was asked to generate system " + index + " outside the host's entry");
                __result = null;
                return false;
            }
            var config = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance
                .ExoplanetSurveyConfig;
            UnityEngine.Random.State keep = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(p.Seed);
                var made = config.GenerateSolarSystemData();
                if (HumanAgencies.Host != null) p.Name = made.Item1;
                string name = p.Given && p.Name != null ? p.Name : made.Item1;
                // every planet and moon is `<system name><letter>` from the game's time-seeded generator: they
                // all take the host's system name, so the same bodies have the same names on every game
                if (name != made.Item1) ObservatoryWorld.RenameBodies(made.Item2, made.Item1, name);
                list.Add(new SystemDiscoveryData { CustomSystemName = name, SolarSystemData = made.Item2 });
            }
            finally { UnityEngine.Random.state = keep; }
            __result = list[index];
            return false;
        }

        // The world object of a system. The id: the host's choice, given to every game through the method's own
        // idObjectInfo (the path the load uses). A system another agency already realised is not made twice: the
        // second agency counts it and the one object stays (the user's ruling on Q-O4).
        [HarmonyPatch(typeof(ObjectInfoManager), "NewSystemDiscover")]
        [HarmonyPrefix]
        static bool NewSystemPrefix(ObjectInfoManager __instance, SystemDiscoveryData currentlyDiscoveredSystem,
            ref int idObjectInfo, ref ObjectInfo __result)
        {
            if (!HumanAgencies.Active) return true;
            PendingSystem p = HumanAgencies.State.Pending;
            ObjectInfo existing = ExistingSystem(__instance, currentlyDiscoveredSystem);
            if (existing != null)
            {
                __result = existing;
                if (p != null && p.Given && p.ObjectId > 0 && p.ObjectId != existing.id)
                    p.Problem = "the system is on the map as " + existing.id + ", the host's is " + p.ObjectId;
                else if (p != null) p.ObjectId = existing.id;
                Log.Info("observatory: system " + currentlyDiscoveredSystem.SystemName + " is on the map already as "
                    + existing.id + "; counted, not made again");
                return false;
            }
            if (HumanAgencies.Host == null && p != null && p.Given && p.ObjectId > 0) idObjectInfo = p.ObjectId;
            return true;
        }

        [HarmonyPatch(typeof(ObjectInfoManager), "NewSystemDiscover")]
        [HarmonyPostfix]
        static void NewSystemPostfix(ObjectInfo __result)
        {
            if (!HumanAgencies.Active) return;
            PendingSystem p = HumanAgencies.State.Pending;
            if (p == null || __result == null) return;
            if (p.Given && p.ObjectId > 0 && p.ObjectId != __result.id)
                p.Problem = "the system's object got id " + __result.id + ", the host's is " + p.ObjectId;
            p.ObjectId = __result.id;
        }

        static ObjectInfo ExistingSystem(ObjectInfoManager mgr, SystemDiscoveryData data)
        {
            if (data == null) return null;
            if (data.IdAsObjectInfo != -1)
            {
                ObjectInfo o = mgr.GetByID(data.IdAsObjectInfo);
                if (o != null) return o;
            }
            ObjectInfoManager.InGameChangeSystemData made = ObservatoryWorld.CreatedObjects(mgr);
            if (made == null || made.listCreatedObject == null) return null;
            foreach (ObjectInfoManager.InGameChangeSystemDataCreatedObject e in made.listCreatedObject)
            {
                if (e.createdObjectType != ObjectInfoManager.ECreatedObjectType.FakePlanetarySystem
                    || e.systemDiscoveryData == null) continue;
                bool same = ReferenceEquals(e.systemDiscoveryData, data) || (!string.IsNullOrEmpty(data.SystemName)
                    && e.systemDiscoveryData.SystemName == data.SystemName);
                if (same) return mgr.GetByID(e.idObjectInfo);
            }
            return null;
        }

        // ---- what the removal's entries and the apply call (the game-facing side of Task 10a and 10b) ----

        // The re-plans noted since the last call, in the order the game's event called the handlers. The host's
        // removal code (a ForgetObject apply, an impact) takes them after the removal.
        // Only the destroyed-target notes are a removal's (a listener note belongs to the step that caused it and
        // runs after that step's entry); every note that cannot be named is a host fault, so the count a 59
        // announces is exactly the number of 57s that follow it.
        public static List<ReplanNote> TakeNotes()
        {
            RuleState st = HumanAgencies.State;
            var taken = new List<ReplanNote>();
            if (st == null) return taken;
            foreach (ReplanNote n in st.Notes)
            {
                if (n.Destroyed == null)
                {
                    // a listener note belongs to the step that caused it and is run by it; one left here is stranded
                    st.Listeners.Release(n.Observatory);
                    Fault("a listener re-plan was left unrun");
                    continue;
                }
                if (n.ToBody >= 1 && ObservatoryWorld.NameOf(n.Observatory, ActionKind.ObservatoryReplan) != null)
                    taken.Add(n);
                else Fault("a re-plan that cannot be named");
            }
            st.Notes.Clear();
            return taken;
        }

        // The host runs each held re-plan, applied at once, on its own record, and raises it (R9-2).
        public static void RaiseReplans(IList<ReplanNote> notes)
        {
            foreach (ReplanNote n in notes) RunNote(n);
        }

        static void RunNote(ReplanNote note)
        {
            RuleState st = HumanAgencies.State;
            IHostDecisions host = HumanAgencies.Host;
            if (st == null || host == null || note == null || note.Observatory == null) return;
            if (ObservatoryRules.ReleasesListener(note.Row != null)) st.Listeners.Release(note.Observatory);
            ActionRequest a = ObservatoryWorld.NameOf(note.Observatory, ActionKind.ObservatoryReplan);
            if (a == null || note.ToBody < 1)
            {
                Fault("a re-plan that cannot be named");
                return;
            }
            a.ToBody = note.ToBody;
            ObsAfter before = ObservatoryWorld.AfterOf(note.Observatory);
            bool recording = st.Dice.BeginRecord();
            if (!recording)
            {
                Fault("a re-plan while another record is open");
                return;
            }
            string problem = ObservatoryWorld.RunNote(note.Observatory, note);
            ObsDraw[] draws = st.Dice.EndRecord();
            if (problem != null) Log.Error("observatory: the held re-plan failed: " + problem);
            ObsAfter after = ObservatoryWorld.AfterOf(note.Observatory);
            // a listener whose game handler did nothing is no re-plan; a destroyed target's was announced already
            if (!ObservatoryRules.RaisesReplan(note.Row != null, draws, before, after)) return;
            Raise(a, draws, after);
        }

        // The deferred listener notes (a re-plan another observatory's step caused) run after that step's entry.
        static void RunListenerNotes()
        {
            RuleState st = HumanAgencies.State;
            if (st == null || st.Notes.Count == 0) return;
            var mine = new List<ReplanNote>();
            foreach (ReplanNote n in st.Notes) if (n.Row != null) mine.Add(n);
            foreach (ReplanNote n in mine) st.Notes.Remove(n);
            foreach (ReplanNote n in mine) RunNote(n);
        }

        // The dev flag's cheat (the game's consoles are off in a session): the value its own console command writes.
        public static void SetSpeedCheat()
        {
            DevConsoleCommands.observatorySpeedCheat = ObservatoryRules.SpeedCheat;
            Log.Info("observatory: speed cheat " + ObservatoryRules.SpeedCheat);
        }
    }
}
