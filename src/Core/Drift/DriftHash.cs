using System;
using System.Runtime.CompilerServices;

namespace SolarExpanseMultiplayer
{
    // FNV-1a 64. Byte is the standard step; Long feeds the 8 bytes little-endian; Text feeds the length (2 bytes) and then
    // each char as 2 bytes. All allocation-free.
    public static class Fnv64
    {
        public const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Byte(ulong h, byte b) { unchecked { return (h ^ b) * Prime; } }

        public static ulong Long(ulong h, long v)
        {
            unchecked
            {
                ulong u = (ulong)v;
                h = (h ^ (u & 0xFF)) * Prime;
                h = (h ^ ((u >> 8) & 0xFF)) * Prime;
                h = (h ^ ((u >> 16) & 0xFF)) * Prime;
                h = (h ^ ((u >> 24) & 0xFF)) * Prime;
                h = (h ^ ((u >> 32) & 0xFF)) * Prime;
                h = (h ^ ((u >> 40) & 0xFF)) * Prime;
                h = (h ^ ((u >> 48) & 0xFF)) * Prime;
                h = (h ^ (u >> 56)) * Prime;
                return h;
            }
        }

        // null is its own code (one 0xFF byte); an empty string is two zero bytes (its length) and nothing more.
        public static ulong Text(ulong h, string s)
        {
            unchecked
            {
                if (s == null) return (h ^ 0xFF) * Prime;
                int n = s.Length;
                h = (h ^ (ulong)(n & 0xFF)) * Prime;
                h = (h ^ (ulong)((n >> 8) & 0xFF)) * Prime;
                for (int i = 0; i < n; i++)
                {
                    char c = s[i];
                    h = (h ^ (ulong)(c & 0xFF)) * Prime;
                    h = (h ^ (ulong)(c >> 8)) * Prime;
                }
                return h;
            }
        }
    }

    // Value canonicalisation: the integer codes the three hash streams are fed with.
    public static class Quantise
    {
        public const int Digits = 4;
        // [add] One day in ticks (the day index of RoundedDate and the tests' day step).
        public const long TicksPerDay = 864000000000L;
        // [add] The fixed Rounded codes of NaN, +Infinity and -Infinity, and the "no date" value of Date / RoundedDate.
        public const long NanCode = long.MinValue, PosInfCode = long.MaxValue, NegInfCode = long.MinValue + 1, NoDate = long.MinValue;

        const int PowLo = -340, PowHi = 340;
        // 10^i at index i - PowLo. Built by repeated multiplication and division (IEEE-correct steps), so the table is the
        // same on every runtime; exact up to 1e22, within rounding noise beyond. Beyond 1e308 it is +Infinity.
        static readonly double[] Pow10 = BuildPow10();

        static double[] BuildPow10()
        {
            var t = new double[PowHi - PowLo + 1];
            double p = 1.0;
            t[-PowLo] = 1.0;
            for (int i = 1; i <= PowHi; i++) { p *= 10.0; t[i - PowLo] = p; }
            for (int i = 1; i <= -PowLo; i++) t[-i - PowLo] = i <= 22 ? 1.0 / t[i - PowLo] : t[-i + 1 - PowLo] / 10.0;
            return t;
        }

        // The IEEE bits; -0.0 is +0.0 and every NaN is one code.
        public static long Exact(double v)
        {
            if (v == 0) return 0;
            if (double.IsNaN(v)) return 0x7FF8000000000000L;
            return BitConverter.DoubleToInt64Bits(v);
        }

