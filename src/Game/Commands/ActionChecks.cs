using System;
using System.Collections.Generic;
using Company = Game.Company;
using Facility = Game.ObjectInfoDataScripts.Facility;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using ResearchDefinition = ScriptableObjectScripts.ResearchDefinition;
using ResearchManager = Manager.ResearchManager;
using ResearchTree = Game.UI.Windows.Windows.ResearchTree.ResearchTree;
using Spacecraft = CustomUpdate.Spacecraft;
using UIManager = Game.UI.UIManager;

namespace SolarExpanseMultiplayer
{
    // The windows' checks, repeated on the host for the ACTING agency (plan "Where the host applies"; Task 1's list in
    // docs/research/commands.md "K10"). The game's world methods check almost nothing themselves, so a request the
    // window would never have built is refused here, before anything changes. Kinds whose commands are not built yet
    // are refused (NotAllowedHere): a request the host cannot check is never applied.
    public static partial class ActionChecks
    {
        // The agency that owns the request's target, or null when there is no such target. AgencyAccess then holds it
        // to the requester's own seat.
        public static string OwnerOf(ActionRequest a)
        {
            Company c = GameAgencies.Find(a.Agency);
            if (c == null) return null;
            if (ResearchCommand.IsResearch(a.Kind) || ActionKind.HostOnly(a.Kind)) return a.Agency;
            // the market, contract and stock kinds name offers, contracts and bodies of their own: their checks decide
            if (WorldChecks.Handles(a.Kind)) return a.Agency;
            if (FlightChecks.Handles(a.Kind)) return FlightChecks.OwnerOf(a);
            if (a.Ship >= 0) return ShipOwner(a.Ship);
            ObjectInfo body = GameAgencies.Body(a.Body);
            if (body == null) return null;
            // an asteroid has no owner: forgetting it is the acting agency's own business (R7-3); the checks decide
            if (a.Kind == ActionKind.ForgetObject) return a.Agency;
            if (a.ItemId >= 0) return ItemOwners.Of(a.Agency, ItemHolders(body, a.ItemId));
            return GameAgencies.Site(body, c) != null ? a.Agency : null;
        }

        // Every agency whose data at the body holds an item with this id (a facility or a build), in the body's order.
        static List<string> ItemHolders(ObjectInfo body, long itemId)
        {
            var holders = new List<string>();
            if (body.ObjectsInfoData == null) return holders;
            foreach (ObjectInfoData d in body.ObjectsInfoData)
            {
                if (d == null || d.company == null || !HasItem(d, itemId)) continue;
                holders.Add(GameAgencies.Wire(d.company));
            }
            return holders;
        }

        static bool HasItem(ObjectInfoData d, long itemId)
        {
            if (d.ListFacility != null)
                foreach (Facility f in d.ListFacility)
                    if (f != null && f.ID == itemId) return true;
            if (d.ProductionItem != null)
                foreach (ProductionItem p in d.ProductionItem)
                    if (p != null && p.ID == itemId) return true;
            return false;
        }

        static string ShipOwner(int ship)
        {
            ShipManager ships = MonoBehaviourSingleton<ShipManager>.Instance;
            if (ships == null) return null;
            foreach (Spacecraft sc in ships.ListAllSpaceShip)
                if (sc != null && sc.ID == ship) return GameAgencies.Wire(sc.GetCompany());
            return null;
        }

        // 0, or the refusal.
        public static int Check(ActionRequest a)
        {
            if (ResearchCommand.IsResearch(a.Kind))
            {
                Company c = GameAgencies.Find(a.Agency);
                if (c == null || c.ResearchManager == null) return RefuseCodes.BadTarget;
                ActionRequest applied;
                return ResearchCommand.Decide(a, new ResearchGame(c), out applied);
            }
            if (WorldChecks.Handles(a.Kind)) return WorldChecks.Check(a);
            if (FlightChecks.Handles(a.Kind)) return FlightChecks.Check(a, Session.Commands.World.NextDayTicks);
            if (ProductionCommand.Handles(a.Kind)) return CheckProduction(a);
            if (ObservatoryCommand.Handles(a.Kind)) return CheckObservatory(a);
            if (a.Kind == ActionKind.ForgetObject) return CheckForget(a);
            if (BuildingChecks.Handles(a.Kind)) return BuildingChecks.Check(a);
            return RefuseCodes.NotAllowedHere;
        }
    }

