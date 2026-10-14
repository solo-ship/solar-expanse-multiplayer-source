using System;
using System.Collections.Generic;
using HarmonyLib;
using Company = Game.Company;
using Facility = Game.ObjectInfoDataScripts.Facility;
using FacilityBaseDescriptor = Data.ScriptableObject.FacilityBaseDescriptor;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using ProductionQuery = Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
using ResearchDataProgress = ScriptableObjectScripts.ResearchDataProgress;
using ResearchDefinition = ScriptableObjectScripts.ResearchDefinition;
using ResearchManager = Manager.ResearchManager;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;
using SpacecraftConstructData = Data.SpacecraftConstructData;

namespace SolarExpanseMultiplayer
{
    // ObjectData in (DD10): the host's copy of one object, as of its day's close, set on a client whose print of it
    // differed, before the client crosses that day. Only what M5's commands touch and the game lets change in place:
    //   * an agency's research: the running topic and its progress, the queue, the started-not-finished topics;
    //   * an agency's builds at a site: its item counter (and name counters), each unfinished build's unit count,
    //     units done and progress, the queued entries' counts;
    //   * an agency's facilities at a site: units switched on, the types' priorities, the deposits mined.
    // What exists cannot be restored (an item missing or extra, another order, a built count, a missing site): that
    // is out of step, and the full rejoin heals it (RestoreRules decides). Every call returns null when restored, else
    // why not. The site is found by its company, never through GetObjectInfoData. Main thread, under Applying.
    public static class ObjectRestore
    {
        static readonly AccessTools.FieldRef<ResearchManager, ResearchDataProgress> Slot =
            AccessTools.FieldRefAccess<ResearchManager, ResearchDataProgress>("slot1");
        static readonly AccessTools.FieldRef<ResearchManager, List<ResearchDataProgress>> Paused =
            AccessTools.FieldRefAccess<ResearchManager, List<ResearchDataProgress>>("startNotFinish");
        static readonly AccessTools.FieldRef<ResearchDataProgress, float> Progress =
            AccessTools.FieldRefAccess<ResearchDataProgress, float>("progress");
        static readonly AccessTools.FieldRef<ObjectInfoData, List<ProductionQuery>> Query =
            AccessTools.FieldRefAccess<ObjectInfoData, List<ProductionQuery>>("listProductionQuery");
        static readonly AccessTools.FieldRef<ObjectInfoData, Dictionary<FacilityBaseDescriptor, int>> Priority =
            AccessTools.FieldRefAccess<ObjectInfoData, Dictionary<FacilityBaseDescriptor, int>>(
                "facilityBaseDescriptorToPriority");
        static readonly AccessTools.FieldRef<ProductionItem, bool> Built =
            AccessTools.FieldRefAccess<ProductionItem, bool>("build");
        static readonly AccessTools.FieldRef<ProductionItem, string> TypeId =
            AccessTools.FieldRefAccess<ProductionItem, string>("idProductionItemType");
        static readonly AccessTools.FieldRef<ProductionItem, int> ToBuild =
            AccessTools.FieldRefAccess<ProductionItem, int>("countToBuild");
        static readonly AccessTools.FieldRef<ProductionItem, int> Done =
            AccessTools.FieldRefAccess<ProductionItem, int>("countBuild");
        static readonly AccessTools.FieldRef<ProductionItem, float> FirstUnit =
            AccessTools.FieldRefAccess<ProductionItem, float>("buildProgress");
        static readonly AccessTools.FieldRef<ProductionItem, List<float>> Units =
            AccessTools.FieldRefAccess<ProductionItem, List<float>>("listBuildProgress");
        static readonly AccessTools.FieldRef<Facility, long> Quantity =
            AccessTools.FieldRefAccess<Facility, long>("quantity");
        static readonly AccessTools.FieldRef<Facility, long> EnabledUnits =
            AccessTools.FieldRefAccess<Facility, long>("enabled");
        static readonly AccessTools.FieldRef<RowExploredResourcesData, bool> Mined =
            AccessTools.FieldRefAccess<RowExploredResourcesData, bool>("selectedToMine");

        public static string Restore(ObjectData d, CounterSync counters)
        {
            if (d == null) return "no object";
            if (d.Code != ObjectCodes.Ok) return "the host answered " + d.Code;
            Company c = GameAgencies.Find(d.Agency);
            if (c == null) return "no agency " + d.Agency;
            switch (d.Kind)
            {
                case ObjectKind.Research: return Research(c, d.Research);
                case ObjectKind.SiteBuilds: return Builds(c, Site(c, d.Body), d.Builds, counters);
                case ObjectKind.SiteFacilities: return Facilities(Site(c, d.Body), d.Facilities);
                default: return "not an object kind: " + d.Kind;
            }
        }