        // 4 significant digits, half away from zero, as (sign) x ((decimal exponent + 400) x 10000 + mantissa 1000..9999).
        // Fixed codes: 0 (and -0.0) is 0, NaN, +Inf and -Inf have their own, and a value under 1e-300 in size is +-1.
        // A mantissa that rounds up to 10000 carries into the next decade, so 9.9996 and 10.0 give one code.
        // The digits come from the value scaled by a power of ten (done with IEEE-exact steps, so every runtime agrees).
        // What matters is that this is DETERMINISTIC: the same double always gives the same code, in every call and in both
        // games. A value that scales to exactly x.5 (1.2345 gives 1234.5) rounds up; most other decimal "ties" are a little
        // above or below the decimal in binary and may round down. A tiny phase difference can still move a value across a
        // bucket edge into the next bucket, so Rounded is a coarse check, not an identity.
        public static long Rounded(double v)
        {
            if (double.IsNaN(v)) return NanCode;
            if (v == 0) return 0;
            if (double.IsPositiveInfinity(v)) return PosInfCode;
            if (double.IsNegativeInfinity(v)) return NegInfCode;
            double a = v < 0 ? -v : v;
            if (a < 1e-300) return v < 0 ? -1 : 1;
            int bin = (int)((BitConverter.DoubleToInt64Bits(a) >> 52) & 0x7FF) - 1023;
            int e = (bin * 78913) >> 18;                                   // floor(bin x log10 2), then corrected
            while (a >= Pow10[e + 1 - PowLo]) e++;
            while (a < Pow10[e - PowLo]) e--;
            int shift = e - (Digits - 1);
            double x = shift >= 0 ? a / Pow10[shift - PowLo] : a * Pow10[-shift - PowLo];
            long m = (long)(x + 0.5);
            if (m >= 10000) { m = 1000; e++; }
            else if (m < 1000) m = 1000;
            long code = (long)(e + 400) * 10000 + m;
            return v < 0 ? -code : code;
        }

        // The day index from the load origin: floor((ticks - origin) / one day); NoDate (long.MinValue) is its own code.
        // Relative to the origin, so the index does not flip at noon or midnight of the clock: two games that loaded the
        // same save sample the same day.
        public static long RoundedDate(long ticks, long originTicks)
        {
            if (ticks == NoDate) return NoDate;
            unchecked
            {
                long d = ticks - originTicks;
                long q = d / TicksPerDay;
                if (d % TicksPerDay < 0) q--;
                return q;
            }
        }
    }

    // The four hashes of one item. Key hashes the key fields alone; the other three start from it.
    public struct ItemHash { public ulong Key, Exact, Rounded, Discrete; }

    // The one sink the state reader visits every area through. int ids, no names built: strings are EXISTING game strings
    // only (hashed by their chars, never copied). One block per (area, agency) per day; an item is BeginItem, its keys
    // (Key) first, then its values (Val / Date), then EndItem.
    public interface IItemSink
    {
        void BeginBlock(int area, int agency);          // agency = index into the header's agency list, or DriftAreas.WorldAgency
        void BeginItem();
        void Key(string existing); void Key(long value);
        void Val(int field, string existing); void Val(int field, long value); void Val(int field, bool value);   // discrete
        void Val(int field, double value);                                                                      // continuous (floats widened)
        void Date(int field, long ticks);                                                                       // long.MinValue = none
        void EndItem(); void EndBlock();
    }

    // Hashes a day's visit into a preallocated DaySnapshot, and on a checkpoint day also copies every item into an
    // ItemCapture. Allocation-free after construction (a name first seen while capturing adds one IdTable entry).
    //
    // Three streams per item, each FNV-1a 64 over the fields in visit order, a separator byte after each field:
    //   Exact    doubles by IEEE bits (Quantise.Exact), dates as ticks, longs, bools, strings
    //   Rounded  doubles to 4 digits (Quantise.Rounded), dates as day indices from the origin, longs, bools, strings
    //   Discrete keys, longs, bools, strings only: a double or a date never reaches it
    // The key fields feed every stream (they are hashed once and the three streams start from that value). A value is
    // preceded by its field id. An area block's hash is the wrapping sum of its items' hashes: order-independent,
    // duplicates count, agencies add up. The block also records its item count and, per continuous value name, the sum
    // of |v| over the finite values.
    public sealed class AreaHasher : IItemSink
    {
        const byte Sep = 0xFF;
        readonly long origin;
        readonly IdTable names;
        readonly int agencyCount;       // -1: not known (only the range of a short is checked)
        readonly double[] acc;          // per field id: sum of |v| in the open block
        readonly bool[] seen;
        readonly int[] touched;
        int touchedCount;

        DaySnapshot target;
        ItemCapture capture;
        bool inBlock, inItem, keysSealed, itemCapturing, overflow, captureOverflow;
        int area, agency, blockItems, valStart;
        ulong sumE, sumR, sumD;
        ItemHash cur;

