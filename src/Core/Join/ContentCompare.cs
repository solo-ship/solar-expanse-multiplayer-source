using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // How one plugin or assembly differs between the host and the joiner.
    public sealed class PluginChange
    {
        public string Id; public string Name; public string HostVersion, JoinerVersion;
        public bool HashDiffers;            // the file hash (or, for an assembly loaded from bytes, its MVID) differs
        public bool OnlyHost, OnlyJoiner;
    }

    public sealed class ContentDiff
    {
        public bool Same; public bool Build; public bool GameVersion; public bool Mod; public bool Managed;
        public bool Overall;      // the overall hash differs although every category matched: a broken or forged summary, never "same"
        public bool LoaderOnly;   // the two sides differ only in loader: informational, never blocks (user)
        public List<PluginChange> Plugins; public List<PluginChange> Assemblies; public List<int> Categories;
    }

    // Compares two summaries (Design decision 7). Text from the peer reached the host through Validate, which sanitises it; the
    // host's own summary was never sanitised. Both sides are therefore put through the same TextSanitiser calls here before anything
    // is compared, and ids that clean to the same text (several "?") are kept as a list per id.
    public static class ContentCompare
    {
        internal static string CleanId(string s) { return TextSanitiser.Clean(s, 128, "?"); }
        internal static string CleanName(string s) { return TextSanitiser.Clean(s, 64, ""); }
        internal static string CleanVersion(string s) { return TextSanitiser.Clean(s, 32, ""); }

        sealed class Entry { public string Id, Name, Version, Sha, Mvid; }

        // The entries that count: loader files left out (judged on the cleaned entry, so both sides are judged alike).
        static List<Entry> Counted(PluginEntry[] list, string loader)
        {
            var result = new List<Entry>();
            bool singleton = false;
            if (list == null) return result;
            foreach (PluginEntry p in list)
            {
                if (p == null) continue;
                var clean = new PluginEntry { Id = p.Id == null ? null : CleanId(p.Id), Name = p.Name == null ? null : CleanName(p.Name),
                                              Version = p.Version == null ? null : CleanVersion(p.Version), Sha256 = p.Sha256, Where = p.Where, Mvid = p.Mvid };
                if (LoaderFiles.IsExcluded(clean, loader))
                {
                    if (!LoaderFiles.IsSingleton(clean)) continue;
                    if (!singleton) { singleton = true; continue; }   // HarmonySharedState: the first is left out, a second one counts
                }
                result.Add(new Entry { Id = clean.Id ?? "?", Name = clean.Name ?? "", Version = clean.Version ?? "", Sha = clean.Sha256 ?? "", Mvid = clean.Mvid ?? "" });
            }
            return result;
        }

        static bool SameEntry(Entry a, Entry b) { return a.Version == b.Version && a.Sha == b.Sha && a.Mvid == b.Mvid; }

        static int Order(Entry a, Entry b)
        {
            int c = string.CompareOrdinal(a.Id, b.Id);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Version, b.Version);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Sha, b.Sha);
            return c != 0 ? c : string.CompareOrdinal(a.Mvid, b.Mvid);
        }

        static List<PluginChange> DiffEntries(PluginEntry[] hostList, string hostLoader, PluginEntry[] joinerList, string joinerLoader)
        {
            List<Entry> host = Counted(hostList, hostLoader), joiner = Counted(joinerList, joinerLoader);
            host.Sort(Order); joiner.Sort(Order);
            var changes = new List<PluginChange>();
            int h = 0, j = 0;
            while (h < host.Count || j < joiner.Count)
            {
                string id = h < host.Count && (j >= joiner.Count || string.CompareOrdinal(host[h].Id, joiner[j].Id) <= 0) ? host[h].Id : joiner[j].Id;
                var hs = new List<Entry>(); var js = new List<Entry>();
                while (h < host.Count && host[h].Id == id) hs.Add(host[h++]);
                while (j < joiner.Count && joiner[j].Id == id) js.Add(joiner[j++]);
                // identical entries cancel; what is left on both sides is paired in order, the rest is on one side only
                for (int a = 0; a < hs.Count; a++)
                    for (int b = 0; b < js.Count; b++)
                        if (SameEntry(hs[a], js[b])) { hs.RemoveAt(a); js.RemoveAt(b); a--; break; }
                int pairs = Math.Min(hs.Count, js.Count);
                for (int k = 0; k < pairs; k++)
                    changes.Add(new PluginChange
                    {
                        Id = id, Name = hs[k].Name.Length > 0 ? hs[k].Name : js[k].Name, HostVersion = hs[k].Version, JoinerVersion = js[k].Version,
                        HashDiffers = hs[k].Sha != js[k].Sha || hs[k].Mvid != js[k].Mvid,
                    });
                for (int k = pairs; k < hs.Count; k++) changes.Add(new PluginChange { Id = id, Name = hs[k].Name, HostVersion = hs[k].Version, JoinerVersion = "", OnlyHost = true });
                for (int k = pairs; k < js.Count; k++) changes.Add(new PluginChange { Id = id, Name = js[k].Name, HostVersion = "", JoinerVersion = js[k].Version, OnlyJoiner = true });
            }
            return changes;
        }

        static CategoryHash Find(CategoryHash[] list, int category)
        {
            if (list == null) return null;
            foreach (CategoryHash c in list) if (c != null && c.Category == category) return c;
            return null;
        }

        static bool SameText(string a, string b, int max, string fallback) { return TextSanitiser.Clean(a, max, fallback) == TextSanitiser.Clean(b, max, fallback); }

        public static ContentDiff Compare(ContentSummary host, ContentSummary joiner)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (joiner == null) throw new ArgumentNullException("joiner");
            var d = new ContentDiff { Categories = new List<int>() };
            d.Build = !SameText(host.GameBuild, joiner.GameBuild, 96, "?");
            d.GameVersion = !SameText(host.GameVersion, joiner.GameVersion, 32, "?");
            d.Mod = !SameText(host.ModVersion, joiner.ModVersion, 32, "?");
            d.Managed = host.ManagedHash != joiner.ManagedHash || host.ManagedCount != joiner.ManagedCount;
            d.Plugins = DiffEntries(host.Plugins, host.Loader, joiner.Plugins, joiner.Loader);
            d.Assemblies = DiffEntries(host.Assemblies, host.Loader, joiner.Assemblies, joiner.Loader);
            for (int c = 0; c < ContentCategory.Count; c++)
            {
                CategoryHash a = Find(host.Categories, c), b = Find(joiner.Categories, c);
                if (a == null && b == null) continue;
                if (a == null || b == null || a.Count != b.Count || a.Hash != b.Hash) d.Categories.Add(c);
            }
            d.Overall = d.Categories.Count == 0 && host.Overall != joiner.Overall;
            d.Same = !(d.Build || d.GameVersion || d.Mod || d.Managed || d.Overall || d.Plugins.Count > 0 || d.Assemblies.Count > 0 || d.Categories.Count > 0);
            d.LoaderOnly = d.Same && host.Loader != joiner.Loader;
            return d;
        }
    }

    // The host's report of what differs, and the joiner's detail parts that feed it. Every line passes through TextSanitiser and the
    // whole report through TextBudget.FitLines (I-13), so a JoinRefused made of it always encodes: callers still look at what
    // Link.Send returns (a message that cannot go out is a lost link, not a sent one).
    public static class ContentReport
    {
        const int MaxNamedIds = 5;          // "Facilities: 3 differ (a, b, c)": up to five ids are named
        const int MaxPluginLines = 6;       // per group (plugins, assemblies); the rest is counted
        const int PartOverheadBytes = 160;  // a ContentDetail's JSON skeleton and frame header, counted generously
        const int PartPayloadBudget = 60 * 1024 - 1024;

        sealed class Pair { public string Id, Hash; }

        static int Order(Pair a, Pair b)
        {
            int c = string.CompareOrdinal(a.Id, b.Id);
            return c != 0 ? c : string.CompareOrdinal(a.Hash, b.Hash);
        }

        // An item list with ids cleaned the way ContentDetail.Validate cleans them, sorted by that cleaned id.
        static List<Pair> Normalise(ItemList l)
        {
            var r = new List<Pair>();
            if (l == null || l.Ids == null) return r;
            for (int i = 0; i < l.Ids.Length; i++)
                r.Add(new Pair { Id = ContentCompare.CleanId(l.Ids[i] ?? ""), Hash = l.Hashes != null && i < l.Hashes.Length ? l.Hashes[i] ?? "" : "" });
            r.Sort(Order);
            return r;
        }

        public static string[] Lines(ContentDiff d, ContentSummary host, ContentSummary joiner, IDictionary<int, ItemList> hostItems, IDictionary<int, ItemList> joinerItems)
        {
            if (d == null) throw new ArgumentNullException("d");
            if (host == null) throw new ArgumentNullException("host");
            if (joiner == null) throw new ArgumentNullException("joiner");
            var lines = new List<string>();
            if (d.Build || d.GameVersion)
            {
                string hv = ContentCompare.CleanVersion(host.GameVersion), jv = ContentCompare.CleanVersion(joiner.GameVersion);
                lines.Add(d.GameVersion ? JoinText.GameVersionLine(hv, jv)
                    : JoinText.GameBuildLine(hv, host.GameBuild, joiner.GameBuild));
            }
            if (d.Mod) lines.Add(JoinText.ModVersionLine(ContentCompare.CleanVersion(host.ModVersion), ContentCompare.CleanVersion(joiner.ModVersion)));
            if (d.Managed) lines.Add(JoinText.ManagedLine());
            AddChanges(lines, "Plugin", d.Plugins);
            AddChanges(lines, "Mod file", d.Assemblies);   // the player-facing word for a loaded assembly
            if (d.Categories != null)
                foreach (int c in d.Categories) AddCategory(lines, c, host, joiner, Get(hostItems, c), Get(joinerItems, c));
            if (lines.Count == 0 && !d.Same) lines.Add(JoinText.OverallLine());
            var clean = new List<string>();
            foreach (string l in lines)
            {
                string s = TextSanitiser.Clean(l, JoinLimits.MaxReportLineChars, "");
                if (s.Length > 0) clean.Add(s);
            }
            return TextBudget.FitLines(clean.ToArray(), JoinLimits.MaxReportLines, JoinLimits.MaxReportLineChars, JoinLimits.ReportMaxBytes);
        }

        static ItemList Get(IDictionary<int, ItemList> items, int category)
        {
            ItemList l;
            return items != null && items.TryGetValue(category, out l) ? l : null;
        }

        static void AddChanges(List<string> lines, string label, List<PluginChange> changes)
        {
            if (changes == null) return;
            for (int i = 0; i < changes.Count && i < MaxPluginLines; i++)
            {
                PluginChange c = changes[i];
                string name = !string.IsNullOrEmpty(c.Name) ? c.Name : c.Id;
                if (c.OnlyHost) lines.Add(FitName(name, n => JoinText.OnlyHostLine(label, n, c.HostVersion)));
                else if (c.OnlyJoiner) lines.Add(FitName(name, n => JoinText.OnlyJoinerLine(label, n, c.JoinerVersion)));
                else if (c.HostVersion != c.JoinerVersion) lines.Add(FitName(name, n => JoinText.VersionsLine(label, n, c.HostVersion, c.JoinerVersion)));
                else lines.Add(FitName(name, n => JoinText.SameVersionOtherFileLine(label, n, c.HostVersion)));
            }
            if (changes.Count > MaxPluginLines) lines.Add(JoinText.MoreDifferencesLine(label, changes.Count - MaxPluginLines));
        }

        // A name too long for the line is cut (with "...") so the rest of the line, such as the "you X" half, is never the part that is lost.
        static string FitName(string name, Func<string, string> line)
        {
            string full = line(name);
            if (full.Length <= JoinLimits.MaxReportLineChars) return full;
            int room = JoinLimits.MaxReportLineChars - (full.Length - name.Length);
            if (room < 4) room = 4;
            int cut = Math.Min(room - 3, name.Length);
            if (cut > 0 && char.IsHighSurrogate(name[cut - 1])) cut--;
            return line(name.Substring(0, cut) + "...");
        }

        static int Count(CategoryHash[] list, int category)
        {
            if (list != null) foreach (CategoryHash c in list) if (c != null && c.Category == category) return c.Count;
            return 0;
        }

        // One category: which items the joiner lacks, which it has extra, which differ. A trimmed list (Complete false) is a sorted
        // prefix, so only ids up to its last one are compared and every figure is a minimum ("at least N").
        static void AddCategory(List<string> lines, int category, ContentSummary host, ContentSummary joiner, ItemList hostList, ItemList joinerList)
        {
            string cat = ContentCategory.Name(category);
            int hostCount = Count(host.Categories, category), joinerCount = Count(joiner.Categories, category);
            if (hostList == null || joinerList == null)
            {
                lines.Add(JoinText.CategoryDiffersLine(cat, hostCount, joinerCount));
                return;
            }
            List<Pair> hp = Normalise(hostList), jp = Normalise(joinerList);
            bool atLeast = !hostList.Complete || !joinerList.Complete;
            if (atLeast)
            {
                string limit = null; bool none = false;
                foreach (var side in new[] { new KeyValuePair<ItemList, List<Pair>>(hostList, hp), new KeyValuePair<ItemList, List<Pair>>(joinerList, jp) })
                {
                    if (side.Key.Complete) continue;
                    if (side.Value.Count == 0) { none = true; continue; }
                    string last = side.Value[side.Value.Count - 1].Id;
                    if (limit == null || string.CompareOrdinal(last, limit) < 0) limit = last;
                }
                hp = Upto(hp, limit, none); jp = Upto(jp, limit, none);
            }
            var missing = new List<string>(); var extra = new List<string>(); var differ = new List<string>();
            int h = 0, j = 0;
            while (h < hp.Count || j < jp.Count)
            {
                string id = h < hp.Count && (j >= jp.Count || string.CompareOrdinal(hp[h].Id, jp[j].Id) <= 0) ? hp[h].Id : jp[j].Id;
                var hs = new List<Pair>(); var js = new List<Pair>();
                while (h < hp.Count && hp[h].Id == id) hs.Add(hp[h++]);
                while (j < jp.Count && jp[j].Id == id) js.Add(jp[j++]);
                for (int a = 0; a < hs.Count; a++)
                    for (int b = 0; b < js.Count; b++)
                        if (hs[a].Hash == js[b].Hash) { hs.RemoveAt(a); js.RemoveAt(b); a--; break; }
                int pairs = Math.Min(hs.Count, js.Count);
                for (int k = 0; k < pairs; k++) differ.Add(id);
                for (int k = pairs; k < hs.Count; k++) missing.Add(id);
                for (int k = pairs; k < js.Count; k++) extra.Add(id);
            }
            if (missing.Count + extra.Count + differ.Count == 0) { lines.Add(JoinText.CategoryDiffersLine(cat, hostCount, joinerCount)); return; }
            if (missing.Count > 0) lines.Add(Named(missing, ids => JoinText.MissingLine(cat, missing.Count, atLeast, ids)));
            if (extra.Count > 0) lines.Add(Named(extra, ids => JoinText.ExtraLine(cat, extra.Count, atLeast, ids)));
            if (differ.Count > 0) lines.Add(Named(differ, ids => JoinText.DifferLine(cat, differ.Count, atLeast, ids)));
        }

        static List<Pair> Upto(List<Pair> list, string limit, bool none)
        {
            var r = new List<Pair>();
            if (none || limit == null) return none ? r : list;
            foreach (Pair p in list) if (string.CompareOrdinal(p.Id, limit) <= 0) r.Add(p);
            return r;
        }

        // The line with as many ids named as fit (at most five, at least one), then "... and N more".
        static string Named(List<string> ids, Func<string, string> line)
        {
            int most = Math.Min(MaxNamedIds, ids.Count);
            for (int k = most; k >= 1; k--)
            {
                string text = string.Join(", ", ids.GetRange(0, k).ToArray());
                if (k < ids.Count) text += ", ... and " + (ids.Count - k) + " more";
                string full = line(text);
                if (full.Length <= JoinLimits.MaxReportLineChars) return full;
            }
            // even one id is too long for a line: cut it, so the count and the tail still show
            string tail = ids.Count > 1 ? ", ... and " + (ids.Count - 1) + " more" : "";
            int room = JoinLimits.MaxReportLineChars - line("x").Length + 1 - tail.Length;
            string one = ids[0];
            if (room < 4) room = 4;
            if (one.Length > room)
            {
                int cut = room - 3;
                if (cut > 0 && char.IsHighSurrogate(one[cut - 1])) cut--;
                one = one.Substring(0, cut) + "...";
            }
            return line(one + tail);
        }

        // ---- the joiner's side ----

        // The ContentDetail parts for the categories the host asked about: ids cleaned and sorted, at most MaxDetailItemsPerPart items
        // and 60 KiB a part, MaxDetailParts parts a category and MaxDetailBytesTotal in all, shared between the categories so one big
        // category cannot starve the rest. A category that does not fit is cut to a sorted prefix and its ItemList is marked
        // Complete = false; every requested category answers with at least one (possibly empty) part, the last one marked Last.
        // `own` is the summary these parts belong to; the host compares the received count with that summary's category count.
        public static ContentDetail[] Details(ContentSummary own, IDictionary<int, ItemList> items, int[] requested)
        {
            if (own == null) throw new ArgumentNullException("own");
            var parts = new List<ContentDetail>();
            if (requested == null) return parts.ToArray();
            var cats = new List<int>();
            foreach (int c in requested) if (c >= 0 && c < ContentCategory.Count && !cats.Contains(c)) cats.Add(c);
            int budget = JoinLimits.MaxDetailBytesTotal;
            for (int n = 0; n < cats.Count; n++)
            {
                int category = cats[n];
                int share = budget / (cats.Count - n);
                ItemList list = Get(items, category);
                List<Pair> pairs = Normalise(list);
                var ids = new List<string>(); var hashes = new List<string>();
                int used = 0, partNo = 0, bytes = PartOverheadBytes;   // used: parts already closed; bytes: the open part so far
                bool trimmed = false;
                for (int i = 0; i < pairs.Count; i++)
                {
                    int cost = ItemBytes(pairs[i].Id);
                    if (ids.Count >= JoinLimits.MaxDetailItemsPerPart || bytes + cost > PartPayloadBudget)
                    {
                        // the open part is full: close it and start the next, if the part count and the share allow another
                        if (ids.Count == 0 || partNo + 1 >= JoinLimits.MaxDetailParts || used + bytes + PartOverheadBytes + cost > share) { trimmed = true; break; }
                        parts.Add(Part(category, partNo++, false, ids, hashes));
                        used += bytes; ids = new List<string>(); hashes = new List<string>(); bytes = PartOverheadBytes;
                    }
                    else if (used + bytes + cost > share) { trimmed = true; break; }
                    ids.Add(pairs[i].Id); hashes.Add(pairs[i].Hash); bytes += cost;
                }
                parts.Add(Part(category, partNo, true, ids, hashes));
                used += bytes;
                budget -= used;
                if (trimmed && list != null) list.Complete = false;
            }
            return parts.ToArray();
        }

        // The measure Details trims to, for one part: its JSON skeleton and frame header (PartOverheadBytes), then each item. The
        // host counts what it receives with this same method (JoinService, against MaxDetailBytesTotal), so an honest joiner's
        // parts always pass and the two sides can never disagree. Never less than the part's real JSON.
        public static int DetailBytes(ContentDetail d)
        {
            int n = PartOverheadBytes;
            if (d != null && d.Ids != null) foreach (string id in d.Ids) n += ItemBytes(id);
            return n;
        }

        static int ItemBytes(string id) { return TextBudget.JsonBytes(id) + 22; }   // "id","hash", and a comma each

        static ContentDetail Part(int category, int part, bool last, List<string> ids, List<string> hashes)
        {
            return new ContentDetail { Category = category, Part = part, Last = last, Ids = ids.ToArray(), Hashes = hashes.ToArray() };
        }

        // The host's view of a joiner's category once its parts have arrived: items in part order, Complete when as many arrived as
        // the joiner's summary says the category holds (a shorter list was cut to the detail budget).
        public static ItemList ItemsFrom(IEnumerable<ContentDetail> parts, int expectedCount)
        {
            var sorted = new List<ContentDetail>();
            if (parts != null) foreach (ContentDetail p in parts) if (p != null && p.Ids != null && p.Hashes != null) sorted.Add(p);
            sorted.Sort((a, b) => a.Part.CompareTo(b.Part));
            var ids = new List<string>(); var hashes = new List<string>();
            foreach (ContentDetail p in sorted)
                for (int i = 0; i < p.Ids.Length && i < p.Hashes.Length; i++) { ids.Add(p.Ids[i]); hashes.Add(p.Hashes[i]); }
            return new ItemList { Ids = ids.ToArray(), Hashes = hashes.ToArray(), Complete = ids.Count == expectedCount };
        }
    }
}