        static ObjectInfoData Site(Company c, int body) { return GameAgencies.Site(GameAgencies.Body(body), c); }

        // ---- research ----

        static ResearchDefinition Def(string id)
        {
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance
                .AllResearchDefinition;
            ResearchDefinition rd = all != null && id != null ? all.GetByID(id) : null;
            return rd != null && rd.ID == id ? rd : null;   // GetByID renames one old id: exact ids only
        }

        // M7's research heal (WorldRestore): the host's rows read back as this same state.
        public static string RestoreResearch(Company c, ResearchState r) { return Research(c, r); }

        static string Research(Company c, ResearchState r)
        {
            ResearchManager rm = c.ResearchManager;
            if (rm == null) return c.ID + " has no research";
            if (r == null) return "no research state";
            // a restore can set what runs and waits, never what is finished: those differences are out of step
            string bad = RestoreRules.ResearchRestoreProblem(r, StartedHere(rm), id =>
            {
                ResearchDefinition d = Def(id);
                return d != null && rm.IsComplete(d);
            });
            if (bad != null) return bad;
            var queue = new List<ResearchDefinition>();
            foreach (string id in r.Queue ?? new string[0])
            {
                ResearchDefinition q = Def(id);
                if (q == null) return "no research topic " + id;
                queue.Add(q);
            }
            ResearchDefinition running = null;
            if (r.Running != null && (running = Def(r.Running)) == null) return "no research topic " + r.Running;
            // the progress objects this game has, reused so the game's own references to them stay good
            var have = new Dictionary<ResearchDefinition, ResearchDataProgress>();
            ResearchDataProgress slot = Slot(rm);
            if (slot != null && slot.ResearchDefinition != null) have[slot.ResearchDefinition] = slot;
            List<ResearchDataProgress> paused = Paused(rm);
            foreach (ResearchDataProgress p in paused ?? new List<ResearchDataProgress>())
                if (p != null && p.ResearchDefinition != null) have[p.ResearchDefinition] = p;
            ResearchDataProgress newSlot = null;
            if (running != null)
            {
                newSlot = Progressed(have, running, c, r.Progress);
            }
            var newPaused = new List<ResearchDataProgress>();
            for (int i = 0; r.Paused != null && i < r.Paused.Length; i++)
            {
                ResearchDefinition p = Def(r.Paused[i]);
                if (p == null) return "no research topic " + r.Paused[i];
                newPaused.Add(Progressed(have, p, c, r.PausedProgress[i]));
            }
            Slot(rm) = newSlot;
            if (paused == null) Paused(rm) = paused = new List<ResearchDataProgress>();
            paused.Clear();
            paused.AddRange(newPaused);
            rm.queueRD.Clear();
            foreach (ResearchDefinition q in queue) rm.queueRD.Enqueue(q);
            // the local player's tree shows the restored queue, as after any research entry
            new ResearchGame(c).Changed();
            return null;
        }

        // The topics this game has started: the running one and the paused ones.
        static List<string> StartedHere(ResearchManager rm)
        {
            var ids = new List<string>();
            ResearchDataProgress slot = Slot(rm);
            if (slot != null && slot.ResearchDefinition != null) ids.Add(slot.ResearchDefinition.ID);
            List<ResearchDataProgress> paused = Paused(rm);
            foreach (ResearchDataProgress p in paused ?? new List<ResearchDataProgress>())
                if (p != null && p.ResearchDefinition != null) ids.Add(p.ResearchDefinition.ID);
            return ids;
        }

        static ResearchDataProgress Progressed(Dictionary<ResearchDefinition, ResearchDataProgress> have,
            ResearchDefinition rd, Company c, double progress)
        {
            ResearchDataProgress p;
            if (!have.TryGetValue(rd, out p)) p = new ResearchDataProgress(rd, c);
            Progress(p) = (float)progress;
            return p;
        }

        // ---- builds ----