        // origin: Drift.LoadTicks (Task 6). names: used only while capturing (a null table captures every string as id -1).
        public AreaHasher(long originTicks, IdTable names) : this(originTicks, names, -1) { }

        // [add] agencyCount: the length of the header's agency list. BeginBlock then refuses an agency index outside
        // DriftAreas.WorldAgency (-1) and 0..agencyCount-1, so a bad index is caught where it is written and not when the
        // record is read (cloud review D-4). -1 checks only that the index fits the file's i16.
        public AreaHasher(long originTicks, IdTable names, int agencyCount)
        {
            origin = originTicks;
            this.names = names;
            this.agencyCount = agencyCount;
            int n = DriftFields.Names.Length;
            acc = new double[n]; seen = new bool[n]; touched = new int[n];
            Quantise.Rounded(1.0);      // builds Quantise's power-of-ten table now (at load), not on the first double of day 0
        }

        // [add] Appends one scalar to the day, checking its indexes (cloud review D-4): the name must be in DriftFields.ScalarNames, a
        // per-agency scalar needs an agency 0..agencyCount-1 (any non-negative one when agencyCount is -1) and a world scalar needs
        // DriftAreas.WorldAgency. False, and nothing added, for a bad index or when the snapshot has no room. No allocation.
        public static bool AddScalar(DaySnapshot t, int agency, int agencyCount, int name, double value)
        {
            if (t == null || name < 0 || name >= DriftFields.ScalarNames.Length) return false;
            if (name < DriftFields.PerAgencyScalars)
            {
                if (agency < 0 || agency > short.MaxValue || (agencyCount >= 0 && agency >= agencyCount)) return false;
            }
            else if (agency != DriftAreas.WorldAgency) return false;
            int n = t.ScalarCount;
            if (n >= t.Scalars.Length) return false;
            t.Scalars[n].Agency = (short)agency;
            t.Scalars[n].Name = (short)name;
            t.Scalars[n].Value = value;
            t.ScalarCount = n + 1;
            return true;
        }

        // The capture ran out of room (or a block went over its cap): the dump is marked truncated, the day's sums are not affected.
        public bool CaptureOverflow { get { return captureOverflow; } }

        // [add] The snapshot ran out of Blocks or Fields: the day is incomplete and must not be trusted.
        public bool SnapshotOverflow { get { return overflow; } }

        // capture == null on non-checkpoint days; otherwise it is cleared here and filled in this visit. Resets the per-day
        // state and the snapshot's blocks and fields (not its Head or Scalars: the caller fills those; DriftWriter.Acquire
        // clears the whole snapshot). The caller must not pass a capture the writer thread still holds (CaptureFree).
        public void BeginDay(DaySnapshot target, ItemCapture capture)
        {
            if (target == null) throw new ArgumentNullException("target");
            this.target = target; this.capture = capture;
            target.BlockCount = 0; target.FieldCount = 0;
            if (capture != null) capture.Clear();
            ClearTouched();                             // a visit that threw half way must leave nothing behind
            inBlock = false; inItem = false; overflow = false; captureOverflow = false;
        }

        // Closes anything left open. The block sums and field sums are already in the target (written at each EndBlock).
        public void EndDay()
        {
            if (inBlock) EndBlock();
            target = null; capture = null;
        }

        public void BeginBlock(int area, int agency)
        {
            if (target == null) throw new InvalidOperationException("BeginBlock outside a day");
            if (area < 0 || area >= DriftAreas.All.Length) throw new ArgumentOutOfRangeException("area");
            if (agency < DriftAreas.WorldAgency || agency > short.MaxValue || (agencyCount >= 0 && agency >= agencyCount)) throw new ArgumentOutOfRangeException("agency");
            if (inBlock) EndBlock();
            this.area = area; this.agency = agency;
            inBlock = true; inItem = false;
            blockItems = 0; sumE = 0; sumR = 0; sumD = 0;
            ClearTouched();
        }

        public void BeginItem()
        {
            if (!inBlock) return;
            if (inItem) EndItem();
            inItem = true; keysSealed = false; itemCapturing = false;
            cur.Key = Fnv64.Offset; cur.Exact = Fnv64.Offset; cur.Rounded = Fnv64.Offset; cur.Discrete = Fnv64.Offset;
            blockItems++;
            if (capture != null)
            {
                if (blockItems > DriftAreas.MaxItems[area] || capture.ItemCount >= capture.Items.Length) { capture.Truncated = true; captureOverflow = true; }
                else { itemCapturing = true; valStart = capture.ValueCount; }
            }
        }

