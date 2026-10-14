using System;

namespace SolarExpanseMultiplayer
{
    // [add] FieldValue.Kind. Bits = the value (KeyLong, Long, Bool: 0 or 1), a name id (KeyName, Name: IdTable id, -1 for a
    // null string), the raw double bits (Double), or ticks (Date; long.MinValue = none). A key has FieldValue.Field = -1.
    public static class ValueKind
    {
        public const byte KeyLong = 0, KeyName = 1, Long = 2, Bool = 3, Name = 4, Double = 5, Date = 6;
        public static readonly string[] Names = { "keylong", "keyname", "long", "bool", "name", "double", "date" };
    }

    // One captured item: its block, its four hashes, and where its raw values sit in ItemCapture.Values.
    public struct ItemRecord { public short Area, Agency; public ulong Key, Exact, Rounded, Discrete; public int FirstValue; public short ValueCount; }

    // One raw value of an item: (field id, kind, 64-bit value).
    public struct FieldValue { public short Field; public byte Kind; public long Bits; }

    // One checkpoint day: every item's hashes and raw values, filled by AreaHasher in the day's own visit and written by
    // the writer thread as a binary dump. Pointer-free arrays, allocated once.
    public sealed class ItemCapture
    {
        public ItemRecord[] Items; public int ItemCount;
        public FieldValue[] Values; public int ValueCount;
        public bool Truncated;      // some item did not fit (or a block went over its cap): the dump is marked truncated

        // maxItems = the sum of all block caps (DriftAreas.CaptureItems); maxValues = the sum of cap x values per item (DriftAreas.CaptureValues).
        public ItemCapture(int maxItems, int maxValues)
        {
            if (maxItems < 0 || maxValues < 0) throw new ArgumentOutOfRangeException("maxItems", "sizes cannot be negative");
            Items = new ItemRecord[maxItems];
            Values = new FieldValue[maxValues];
        }

        public void Clear() { ItemCount = 0; ValueCount = 0; Truncated = false; }
    }
}
