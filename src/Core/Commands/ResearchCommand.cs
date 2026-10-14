using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The game's research stages (ResearchManager.EResearchState, in its order).
    public static class ResearchStage
    {
        public const int None = 0, ReadyToResearch = 1, ReadyToContinue = 2, Lock = 3, InProgress = 4, Finish = 5,
            LockByUnlock = 6;
    }

    // One agency's research as the research kinds read it: always the ACTING agency's own manager, never the local
    // player's (the research windows read GameManager.Player, docs/research/commands.md "K10").
    public interface IResearchView
    {
        bool Known(string id);
        int Stage(string id);              // ResearchStage
        bool LockedForUi(string id);       // the tree's own lock: its buttons stay off
        bool CanStart(string id);          // ResearchManager.CheckCanStartResearch
        bool Running { get; }              // a topic is in the running slot
        string[] Queue();                  // queueRD, head first
        // GetAllRDNeed rebuilt for this agency: the unfinished prerequisites, the ready ones first, then the topic
        string[] NeedList(string id);
    }

    // The game calls a research entry makes, on the acting agency's own manager.
    public interface IResearchOps : IResearchView
    {
        bool Start(string id, bool silent, bool automated);   // StartResearch; false = the game refused
        void AddToQueue(string id, bool silent);
        void ClearQueue();
        void Enqueue(string id);                               // queueRD.Enqueue, as the windows' rebuild does
        void Stop();                                           // StopResearch
        void Changed();                                        // the entry is applied: what shows it is redrawn
    }

    // R1-R5 (DD2): what the windows allow, repeated for the acting agency on the host, and the windows' own world
    // calls, run the same way on every game. Pure, so the decisions are tests rather than game runs.
    public static class ResearchCommand
    {
        public static bool IsResearch(int kind)
        {
            return kind >= ActionKind.ResearchStart && kind <= ActionKind.ResearchSetQueue;
        }

        // 0, or the refusal. applied: the request as every game applies it (R3 expanded, a set normalised).
        public static int Decide(ActionRequest a, IResearchView v, out ActionRequest applied)
        {
            applied = null;
            if (a == null || v == null || !IsResearch(a.Kind)) return RefuseCodes.BadTarget;
            int code;
            switch (a.Kind)
            {
                case ActionKind.ResearchStart: code = Startable(a.Item, v); break;
                case ActionKind.ResearchAdd:
                    code = Startable(a.Item, v);
                    if (code == 0 && Contains(v.Queue(), a.Item)) code = RefuseCodes.AlreadyDone;
                    break;
                case ActionKind.ResearchAddWithPrereqs: return WithPrereqs(a, v, out applied);
                default: return SetQueue(a, v, out applied);
            }
            if (code == 0) applied = Copy(a, a.Ids);
            return code;
        }

        // The start button and the add button are on only for a topic ready to research or to continue
        // (D 143066-143160, 143863-143872).
        static int Startable(string id, IResearchView v)
        {
            if (id == null || !v.Known(id)) return RefuseCodes.BadTarget;
            if (v.LockedForUi(id)) return RefuseCodes.Locked;
            int stage = v.Stage(id);
            if (stage == ResearchStage.ReadyToResearch || stage == ResearchStage.ReadyToContinue) return 0;
            if (stage == ResearchStage.Finish || stage == ResearchStage.InProgress) return RefuseCodes.AlreadyDone;
            if (stage == ResearchStage.Lock || stage == ResearchStage.LockByUnlock) return RefuseCodes.Locked;
            return RefuseCodes.BadTarget;
        }

        // R3: only a locked topic that is not queued opens the popup (a queued one is a move, R4); the host expands
        // the popup's list from the acting agency's own stages.
        static int WithPrereqs(ActionRequest a, IResearchView v, out ActionRequest applied)
        {
            applied = null;
            string id = a.Item;
            if (id == null || !v.Known(id)) return RefuseCodes.BadTarget;
            if (v.LockedForUi(id)) return RefuseCodes.Locked;
            string[] queue = v.Queue();
            if (Contains(queue, id)) return RefuseCodes.AlreadyDone;
            int stage = v.Stage(id);
            if (stage == ResearchStage.Finish || stage == ResearchStage.InProgress) return RefuseCodes.AlreadyDone;
            if (stage == ResearchStage.LockByUnlock) return RefuseCodes.Locked;
            if (stage != ResearchStage.Lock) return RefuseCodes.NotAllowedHere;
            string[] ids = ResearchQueueRules.WithPrereqs(id, v.NeedList,
                t => v.Stage(t) == ResearchStage.Finish, new List<string>(queue));
            if (ResearchQueueRules.Problem(ids) != null) return RefuseCodes.TooLarge;
            applied = Copy(a, ids);
            return 0;
        }

        // R4 and R5: the whole queue the player now sees. Finished and unknown topics are left out; anything the
        // game's rebuild would drop on its own (a head that cannot start, a topic before its prerequisite) is refused.
        static int SetQueue(ActionRequest a, IResearchView v, out ActionRequest applied)
        {
            applied = null;
            string[] ids = ResearchQueueRules.Normalise(a.Ids, v.Known, t => v.Stage(t) == ResearchStage.Finish);
            if (ResearchQueueRules.Problem(ids) != null) return RefuseCodes.BadTarget;
            foreach (string id in ids) if (v.LockedForUi(id)) return RefuseCodes.Locked;
            if (ResearchQueueRules.OrderProblem(ids, v.NeedList, t => v.Stage(t) == ResearchStage.Finish) != null)
                return RefuseCodes.Locked;
            if (ResearchQueueRules.HeadProblem(ids, v.CanStart) != null) return RefuseCodes.Locked;
            applied = Copy(a, ids);
            return 0;
        }

        // The windows' own world calls for the applied request (the same on the host and on every client). null, or
        // why the game did not take it (on a client: out of step).
        public static string Apply(ActionRequest applied, IResearchOps g)
        {
            if (applied == null || g == null) return "no request";
            string problem = Run(applied, g);
            g.Changed();
            return problem;
        }

        static string Run(ActionRequest applied, IResearchOps g)
        {
            switch (applied.Kind)
            {
                case ActionKind.ResearchStart:
                    return g.Start(applied.Item, false, false) ? null : "the game would not start " + applied.Item;
                case ActionKind.ResearchAdd:
                    // the add button starts the topic when nothing runs (D 143345-143363)
                    if (g.Running) g.AddToQueue(applied.Item, false);
                    else if (!g.Start(applied.Item, false, false)) return "the game would not start " + applied.Item;
                    return null;
                case ActionKind.ResearchAddWithPrereqs:
                    // the popup's OK delegate (D 143466-143479): each topic not queued yet is started when the queue
                    // is empty, else added; the prerequisites count as automated
                    if (applied.Ids == null) return "no expanded list";
                    foreach (string id in applied.Ids)
                    {
                        if (Contains(g.Queue(), id)) continue;
                        bool automated = id != applied.Item;
                        if (g.Queue().Length == 0) g.Start(id, false, automated);
                        else g.AddToQueue(id, automated);
                    }
                    return null;
                case ActionKind.ResearchSetQueue:
                    // the R4/R5 rebuild (D 142502-142517, 142594-142605)
                    if (applied.Ids == null) return "no queue";
                    g.ClearQueue();
                    foreach (string id in applied.Ids)
                    {
                        if (g.Queue().Length == 0) g.Start(id, true, true);
                        else g.Enqueue(id);
                    }
                    if (applied.Ids.Length == 0) g.Stop();
                    return null;
                default: return "not a research kind";
            }
        }

        static bool Contains(string[] list, string id) { return list != null && Array.IndexOf(list, id) >= 0; }

        static ActionRequest Copy(ActionRequest a, string[] ids)
        {
            return new ActionRequest { Req = a.Req, Kind = a.Kind, Agency = a.Agency, Item = a.Item,
                Ids = ids == null ? null : (string[])ids.Clone() };
        }
    }
}