        public void Key(string existing)
        {
            if (!inItem) return;
            unchecked
            {
                cur.Key = Fnv64.Byte(Fnv64.Text(cur.Key, existing), Sep);
                if (keysSealed)
                {
                    cur.Exact = Fnv64.Byte(Fnv64.Text(cur.Exact, existing), Sep);
                    cur.Rounded = Fnv64.Byte(Fnv64.Text(cur.Rounded, existing), Sep);
                    cur.Discrete = Fnv64.Byte(Fnv64.Text(cur.Discrete, existing), Sep);
                }
            }
            if (itemCapturing) Put(-1, ValueKind.KeyName, NameId(existing));
        }

        public void Key(long value)
        {
            if (!inItem) return;
            unchecked
            {
                cur.Key = Fnv64.Byte(Fnv64.Long(cur.Key, value), Sep);
                if (keysSealed)
                {
                    cur.Exact = Fnv64.Byte(Fnv64.Long(cur.Exact, value), Sep);
                    cur.Rounded = Fnv64.Byte(Fnv64.Long(cur.Rounded, value), Sep);
                    cur.Discrete = Fnv64.Byte(Fnv64.Long(cur.Discrete, value), Sep);
                }
            }
            if (itemCapturing) Put(-1, ValueKind.KeyLong, value);
        }

        public void Val(int field, string existing)
        {
            if (!inItem) return;
            Begin(field);
            byte f = (byte)field;
            cur.Exact = Fnv64.Byte(Fnv64.Text(Fnv64.Byte(cur.Exact, f), existing), Sep);
            cur.Rounded = Fnv64.Byte(Fnv64.Text(Fnv64.Byte(cur.Rounded, f), existing), Sep);
            cur.Discrete = Fnv64.Byte(Fnv64.Text(Fnv64.Byte(cur.Discrete, f), existing), Sep);
            if (itemCapturing) Put(field, ValueKind.Name, NameId(existing));
        }

        public void Val(int field, long value)
        {
            if (!inItem) return;
            Begin(field);
            byte f = (byte)field;
            cur.Exact = Fnv64.Byte(Fnv64.Long(Fnv64.Byte(cur.Exact, f), value), Sep);
            cur.Rounded = Fnv64.Byte(Fnv64.Long(Fnv64.Byte(cur.Rounded, f), value), Sep);
            cur.Discrete = Fnv64.Byte(Fnv64.Long(Fnv64.Byte(cur.Discrete, f), value), Sep);
            if (itemCapturing) Put(field, ValueKind.Long, value);
        }

        public void Val(int field, bool value)
        {
            if (!inItem) return;
            Begin(field);
            byte f = (byte)field;
            byte b = value ? (byte)1 : (byte)0;
            cur.Exact = Fnv64.Byte(Fnv64.Byte(Fnv64.Byte(cur.Exact, f), b), Sep);
            cur.Rounded = Fnv64.Byte(Fnv64.Byte(Fnv64.Byte(cur.Rounded, f), b), Sep);
            cur.Discrete = Fnv64.Byte(Fnv64.Byte(Fnv64.Byte(cur.Discrete, f), b), Sep);
            if (itemCapturing) Put(field, ValueKind.Bool, b);
        }

        public void Val(int field, double value)
        {
            if (!inItem) return;
            Begin(field);
            byte f = (byte)field;
            cur.Exact = Fnv64.Byte(Fnv64.Long(Fnv64.Byte(cur.Exact, f), Quantise.Exact(value)), Sep);
            cur.Rounded = Fnv64.Byte(Fnv64.Long(Fnv64.Byte(cur.Rounded, f), Quantise.Rounded(value)), Sep);
            // Discrete: a double never reaches it.
            if (!seen[field]) { seen[field] = true; touched[touchedCount++] = field; acc[field] = 0; }   // the field is listed even when every value is non-finite
            double a = value < 0 ? -value : value;
            if (a - a == 0) acc[field] += a;                                                              // finite only
            if (itemCapturing) Put(field, ValueKind.Double, BitConverter.DoubleToInt64Bits(value));
        }

