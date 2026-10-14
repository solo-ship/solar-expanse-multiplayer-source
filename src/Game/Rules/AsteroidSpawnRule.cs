using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Company = Game.Company;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoManager = Manager.ObjectInfoManager;
using ObjectSubType = ScriptableObjectScripts.ObjectSubType;
using Spacecraft = CustomUpdate.Spacecraft;
using SpaceraftCustom3dview = CustomUpdate.SpaceraftCustom3dview;

namespace SolarExpanseMultiplayer
{
    // What the host's wrapper of one asteroid spawn remembers.
    public sealed class SpawnWrap
    {
        public AsteroidSpawn Made;       // the host's values for the spawn
        public AsteroidSpawn Expected;   // a client: the entry's spawn
        public bool OwnRecord;           // the host opened a record for this spawn alone (a spawn with no removal)
        public UnityEngine.Random.State Keep;   // the game's Random before the spawn's seed, given back afterwards
    }

    // What the host's wrapper of one impact remembers.
    public sealed class ImpactWrap
    {
        public string Agency;
        public int Asteroid;
        public long Mission;
        public bool Pull;                    // an orbit pull: it moves the asteroid and removes nothing (M6)
        public int Orbit;                    // a pull's target orbit
        public List<TargetCancel> Cancels;   // the flights the impact cancels, decided before its entry (DD-F12)
    }

    // DD15: asteroid creation, removal and impact are host-decided.
    //   * A removal makes the game spawn replacement asteroids (SpawnAsteroidIfNeed -> CreateDynamicAsteroid). The host
    //     fills the game's own replay argument (`orgin`: the seven values and the name) from the mod's generator and
    //     records what was made; a client, inside the host's entry, passes that entry's spawn through the same
    //     argument and the id; a client outside an entry spawns nothing. The load's own replay is left alone.
    //   * An impact (FS3, the ship's landing handler) is run by the host for every human agency's ship, and by a
    //     client only inside the host's entry for that mission. The host raises the removal (59) with the spawns, then
    //     the held observatory re-plans (57), then the reason-5 cancels of the flights to the asteroid (DD-F12), all
    //     counted in the 59. An orbit pull (M6, DD-F7) is run the same way and raises AsteroidPulled (72) with the
    //     host's two display draws and the new low orbit's id; a client takes them from the entry.
    // Every patch is a no-op outside a session.
    [HarmonyPatch]
    public static class AsteroidSpawnRule
    {
        // ---- the spawn ----

        static ObjectInfoManager.AsteroidRandomCreateDynamicData ToGame(AsteroidSpawn s)
        {
            return new ObjectInfoManager.AsteroidRandomCreateDynamicData
            {
                massRandom = s.Mass, aRandom = s.A, eccRandom = s.Ecc, pRandom = s.P, omega_ucRandom = s.OmegaUc,
                omega_lcRandom = s.OmegaLc, inclinationRandom = s.Incl, nameAsteroid = s.Name
            };
        }

        static bool NameTaken(ObjectInfoManager mgr, string name)
        {
            foreach (ObjectInfo o in mgr.allObjectInfos)
                if (o != null && o.idTranslation == name) return true;
            return false;
        }

        [HarmonyPatch(typeof(ObjectInfoManager), "CreateDynamicAsteroid")]
        [HarmonyPrefix]
        static bool SpawnPrefix(ObjectInfoManager __instance, ObjectInfoGroups groupToAdd, ObjectSubType objectSubType,
            ref ObjectInfoManager.AsteroidRandomCreateDynamicData orgin, ref int idObjectInfo, ref ObjectInfo __result,
            ref SpawnWrap __state)
        {
            if (!HumanAgencies.Active) return true;
            if (Manager.LoadSaveManager.OnExtractAllFromSaveData) return true;   // the load's replay of saved asteroids
            RuleState st = HumanAgencies.State;
            int group = ObservatoryWorld.GroupIndex(groupToAdd);
            string subType = objectSubType != null ? objectSubType.ID : null;
            if (HumanAgencies.Host == null)
            {
                string problem;
                AsteroidSpawn given = st.Spawns.Replaying ? st.Spawns.Take(group, subType, out problem) : null;
                if (given == null)
                {
                    if (!st.Spawns.Replaying)
                        st.Spawns.NoteStray("an asteroid spawn on a client outside the host's entry (group " + group
                            + ")");
                    __result = null;
                    return false;
                }
                orgin = ToGame(given);
                idObjectInfo = given.Id;
                __state = new SpawnWrap { Expected = given };
                Seed(__state, given);
                return true;
            }
            AsteroidSpawn made = AsteroidMaker.Make(st.SpawnRng, group, subType, __instance.asteroidGenerateMinMass,
                __instance.asteroidGenerateMaxMass, name => NameTaken(__instance, name));
            orgin = ToGame(made);
            __state = new SpawnWrap { Made = made, OwnRecord = !st.Spawns.Recording && st.Spawns.BeginRecord() };
            Seed(__state, made);
            return true;
        }

