using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The content check's hashing (M4 Task 5; plan "Interfaces, Content" and Design decision 7). The game-side scan (Task 10) walks
    // the definitions by reflection and feeds every effective value into a DefinitionHasher; this file only turns that stream into
    // hashes, so it is pure and unit-tested. Nothing here reads the game.

    // One category's items, sorted by id (ordinal), each with its 16-hex item hash. Complete is false when the list was cut to
    // the detail budget: the report then says "at least N".
    public sealed class ItemList { public string[] Ids; public string[] Hashes; public bool Complete; }

    internal static class ContentHex
    {
        const string Digits = "0123456789abcdef";

        public static string Of(byte[] bytes, int count)
        {
            var c = new char[count * 2];
            for (int i = 0; i < count; i++) { c[i * 2] = Digits[bytes[i] >> 4]; c[i * 2 + 1] = Digits[bytes[i] & 15]; }
            return new string(c);
        }

        // length-prefixed UTF-8, so no two field sequences write the same bytes; null is told apart from empty
        public static void Str(BinaryWriter w, string s)
        {
            if (s == null) { w.Write((byte)0); return; }
            byte[] b = Encoding.UTF8.GetBytes(s);
            w.Write((byte)1);
            w.Write(b.Length);
            w.Write(b);
        }
    }

    // Hashes the effective values of the game's definitions, one item at a time.
    // - Across items the order does not matter (an item is hashed alone and the category sorts its (id, hash) pairs).
    // - Within an item every field's name, kind and value count, in the order given; objects and lists are bracketed.
    // - A float is hashed by its bits with -0.0 as 0.0 and every NaN as one NaN.
    // - Misuse (a field outside an item, a mismatched End...) throws: it is a bug in the scan, never something to hash.
    public sealed class DefinitionHasher
    {
        // Fields: each top-level field's name and a 64-bit FNV-1a of its bytes, for naming what changed in a log line.
        sealed class Rec
        {
            public string Id, Hash;
            public string[] FieldNames;
            public ulong[] FieldHashes;
        }

        const byte TStr = 1, TInt = 2, TNum = 3, TBool = 4, TRef = 5, TBeginObject = 6, TEndObject = 7, TBeginList = 8, TEndList = 9;
        const long CanonicalNaNBits = 0x7FF8000000000000L;

        readonly List<Rec>[] items = new List<Rec>[ContentCategory.Count];
        readonly MemoryStream buf = new MemoryStream();
        readonly BinaryWriter w;
        sealed class Frame { public byte Kind; public int Declared, Written; }
        readonly Stack<Frame> open = new Stack<Frame>();
        bool inItem;
        int category;
        string id;
        readonly List<string> segNames = new List<string>();
        readonly List<int> segStarts = new List<int>();

        public DefinitionHasher()
        {
            w = new BinaryWriter(buf);
            for (int i = 0; i < items.Length; i++) items[i] = new List<Rec>();
        }

        public void BeginItem(int category, string id)
        {
            if (category < 0 || category >= ContentCategory.Count) throw new ArgumentOutOfRangeException("category");
            if (inItem) throw new InvalidOperationException("BeginItem inside an item");
            inItem = true; this.category = category; this.id = id ?? "";
            buf.SetLength(0);
            open.Clear();
            segNames.Clear();
            segStarts.Clear();
        }

        void Field(byte tag, string field)
        {
            if (!inItem) throw new InvalidOperationException("a field outside an item");
            if (open.Count > 0 && open.Peek().Kind == TBeginList) open.Peek().Written++;   // an element of the open list
            if (open.Count == 0) { w.Flush(); segNames.Add(field); segStarts.Add((int)buf.Length); }
            w.Write(tag);
            ContentHex.Str(w, field);
        }

        public void Str(string field, string value) { Field(TStr, field); ContentHex.Str(w, value); }
        public void Int(string field, long value) { Field(TInt, field); w.Write(value); }
        public void Bool(string field, bool value) { Field(TBool, field); w.Write(value ? (byte)1 : (byte)0); }
        public void Ref(string field, string id) { Field(TRef, field); ContentHex.Str(w, id); }

        public void Num(string field, double value)
        {
            Field(TNum, field);
            long bits = double.IsNaN(value) ? CanonicalNaNBits : value == 0 ? 0L : BitConverter.DoubleToInt64Bits(value);   // -0.0 == 0
            w.Write(bits);
        }

        public void BeginObject(string field) { Field(TBeginObject, field); open.Push(new Frame { Kind = TBeginObject }); }

        public void EndObject()
        {
            if (!inItem || open.Count == 0 || open.Peek().Kind != TBeginObject) throw new InvalidOperationException("EndObject without a matching BeginObject");
            open.Pop(); w.Write(TEndObject);
        }

        public void BeginList(string field, int count) { Field(TBeginList, field); w.Write(count); open.Push(new Frame { Kind = TBeginList, Declared = count }); }

        public void EndList()
        {
            if (!inItem || open.Count == 0 || open.Peek().Kind != TBeginList) throw new InvalidOperationException("EndList without a matching BeginList");
            Frame list = open.Peek();
            if (list.Written != list.Declared) throw new InvalidOperationException("EndList: " + list.Written + " elements written for a declared count of " + list.Declared);
            open.Pop(); w.Write(TEndList);
        }

        // A duplicate id in a category is kept: both items are listed, counted and hashed (nothing collapses them, as a dictionary keyed by
        // id would), so a category with a duplicate never hashes like one without it, and Count says how many there are.
        public void EndItem()
        {
            if (!inItem) throw new InvalidOperationException("EndItem without BeginItem");
            if (open.Count != 0) throw new InvalidOperationException("EndItem with an object or list still open");
            w.Flush();
            byte[] bytes = buf.GetBuffer();
            int end = (int)buf.Length;
            var names = segNames.ToArray();
            var fieldHashes = new ulong[names.Length];
            for (int i = 0; i < names.Length; i++)
                fieldHashes[i] = Fnv(bytes, segStarts[i], i + 1 < names.Length ? segStarts[i + 1] : end);
            using (SHA256 sha = SHA256.Create())
                items[category].Add(new Rec { Id = id, Hash = ContentHex.Of(sha.ComputeHash(bytes, 0, end), 8),
                    FieldNames = names, FieldHashes = fieldHashes });
            inItem = false;
        }

        static ulong Fnv(byte[] b, int from, int to)
        {
            ulong h = 14695981039346656037UL;
            for (int i = from; i < to; i++) { h ^= b[i]; h *= 1099511628211UL; }
            return h;
        }

        // The items that differ between two scans, category by category in id order. An item with the same id and hash
        // on both sides is unchanged, so of two items sharing an id only the one that changed is named. A leftover pair
        // under one id is a change, named with the top-level fields whose bytes differ; the rest are only before or
        // only now.
        public static List<ItemChange> Changes(DefinitionHasher before, DefinitionHasher now)
        {
            var result = new List<ItemChange>();
            if (before == null || now == null) return result;
            for (int c = 0; c < ContentCategory.Count; c++)
            {
                List<Rec> a = before.Sorted(c), b = now.Sorted(c);
                int i = 0, j = 0;
                while (i < a.Count || j < b.Count)
                {
                    bool fromA = i < a.Count && (j >= b.Count || string.CompareOrdinal(a[i].Id, b[j].Id) <= 0);
                    string key = fromA ? a[i].Id : b[j].Id;
                    var x = new List<Rec>(); var y = new List<Rec>();
                    while (i < a.Count && a[i].Id == key) x.Add(a[i++]);
                    while (j < b.Count && b[j].Id == key) y.Add(b[j++]);
                    for (int p = 0; p < x.Count; p++)
                        for (int q = 0; q < y.Count; q++)
                            if (x[p].Hash == y[q].Hash) { x.RemoveAt(p); y.RemoveAt(q); p--; break; }
                    int pairs = Math.Min(x.Count, y.Count);
                    for (int k = 0; k < pairs; k++)
                        result.Add(Change(c, key, ItemChange.Changed, FieldsDiffering(x[k], y[k])));
                    for (int k = pairs; k < x.Count; k++)
                        result.Add(Change(c, key, ItemChange.OnlyBefore, new string[0]));
                    for (int k = pairs; k < y.Count; k++)
                        result.Add(Change(c, key, ItemChange.OnlyNow, new string[0]));
                }
            }
            return result;
        }

        static ItemChange Change(int category, string id, int kind, string[] fields)
        {
            return new ItemChange { Category = category, Id = id, Kind = kind, Fields = fields };
        }

        // Field names in the later item's order, then any the earlier one alone had; a name appears once. Two items of
        // one type have the same field list (a base type's private field may share a derived one's name), so those are
        // compared place by place.
        static string[] FieldsDiffering(Rec a, Rec b)
        {
            var names = new List<string>();
            bool sameList = a.FieldNames.Length == b.FieldNames.Length;
            for (int k = 0; sameList && k < a.FieldNames.Length; k++) sameList = a.FieldNames[k] == b.FieldNames[k];
            if (sameList)
            {
                for (int k = 0; k < a.FieldNames.Length; k++)
                    if (a.FieldHashes[k] != b.FieldHashes[k]) AddOnce(names, a.FieldNames[k]);
                return names.ToArray();
            }
            for (int k = 0; k < b.FieldNames.Length; k++)
            {
                int at = Array.IndexOf(a.FieldNames, b.FieldNames[k]);
                if (at < 0 || a.FieldHashes[at] != b.FieldHashes[k]) AddOnce(names, b.FieldNames[k]);
            }
            for (int k = 0; k < a.FieldNames.Length; k++)
                if (Array.IndexOf(b.FieldNames, a.FieldNames[k]) < 0) AddOnce(names, a.FieldNames[k]);
            return names.ToArray();
        }

        static void AddOnce(List<string> names, string n) { if (!names.Contains(n)) names.Add(n); }

        static int Order(Rec a, Rec b)
        {
            int c = string.CompareOrdinal(a.Id, b.Id);
            return c != 0 ? c : string.CompareOrdinal(a.Hash, b.Hash);
        }

        List<Rec> Sorted(int c)
        {
            if (c < 0 || c >= ContentCategory.Count) throw new ArgumentOutOfRangeException("category");
            var list = new List<Rec>(items[c]);
            list.Sort(Order);
            return list;
        }

        // Every category in order (empty ones too), each: the item count and a SHA-256 over its sorted (id, item hash) pairs.
        public CategoryHash[] Categories()
        {
            var result = new CategoryHash[ContentCategory.Count];
            for (int c = 0; c < result.Length; c++)
            {
                List<Rec> list = Sorted(c);
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        ContentHex.Str(bw, list[i].Id);
                        ContentHex.Str(bw, list[i].Hash);
                    }
                    bw.Flush();
                    using (SHA256 sha = SHA256.Create())
                        result[c] = new CategoryHash { Category = c, Count = list.Count, Hash = ContentHex.Of(sha.ComputeHash(ms.ToArray()), 32) };
                }
            }
            return result;
        }

        // SHA-256 over the categories in order (number, count, hash), so it covers every definition the game has.
        public string Overall()
        {
            CategoryHash[] cats = Categories();
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                foreach (CategoryHash c in cats) { bw.Write(c.Category); bw.Write(c.Count); ContentHex.Str(bw, c.Hash); }
                bw.Flush();
                using (SHA256 sha = SHA256.Create()) return ContentHex.Of(sha.ComputeHash(ms.ToArray()), 32);
            }
        }

        public ItemList Items(int category)
        {
            List<Rec> list = Sorted(category);
            var l = new ItemList { Ids = new string[list.Count], Hashes = new string[list.Count], Complete = true };
            for (int i = 0; i < list.Count; i++) { l.Ids[i] = list[i].Id; l.Hashes[i] = list[i].Hash; }
            return l;
        }
    }

    // ManagedHash: one SHA-256 over every file name and file hash of the game's Managed folder, sorted by name (ordinal), each
    // as "name\nhash\n" in UTF-8. The formula of the S7 spike (157 files, bf3fef0f...); the scan reads the folder, this folds it.
    public static class ManagedHasher
    {
        public static string Compute(string[] names, string[] hashes)
        {
            if (names == null) throw new ArgumentNullException("names");
            if (hashes == null) throw new ArgumentNullException("hashes");
            if (names.Length != hashes.Length) throw new ArgumentException("names and hashes differ in length");
            var order = new int[names.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => string.CompareOrdinal(names[a], names[b]));
            using (var sha = SHA256.Create())
            {
                foreach (int i in order)
                {
                    byte[] part = Encoding.UTF8.GetBytes(names[i] + "\n" + hashes[i] + "\n");
                    sha.TransformBlock(part, 0, part.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return ContentHex.Of(sha.Hash, 32);
            }
        }
    }

    // Puts a joiner's (or the host's) content summary together from what the scan found: the game version is clipped to its
    // 32-character cap (ModInfo.ClipGameVersion, as the Hello does), text is sanitised the way the receiver will sanitise it,
    // and a summary that would not fit (more than 256 plugins or assemblies, or past SummaryMaxBytes) is refused here with the
    // reason, so the joiner can say SummaryTooBig and send nothing (Design decision 7). The loader files stay in the lists: the
    // comparison leaves them out, the summary and the log still show them.
    public static class ContentSummaries
    {
        public static ContentSummary Build(string gameBuild, string rawGameVersion, string modVersion, string loader, string managedHash, int managedCount,
                                           IList<PluginEntry> plugins, IList<PluginEntry> assemblies, DefinitionHasher defs, out string error)
        {
            bool tooBig;
            return Build(gameBuild, rawGameVersion, modVersion, loader, managedHash, managedCount, plugins, assemblies,
                         defs, out error, out tooBig);
        }

        // tooBig: the summary was refused only for its size (past the 60 KiB cap once encoded), not for a bad field, so
        // the joiner can tell SummaryTooBig from a scan that is unusable.
        public static ContentSummary Build(string gameBuild, string rawGameVersion, string modVersion, string loader,
                                           string managedHash, int managedCount, IList<PluginEntry> plugins,
                                           IList<PluginEntry> assemblies, DefinitionHasher defs, out string error,
                                           out bool tooBig)
        {
            error = null;
            tooBig = false;
            if (defs == null) { error = "no content hashes"; return null; }
            var s = new ContentSummary
            {
                GameBuild = gameBuild, GameVersion = ModInfo.ClipGameVersion(rawGameVersion), ModVersion = modVersion, Loader = loader,
                ManagedHash = managedHash, ManagedCount = managedCount,
                Plugins = Copy(plugins), Assemblies = Copy(assemblies), Categories = defs.Categories(), Overall = defs.Overall(),
            };
            string bad = s.Validate();   // refuses a bad field and sanitises the rest in place
            if (bad != null) { error = bad; return null; }
            byte[] body; string why;
            // over 60 KiB
            if (!Envelope.TryEncode(s, 0, out body, out why)) { error = why; tooBig = true; return null; }
            return s;
        }

        static PluginEntry[] Copy(IList<PluginEntry> list)
        {
            if (list == null) return new PluginEntry[0];
            var r = new PluginEntry[list.Count];
            for (int i = 0; i < r.Length; i++)
            {
                PluginEntry p = list[i];
                r[i] = p == null ? null : new PluginEntry
                {
                    Id = p.Id == null ? null : TextSanitiser.Clean(p.Id, 128, "?"), Name = p.Name == null ? null : TextSanitiser.Clean(p.Name, 64, ""),
                    Version = p.Version == null ? null : TextSanitiser.Clean(p.Version, 32, ""), Sha256 = p.Sha256, Where = p.Where, Mvid = p.Mvid,
                };
            }
            return r;
        }
    }
}