        static string Builds(Company c, ObjectInfoData site, SiteBuildState s, CounterSync counters)
        {
            if (s == null) return "no builds in the copy";
            if (site == null) return "no site of " + c.ID + " here";
            var mine = new List<ProductionItem>();
            var facts = new List<BuildFact>();
            if (site.ProductionItem != null)
                foreach (ProductionItem p in site.ProductionItem)
                {
                    if (p == null || Built(p)) continue;
                    mine.Add(p);
                    facts.Add(new BuildFact { ItemId = p.ID, Item = TypeOf(p), Craft = p is SpacecraftConstructData });
                }
            var queue = new List<QueueRow>();
            List<ProductionQuery> q = Query(site);
            for (int i = 0; q != null && i < q.Count; i++)
                queue.Add(new QueueRow { Item = q[i] != null && q[i].MyIDScriptableObjectProductionItem != null
                    ? q[i].MyIDScriptableObjectProductionItem.ID : "" });
            string bad = RestoreRules.BuildsProblem(facts, s.Building) ?? RestoreRules.QueueProblem(queue, s.Queue);
            if (bad != null) return bad;
            bad = counters.Set(s.Counters);
            if (bad != null) return bad;
            for (int i = 0; i < mine.Count; i++)
            {
                bad = Unit(mine[i], s.Building[i]);
                if (bad != null) return bad;
            }
            for (int i = 0; q != null && i < q.Count; i++) q[i].countToBuild = s.Queue[i].Count;
            return null;
        }

        // One build's units as the host has them: how many, how many done, and the progress of the one in hand.
        static string Unit(ProductionItem p, BuildRow h)
        {
            int count = Math.Max(h.Count, 0), done = RestoreRules.CountBuild(h.Count, h.CountBuild);
            float progress = RestoreRules.UnitProgress(h.Progress);
            ToBuild(p) = count;
            if (p.CountToBuild != count) return "build " + h.Item + " #" + h.ItemId + " keeps its own unit count";
            Done(p) = done;
            List<float> units = Units(p);
            if (units == null) Units(p) = units = new List<float>();
            if (count == 0)
            {
                // an old save's single unit: its progress is the item's own
                FirstUnit(p) = progress;
                if (units.Count == 1) units[0] = progress;
                return null;
            }
            RestoreRules.Units(units, count, done, progress);
            FirstUnit(p) = units[0];
            return null;
        }

        static string TypeOf(ProductionItem p)
        {
            var f = p as Facility;
            if (f != null) return f.facilityDescriptor != null ? f.facilityDescriptor.ID : "";
            return TypeId(p) ?? "";
        }

        // ---- facilities ----

        static string Facilities(ObjectInfoData site, SiteFacilityState s)
        {
            if (s == null) return "no facilities in the copy";
            if (site == null) return "no such site here";
            var facts = new List<FacilityFact>();
            var built = new List<Facility>();
            if (site.ListFacility != null)
                foreach (Facility f in site.ListFacility)
                {
                    if (f == null) continue;
                    bool b = Built(f);
                    facts.Add(new FacilityFact { ItemId = f.ID, Item = TypeOf(f), Quantity = Quantity(f), Built = b });
                    if (b) built.Add(f);
                }
            string bad = RestoreRules.FacilitiesProblem(facts, s.Built);
            if (bad != null) return bad;
            List<RowExploredResourcesData> rows = site.listExploredResourcesRows;
            var resources = new List<string>();
            for (int i = 0; rows != null && i < rows.Count; i++)
                resources.Add(rows[i] != null && rows[i].ResourceType != null ? rows[i].ResourceType.ID : "");
            bool[] mined = RestoreRules.Mining(resources, s.Mining, out bad);
            if (bad != null) return bad;
            for (int i = 0; i < built.Count; i++)
            {
                Facility f = built[i];
                FacilityRow h = s.Built[i];
                long on = RestoreRules.Enabled(h.Enabled, h.Count);
                // the game's own setter, so energy and the site's summaries follow; only when it differs
                if (EnabledUnits(f) != on) f.Enabled = on;
                if (EnabledUnits(f) != on) return "facility " + h.Item + " #" + h.ItemId + " will not take " + on
                    + " units on";
                bad = PriorityOf(site, f, h.Priority);
                if (bad != null) return bad;
            }
            // straight into the rows: a row's change event would run the observatories' handlers inside a restore
            for (int i = 0; rows != null && i < rows.Count; i++)
                if (rows[i] != null && Mined(rows[i]) != mined[i]) Mined(rows[i]) = mined[i];
            return null;
        }

        static string PriorityOf(ObjectInfoData site, Facility f, int priority)
        {
            FacilityBaseDescriptor d = f.facilityDescriptor;
            if (d == null) return "a facility with no type";
            Dictionary<FacilityBaseDescriptor, int> stored = Priority(site);
            int now;
            if (stored == null || !stored.TryGetValue(d, out now)) now = d.Priority;
            if (now != priority) site.SetPriority(d, priority);
            return null;
        }
    }
}