        // Every game runs the creation on the spawn's seed: the asteroid's deposits are drawn inside it from the game's
        // own Random (ResourcesRandomization), so the same seed makes the same deposit rows everywhere. The game's
        // Random is given back in the finalizer, so nothing after the spawn draws differently because of it.
        static void Seed(SpawnWrap w, AsteroidSpawn s)
        {
            w.Keep = UnityEngine.Random.state;
            UnityEngine.Random.InitState(s.Seed);
        }

        // A finalizer, so a creation that throws part way still closes the record it opened.
        [HarmonyPatch(typeof(ObjectInfoManager), "CreateDynamicAsteroid")]
        [HarmonyFinalizer]
        static void SpawnFinalizer(ObjectInfo __result, SpawnWrap __state)
        {
            if (!HumanAgencies.Active) return;
            if (__state == null) return;
            UnityEngine.Random.state = __state.Keep;
            RuleState st = HumanAgencies.State;
            if (__state.Expected != null)
            {
                AsteroidSpawn e = __state.Expected;
                if (__result == null || __result.id != e.Id || __result.idTranslation != e.Name)
                    st.Spawns.Mismatch("the asteroid the game made is not the entry's (id " + e.Id + ", " + e.Name
                        + ")");
                return;
            }
            AsteroidSpawn made = __state.Made;
            if (__result != null)
            {
                made.Id = __result.id;
                made.Name = __result.idTranslation;
                st.Spawns.Add(made);
            }
            if (!__state.OwnRecord) return;
            AsteroidSpawn[] spawns = st.Spawns.EndRecord();
            IHostDecisions host = HumanAgencies.Host;
            if (host == null || spawns == null) return;
            // a spawn with no removal behind it: every game creates it from the entry
            var a = new ActionRequest { Kind = ActionKind.AsteroidSpawned, Agency = WorldAgency(), Body = made.Id };
            try { host.Decided(a, new ApplyNumbers { Spawns = spawns }); }
            catch (Exception ex) { Log.Error("asteroid: could not raise AsteroidSpawned: " + ex.GetType().Name); }
        }

        // An entry names an agency; a spawn with no removal has none, so the host's own agency stands in.
        static string WorldAgency()
        {
            Company player = MonoBehaviourSingleton<Manager.GameManager>.Instance.Player;
            return player != null ? player.ID : null;
        }

        // ---- the impact (FS3) ----

        // The landing handler's `ship's company != local player` test, with the session's rule in its place.
        public static bool SkipsImpact(UnityEngine.Object company, UnityEngine.Object player, Spacecraft ship,
            ObjectInfo target)
        {
            if (!HumanAgencies.Active) return company != player;
            bool orbit = target != null && target.objectTypes == Data.EObjectTypes.Orbit;
            return ImpactRules.Skips(true, company == player, HumanAgencies.Host != null,
                HumanAgencies.IsHuman(company as Company), orbit, AppliesMissionOf(ship));
        }

        static bool AppliesMissionOf(Spacecraft ship)
        {
            RuleState st = HumanAgencies.State;
            if (st.ImpactMission < 0) return false;
            MissionInfo mission = ship != null ? ship.GetMissionInfo() : null;
            return mission != null && mission.id == st.ImpactMission;
        }

