using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // When a world area differs and no heal can restore it (the joiner then rejoins from a fresh copy, which hides the
    // difference), both games write that area's rows as text: the joiner as it leaves, the host when it reads the
    // leave reason. Diffing the two logs names the item and the field. One line per item, capped, sanitised.
    public static class WorldRowsText
    {
        public const int MaxLines = 256, MaxLine = 400;

        // "world: <area> differs ...", "world: <area> still differs ...", "the host cannot heal <area> ..." and
        // "heal: <n> groups of <area> differ": a heal the host could not send, or one too big to ask for, hides the
        // difference as surely as one it never tries.
        static readonly Regex Differs =
            new Regex(@"world: (\w+) (still )?differs|the host cannot heal (\w+)|groups of (\w+) differ");

        // The DriftAreas index a leave reason names ("world: missions differs at day 22 ..."), or -1.
        public static int AreaIn(string why)
        {
            if (why == null) return -1;
            Match m = Differs.Match(why);
            if (!m.Success) return -1;
            string area = m.Groups[1].Success ? m.Groups[1].Value
                : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            for (int i = 0; i < DriftAreas.All.Length; i++) if (DriftAreas.All[i] == area) return i;
            return -1;
        }
    }

    // Renders one area's items of a StateReader visit: "<agency> <key>/<key> f<field>=<value> ...", every field the
    // reader writes (discrete and continuous), in visit order.
    public sealed class AreaTextSink : IItemSink
    {
        readonly int area;
        readonly string[] agencies;
        readonly StringBuilder line = new StringBuilder();
        bool inArea, keysDone;
        public readonly List<string> Lines = new List<string>();
        public int Dropped;

        public AreaTextSink(int area, string[] agencies) { this.area = area; this.agencies = agencies; }

        public void BeginBlock(int a, int agency)
        {
            inArea = a == area;
            if (!inArea) return;
            blockAgency = agency >= 0 && agencies != null && agency < agencies.Length ? agencies[agency] : "-";
        }
        string blockAgency = "-";

        public void BeginItem()
        {
            if (!inArea) return;
            line.Length = 0;
            line.Append(blockAgency).Append(' ');
            keysDone = false;
        }

        public void Key(string existing) { AddKey(existing ?? ""); }
        public void Key(long value) { AddKey(value.ToString(CultureInfo.InvariantCulture)); }

        void AddKey(string k)
        {
            if (!inArea) return;
            if (keysDone) return;
            if (line.Length > 0 && line[line.Length - 1] != ' ') line.Append('/');
            line.Append(k);
        }

        void Field(int field, string v)
        {
            if (!inArea) return;
            keysDone = true;
            line.Append(" f").Append(field).Append('=').Append(v);
        }

        public void Val(int field, string existing) { Field(field, "\"" + Visible(existing) + "\""); }

        // A control character is written as an escape, never as the space the one-line log would make of it: the
        // print hashes the text as it is, so two rows that differ only by a newline must not read the same.
        static string Visible(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (char.IsControl(c))
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public void Val(int field, long value) { Field(field, value.ToString(CultureInfo.InvariantCulture)); }
        public void Val(int field, bool value) { Field(field, value ? "1" : "0"); }
        public void Val(int field, double value) { Field(field, value.ToString("R", CultureInfo.InvariantCulture)); }
        public void Date(int field, long ticks) { Field(field, "t" + ticks.ToString(CultureInfo.InvariantCulture)); }

        public void EndItem()
        {
            if (!inArea) return;
            if (Lines.Count >= WorldRowsText.MaxLines) { Dropped++; return; }
            string s = TextSanitiser.OneLine(line.ToString());
            Lines.Add(s.Length > WorldRowsText.MaxLine ? s.Substring(0, WorldRowsText.MaxLine) : s);
        }

        public void EndBlock() { if (inArea) inArea = false; }
    }
}
