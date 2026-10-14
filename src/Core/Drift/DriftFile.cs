using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The strings of a record's header, written ONCE at the start of the file (allowed by the allocation budget).
    public sealed class DriftHeader
    {
        public int Format; public string Profile, Role, ModVersion, GameBuild, SaveHash, Flags, Skipped, StartedLocal; public long LoadTicks;
        public string[] Agencies;                       // index = the agency id used in every block (Company.ID, sorted ordinal)
        public static readonly string[] MustMatch = { "ai-off", "no-autosave", "seed" };   // with GameBuild, ModVersion and SaveHash, equal in both records
    }

    // The head of a checkpoint dump. ItemCount, ValueCount and Truncated are filled in from the capture by WriteDump.
    public struct DumpHead
    {
        public int Day; public long KeyTicks, AtTicks; public int ItemCount, ValueCount; public byte Truncated;
        public int CaptureMicros; public byte CaptureGcHit;    // copied from that day's DayHead (CostMicros, GcHit)
    }

    // ---- OFFLINE ONLY (built by the readers; allocation is fine here) ----

    public sealed class DayRecord
    {
        public int Day; public long KeyTicks, AtTicks, RealMs; public double ElapsedDays; public float Scale;
        public int CostMicros, FrameMaxMs; public bool GcHit, Captured; public int GcBefore, GcAfter;
        public List<AreaSum> Areas = new List<AreaSum>(); public List<DriftScalar> Scalars = new List<DriftScalar>();
    }
    public sealed class AreaSum
    {
        public string Area, Agency; public int Count; public ulong Exact, Rounded, Discrete; public bool Capped;
        public List<FieldMagnitude> Magnitudes = new List<FieldMagnitude>();
    }
    public sealed class FieldMagnitude { public string Name; public double Sum; }
    public sealed class DriftScalar { public string Agency, Name; public double Value; }
    public sealed class DriftEventLine { public int Day; public string Kind, Text; }
    public sealed class DriftRecordFile
    {
        public DriftHeader Header; public string[] Names; public List<DayRecord> Days;
        public List<DriftEventLine> Events; public int LastDay, Dropped; public bool Ended, Truncated;
        // [add] Things the reader got past without failing the record (an agency, area, field or scalar index outside the header's
        // table, shown as #n). At most DriftFile.MaxWarnings are listed. Callers that report should print them.
        public List<string> Warnings = new List<string>();
        // [add] The record's own area, field and scalar name tables (its header), used to render dumps written by the same build.
        public string[] AreaNames, FieldNames, ScalarNames;
    }
    public sealed class DumpFile { public string Profile, Role, GameBuild, RecordFileName; public DumpHead Head; public List<DumpItem> Items; }
    public sealed class DumpItem
    {
        public string Area, Agency; public ulong Key, Exact, Rounded, Discrete; public List<KeyValuePair<string, string>> Values;   // field name -> rendered value
        // [add] The raw item, kept so JoinNames can render it again with the record's names.
        public short AreaIndex, AgencyIndex; public FieldValue[] Raw;
    }

    // Binary format 4 (little-endian; strings are UTF-8 with a u16 length; see the plan, "The binary layout"), and its offline
    // text rendering. The Write* methods are for the WRITER thread only: no allocation per item or per day (one reused
    // per-thread buffer, one Write call per record). The Read* and *Text methods are offline.
    public static class DriftFile
    {
        public const int Format = 4;                    // revision 4: binary; text is rendered offline
        // [add] The record type bytes of the framing (type, u32 length, payload), public so the tests can walk a file by its framing.
        public const byte RecHeader = 1, RecName = 2, RecDay = 3, RecEvent = 4, RecEnd = 5;
        const int MaxStringChars = 16000;               // a string is cut here (UTF-8 stays under the u16 length)
        const int FlushAt = 60000;                      // a dump flushes its buffer to the stream past this size
        internal const long MaxReadBytes = 512L << 20;
        internal const int MaxNameId = 4000000;         // a name record's id must be 0..this (it sizes the joined table)
        internal const int MaxDumpValuesPerItem = 64;   // the reader refuses an item with more values (the real maximum is 15: DriftAreas.MaxValuesPerItem)
        internal const int MaxDay = 1000000;            // a day record's day number must be 0..this (a crafted int.MaxValue made a loop run forever)
        public const int MaxWarnings = 20;
        static readonly byte[] MagicRecord = { (byte)'S', (byte)'E', (byte)'M', (byte)'P', (byte)'D', (byte)'R', (byte)'F', (byte)'T' };
        static readonly byte[] MagicDump = { (byte)'S', (byte)'E', (byte)'M', (byte)'P', (byte)'D', (byte)'U', (byte)'M', (byte)'P' };
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ===== writing: game side, WRITER thread only =====

        public static void WriteRecordStart(BinaryWriter w, DriftHeader h) { Emit(w, EncodeRecordStart(h)); }     // magic, format, header record
        public static void WriteName(BinaryWriter w, int id, string name) { Emit(w, EncodeName(id, name)); }      // one id-to-name entry
        public static void WriteDay(BinaryWriter w, DaySnapshot s) { Emit(w, EncodeDay(s)); }
        public static void WriteEvent(BinaryWriter w, EventRecord e) { Emit(w, EncodeEvent(e)); }
        public static void WriteEnd(BinaryWriter w, int lastDay, int dropped) { Emit(w, EncodeEnd(lastDay, dropped)); }

        // The record file name is where the names are (the dump holds none).
        public static void WriteDump(BinaryWriter w, DriftHeader h, string recordFileName, DumpHead d, ItemCapture c)
        {
            WriteDump(w, h, recordFileName, d, c, DriftPaths.MaxDumpBytes);
        }

        // ---- the per-thread buffer: every record is built here and written with one call ----

        [ThreadStatic] static Buf scratch;
        static Buf Scratch()
        {
            Buf b = scratch;
            if (b == null) { b = new Buf(70000); scratch = b; }
            b.N = 0;
            return b;
        }

        // [add] Called by the writer thread at start, so the buffer is allocated at load and not on the first day.
        internal static void PrepareThread() { Scratch(); }

        // Writes the first n bytes of the thread's buffer.
        internal static void Emit(BinaryWriter w, int n) { w.Write(scratch.B, 0, n); }

        // Each Encode* builds one record (or the file start) in the buffer and returns its size in bytes.
        internal static int EncodeRecordStart(DriftHeader h)
        {
            Buf b = Scratch();
            b.Raw(MagicRecord);
            b.U16(Format);
            int at = b.Begin(RecHeader);
            b.Str(h.Profile); b.Str(h.Role); b.Str(h.ModVersion); b.Str(h.GameBuild); b.Str(h.SaveHash); b.Str(h.Flags); b.Str(h.Skipped); b.Str(h.StartedLocal);
            b.I64(h.LoadTicks);
            string[] ag = h.Agencies;
            int na = ag == null ? 0 : ag.Length;
            b.U16(na); for (int i = 0; i < na; i++) b.Str(ag[i]);
            b.U16(DriftAreas.All.Length); for (int i = 0; i < DriftAreas.All.Length; i++) b.Str(DriftAreas.All[i]);
            b.U16(DriftFields.Names.Length); for (int i = 0; i < DriftFields.Names.Length; i++) b.Str(DriftFields.Names[i]);
            b.U16(DriftFields.ScalarNames.Length); for (int i = 0; i < DriftFields.ScalarNames.Length; i++) b.Str(DriftFields.ScalarNames[i]);
            b.End(at);
            return b.N;
        }

        internal static int EncodeName(int id, string name)
        {
            Buf b = Scratch();
            int at = b.Begin(RecName);
            b.I32(id); b.Str(name);
            b.End(at);
            return b.N;
        }

        internal static int EncodeDay(DaySnapshot s)
        {
            Buf b = Scratch();
            int at = b.Begin(RecDay);
            DayHead h = s.Head;
            b.I32(h.Day); b.I64(h.KeyTicks); b.I64(h.AtTicks); b.I64(h.RealMs); b.F64(h.ElapsedDays); b.F32(h.Scale);
            b.I32(h.CostMicros); b.I32(h.FrameMaxMs); b.I32(h.GcBefore); b.I32(h.GcAfter); b.U8(h.GcHit); b.U8(h.Captured);
            int nb = Math.Min(Math.Min(s.BlockCount, s.Blocks.Length), 65535);
            b.U16(nb);
            for (int i = 0; i < nb; i++)
            {
                b.I16(s.Blocks[i].Area); b.I16(s.Blocks[i].Agency); b.I32(s.Blocks[i].Count);
                b.U64(s.Blocks[i].Exact); b.U64(s.Blocks[i].Rounded); b.U64(s.Blocks[i].Discrete); b.U8(s.Blocks[i].Capped);
            }
            int nf = Math.Min(Math.Min(s.FieldCount, s.Fields.Length), 65535);
            b.U16(nf);
            for (int i = 0; i < nf; i++) { b.I16(s.Fields[i].Area); b.I16(s.Fields[i].Agency); b.I16(s.Fields[i].Field); b.F64(s.Fields[i].Sum); }
            int ns = Math.Min(Math.Min(s.ScalarCount, s.Scalars.Length), 65535);
            b.U16(ns);
            for (int i = 0; i < ns; i++) { b.I16(s.Scalars[i].Agency); b.I16(s.Scalars[i].Name); b.F64(s.Scalars[i].Value); }
            b.End(at);
            return b.N;
        }

        internal static int EncodeEvent(EventRecord e)
        {
            Buf b = Scratch();
            int at = b.Begin(RecEvent);
            b.I32(e.Day); b.U8(e.Kind); b.I32(e.A); b.I32(e.B); b.I32(e.C); b.I32(e.TextId);
            b.End(at);
            return b.N;
        }

        internal static int EncodeEnd(int lastDay, int dropped)
        {
            Buf b = Scratch();
            int at = b.Begin(RecEnd);
            b.I32(lastDay); b.I32(dropped);
            b.End(at);
            return b.N;
        }

        // The dump head: magic, format, profile, role, game build, record file name, the DumpHead fields. Its size does not
        // depend on the numbers, so it can be built once to measure and again with the final counts.
        static void PutDumpHead(Buf b, DriftHeader h, string recordFileName, DumpHead d, int items, int values, bool truncated)
        {
            b.Raw(MagicDump);
            b.U16(Format);
            b.Str(h.Profile); b.Str(h.Role); b.Str(h.GameBuild); b.Str(recordFileName);
            b.I32(d.Day); b.I64(d.KeyTicks); b.I64(d.AtTicks); b.I32(items); b.I32(values); b.U8(truncated ? (byte)1 : (byte)0);
            b.I32(d.CaptureMicros); b.U8(d.CaptureGcHit);
        }

        // [add] maxBytes is a parameter for the tests; the public WriteDump passes DriftPaths.MaxDumpBytes. Items that would
        // push the file past maxBytes are left out and the dump is marked truncated. The head's ItemCount, ValueCount and
        // Truncated are taken from the capture, not from d.
        internal static void WriteDump(BinaryWriter w, DriftHeader h, string recordFileName, DumpHead d, ItemCapture c, long maxBytes)
        {
            Buf b = Scratch();
            PutDumpHead(b, h, recordFileName, d, 0, 0, false);
            long size = b.N + 4;                        // the head, and the repeated item count
            int total = Math.Min(c.ItemCount, c.Items.Length);
            int n = 0; int values = 0;
            for (; n < total; n++)
            {
                int vc = ValuesOf(c, n);                // the same count the item is written with, so the head's ValueCount is honest
                long add = 38 + 11L * vc;
                if (size + add > maxBytes) break;
                size += add; values += vc;
            }
            bool truncated = c.Truncated || n < total;
            b.N = 0;
            PutDumpHead(b, h, recordFileName, d, n, values, truncated);
            b.I32(n);
            for (int i = 0; i < n; i++)
            {
                if (b.N >= FlushAt) { w.Write(b.B, 0, b.N); b.N = 0; }
                int first = c.Items[i].FirstValue, vc = ValuesOf(c, i);
                b.I16(c.Items[i].Area); b.I16(c.Items[i].Agency);
                b.U64(c.Items[i].Key); b.U64(c.Items[i].Exact); b.U64(c.Items[i].Rounded); b.U64(c.Items[i].Discrete);
                b.U16(vc);
                for (int k = 0; k < vc; k++) { b.I16(c.Values[first + k].Field); b.U8(c.Values[first + k].Kind); b.I64(c.Values[first + k].Bits); }
            }
            w.Write(b.B, 0, b.N);
        }

        // The number of values item i is written with: its own count, or none for a bad item (never from AreaHasher): a negative
        // count, or a range that starts before or runs past the capture's values.
        static int ValuesOf(ItemCapture c, int i)
        {
            int first = c.Items[i].FirstValue, vc = c.Items[i].ValueCount;
            return first < 0 || vc < 0 || (long)first + vc > c.Values.Length ? 0 : vc;
        }

        // The growable little-endian buffer behind every record.
        sealed class Buf
        {
            public byte[] B; public int N;
            public Buf(int capacity) { B = new byte[capacity]; }
            void Need(int k) { if (N + k > B.Length) Array.Resize(ref B, Math.Max(B.Length * 2, N + k)); }
            public void U8(byte v) { Need(1); B[N++] = v; }
            public void U16(int v) { Need(2); B[N] = (byte)v; B[N + 1] = (byte)(v >> 8); N += 2; }
            public void I16(short v) { U16(v); }
            public void I32(int v) { Need(4); B[N] = (byte)v; B[N + 1] = (byte)(v >> 8); B[N + 2] = (byte)(v >> 16); B[N + 3] = (byte)(v >> 24); N += 4; }
            public void I64(long v) { Need(8); for (int i = 0; i < 8; i++) B[N + i] = (byte)(v >> (8 * i)); N += 8; }
            public void U64(ulong v) { I64((long)v); }
            public void F64(double v) { I64(BitConverter.DoubleToInt64Bits(v)); }
            public void F32(float v) { FloatBits u = default(FloatBits); u.F = v; I32(u.I); }
            public void Raw(byte[] src) { Need(src.Length); Buffer.BlockCopy(src, 0, B, N, src.Length); N += src.Length; }
            public void Str(string s)
            {
                if (s == null) s = "";
                int chars = s.Length > MaxStringChars ? MaxStringChars : s.Length;
                Need(2 + chars * 3);
                int n = Utf8.GetBytes(s, 0, chars, B, N + 2);
                B[N] = (byte)n; B[N + 1] = (byte)(n >> 8);
                N += 2 + n;
            }
            // A framed record: type, a u32 payload length (patched by End), then the payload.
            public int Begin(byte type) { Need(5); int at = N; B[N] = type; N += 5; return at; }
            public void End(int at)
            {
                int len = N - at - 5;
                B[at + 1] = (byte)len; B[at + 2] = (byte)(len >> 8); B[at + 3] = (byte)(len >> 16); B[at + 4] = (byte)(len >> 24);
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        struct FloatBits { [FieldOffset(0)] public float F; [FieldOffset(0)] public int I; }

        // ===== reading: offline =====

        sealed class DriftFormatException : Exception { public DriftFormatException(string m) : base(m) { } }

        sealed class Rd
        {
            readonly byte[] b; int p; readonly int end;
            public Rd(byte[] data, int start, int endExclusive) { b = data; p = start; end = endExclusive; }
            public int Left { get { return end - p; } }
            void Need(int k) { if (k < 0 || end - p < k) throw new DriftFormatException("the data ends inside a field"); }
            public byte U8() { Need(1); return b[p++]; }
            public int U16() { Need(2); int v = b[p] | (b[p + 1] << 8); p += 2; return v; }
            public short I16() { return (short)U16(); }
            public int I32() { Need(4); int v = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24); p += 4; return v; }
            public long I64() { Need(8); long v = 0; for (int i = 7; i >= 0; i--) v = (v << 8) | b[p + i]; p += 8; return v; }
            public ulong U64() { return (ulong)I64(); }
            public double F64() { return BitConverter.Int64BitsToDouble(I64()); }
            public float F32() { FloatBits u = default(FloatBits); u.I = I32(); return u.F; }
            public string Str() { int n = U16(); Need(n); string s = Utf8.GetString(b, p, n); p += n; return s; }
        }

        static byte[] ReadAll(Stream s) { return ReadAll(s, MaxReadBytes); }

        internal static byte[] ReadAll(Stream s, long cap)
        {
            string big = "the file is larger than " + (cap >= (1L << 20) ? (cap >> 20) + " MB" : cap + " bytes");
            if (s.CanSeek && s.Length - s.Position > cap) throw new DriftFormatException(big);      // refused before a byte is read
            var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > cap) throw new DriftFormatException(big);
            }
            return ms.ToArray();
        }

        static void CheckMagic(byte[] d, byte[] magic, string what)
        {
            if (d.Length < 10) throw new DriftFormatException("not a drift " + what + ": the file is too short");
            for (int i = 0; i < 8; i++) if (d[i] != magic[i]) throw new DriftFormatException("not a drift " + what + ": bad magic");
            int fmt = d[8] | (d[9] << 8);
            if (fmt != Format) throw new DriftFormatException("unsupported " + what + " format " + fmt + " (this tool reads " + Format + ")");
        }

        // Never throws; a truncated tail keeps the complete records and sets f.Truncated. Reads the WHOLE file first and
        // joins the names at the end (a name record may follow the record that first uses its id, after a drop or reorder).
        public static bool TryReadRecord(Stream s, out DriftRecordFile f, out string error)
        {
            f = null; error = null;
            try
            {
                f = ParseRecord(ReadAll(s));
                return true;
            }
            catch (DriftFormatException e) { f = null; error = e.Message; return false; }
            catch (Exception e) { f = null; error = "could not read the record: " + e.GetType().Name + ": " + e.Message; return false; }
        }

        static DriftRecordFile ParseRecord(byte[] d)
        {
            CheckMagic(d, MagicRecord, "record");
            var f = new DriftRecordFile { Days = new List<DayRecord>(), Events = new List<DriftEventLine>(), LastDay = -1 };
            var raw = new List<EventRecord>();
            var nameMap = new Dictionary<int, string>();
            int pos = 10, index = 0, maxId = -1;
            while (pos < d.Length)
            {
                if (d.Length - pos < 5) { f.Truncated = true; break; }
                byte type = d[pos];
                long len = (long)d[pos + 1] | ((long)d[pos + 2] << 8) | ((long)d[pos + 3] << 16) | ((long)d[pos + 4] << 24);
                if (len > d.Length - pos - 5) { f.Truncated = true; break; }
                if (f.Header == null && type != RecHeader) throw new DriftFormatException("the first record is not the header");
                var r = new Rd(d, pos + 5, pos + 5 + (int)len);
                try
                {
                    switch (type)
                    {
                        case RecHeader:
                            if (f.Header != null) throw new DriftFormatException("a second header record");
                            ParseHeader(r, f);
                            break;
                        case RecName:
                            {
                                int id = r.I32();
                                string name = r.Str();
                                if (id < 0 || id > MaxNameId) throw new DriftFormatException("name id " + id + " is out of range");
                                nameMap[id] = name;
                                if (id > maxId) maxId = id;
                                break;
                            }
                        case RecDay: f.Days.Add(ParseDay(r, f)); break;
                        case RecEvent:
                            {
                                EventRecord e;
                                e.Day = r.I32(); e.Kind = r.U8(); e.A = r.I32(); e.B = r.I32(); e.C = r.I32(); e.TextId = r.I32();
                                raw.Add(e);
                                break;
                            }
                        case RecEnd: f.LastDay = r.I32(); f.Dropped = r.I32(); f.Ended = true; break;
                        default: throw new DriftFormatException("unknown record type " + type);
                    }
                }
                catch (DriftFormatException e) { throw new DriftFormatException("record " + index + " (type " + type + ", offset " + pos + "): " + e.Message); }
                pos += 5 + (int)len; index++;
            }
            if (f.Header == null) throw new DriftFormatException(f.Truncated ? "the file ends inside the header record" : "the file has no header record");
            f.Names = new string[maxId + 1];
            foreach (KeyValuePair<int, string> kv in nameMap) f.Names[kv.Key] = kv.Value;
            foreach (EventRecord e in raw)
                f.Events.Add(new DriftEventLine { Day = e.Day, Kind = DriftEventKind.NameOf(e.Kind), Text = EventText(e, f.Names) });
            if (!f.Ended && f.Days.Count > 0) f.LastDay = f.Days[f.Days.Count - 1].Day;
            return f;
        }

        static void ParseHeader(Rd r, DriftRecordFile f)
        {
            var h = new DriftHeader { Format = Format };
            h.Profile = r.Str(); h.Role = r.Str(); h.ModVersion = r.Str(); h.GameBuild = r.Str(); h.SaveHash = r.Str(); h.Flags = r.Str(); h.Skipped = r.Str(); h.StartedLocal = r.Str();
            h.LoadTicks = r.I64();
            h.Agencies = StrList(r);
            f.AreaNames = StrList(r); f.FieldNames = StrList(r); f.ScalarNames = StrList(r);
            f.Header = h;
        }

        static string[] StrList(Rd r)
        {
            int n = r.U16();
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = r.Str();
            return a;
        }

        // An index outside the header's table is shown as #n with a warning: one bad index loses that name, not the whole record
        // (cloud review D-4).
        static string Pick(DriftRecordFile f, int day, string[] table, int index, string what)
        {
            if (table != null && index >= 0 && index < table.Length) return table[index];
            Warn(f, "day " + day.ToString(Inv) + ": " + what + " index " + index.ToString(Inv) + " is outside the header's table of " + (table == null ? 0 : table.Length) + "; shown as #" + index.ToString(Inv));
            return "#" + index.ToString(Inv);
        }

        static string AgencyName(DriftRecordFile f, int day, int index)
        {
            return index == DriftAreas.WorldAgency ? DriftAreas.World : Pick(f, day, f.Header.Agencies, index, "agency");
        }

        static void Warn(DriftRecordFile f, string text)
        {
            if (f.Warnings.Count > MaxWarnings || f.Warnings.Contains(text)) return;
            f.Warnings.Add(f.Warnings.Count == MaxWarnings ? "further index warnings are not listed" : text);
        }

        static DayRecord ParseDay(Rd r, DriftRecordFile f)
        {
            var day = new DayRecord();
            day.Day = r.I32();
            if (day.Day < 0 || day.Day > MaxDay) throw new DriftFormatException("day number " + day.Day + " is outside 0.." + MaxDay);
            day.KeyTicks = r.I64(); day.AtTicks = r.I64(); day.RealMs = r.I64(); day.ElapsedDays = r.F64(); day.Scale = r.F32();
            day.CostMicros = r.I32(); day.FrameMaxMs = r.I32(); day.GcBefore = r.I32(); day.GcAfter = r.I32();
            day.GcHit = r.U8() != 0; day.Captured = r.U8() != 0;
            int nb = r.U16();
            var byBlock = new Dictionary<int, AreaSum>();
            for (int i = 0; i < nb; i++)
            {
                short area = r.I16(), agency = r.I16();
                var a = new AreaSum { Area = Pick(f, day.Day, f.AreaNames, area, "area"), Agency = AgencyName(f, day.Day, agency) };
                a.Count = r.I32(); a.Exact = r.U64(); a.Rounded = r.U64(); a.Discrete = r.U64(); a.Capped = r.U8() != 0;
                day.Areas.Add(a);
                byBlock[(area << 16) | (agency & 0xFFFF)] = a;
            }
            int nf = r.U16();
            for (int i = 0; i < nf; i++)
            {
                short area = r.I16(), agency = r.I16(), field = r.I16();
                double sum = r.F64();
                AreaSum a;
                if (!byBlock.TryGetValue((area << 16) | (agency & 0xFFFF), out a)) throw new DriftFormatException("a field sum without its block");
                a.Magnitudes.Add(new FieldMagnitude { Name = Pick(f, day.Day, f.FieldNames, field, "field"), Sum = sum });
            }
            int ns = r.U16();
            for (int i = 0; i < ns; i++)
            {
                short agency = r.I16(), name = r.I16();
                double v = r.F64();
                day.Scalars.Add(new DriftScalar { Agency = AgencyName(f, day.Day, agency), Name = Pick(f, day.Day, f.ScalarNames, name, "scalar"), Value = v });
            }
            return day;
        }

        // The rendered text of an event: "a=<A> b=<B> c=<C>", then " text=<name>" when it has a text id (#<id> when the name is unknown).
        static string EventText(EventRecord e, string[] names)
        {
            string t = "a=" + e.A.ToString(Inv) + " b=" + e.B.ToString(Inv) + " c=" + e.C.ToString(Inv);
            if (e.TextId >= 0) t += " text=" + Clean(names != null && e.TextId < names.Length && names[e.TextId] != null ? names[e.TextId] : "#" + e.TextId.ToString(Inv));
            return t;
        }

        public static bool TryReadDump(Stream s, out DumpFile d, out string error)
        {
            d = null; error = null;
            try
            {
                d = ParseDump(ReadAll(s, DriftPaths.MaxDumpBytes));       // the writer never makes a dump over this
                return true;
            }
            catch (DriftFormatException e) { d = null; error = e.Message; return false; }
            catch (Exception e) { d = null; error = "could not read the dump: " + e.GetType().Name + ": " + e.Message; return false; }
        }

        static DumpFile ParseDump(byte[] data)
        {
            CheckMagic(data, MagicDump, "dump");
            var r = new Rd(data, 10, data.Length);
            var d = new DumpFile { Items = new List<DumpItem>() };
            d.Profile = r.Str(); d.Role = r.Str(); d.GameBuild = r.Str(); d.RecordFileName = r.Str();
            DumpHead h;
            h.Day = r.I32(); h.KeyTicks = r.I64(); h.AtTicks = r.I64(); h.ItemCount = r.I32(); h.ValueCount = r.I32(); h.Truncated = r.U8();
            h.CaptureMicros = r.I32(); h.CaptureGcHit = r.U8();
            d.Head = h;
            int n = r.I32();
            if (n != h.ItemCount) throw new DriftFormatException("the item count differs from the dump head's");
            if (n < 0 || (long)n * 38 > r.Left) throw new DriftFormatException("the dump ends before its " + n + " items");
            long values = 0;
            for (int i = 0; i < n; i++)
            {
                var it = new DumpItem();
                it.AreaIndex = r.I16(); it.AgencyIndex = r.I16();
                it.Key = r.U64(); it.Exact = r.U64(); it.Rounded = r.U64(); it.Discrete = r.U64();
                int vc = r.U16();
                if (vc > MaxDumpValuesPerItem) throw new DriftFormatException("item " + i + " has " + vc + " values (the most an item can hold is " + MaxDumpValuesPerItem + ")");
                if ((long)vc * 11 > r.Left) throw new DriftFormatException("the dump ends inside an item");
                it.Raw = new FieldValue[vc];
                for (int k = 0; k < vc; k++) { it.Raw[k].Field = r.I16(); it.Raw[k].Kind = r.U8(); it.Raw[k].Bits = r.I64(); }
                values += vc;
                d.Items.Add(it);
            }
            if (values != h.ValueCount) throw new DriftFormatException("the value count differs from the dump head's (" + values + " values, the head says " + h.ValueCount + ")");
            JoinNames(d, null);
            return d;
        }

        // [add] Renders every item's area, agency and values with the record's tables and names (the tables of this build
        // when names is null; unknown ids as #<id>). Idempotent. TryReadDump has already done it without a record; the
        // comparer calls it with each dump's own record before DumpDiff, and DumpText calls it itself.
        public static void JoinNames(DumpFile d, DriftRecordFile names)
        {
            if (d == null) return;
            string[] areas = names != null && names.AreaNames != null ? names.AreaNames : DriftAreas.All;
            string[] fields = names != null && names.FieldNames != null ? names.FieldNames : DriftFields.Names;
            string[] agencies = names != null && names.Header != null ? names.Header.Agencies : null;
            string[] ids = names != null ? names.Names : null;
            foreach (DumpItem it in d.Items)
            {
                it.Area = it.AreaIndex >= 0 && it.AreaIndex < areas.Length ? areas[it.AreaIndex] : "area" + it.AreaIndex.ToString(Inv);
                if (it.AgencyIndex == DriftAreas.WorldAgency) it.Agency = DriftAreas.World;
                else if (agencies != null && it.AgencyIndex >= 0 && it.AgencyIndex < agencies.Length) it.Agency = agencies[it.AgencyIndex];
                else it.Agency = it.AgencyIndex.ToString(Inv);
                it.Values = new List<KeyValuePair<string, string>>(it.Raw.Length);
                foreach (FieldValue v in it.Raw)
                {
                    string fname = v.Field < 0 ? "key" : (v.Field < fields.Length ? fields[v.Field] : "field" + v.Field.ToString(Inv));
                    it.Values.Add(new KeyValuePair<string, string>(fname, RenderValue(v.Kind, v.Bits, ids)));
                }
            }
        }

        static string RenderValue(byte kind, long bits, string[] ids)
        {
            switch (kind)
            {
                case ValueKind.KeyLong:
                case ValueKind.Long: return bits.ToString(Inv);
                case ValueKind.Bool: return bits != 0 ? "true" : "false";
                case ValueKind.KeyName:
                case ValueKind.Name:
                    {
                        if (bits == -1) return "null";
                        string n = ids != null && bits >= 0 && bits < ids.Length ? ids[bits] : null;
                        return n != null ? Clean(n) : "#" + bits.ToString(Inv);
                    }
                case ValueKind.Double: return BitConverter.Int64BitsToDouble(bits).ToString("G17", Inv);
                case ValueKind.Date: return bits == Quantise.NoDate ? "none" : bits.ToString(Inv);
                default: return "?" + bits.ToString(Inv);
            }
        }

        // ===== the text form (offline): tab-separated, InvariantCulture, doubles in G17, hashes as 16 lower-case hex digits =====

        static string Clean(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(CleanChars) < 0) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(Array.IndexOf(CleanChars, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
        // Every character that some viewer treats as a line break (or that the grammar uses) is mapped: LF, CR, VT, FF, NEL and the
        // Unicode line and paragraph separators, so a name or a stop reason from the file cannot add a line to any text output.
        static bool IsBreak(char c)
        {
            return c == '\n' || c == '\r' || c == '\u000B' || c == '\u000C' || c == '\u0085' || c == '\u2028' || c == '\u2029';
        }
        static readonly char[] CleanChars = { '\t', '\r', '\n', ';', '=', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029' };
        static string Cell(string s)
        {
            if (s == null || s.Length == 0) return "";
            bool any = false;
            foreach (char c in s) if (c == '\t' || IsBreak(c)) { any = true; break; }
            if (!any) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c == '\t' || IsBreak(c) ? ' ' : c);
            return sb.ToString();
        }

        static string Line(params string[] cells) { return string.Join("\t", cells); }
        static string N(long v) { return v.ToString(Inv); }
        static string G(double v) { return v.ToString("G17", Inv); }
        static string X(ulong v) { return v.ToString("x16", Inv); }

        // H, D, A, M, S and E lines (the plan's grammar). Each day's lines are followed by the events of that day.
        public static IEnumerable<string> RecordText(DriftRecordFile f)
        {
            DriftHeader h = f.Header;
            yield return Line("H", N(Format), Cell(h.Profile), Cell(h.Role), Cell(h.ModVersion), Cell(h.GameBuild), Cell(h.SaveHash),
                N(h.LoadTicks), Cell(h.StartedLocal), Cell(h.Flags), string.IsNullOrEmpty(h.Skipped) ? "-" : Cell(h.Skipped));
            int ei = 0;
            foreach (DayRecord d in f.Days)
            {
                string day = N(d.Day);
                yield return Line("D", day, N(d.KeyTicks), N(d.AtTicks), G(d.ElapsedDays), d.Scale.ToString("G9", Inv), N(d.RealMs), N(d.CostMicros),
                    N(d.FrameMaxMs), d.GcHit ? "1" : "0", N(d.GcBefore), N(d.GcAfter), d.Captured ? "1" : "0");
                foreach (AreaSum a in d.Areas)
                {
                    // the names come from the file's own tables: escaped like the header cells, so a tab or a line break in one
                    // (a corrupt or hostile file) cannot add a column or a line
                    string area = Cell(a.Area), agency = Cell(a.Agency);
                    yield return Line("A", day, area, agency, N(a.Count), X(a.Exact), X(a.Rounded), X(a.Discrete), a.Capped ? "1" : "0");
                    foreach (FieldMagnitude m in a.Magnitudes) yield return Line("M", day, area, agency, Cell(m.Name), G(m.Sum));
                }
                foreach (DriftScalar sc in d.Scalars) yield return Line("S", day, Cell(sc.Agency), Cell(sc.Name), G(sc.Value));
                while (ei < f.Events.Count && f.Events[ei].Day <= d.Day) { yield return EventLine(f.Events[ei]); ei++; }
            }
            while (ei < f.Events.Count) { yield return EventLine(f.Events[ei]); ei++; }
        }

        static string EventLine(DriftEventLine e) { return Line("E", N(e.Day), Cell(e.Kind), Cell(e.Text)); }

        // H, then an R and an I line per item, with the names joined in from the record (see JoinNames).
        public static IEnumerable<string> DumpText(DumpFile d, DriftRecordFile names)
        {
            JoinNames(d, names);
            yield return Line("H", N(Format), Cell(d.Profile), Cell(d.Role), N(d.Head.Day), N(d.Head.KeyTicks), N(d.Head.AtTicks), Cell(d.GameBuild),
                d.Head.Truncated != 0 ? "1" : "0", N(d.Head.CaptureMicros), d.Head.CaptureGcHit != 0 ? "1" : "0");
            var sb = new StringBuilder();
            foreach (DumpItem it in d.Items)
            {
                string key = X(it.Key), area = Cell(it.Area), agency = Cell(it.Agency);
                yield return Line("R", area, agency, key, X(it.Exact), X(it.Rounded), X(it.Discrete));
                sb.Length = 0;
                for (int i = 0; i < it.Values.Count; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(Cell(it.Values[i].Key)).Append('=').Append(it.Values[i].Value);     // the value is already Clean
                }
                yield return Line("I", area, agency, key, sb.ToString());
            }
        }
    }
}