        [HarmonyPatch(typeof(SpaceraftCustom3dview), "SpaceCraftOnrocketLandNormal")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> ImpactGuard(IEnumerable<CodeInstruction> code)
        {
            return Il.Replace(code, "SpaceraftCustom3dview.SpaceCraftOnrocketLandNormal", 1,
                (c, i) => i > 0 && Il.IsGetPlayer(c[i - 1])
                    && Il.Calls(c[i], typeof(UnityEngine.Object), "op_Inequality", 2),
                () => new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(AsteroidSpawnRule), nameof(SkipsImpact)))
                });
        }

        // The landing handler's new low orbit takes its id from the host's 72 on a client.
        [HarmonyPatch(typeof(SpaceraftCustom3dview), "SpaceCraftOnrocketLandNormal")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> PullOrbitId(IEnumerable<CodeInstruction> code)
        {
            return Il.Replace(code, "SpaceraftCustom3dview.SpaceCraftOnrocketLandNormal (CreateOrbit)", 1,
                (c, i) => Il.Calls(c[i], typeof(ObjectInfoManager), "CreateOrbit", 4),
                () => new[]
                {
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FlightSources), "CreateOrbitFor"))
                });
        }

        // The pulled asteroid's two display values (its distance and spin) are the host's draws, not each game's
        // own UnityEngine.Random.
        [HarmonyPatch(typeof(SpaceraftCustom3dview), "AsteroidOnOrbitSetRotation")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> PullDraws(IEnumerable<CodeInstruction> code)
        {
            const string where = "SpaceraftCustom3dview.AsteroidOnOrbitSetRotation";
            List<CodeInstruction> first = Il.Replace(code, where + " (distance)", 1,
                (c, i) => IsRandomRange(c[i], typeof(float)),
                () => new[]
                {
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FlightSources), "PullDistance"))
                });
            return Il.Replace(first, where + " (rotation)", 1, (c, i) => IsRandomRange(c[i], typeof(int)),
                () => new[]
                {
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FlightSources), "PullRotation"))
                });
        }

        static bool IsRandomRange(CodeInstruction i, Type result)
        {
            var m = i.operand as System.Reflection.MethodInfo;
            return Il.Calls(i, typeof(UnityEngine.Random), "Range", 2) && m != null && m.ReturnType == result;
        }

        // The host wraps an impact it runs: the spawns are recorded, and when the handler is done the removal and the
        // held observatory re-plans become entries. The handler's own early returns (no asteroid, already destroyed)
        // leave nothing recorded and raise nothing.
        [HarmonyPatch(typeof(SpaceraftCustom3dview), "SpaceCraftOnrocketLandNormal")]
        [HarmonyPrefix]
        static void ImpactPrefix(Spacecraft __0, ObjectInfo __1, ref ImpactWrap __state)
        {
            if (!HumanAgencies.Active) return;
            if (HumanAgencies.Host == null || HumanAgencies.Applying) return;
            // the handler's parameters are the event's generated arg1, arg2 and fromStart: bound by position
            Spacecraft ship = __0;
            try
            {
                Company owner = ship.GetCompany();
                ObjectInfo asteroid = ship.CurrentlyOnThisObject;
                if (owner == null || asteroid == null || asteroid.IsInGameDestroy || !ship.CargoAll.entireAsteroid)
                    return;
                if (__1 == null) return;
                Company player = MonoBehaviourSingleton<Manager.GameManager>.Instance.Player;
                if (SkipsImpact(owner, player, ship, __1)) return;
                RuleState st = HumanAgencies.State;
                MissionInfo mission = ship.GetMissionInfo();
                long id = mission != null ? mission.id : -1;
                if (__1.objectTypes == Data.EObjectTypes.Orbit)
                {
                    // an orbit pull moves the asteroid: nothing is removed or spawned, only its two display values
                    // and a new low orbit are made, and the host's are recorded for the entry
                    if (st.Flights.PullMade != null) return;
                    st.Flights.PullMade = new AsteroidPull { Asteroid = asteroid.id, Parent = __1.id };
                    st.Flights.PullMission = id;
                    __state = new ImpactWrap { Agency = owner.ID, Asteroid = asteroid.id, Mission = id, Pull = true,
                        Orbit = __1.id };
                    return;
                }
                // the flights to the asteroid that arrive after this one are decided now, so the 59 can count them
                var cancels = mission != null
                    ? FlightSources.TargetGoneCancels(asteroid.id, mission.DateArrive.Ticks)
                    : new List<TargetCancel>();
                if (!st.Spawns.BeginRecord()) return;   // inside another record: its entry covers the spawns
                // the cancels the destruction makes by itself (a route's planned leg) belong to this entry
                st.Flights.BeginQuiet();
                __state = new ImpactWrap { Agency = owner.ID, Asteroid = asteroid.id, Mission = id,
                    Cancels = cancels };
            }
            catch (Exception e)
            {
                Log.Error("asteroid: could not note an impact (the clients will wait for it): " + e.GetType().Name);
            }
        }

        // A finalizer, so a handler that throws part way still tells the clients, who are holding for this entry.
        [HarmonyPatch(typeof(SpaceraftCustom3dview), "SpaceCraftOnrocketLandNormal")]
        [HarmonyFinalizer]
        static void ImpactFinalizer(ImpactWrap __state)
        {
            if (!HumanAgencies.Active) return;
            if (__state == null) return;
            RuleState st = HumanAgencies.State;
            if (__state.Pull) { PullDone(st, __state); return; }
            st.Flights.EndQuiet();
            AsteroidSpawn[] spawns = st.Spawns.EndRecord();
            List<ReplanNote> notes = ObservatoryRule.TakeNotes();
            IHostDecisions host = HumanAgencies.Host;
            if (host == null) return;
            // A ship that lands with no mission is in no client's watch, so no client stops before it and runs the
            // branch inside its own landing: the branch's other changes (the ship hidden, the asteroid's deposits
            // given to the target, the ships and modules moved to the target's low orbit) would happen here only. The
            // clients rejoin from this world instead; the held re-plans still run here, after the reset.
            if (__state.Mission < 0)
            {
                host.HostFault("an impact on asteroid " + __state.Asteroid + " by " + __state.Agency
                    + " has no mission the clients can land it with");
                ObservatoryRule.RaiseReplans(notes);
                return;
            }
            int cancels = __state.Cancels != null ? __state.Cancels.Count : 0;
            var a = new ActionRequest { Kind = ActionKind.ObjectRemoved, Agency = __state.Agency,
                Body = __state.Asteroid, ItemId = __state.Mission,
                Count = __state.Mission >= 0 ? notes.Count + cancels : 0 };
            try { host.Decided(a, new ApplyNumbers { Spawns = spawns }); }
            catch (Exception e)
            {
                Log.Error("asteroid: could not raise ObjectRemoved: " + e.GetType().Name);
                return;
            }
            // the re-plans follow it at once, in the order the game called them; every one was nameable (TakeNotes)
            ObservatoryRule.RaiseReplans(notes);
            RaiseTargetGone(st, host, __state);
        }

        // DD-F12: each flight to the destroyed asteroid is cancelled here, then announced with reason 5 (the 59
        // counted them after its re-plans). A cancel that did not take leaves this game apart from the clients'.
        static void RaiseTargetGone(RuleState st, IHostDecisions host, ImpactWrap wrap)
        {
            if (wrap.Cancels == null) return;
            foreach (TargetCancel c in wrap.Cancels)
            {
                string problem;
                st.Flights.BeginQuiet();
                try { problem = FlightSources.CancelTargetGone(c); }
                catch (Exception e) { problem = e.GetType().Name; }
                finally { st.Flights.EndQuiet(); }
                FlightDecisions.Cancelled(host, c.Agency, c.MissionId, CancelReasons.TargetGone);
                if (problem != null)
                    host.HostFault("flight " + c.MissionId + " to the destroyed asteroid " + wrap.Asteroid
                        + " could not be cancelled: " + problem);
            }
        }

        // The pull's entry: the asteroid, the orbit it went into, the host's draws and the new low orbit's id.
        static void PullDone(RuleState st, ImpactWrap wrap)
        {
            AsteroidPull pull = st.Flights.PullMade;
            st.Flights.PullMade = null;
            st.Flights.PullMission = -1;
            IHostDecisions host = HumanAgencies.Host;
            if (host == null || pull == null) return;
            if (wrap.Mission < 0)
            {
                host.HostFault("a pull of asteroid " + wrap.Asteroid + " by " + wrap.Agency
                    + " has no mission the clients can land it with");
                return;
            }
            FlightDecisions.Pulled(host, wrap.Agency, wrap.Asteroid, wrap.Orbit, wrap.Mission, pull, 0);
        }

        // A client's apply of an impact's ObjectRemoved: runs the game's original landing handler for this mission
        // (the transpiled test passes for it and for nothing else).
        public static string RunImpact(long mission, Action original)
        {
            RuleState st = HumanAgencies.State;
            if (st == null) return "no session";
            if (st.ImpactMission >= 0) return "an impact inside another";
            st.ImpactMission = mission;
            try { original(); return null; }
            catch (Exception e) { return "the landing handler threw " + e.GetType().Name; }
            finally { st.ImpactMission = -1; }
        }
    }
}
