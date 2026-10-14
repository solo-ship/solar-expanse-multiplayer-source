using System;
using System.Collections.Generic;
using HarmonyLib;
using Company = Game.Company;
using Facility = Game.ObjectInfoDataScripts.Facility;
using FacilityBaseDescriptor = Data.ScriptableObject.FacilityBaseDescriptor;
using ObjectInfo = Game.Info.ObjectInfo;
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
    // The three objects an entry can change (DD10), as the day-boundary print sees them and as the host's kept copy
    // carries them: an agency's research, its builds at a site, its facilities at a site. Both sides print through
    // ObjectRows, so the host and every client make the same rows from the same state. Reads go through fields, never
    // through the game's getters that write (BuildProgress fills a list, ProductionItemType caches, GetPriority adds a
    // default): a print must not change what it prints. Stateless; a missing field fails the class at its first use,
    // which the driver turns into a new stream.
    public static class ObjectRead
    {
        static readonly AccessTools.FieldRef<ResearchManager, List<ResearchDataProgress>> Paused =
            AccessTools.FieldRefAccess<ResearchManager, List<ResearchDataProgress>>("startNotFinish");
        static readonly AccessTools.FieldRef<ResearchManager, List<ResearchDefinition>> Completed =
            AccessTools.FieldRefAccess<ResearchManager, List<ResearchDefinition>>("completedResearch");
        static readonly AccessTools.FieldRef<ObjectInfoData, List<ProductionQuery>> Query =
            AccessTools.FieldRefAccess<ObjectInfoData, List<ProductionQuery>>("listProductionQuery");
        static readonly AccessTools.FieldRef<ObjectInfoData, Dictionary<FacilityBaseDescriptor, int>> Priority =
            AccessTools.FieldRefAccess<ObjectInfoData, Dictionary<FacilityBaseDescriptor, int>>(
                "facilityBaseDescriptorToPriority");
        static readonly AccessTools.FieldRef<ProductionItem, bool> Built =
            AccessTools.FieldRefAccess<ProductionItem, bool>("build");
        static readonly AccessTools.FieldRef<ProductionItem, string> TypeId =
            AccessTools.FieldRefAccess<ProductionItem, string>("idProductionItemType");
        static readonly AccessTools.FieldRef<ProductionItem, float> Progress1 =
            AccessTools.FieldRefAccess<ProductionItem, float>("buildProgress");
        static readonly AccessTools.FieldRef<ProductionItem, List<float>> Units =
            AccessTools.FieldRefAccess<ProductionItem, List<float>>("listBuildProgress");
        static readonly AccessTools.FieldRef<Facility, long> Quantity =
            AccessTools.FieldRefAccess<Facility, long>("quantity");
        static readonly AccessTools.FieldRef<Facility, long> Enabled =
            AccessTools.FieldRefAccess<Facility, long>("enabled");

        public static string Print(int kind, string agency, int body)
        {
            return ObjectPrint.Of(kind, agency, body, Rows(kind, agency, body));
        }

        static List<PrintRow> Rows(int kind, string agency, int body)
        {
            Company c = Need(agency);
            if (kind == ObjectKind.Research)
            {
                ResearchState r = ResearchOf(c);
                return ObjectRows.Research(r.Running, r.Queue, r.Paused, CompletedOf(c));
            }
            ObjectInfoData site = GameAgencies.Site(GameAgencies.Body(body), c);
            if (kind == ObjectKind.SiteBuilds)
            {
                // a site the agency does not have prints as an empty one on every game
                if (site == null) return ObjectRows.Builds(c.productionCountID, null, null);
                return ObjectRows.Builds(c.productionCountID, BuildFacts(site), QueueOf(site));
            }
            if (kind != ObjectKind.SiteFacilities) throw new ArgumentException("not an object kind: " + kind);
            if (site == null) return ObjectRows.Facilities(null, null, null);
            return ObjectRows.Facilities(FacilityFacts(site), Priorities(site), MiningOf(site));
        }

        // The host's copy of the object as it stands (Code set when it cannot be read).
        public static ObjectData Read(int kind, string agency, int body)
        {
            Company c = Need(agency);
            var d = new ObjectData { Code = ObjectCodes.Ok, Kind = kind, Agency = agency, Body = body, Piece = 0,
                Pieces = 1 };
            if (kind == ObjectKind.Research) { d.Research = ResearchOf(c); return d; }
            ObjectInfoData site = GameAgencies.Site(GameAgencies.Body(body), c);
            if (site == null) return new ObjectData { Code = ObjectCodes.NoSuchObject, Kind = kind, Agency = agency,
                Body = body, Piece = 0, Pieces = 1 };
            if (kind == ObjectKind.SiteBuilds) d.Builds = BuildsOf(c, agency, site);
            else if (kind == ObjectKind.SiteFacilities) d.Facilities = FacilitiesOf(site);
            else throw new ArgumentException("not an object kind: " + kind);
            return d;
        }

        static Company Need(string agency)
        {
            Company c = GameAgencies.Find(agency);
            if (c == null) throw new InvalidOperationException("no agency " + agency);
            return c;
        }

        // ---- research ----

        // The topics the agency has finished (their ids), for the print's finished-set row.
        internal static List<string> CompletedOf(Company c)
        {
            ResearchManager rm = c.ResearchManager;
            if (rm == null) throw new InvalidOperationException(c.ID + " has no research");
            var ids = new List<string>();
            List<ResearchDefinition> done = Completed(rm);
            if (done != null) foreach (ResearchDefinition rd in done) ids.Add(rd != null ? rd.ID : "");
            return ids;
        }

        internal static ResearchState ResearchOf(Company c)
        {
            ResearchManager rm = c.ResearchManager;
            if (rm == null) throw new InvalidOperationException(c.ID + " has no research");
            ResearchDataProgress slot = rm.Slot1;
            var queue = new List<string>();
            foreach (ResearchDefinition rd in rm.queueRD) queue.Add(rd != null ? rd.ID : "");
            var paused = new List<string>();
            var progress = new List<double>();
            List<ResearchDataProgress> list = Paused(rm);
            if (list != null)
                foreach (ResearchDataProgress p in list)
                {
                    if (p == null) continue;
                    paused.Add(p.ResearchDefinition != null ? p.ResearchDefinition.ID : "");
                    progress.Add(p.Progress);
                }
            return new ResearchState
            {
                Running = slot != null && slot.ResearchDefinition != null ? slot.ResearchDefinition.ID : null,
                Progress = slot != null ? slot.Progress : 0.0,
                Queue = queue.ToArray(),
                Paused = paused.ToArray(),
                PausedProgress = progress.ToArray()
            };
        }

        // ---- builds ----

        // The unfinished builds in the game's order (productionItems: facilities and crafts together); a queued,
        // unpaid entry is not one of them (its placeholder lives elsewhere), it is a queue row.
        static List<BuildFact> BuildFacts(ObjectInfoData site)
        {
            var facts = new List<BuildFact>();
            if (site.ProductionItem == null) return facts;
            foreach (ProductionItem p in site.ProductionItem)
            {
                if (p == null || Built(p)) continue;
                facts.Add(new BuildFact { ItemId = p.ID, Item = TypeOf(p), Craft = p is SpacecraftConstructData,
                    CountToBuild = p.CountToBuild, CountBuild = p.CountBuild });
            }
            return facts;
        }

        static List<QueueRow> QueueOf(ObjectInfoData site)
        {
            var rows = new List<QueueRow>();
            List<ProductionQuery> q = Query(site);
            if (q == null) return rows;
            foreach (ProductionQuery e in q)
                if (e != null)
                    rows.Add(new QueueRow { Item = e.MyIDScriptableObjectProductionItem != null
                        ? e.MyIDScriptableObjectProductionItem.ID : "", Count = e.countToBuild });
            return rows;
        }

        static SiteBuildState BuildsOf(Company c, string agency, ObjectInfoData site)
        {
            var rows = new List<BuildRow>();
            if (site.ProductionItem != null)
                foreach (ProductionItem p in site.ProductionItem)
                {
                    if (p == null || Built(p)) continue;
                    var craft = p as SpacecraftConstructData;
                    var row = new BuildRow { ItemId = p.ID, Item = TypeOf(p), Craft = craft != null,
                        Count = p.CountToBuild, CountBuild = p.CountBuild, Progress = ProgressOf(p) };
                    if (craft != null)
                    {
                        // the language-independent key of the craft's name and the number its name was made from;
                        // each game composes the display name itself (N7)
                        row.NameKey = row.Item;
                        row.NameNumber = ObjectRows.NameNumber(craft.RocketGiveName);
                    }
                    rows.Add(row);
                }
            return new SiteBuildState
            {
                Counters = NameCounters.WithSite(new[] { new CounterRow { Which = CounterKind.ProductionCountId,
                    Agency = agency, Before = c.productionCountID, After = c.productionCountID } }, c, agency,
                    rows.ToArray()),
                Building = rows.ToArray(),
                Queue = QueueOf(site).ToArray()
            };
        }

        // A facility's type from its descriptor; anything else from the saved id (the getter caches and, for a
        // facility, writes the id).
        static string TypeOf(ProductionItem p)
        {
            var f = p as Facility;
            if (f != null) return f.facilityDescriptor != null ? f.facilityDescriptor.ID : "";
            return TypeId(p) ?? "";
        }

        // ProductionItem.BuildProgress without its writes: the first unit not finished, as the getter finds it.
        static double ProgressOf(ProductionItem p)
        {
            float first = Progress1(p);
            List<float> list = Units(p);
            if (p.CountToBuild == 0) return list != null && list.Count == 1 ? list[0] : first;
            int n = list == null ? 0 : list.Count;
            int total = Math.Max(n == 0 ? 1 : n, p.CountBuild);
            for (int i = 0; i < total; i++)
            {
                float x = i < n ? list[i] : (i == 0 ? first : 0f);
                if (!(x >= 1f)) return x;
            }
            return p.CountBuild < n ? list[p.CountBuild] : first;
        }

        // ---- facilities ----

        static List<FacilityFact> FacilityFacts(ObjectInfoData site)
        {
            var facts = new List<FacilityFact>();
            if (site.ListFacility == null) return facts;
            foreach (Facility f in site.ListFacility)
                if (f != null)
                    facts.Add(new FacilityFact { ItemId = f.ID, Item = TypeOf(f), Quantity = Quantity(f),
                        Enabled = Enabled(f), Built = Built(f) });
            return facts;
        }

        // The stored priority of each type that has built facilities here: the game's default for a type it has not
        // stored yet (what its own GetPriority would add).
        static Dictionary<string, int> Priorities(ObjectInfoData site)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<FacilityBaseDescriptor, int> stored = Priority(site);
            if (site.ListFacility == null) return result;
            foreach (Facility f in site.ListFacility)
            {
                if (f == null || !Built(f) || f.facilityDescriptor == null) continue;
                int p;
                if (stored == null || !stored.TryGetValue(f.facilityDescriptor, out p))
                    p = f.facilityDescriptor.Priority;
                result[f.facilityDescriptor.ID] = p;
            }
            return result;
        }

        static List<string> MiningOf(ObjectInfoData site)
        {
            var keys = new List<string>();
            List<RowExploredResourcesData> rows = site.listExploredResourcesRows;
            for (int i = 0; rows != null && i < rows.Count; i++)
            {
                RowExploredResourcesData r = rows[i];
                if (r != null && r.SelectedToMine)
                    keys.Add(ObjectRows.MiningKey(r.ResourceType != null ? r.ResourceType.ID : "", i));
            }
            return keys;
        }

        static SiteFacilityState FacilitiesOf(ObjectInfoData site)
        {
            var built = new List<FacilityRow>();
            Dictionary<string, int> priorities = Priorities(site);
            foreach (FacilityFact f in FacilityFacts(site))
            {
                if (!f.Built) continue;
                int p;
                priorities.TryGetValue(f.Item, out p);
                built.Add(new FacilityRow { Item = f.Item, ItemId = f.ItemId, Count = Units32(f.Quantity),
                    Enabled = Units32(f.Enabled), Priority = p });
            }
            return new SiteFacilityState { Built = built.ToArray(), Mining = MiningOf(site).ToArray() };
        }

        // A unit count as the message carries it: past the cap is not a count any game makes (the message's own
        // check then refuses the copy, and the client rejoins fully).
        static int Units32(long v) { return v < int.MinValue || v > int.MaxValue ? int.MaxValue : (int)v; }
    }
}