    // One agency's research manager as ResearchCommand reads and changes it. Every read is of that agency, never of
    // the local player; the local research window is refreshed only when the agency is the local player's.
    sealed class ResearchGame : IResearchOps
    {
        readonly Company company;
        readonly ResearchManager rm;

        public ResearchGame(Company company)
        {
            this.company = company;
            rm = company.ResearchManager;
            // the topics queued now are redrawn too: a topic taken out of the queue must lose its queue mark, as the
            // window's own remove marks the old queue before it changes it
            Redraw(false);
        }

        static ResearchDefinition Def(string id)
        {
            if (id == null) return null;
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance
                .AllResearchDefinition;
            ResearchDefinition rd = all != null ? all.GetByID(id) : null;
            return rd != null && rd.ID == id ? rd : null;   // GetByID renames one old resource id: exact ids only
        }

        static ResearchDefinition Need(string id)
        {
            ResearchDefinition rd = Def(id);
            if (rd == null) throw new InvalidOperationException("no research topic " + id);
            return rd;
        }

        public bool Known(string id) { return Def(id) != null; }

        public int Stage(string id)
        {
            switch (rm.GetStage(Need(id)))
            {
                case ResearchManager.EResearchState.ReadyToResearch: return ResearchStage.ReadyToResearch;
                case ResearchManager.EResearchState.ReadyToContinue: return ResearchStage.ReadyToContinue;
                case ResearchManager.EResearchState.Lock: return ResearchStage.Lock;
                case ResearchManager.EResearchState.InProgress: return ResearchStage.InProgress;
                case ResearchManager.EResearchState.Finish: return ResearchStage.Finish;
                case ResearchManager.EResearchState.LockByUnlock: return ResearchStage.LockByUnlock;
                default: return ResearchStage.None;
            }
        }

        public bool LockedForUi(string id) { return Need(id).IsLockedForUI; }

        public bool CanStart(string id) { return rm.CheckCanStartResearch(Need(id)); }

        public bool Running { get { return rm.Slot1 != null; } }

        public string[] Queue()
        {
            var ids = new List<string>();
            foreach (ResearchDefinition rd in rm.queueRD) ids.Add(rd != null ? rd.ID : "");
            return ids.ToArray();
        }

        // GetAllRDNeed (D 143428-143449) for this agency: GetStageRecurrent's list reversed, the ready topics first,
        // then the rest, then the topic.
        public string[] NeedList(string id)
        {
            ResearchDefinition rd = Need(id);
            List<ResearchDefinition> unlock;
            rm.GetStageRecurrent(rd, out unlock);
            unlock.Reverse();
            var ids = new List<string>();
            foreach (ResearchDefinition r in unlock)
                if (r != null && rm.GetStage(r) == ResearchManager.EResearchState.ReadyToResearch) ids.Add(r.ID);
            foreach (ResearchDefinition r in unlock)
                if (r != null && rm.GetStage(r) != ResearchManager.EResearchState.ReadyToResearch) ids.Add(r.ID);
            ids.Add(rd.ID);
            return ids.ToArray();
        }

        public bool Start(string id, bool silent, bool automated)
        {
            return rm.StartResearch(Need(id), silent, automated);
        }

        public void AddToQueue(string id, bool silent) { rm.AddToQueue(Need(id), silent); }

        public void ClearQueue() { rm.queueRD.Clear(); }

        public void Enqueue(string id) { rm.queueRD.Enqueue(Need(id)); }

        public void Stop() { rm.StopResearch(); }

        public void Changed() { Redraw(true); }

        // The local player's research tree shows its own queue: around a change to it, the tree is marked and, when it
        // is open, redrawn, as the window's own handlers do. Another agency's research never touches the local tree. A
        // window that fails to redraw must not undo the change the world already took.
        void Redraw(bool look)
        {
            if (!company.IsPlayer) return;
            try
            {
                UIManager ui = SerializedMonoBehaviourSingleton<UIManager>.Instance;
                ResearchTree tree = ui != null ? ui.GetWindow<ResearchTree>() : null;
                if (tree == null) return;
                tree.MarkDirtyInQueue();
                if (look && tree.Open) tree.UpdateLookUI();
            }
            catch (Exception e) { Log.Warn("command: the research tree did not redraw: " + e.GetType().Name); }
        }
    }
}
