using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // A second reader of M3's visit (M7 DD-W10): the same calls AreaHasher hears, kept as text rows so a difference can
    // be found by group and a group can be restored. Every game builds the rows with this one class, so equal worlds
    // give equal rows.
    //
    // The row layout (the restore side reads it):
    //   Key      the key parts in visit order, then the string values in visit order, joined by '/'. The group of a row
    //            is the first part (WorldPrint.GroupOf); money and research are one fixed group each.
    //   Values   every other value in visit order as a double: a long or a bool by its number, a double as it is, a date
    //            as TWO numbers (whole days since 0001-01-01, then the ticks within that day), so no tick is lost to
    //            double rounding. A date that is none (long.MinValue) is -1 and -1.
    //   DiscreteHash   FNV-1a over the key parts and the discrete values (field id and value), as AreaHasher's Discrete
    //            stream is made, so a row's hash moves when and only when a discrete value does.
    // Never throws on a call out of order: a call outside a block or an item is ignored (the reader's own guard
    // reports the area).
    public sealed class GroupSink : IItemSink
    {
        readonly string[] agencyNames;
        readonly Dictionary<int, List<ItemRow>> blocks = new Dictionary<int, List<ItemRow>>();
        readonly StringBuilder tail = new StringBuilder();
        readonly List<double> values = new List<double>(16);
        string keyHead;
        int keyParts;
        int area = -1, agency = DriftAreas.WorldAgency, blockItems;
        bool inBlock, inItem, capped;
        ulong hash;
        List<ItemRow> current;

        // agencyNames: the wire id of each agency by the reader's agency index.
        public GroupSink(string[] agencyNames) { this.agencyNames = agencyNames ?? new string[0]; }

        // Some block went over its item cap (DriftAreas.MaxItems): its rows past the cap are not kept, as AreaHasher's
        // capture does not keep them.
        public bool Capped { get { return capped; } }

        static int Slot(int area, int agency) { return area * 65536 + (agency + 1); }

        // The rows of one block, in visit order; empty when the block was not visited.
        public IList<ItemRow> Rows(int area, int agencyIndex)
        {
            List<ItemRow> list;
            return blocks.TryGetValue(Slot(area, agencyIndex), out list) ? list : (IList<ItemRow>)new ItemRow[0];
        }

        // An area's rows as the drivers ask for them: a world area's; one agency's (its wire id) of a per-agency area;
        // or, with agency null, every agency's in the reader's agency order (a client's own group prints).
        public IList<ItemRow> Items(int area, string agency)
        {
            if (!DriftAreas.PerAgency(area)) return Rows(area, DriftAreas.WorldAgency);
            if (agency != null)
            {
                int index = Array.IndexOf(agencyNames, agency);
                return index < 0 ? (IList<ItemRow>)new ItemRow[0] : Rows(area, index);
            }
            var all = new List<ItemRow>();
            for (int i = 0; i < agencyNames.Length; i++) all.AddRange(Rows(area, i));
            return all;
        }

        public void BeginBlock(int a, int ag)
        {
            if (inBlock) EndBlock();
            area = a; agency = ag; inBlock = true; blockItems = 0;
            int slot = Slot(a, ag);
            if (!blocks.TryGetValue(slot, out current)) blocks[slot] = current = new List<ItemRow>();
            else current.Clear();
        }

        public void BeginItem()
        {
            if (!inBlock) return;
            if (inItem) EndItem();
            inItem = true;
            keyHead = null; keyParts = 0; tail.Length = 0; values.Clear();
            hash = Fnv64.Offset;
            blockItems++;
        }

        void AddKey(string part)
        {
            if (keyParts == 0) keyHead = part;
            else tail.Append('/').Append(part);
            keyParts++;
        }

        public void Key(string existing)
        {
            if (!inItem) return;
            hash = Fnv64.Text(hash, existing);
            AddKey(existing ?? "");
        }

        public void Key(long value)
        {
            if (!inItem) return;
            hash = Fnv64.Long(hash, value);
            AddKey(value.ToString(CultureInfo.InvariantCulture));
        }

        // strings join the key behind the key parts; they are discrete
        public void Val(int field, string existing)
        {
            if (!inItem) return;
            hash = Fnv64.Text(Fnv64.Long(hash, field), existing);
            tail.Append('/').Append(existing ?? "");
        }

        public void Val(int field, long value)
        {
            if (!inItem) return;
            hash = Fnv64.Long(Fnv64.Long(hash, field), value);
            values.Add(value);
        }

        public void Val(int field, bool value)
        {
            if (!inItem) return;
            hash = Fnv64.Long(Fnv64.Long(hash, field), value ? 1 : 0);
            values.Add(value ? 1 : 0);
        }

        public void Val(int field, double value)
        {
            if (!inItem) return;
            values.Add(value);
        }

        public void Date(int field, long ticks)
        {
            if (!inItem) return;
            if (ticks == long.MinValue) { values.Add(-1); values.Add(-1); return; }
            values.Add(ticks / TimeSpan.TicksPerDay);
            values.Add(ticks % TimeSpan.TicksPerDay);
        }

        public void EndItem()
        {
            if (!inItem) return;
            inItem = false;
            if (blockItems > DriftAreas.MaxItems[area]) { capped = true; return; }
            string key = keyHead == null ? "" : tail.Length == 0 ? keyHead : keyHead + tail;
            string who = agency >= 0 && agency < agencyNames.Length ? agencyNames[agency] : null;
            current.Add(new ItemRow { Agency = DriftAreas.PerAgency(area) ? who : null, Key = key,
                DiscreteHash = (long)hash, Values = values.ToArray() });
        }

        public void EndBlock()
        {
            if (inItem) EndItem();
            inBlock = false;
        }
    }
}
