using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using ObjectInfo = Game.Info.ObjectInfo;

namespace SolarExpanseMultiplayer
{
    // NukeAsteroid (kind 85; M7 DD-W8): the steps of vanilla's VirtualDestroyFromNuclearBomb for the ACTING agency, in
    // its order (D 124822-124871), on every game: the removal under the spawn record (the host) or its replay (a
    // client: the game makes exactly the host's asteroids), then the static nuke event, raised through its backing
    // delegate so the DetonateNuclearDevice objectives hear the actor and not the local player. Vanilla's own loop
    // cancels the local player's missions only; here the host cancels M6's set for every agency and raises each as a
    // reason-5 71, which a client applies from the entry (its own removal cancels the route legs by itself). The
    // explosion and the camera are the acting game's alone.
    public static partial class ActionApply
    {
        static readonly FieldInfo NukeEvent = AccessTools.Field(typeof(ObjectInfo), "virtualDestroyFromNuclearBomb");

        static ApplyOutcome ApplyNuke(ActionRequest a, ApplyNumbers given)
        {
            bool host = given == null;
            Company actor = GameAgencies.Find(a.Agency);
            ObjectInfo o = GameAgencies.Body(a.Body);
            if (host)
            {
                int code = actor == null ? RefuseCodes.BadTarget : WorldNuke.Check(a, actor);
                if (code != 0) return Refused(code);
            }
            else if (actor == null || o == null || o.IsInGameDestroy) return OutOfStep(a, "the asteroid is not here to nuke");
            AsteroidSpawnState spawns = HumanAgencies.Spawns;
            RuleState st = HumanAgencies.State;
            if (spawns == null || st == null)
            {
                if (host) return Refused(RefuseCodes.NotAllowedHere);
                return OutOfStep(a, "no asteroid rules are running");
            }
            // decided before the removal, from the missions as they are now
            List<TargetCancel> cancels = host ? FlightSources.TargetGoneCancels(o.id, WorldNuke.Now()) : null;
            bool opened = host ? spawns.BeginRecord() : spawns.BeginReplay(given.Spawns);
            if (!opened)
            {
                if (host) throw new InvalidOperationException("an asteroid spawn record is open already");
                return OutOfStep(a, "an asteroid spawn replay is open already");
            }
            bool acting = ReferenceEquals(actor, WindowCapture.Player());
            if (host) st.Flights.BeginQuiet();
            AsteroidSpawn[] made = null;
            string replay = null;
            try
            {
                if (acting) Explode(o);
                o.VirtualDestroy(null);
                if (acting) FollowWithCamera(o);
                Detonate(o, actor);
            }
            finally
            {
                if (host) { st.Flights.EndQuiet(); made = spawns.EndRecord(); }
                else replay = spawns.EndReplay();
            }
            CloseWindowsOn(o);
            if (!host) return replay != null ? OutOfStep(a, replay) : Done(a, null, new List<PrintEntry>());
            // raised inside this apply, the re-plans and the cancels go out right after this entry, in the game's order
            ObservatoryRule.RaiseReplans(ObservatoryRule.TakeNotes());
            RaiseNukeCancels(st, HumanAgencies.Host, o.id, cancels);
            return Done(a, new ApplyNumbers { Spawns = made }, new List<PrintEntry>());
        }

        // The static event with the actor as its company; a listener that throws must not undo the destruction.
        static void Detonate(ObjectInfo o, Company actor)
        {
            var handlers = NukeEvent != null ? NukeEvent.GetValue(null) as Action<ObjectInfo, Company> : null;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action<ObjectInfo, Company>)d)(o, actor); }
                catch (Exception e) { Log.Warn("command: a nuke listener threw " + e.GetType().Name); }
            }
        }

        // Each flight of the set is cancelled here and announced with reason 5 (the cancel the clients apply). A cancel
        // that did not take leaves this game apart from the clients': a host fault.
        static void RaiseNukeCancels(RuleState st, IHostDecisions host, int asteroid, List<TargetCancel> cancels)
        {
            if (host == null || cancels == null) return;
            foreach (TargetCancel c in cancels)
            {
                string problem;
                st.Flights.BeginQuiet();
                try { problem = FlightSources.CancelTargetGone(c); }
                catch (Exception e) { problem = e.GetType().Name; }
                finally { st.Flights.EndQuiet(); }
                FlightDecisions.Cancelled(host, c.Agency, c.MissionId, CancelReasons.TargetGone);
                if (problem != null)
                    host.HostFault("flight " + c.MissionId + " to the nuked asteroid " + asteroid
                        + " could not be cancelled: " + problem);
            }
        }

        static void Explode(ObjectInfo o)
        {
            try
            {
                UnityEngine.GameObject prefab = SerializedMonoBehaviourSingleton<Manager.ReferenceController>.Instance
                    .asteroidExplosionPrefab;
                UnityEngine.GameObject boom = UnityEngine.Object.Instantiate(prefab);
                boom.transform.parent = o.transform.parent;
                boom.transform.localPosition = UnityEngine.Vector3.zero;
                UnityEngine.Object.Destroy(boom, 10f);
            }
            catch (Exception e) { Log.Warn("command: the nuke's explosion did not show: " + e.GetType().Name); }
        }

        static void FollowWithCamera(ObjectInfo o)
        {
            try
            {
                var target = new UnityEngine.GameObject("targetCameraAsteroidImpact");
                target.transform.parent = o.transform.parent;
                target.transform.localPosition = UnityEngine.Vector3.zero;
                MonoBehaviourSingleton<CameraControl.MyCameraController>.Instance.ChangeTarget(target.transform);
            }
            catch (Exception e) { Log.Warn("command: the camera did not follow the nuke: " + e.GetType().Name); }
        }
    }
}
