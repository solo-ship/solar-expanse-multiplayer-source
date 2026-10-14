using System;

namespace SolarExpanseMultiplayer
{
    // The structs below hold NO reference fields (pointer-free): the game's Boehm collector allocates an array of them as
    // atomic memory and never scans it. Every array is allocated once, at load (docs/research/gc.md).

    // What DriftProbe hands to the recorder at a day boundary.
    public struct DayTick { public long KeyTicks, AtTicks; public double ElapsedDays; }

    // One day's header. The binary record writes these fields in this order (DriftFile format 4).
    public struct DayHead
    {
        public int Day; public long KeyTicks, AtTicks, RealMs; public double ElapsedDays; public float Scale;
        public int CostMicros, FrameMaxMs, GcBefore, GcAfter; public byte GcHit;
        public byte Captured;       // 1 on a checkpoint day: this read includes the capture copy (the "capture step")
    }

    // One (area, agency) block of one day: the wrapping sums of its item hashes. Agency = the index into the header's agency
    // list, or DriftAreas.WorldAgency.
    public struct BlockSum { public short Area, Agency; public int Count; public ulong Exact, Rounded, Discrete; public byte Capped; }

    // The sum of |v| over one value name's finite values in one block (a continuous scalar for phase-corrected residuals).
    public struct FieldSum { public short Area, Agency, Field; public double Sum; }

    // A named scalar of the day; Name = index into DriftFields.ScalarNames.
    public struct ScalarValue { public short Agency, Name; public double Value; }

    // A recorder event. TextId is a name id (IdTable) or -1; the text is rendered offline from the kind and the numbers.
    public struct EventRecord { public int Day; public byte Kind; public int A, B, C; public int TextId; }

    // [add] The event kinds of format 4 (the byte in EventRecord.Kind). APPEND ONLY.
    public static class DriftEventKind
    {
        public const byte Ai = 0, AiCheck = 1, Autosave = 2, Random = 3, Stop = 4, Cap = 5, Warn = 6, EndGame = 7,
            DayThrew = 8, Load = 9, Dump = 10, Dropped = 11, Stall = 12;

        public static readonly string[] Names =
        {
            "ai", "aicheck", "autosave", "random", "stop", "cap", "warn", "endgame", "daythrew", "load", "dump", "dropped", "stall"
        };

        public static string NameOf(byte kind) { return kind < Names.Length ? Names[kind] : "kind" + kind; }
    }

    // One day, preallocated. Two of them are double-buffered by DriftWriter (Acquire / Submit).
    public sealed class DaySnapshot
    {
        public DayHead Head;
        public BlockSum[] Blocks; public int BlockCount;
        public FieldSum[] Fields; public int FieldCount;
        public ScalarValue[] Scalars; public int ScalarCount;

        public DaySnapshot(int maxBlocks, int maxFields, int maxScalars)
        {
            if (maxBlocks < 0 || maxFields < 0 || maxScalars < 0) throw new ArgumentOutOfRangeException("maxBlocks", "sizes cannot be negative");
            Blocks = new BlockSum[maxBlocks];
            Fields = new FieldSum[maxFields];
            Scalars = new ScalarValue[maxScalars];
        }

        // Forgets the day (the arrays keep their size).
        public void Clear()
        {
            Head = default(DayHead);
            BlockCount = 0; FieldCount = 0; ScalarCount = 0;
        }
    }
}
