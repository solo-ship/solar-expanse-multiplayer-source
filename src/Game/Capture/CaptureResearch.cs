using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using ResearchDefinition = ScriptableObjectScripts.ResearchDefinition;
using ResearchManager = Manager.ResearchManager;
using ResearchTree = Game.UI.Windows.Windows.ResearchTree.ResearchTree;
using ResearchTreeElement = Game.UI.Windows.Windows.ResearchTree.ResearchTreeElement;
using ResearchTreeRightPanel = Game.UI.Windows.Windows.ResearchTree.ResearchTreeRightPanel;

namespace SolarExpanseMultiplayer
{
    // R1-R5, the research tree's clicks (docs/research/commands.md, K10). Each captured entry sends the agency's own
    // request and the tree changes nothing until the host's entry arrives:
    //   R1 start a topic: ResearchTree.StartResearch (the left panel's button) and AddToQueryOnClick with nothing
    //      running;
    //   R2 add to the queue: AddToQueryOnClick with a topic running;
    //   R3 queue a locked topic with its prerequisites: the confirm popup's OK (the host expands the prerequisites);
    //   R4 move up or down: AddRemoveQueue, sent as the whole queue after the move;
    //   R5 remove: DeleteFromQueue, sent as the whole queue after the removal and the topics that depended on it.
    // UIResearch.OnClickBtn is not captured: no such window exists in this build (Q1).
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class CaptureResearch
    {
        static readonly Dictionary<string, CaptureHandler> Handlers = new Dictionary<string, CaptureHandler>
        {
            { "ResearchTree.StartResearch", StartOnTree },
            { "ResearchTreeRightPanel.AddToQueryOnClick", AddToQuery },
            { "ResearchTreeRightPanel/<>c__DisplayClass36_0.<UpgradeOnClick>b__0", AddWithPrerequisites },
            { "ResearchTreeElement.AddRemoveQueue", MoveInQueue },
            { "ResearchTreeElement.DeleteFromQueue", RemoveFromQueue }
        };

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.Method(typeof(ResearchTree), "StartResearch");
            yield return HookTargets.Method(typeof(ResearchTreeRightPanel), "AddToQueryOnClick");
            yield return HookTargets.Lambda(typeof(ResearchTreeRightPanel), "<>c__DisplayClass36_0",
                "<UpgradeOnClick>b__0");
            yield return HookTargets.Method(typeof(ResearchTreeElement), "AddRemoveQueue");
            yield return HookTargets.Method(typeof(ResearchTreeElement), "DeleteFromQueue");
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            return CaptureRun.Run(Handlers, seam, __originalMethod, __instance, __args);
        }

        static ActionRequest Topic(int kind, ResearchDefinition rd)
        {
            return new ActionRequest { Kind = kind, Agency = WindowCapture.Local(), Item = rd.ID };
        }

        static ResearchManager Researcher()
        {
            Company player = WindowCapture.Player();
            if (player == null || player.ResearchManager == null) throw new InvalidOperationException("no player");
            return player.ResearchManager;
        }

        // ---- R1 ----

        static bool StartOnTree(ICaptureSeam seam, object self, object[] args)
        {
            var rd = args[0] as ResearchDefinition;
            if (rd == null) return true;   // the game logs it and does nothing
            if (WindowCapture.Send(seam, Topic(ActionKind.ResearchStart, rd), null))
                seam.Memory.NoteQueueAdd(rd.ID, true, NetClock.NowMs);
            return false;
        }

        // ---- R1, R2 ----

        // The right panel's button: starts the selected topic when nothing runs, else queues it (the game's own pick
        // of topic: the panel's second selection when it is open, else the first).
        static bool AddToQuery(ICaptureSeam seam, object self, object[] args)
        {
            ResearchTree tree = HookTargets.Field<ResearchTree>(self, "researchTree");
            bool idle = Researcher().Slot1 == null;
            ResearchDefinition rd = tree.CurrentSelectResearchDefinition2;
            if (!idle && !(tree.IsOpenResearchTreeRightPanel && rd != null)) rd = tree.CurrentSelectResearchDefinition;
            if (rd == null) { WindowCapture.CannotSend(seam); return false; }
            if (WindowCapture.Send(seam, Topic(idle ? ActionKind.ResearchStart : ActionKind.ResearchAdd, rd), null))
                seam.Memory.NoteQueueAdd(rd.ID, idle, NetClock.NowMs);
            return false;
        }

        // ---- R3 ----

        static bool AddWithPrerequisites(ICaptureSeam seam, object self, object[] args)
        {
            var rd = HookTargets.Field<ResearchDefinition>(self, "rd");
            if (rd == null) { WindowCapture.CannotSend(seam); return false; }
            if (WindowCapture.Send(seam, Topic(ActionKind.ResearchAddWithPrereqs, rd), null))
                seam.Memory.NoteQueueAdd(rd.ID, false, NetClock.NowMs);
            return false;
        }

        // ---- R4, R5: the whole queue after the edit ----

        // The queue as the game holds it, topic by topic.
        static string[] LocalQueue()
        {
            return Researcher().queueRD.Select(d => d.ID).ToArray();
        }

        static ActionRequest QueueDraft()
        {
            return new ActionRequest { Kind = ActionKind.ResearchSetQueue, Agency = WindowCapture.Local() };
        }

        // The queue the player is editing: the one last sent while the host has not answered it, else the game's.
        static string[] QueueBase(ICaptureSeam seam, ActionRequest draft)
        {
            return seam.Memory.WithAdds(seam.Memory.BaseIds(seam, ActionShapes.Key(draft), LocalQueue()),
                NetClock.NowMs);
        }

        static void SendQueue(ICaptureSeam seam, ActionRequest draft, string[] ids)
        {
            draft.Ids = ids;
            if (WindowCapture.Send(seam, draft, null)) seam.Memory.SentIds(ActionShapes.Key(draft), ids);
        }

        static bool MoveInQueue(ICaptureSeam seam, object self, object[] args)
        {
            var rd = HookTargets.Field<ResearchDefinition>(self, "researchDefinition");
            if (rd == null) { WindowCapture.CannotSend(seam); return false; }
            ActionRequest draft = QueueDraft();
            SendQueue(seam, draft, QueueEdits.Move(QueueBase(seam, draft), rd.ID, (bool)args[0]));
            return false;
        }

        static bool RemoveFromQueue(ICaptureSeam seam, object self, object[] args)
        {
            var rd = HookTargets.Field<ResearchDefinition>(self, "researchDefinition");
            if (rd == null) { WindowCapture.CannotSend(seam); return false; }
            ActionRequest draft = QueueDraft();
            SendQueue(seam, draft, QueueEdits.Remove(QueueBase(seam, draft), rd.ID, Needs));
            return false;
        }

        // GetAllRDNeed for a queued topic: the topics it takes, itself last, by id.
        static IList<string> Needs(string id)
        {
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance
                .AllResearchDefinition;
            ResearchDefinition rd = all != null ? all.GetByID(id) : null;
            if (rd == null || rd.ID != id) throw new InvalidOperationException("no research topic " + id);
            return ResearchTreeRightPanel.GetAllRDNeed(rd).Select(d => d.ID).ToList();
        }
    }
}
