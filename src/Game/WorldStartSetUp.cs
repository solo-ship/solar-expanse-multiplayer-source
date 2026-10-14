using System;
using HarmonyLib;
using ObjectInfoManager = Manager.ObjectInfoManager;

namespace SolarExpanseMultiplayer
{
    // Has the game's own start set-up run in this world? ObjectInfoManager.Update raises solarSystemLoadEvent once per
    // scene (its private flag solarSystemLoadEventWas), and every body's ObjectInfo.StartStuff answers it: it re-rolls
    // the body's deposits from UnityEngine.Random (ResourcesRandomization) and rebuilds each agency's explored rows. A
    // loaded save runs it before its extract (LoadInternal waits frames first), so the save's rows win; a new game
    // (GameManager.InitFromStartGameConfiguration, Invoked in the scene's first frame) runs it the frame AFTER its
    // load, so the deposits the new game plays on are the re-rolled ones. A join snapshot taken in between carries rows
    // the host then throws away, and the joiner keeps them: out of step at the first day. The host's batch waits for
    // this.
    // Main thread only; never throws.
    public static class WorldStartSetUp
    {
        static readonly AccessTools.FieldRef<ObjectInfoManager, bool> ran = Field();
        static bool warned;

        static AccessTools.FieldRef<ObjectInfoManager, bool> Field()
        {
            try { return AccessTools.FieldRefAccess<ObjectInfoManager, bool>("solarSystemLoadEventWas"); }
            catch (Exception) { return null; }
        }

        // False with no world. A game build without the flag answers true (the old behaviour: no wait), logged once,
        // so a game update cannot stop every join; the update check's string lookups (build-compare) list the field.
        public static bool HasRun
        {
            get
            {
                try
                {
                    if (MonoBehaviourSingleton<ObjectInfoManager>.InstanceIsNull) return false;
                    ObjectInfoManager m = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
                    if (ran == null)
                    {
                        if (!warned) Log.Error("join: ObjectInfoManager.solarSystemLoadEventWas not found (a game "
                            + "update?): join snapshots no longer wait for a new game's start set-up");
                        warned = true;
                        return true;
                    }
                    return ran(m);
                }
                catch (Exception e)
                {
                    if (!warned) Log.Error("join: the start set-up test threw " + e.GetType().Name);
                    warned = true;
                    return false;
                }
            }
        }
    }
}
