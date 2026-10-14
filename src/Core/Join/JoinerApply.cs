using System;
using System.Collections;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // More of the joiner's load rules that need no game, so they get real tests: the menu, the Unity log levels, the
    // apply step's verdict, which saved trees to empty, and a coroutine guard. The game glue (src/Game/SnapshotLoad.cs)
    // only gathers the facts.

    // ------------------------------------------------------------------ the menu

    public static class MenuRules
    {
        // At the main menu with nothing of ours loading: the Menu scene active, no GameManager, a LoadSaveManager.
        public static bool AtMenu(string activeScene, bool gameManagerExists, bool loadSaveManagerExists, bool loading)
        {
            return !loading && activeScene == "Menu" && !gameManagerExists && loadSaveManagerExists;
        }

        // What makes the audited cast operators safe: while the snapshot is deserialised no GameManager and no
        // ObjectInfoManager exists anywhere, active or not, so a cast that reaches for game state throws and the
        // deserialise aborts instead of tying the stranger's objects to a live world. Null = the premise holds.
        public static string Premise(bool gameManagerExists, bool objectInfoManagerExists)
        {
            if (!gameManagerExists && !objectInfoManagerExists) return null;
            return "a running game's managers exist (" + (gameManagerExists ? "GameManager" : "") +
                   (gameManagerExists && objectInfoManagerExists ? ", " : "") +
                   (objectInfoManagerExists ? "ObjectInfoManager" : "")
                       + "): the snapshot is deserialised only at the menu";
        }

        public static string PremiseLine(bool gameManagerExists, bool objectInfoManagerExists,
            bool loadSaveManagerExists)
        {
            return "join: menu check: GameManager " + (gameManagerExists ? "PRESENT" : "absent")
                + ", ObjectInfoManager " +
                   (objectInfoManagerExists ? "PRESENT" : "absent") + ", LoadSaveManager " +
                   (loadSaveManagerExists ? "present" : "absent");
        }
    }

    // ------------------------------------------------------------------ the Unity log levels

    // LogKind's values are UnityEngine.LogType's (Error 0, Assert 1, Warning 2, Log 3, Exception 4), pinned by tests.
    public static class LogKinds
    {
        // A level this build does not know counts as an error: a log tap must never read an unknown level as harmless.
        public static LogKind FromUnity(int logType)
        {
            switch (logType)
            {
                case 0: return LogKind.Error;
                case 1: return LogKind.Assert;
                case 2: return LogKind.Warning;
                case 3: return LogKind.Log;
                case 4: return LogKind.Exception;
                default: return LogKind.Error;
            }
        }
    }

    // ------------------------------------------------------------------ the apply step's verdict

    // After the extract the joiner copies the host's day boundaries, switches every rival's AI off and the yearly
    // autosave off. Each step catches its own failure, so the verdict is what keeps a half-applied load from being
    // reported as loaded: a joiner whose rivals still think, or whose days are keyed off the host's, would part from
    // the host.
    public static class ApplyVerdict
    {
        // Null = every step held. aiOff: agencies switched off, -1 when the step did not run or threw. rivalTreesLeft:
        // behaviour trees still alive for those agencies afterwards, -1 when they could not be counted.
        public static string Decide(int aiOff, int rivalTreesLeft, bool autosaveOff, bool boundaryCopied,
            bool noBoundaryCopy)
        {
            if (!boundaryCopied && !noBoundaryCopy) return "the host's day boundaries were not copied";
            if (aiOff < 0) return "the rivals' AI was not switched off";
            if (rivalTreesLeft < 0) return "the rivals' behaviour trees could not be counted";
            if (rivalTreesLeft > 0) return rivalTreesLeft + " rival behaviour trees are still alive";
            if (!autosaveOff) return "the yearly autosave is still on";
            return null;
        }
    }

    // The rivals' behaviour trees still alive after the AI-off step, by kind, for the apply verdict and its log line.
    // A loading thread whose owner cannot be read is counted against every rival (it might be any of them), so it is
    // also kept apart in Unowned: a failed verdict then shows whether such a thread alone was the cause.
    public sealed class TreeTally
    {
        public int Running, Paused, Loading, Unowned;
        public bool Failed;   // a count could not be taken: the verdict fails

        public void Add(int running, int paused, int loading, int unowned)
        {
            Running += running; Paused += paused; Loading += loading; Unowned += unowned;
        }

        // -1 when it could not be counted.
        public int Total { get { return Failed ? -1 : Running + Paused + Loading; } }

        public string Line
        {
            get
            {
                if (Failed) return "the trees could not be counted";
                return "running " + Running + ", paused " + Paused + ", loading " + Loading +
                       " (of them with no readable owner " + Unowned + ")";
            }
        }
    }

    // ------------------------------------------------------------------ the claimed agency's saved trees

    public static class OwnTrees
    {
        // The positions in the snapshot's AI saves (their agency ids, as the game writes them) that belong to the
        // claimed agency (a wire id, lower case): their saved behaviour trees are emptied before the load, the rest is
        // kept.
        public static List<int> ToEmpty(IList<string> agencyIds, string claimedWire)
        {
            var at = new List<int>();
            if (agencyIds == null || claimedWire == null) return at;
            for (int i = 0; i < agencyIds.Count; i++)
                if (agencyIds[i] != null && AgencyWire.From(agencyIds[i]) == claimedWire) at.Add(i);
            return at;
        }
    }

    // ------------------------------------------------------------------ a coroutine that cannot strand its load

    public static class GuardedSteps
    {
        // Runs inner step by step; when a step throws, onThrow hears it once and the enumeration ends. A load whose
        // coroutine threw would otherwise sit "loading" with the heartbeats holding the host's pause until the 300 s
        // deadline.
        public static IEnumerator Run(IEnumerator inner, Action<Exception> onThrow)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            while (true)
            {
                bool more;
                try { more = inner.MoveNext(); }
                catch (Exception e)
                {
                    try { if (onThrow != null) onThrow(e); }
                    catch (Exception) { }
                    yield break;
                }
                if (!more) yield break;
                yield return inner.Current;
            }
        }
    }
}
