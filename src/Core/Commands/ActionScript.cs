using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The -semp-actions file of a test run: a list of requests that a dev trigger sends one at a time. A line is
    // "key=value" pairs separated by ';' (keys are ActionRequest's fields in lower case; a value may contain spaces, as
    // content ids can; blank lines and lines starting with '#' are skipped; the n-th request is the n-th line that is
    // neither). Ids are comma-separated; Targets are "body:ratio:count" rows, comma-separated. It is parsed with the
    // care the wire gets: the size caps first, each value strictly, and every request must pass the same Validate a
    // client's request does, so a typo is an error at start-up and never a half-run script.
    public static class ActionScript
    {
        // null + error for anything that is not a whole valid script.
        public static ActionRequest[] Parse(string text, out string error)
        {
            error = null;
            if (text == null) { error = "no script"; return null; }
            if (Encoding.UTF8.GetByteCount(text) > ActionLimits.MaxScriptBytes)
            {
                error = "the script is over " + ActionLimits.MaxScriptBytes + " bytes";
                return null;
            }
            string[] lines = text.Split('\n');
            int count = lines.Length - (lines.Length > 0 && lines[lines.Length - 1].Length == 0 ? 1 : 0);
            if (count > ActionLimits.MaxScriptLines)
            {
                error = "the script has more than " + ActionLimits.MaxScriptLines + " lines";
                return null;
            }
            var requests = new List<ActionRequest>();
            for (int i = 0; i < count; i++)
            {
                string line = lines[i].TrimEnd('\r');
                string where = "line " + (i + 1) + ": ";
                if (line.Length > ActionLimits.MaxScriptLineChars)
                {
                    error = where + "longer than " + ActionLimits.MaxScriptLineChars + " characters";
                    return null;
                }
                foreach (char c in line)
                    if (c < ' ' && c != '\t') { error = where + "a control character"; return null; }
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                ActionRequest a = ParseLine(trimmed, out error);
                if (a == null) { error = where + error; return null; }
                string bad = a.Validate();
                if (bad != null) { error = where + bad; return null; }
                requests.Add(a);
            }
            if (requests.Count == 0) { error = "the script has no actions"; return null; }
            return requests.ToArray();
        }

        static ActionRequest ParseLine(string line, out string error)
        {
            error = null;
            var a = new ActionRequest();
            var seen = new HashSet<string>();
            foreach (string pair in line.Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) { error = "'" + Show(pair) + "' has no '='"; return null; }
                string key = pair.Substring(0, eq).Trim().ToLowerInvariant(), value = pair.Substring(eq + 1).Trim();
                if (key.Length == 0) { error = "a value has no key"; return null; }
                if (!seen.Add(key)) { error = "'" + Show(key) + "' is given twice"; return null; }
                if (!Set(a, key, value, out error)) return null;
            }
            return a;
        }

        static bool Set(ActionRequest a, string key, string value, out string error)
        {
            error = null;
            long n; double d; uint u;
            switch (key)
            {
                case "req":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out u)) return Bad(key,
                        out error);
                    a.Req = u; return true;
                case "kind":
                    int kind;
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                        out kind)) { a.Kind = kind; return true; }
                    a.Kind = ActionKind.Parse(value);
                    if (a.Kind == 0) error = "'" + Show(value) + "' is not an action kind";
                    return a.Kind != 0;
                case "agency": a.Agency = value; return true;
                case "item": a.Item = value; return true;
                case "body": return Int(key, value, out a.Body, out error);
                case "tobody": return Int(key, value, out a.ToBody, out error);
                case "group": return Int(key, value, out a.Group, out error);
                case "ship": return Int(key, value, out a.Ship, out error);
                case "count": return Int(key, value, out a.Count, out error);
                case "position": return Int(key, value, out a.Position, out error);
                case "toposition": return Int(key, value, out a.ToPosition, out error);
                case "value": return Int(key, value, out a.Value, out error);
                case "itemid":
                    if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                        out n)) return Bad(key, out error);
                    a.ItemId = n; return true;
                case "amount":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                        || double.IsNaN(d) || double.IsInfinity(d))
                        return Bad(key, out error);
                    a.Amount = d; return true;
                case "ids":
                    a.Ids = value.Length == 0 ? new string[0] : value.Split(',');
                    return true;
                case "targets": return Targets(a, value, out error);
                default:
                    error = "unknown key '" + Show(key) + "'";
                    return false;
            }
        }

        static bool Int(string key, string value, out int field, out string error)
        {
            error = null;
            if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                out field)) return true;
            return Bad(key, out error);
        }

        static bool Targets(ActionRequest a, string value, out string error)
        {
            error = null;
            if (value.Length == 0) { a.Targets = new TargetRow[0]; return true; }
            string[] rows = value.Split(',');
            var list = new TargetRow[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                string[] parts = rows[i].Split(':');
                var row = new TargetRow();
                double ratio;
                if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out row.Body)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ratio)
                    || double.IsNaN(ratio) || double.IsInfinity(ratio)
                    || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                        out row.Count))
                    return Bad("targets", out error);
                row.Ratio = ratio;
                list[i] = row;
            }
            a.Targets = list;
            return true;
        }

        static bool Bad(string key, out string error)
        {
            error = "'" + Show(key) + "' has a bad value";
            return false;
        }

        // What an error may quote of the file: short and plain, never a hostile string at full length.
        static string Show(string s) { return TextSanitiser.Clean(s, 24, "?"); }
    }
}