        public void Date(int field, long ticks)
        {
            if (!inItem) return;
            Begin(field);
            byte f = (byte)field;
            cur.Exact = Fnv64.Byte(Fnv64.Long(Fnv64.Byte(cur.Exact, f), ticks), Sep);
            cur.Rounded = Fnv64.Byte(Fnv64.Long(Fnv64.Byte(cur.Rounded, f), Quantise.RoundedDate(ticks, origin)), Sep);
            // Discrete: a date never reaches it.
            if (itemCapturing) Put(field, ValueKind.Date, ticks);
        }

        public void EndItem()
        {
            if (!inItem) return;
            Seal();                                     // an item with keys only: the value streams are the key hash
            inItem = false;
            unchecked { sumE += cur.Exact; sumR += cur.Rounded; sumD += cur.Discrete; }
            if (itemCapturing)
            {
                int i = capture.ItemCount;
                capture.Items[i].Area = (short)area; capture.Items[i].Agency = (short)agency;
                capture.Items[i].Key = cur.Key; capture.Items[i].Exact = cur.Exact;
                capture.Items[i].Rounded = cur.Rounded; capture.Items[i].Discrete = cur.Discrete;
                capture.Items[i].FirstValue = valStart; capture.Items[i].ValueCount = (short)(capture.ValueCount - valStart);
                capture.ItemCount = i + 1;
                itemCapturing = false;
            }
        }

        public void EndBlock()
        {
            if (!inBlock) return;
            if (inItem) EndItem();
            inBlock = false;
            // list the continuous value names in field order, whatever order they were first seen in
            for (int i = 1; i < touchedCount; i++)
            {
                int v = touched[i], j = i - 1;
                while (j >= 0 && touched[j] > v) { touched[j + 1] = touched[j]; j--; }
                touched[j + 1] = v;
            }
            // A block that does not fit is skipped WITH its field sums: a field sum without its block makes the reader refuse the
            // whole record. SnapshotOverflow stays set, so the recorder knows the day is incomplete.
            bool fitted = target.BlockCount < target.Blocks.Length;
            if (!fitted) overflow = true;
            else
            {
                int b = target.BlockCount++;
                target.Blocks[b].Area = (short)area; target.Blocks[b].Agency = (short)agency;
                target.Blocks[b].Count = blockItems;
                target.Blocks[b].Exact = sumE; target.Blocks[b].Rounded = sumR; target.Blocks[b].Discrete = sumD;
                target.Blocks[b].Capped = blockItems > DriftAreas.MaxItems[area] ? (byte)1 : (byte)0;
            }
            for (int i = 0; i < touchedCount; i++)
            {
                int field = touched[i];
                seen[field] = false;
                if (!fitted) continue;
                if (target.FieldCount >= target.Fields.Length) { overflow = true; continue; }
                int k = target.FieldCount++;
                target.Fields[k].Area = (short)area; target.Fields[k].Agency = (short)agency;
                target.Fields[k].Field = (short)field; target.Fields[k].Sum = acc[field];
            }
            touchedCount = 0;
        }

        void ClearTouched()
        {
            for (int i = 0; i < touchedCount; i++) seen[touched[i]] = false;
            touchedCount = 0;
        }

        void Begin(int field)
        {
            if ((uint)field >= (uint)acc.Length) throw new ArgumentOutOfRangeException("field");
            Seal();
        }

        // The first value ends the keys: the three streams start from the key hash.
        void Seal()
        {
            if (keysSealed) return;
            keysSealed = true;
            cur.Exact = cur.Key; cur.Rounded = cur.Key; cur.Discrete = cur.Key;
        }

        int NameId(string s)
        {
            if (names == null) return -1;
            bool isNew;
            return names.IdOf(s, out isNew);
        }

        // One raw value into the capture; when the arrays are full the item is dropped from the capture (rolled back) and the capture is marked truncated.
        void Put(int field, byte kind, long bits)
        {
            if (capture.ValueCount >= capture.Values.Length)
            {
                capture.ValueCount = valStart;
                capture.Truncated = true; captureOverflow = true; itemCapturing = false;
                return;
            }
            int i = capture.ValueCount++;
            capture.Values[i].Field = (short)field; capture.Values[i].Kind = kind; capture.Values[i].Bits = bits;
        }
    }
}
