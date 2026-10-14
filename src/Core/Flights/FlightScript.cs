using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    public sealed class FlightLine
    {
        public int Start, Target; public long Ship, Rocket; public CargoRow[] Cargo;
        public double DepartDays;
    }

    // The -semp-flights file (tests only): one flight per line, so trigger "fly <n>" means line n. Each line is
    //   start=<id>;target=<id>;ship=<id>;rocket=<id>;cargo=<res>:<amount>,...;depart=+<days>
    // start, target, ship and depart are required; rocket (default -1: none) and cargo (default none) are optional.
    // Blank lines and comments are errors, not skipped, so the line numbers in the file and in the result agree. The
    // file is read by a test profile only, but it is still capped: it is parsed before any game state exists.
    public static class FlightScript
    {
        public const int MaxBytes = 16 * 1024, MaxLines = 64, MaxLineChars = 512;
        const double MaxDepartDays = 36500;

        // The lines, or null with an error that names the line.
        public static FlightLine[] Parse(string text, out string error)
        {
            error = null;
            if (text == null) { error = "no text"; return null; }
            if (Encoding.UTF8.GetByteCount(text) > MaxBytes) { error = "the file is over 16 KiB"; return null; }
            string[] lines = text.Split('\n');
            int count = lines.Length;
            // one newline at the end is the end of the last line, not an empty line
            if (count > 0 && lines[count - 1].Length == 0) count--;
            if (count == 0) { error = "the file has no flights"; return null; }
            if (count > MaxLines) { error = "the file has more than 64 lines"; return null; }
            var result = new FlightLine[count];
            for (int i = 0; i < count; i++)
            {
                string line = lines[i];
                if (line.EndsWith("\r", StringComparison.Ordinal)) line = line.Substring(0, line.Length - 1);
                string why = ParseLine(line, out result[i]);
                if (why != null) { error = "line " + (i + 1) + ": " + why; return null; }
            }
            return result;
        }

        static string ParseLine(string line, out FlightLine flight)
        {
            flight = null;
            if (line.Length > MaxLineChars) return "over 512 characters";
            if (line.Length == 0) return "empty";
            foreach (char c in line)
                if (c < ' ' || c == 0x7F) return "a control character";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var f = new FlightLine { Rocket = -1, Cargo = new CargoRow[0] };
            bool start = false, target = false, ship = false, depart = false;
            foreach (string part in line.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) return "'" + part + "' is not name=value";
                string name = part.Substring(0, eq), value = part.Substring(eq + 1);
                if (!seen.Add(name)) return name + " is given twice";
                string why = null;
                switch (name)
                {
                    case "start": why = Body(value, out f.Start); start = why == null; break;
                    case "target": why = Body(value, out f.Target); target = why == null; break;
                    case "ship": why = Id(value, out f.Ship); ship = why == null; break;
                    case "rocket": why = Id(value, out f.Rocket); break;
                    case "cargo": why = CargoList(value, out f.Cargo); break;
                    case "depart": why = Depart(value, out f.DepartDays); depart = why == null; break;
                    default: return "unknown field " + name;
                }
                if (why != null) return name + ": " + why;
            }
            if (!start) return "start is missing";
            if (!target) return "target is missing";
            if (!ship) return "ship is missing";
            if (!depart) return "depart is missing";
            if (f.Start == f.Target) return "start is the target";
            flight = f;
            return null;
        }

        static string Body(string s, out int id)
        {
            id = 0;
            long v;
            if (!Whole(s, out v) || v < 1 || v > ActionLimits.MaxBodyId) return "not a body id";
            id = (int)v;
            return null;
        }

        static string Id(string s, out long id)
        {
            if (!Whole(s, out id) || id < 0) return "not an id";
            return null;
        }

        // digits only (and no sign), so "+5" and " 5" are not silently taken
        static bool Whole(string s, out long v)
        {
            v = 0;
            if (s.Length == 0 || s.Length > 18) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
        }

        static string CargoList(string s, out CargoRow[] rows)
        {
            rows = null;
            var list = new List<CargoRow>();
            if (s.Length > 0)
                foreach (string item in s.Split(','))
                {
                    int colon = item.IndexOf(':');
                    if (colon <= 0) return "'" + item + "' is not resource:amount";
                    string res = item.Substring(0, colon);
                    if (!ActionShapes.IsContentId(res)) return "'" + res + "' is not a resource id";
                    double amount;
                    if (!Amount(item.Substring(colon + 1), out amount) || !(amount > 0)
                        || amount > ActionLimits.MaxMoney)
                        return "'" + item + "' has a bad amount";
                    if (list.Count >= ActionLimits.MaxCargoRows) return "more than 32 cargo rows";
                    list.Add(new CargoRow { Kind = CargoKinds.Resource, Item = res, Amount = amount });
                }
            rows = list.ToArray();
            return null;
        }

        static string Depart(string s, out double days)
        {
            days = 0;
            if (s.Length < 2 || s[0] != '+') return "not +<days>";
            if (!Amount(s.Substring(1), out days) || days < 0 || days > MaxDepartDays)
                return "not +<days> within 100 years";
            return null;
        }

        // a plain decimal number: digits with at most one point, no sign, no exponent
        static bool Amount(string s, out double d)
        {
            d = 0;
            if (s.Length == 0 || s.Length > 24) return false;
            int points = 0;
            foreach (char c in s)
            {
                if (c == '.') points++;
                else if (c < '0' || c > '9') return false;
            }
            if (points > 1 || s[0] == '.' || s[s.Length - 1] == '.') return false;
            return double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out d);
        }
    }
}
