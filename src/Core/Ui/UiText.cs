using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SolarExpanseMultiplayer
{
    // The text table (Design decision 12): every player-facing line by key, English compiled in (UiStrings), and an
    // optional translation file that may replace lines. Get never throws: a key missing from the translation reads in
    // English, a key nobody knows logs once and reads as the key itself. A translation is loaded whole or not at all.
    public static class UiText
    {
        public const int MaxTranslationChars = 100000;   // the whole file
        public const int MaxLineChars = 600;             // one translated line
        const int MaxLocaleChars = 32;
        const int MaxLoggedMisses = 64;                  // a flood of bad keys cannot grow the log set for ever
        const int MaxPlaceholder = 3;                    // {0} to {3}

        static readonly object Gate = new object();
        static readonly HashSet<string> Missed = new HashSet<string>(StringComparer.Ordinal);
        static Dictionary<string, string> translation = new Dictionary<string, string>(StringComparer.Ordinal);
        static string locale = "en";

        public static string Locale { get { lock (Gate) return locale; } }

        public static bool Has(string key)
        {
            return key != null && UiStrings.English.ContainsKey(key);
        }

        public static IEnumerable<string> Keys()
        {
            return UiStrings.English.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        // The line for a key with its arguments in place. Arguments are positional ({0}..{3}) and formatted without
        // regard to the machine's culture.
        public static string Get(string key, params object[] args)
        {
            try
            {
                if (key == null) return "";
                string english;
                if (!UiStrings.English.TryGetValue(key, out english))
                {
                    LogMissOnce(key);
                    return key;
                }
                string line;
                Dictionary<string, string> table;
                lock (Gate) table = translation;
                if (!table.TryGetValue(key, out line)) line = english;
                // a line that does not fit its arguments (a caller gave too few) reads as English, as it is
                return Fill(line, args) ?? english;
            }
            catch (Exception) { return key ?? ""; }
        }

        // null when the line does not fit its arguments
        static string Fill(string line, object[] args)
        {
            if (args == null || args.Length == 0) return line;
            try { return string.Format(System.Globalization.CultureInfo.InvariantCulture, line, args); }
            // a bad format, or an argument whose ToString throws: the caller falls back to the English line as it is
            catch (Exception) { return null; }
        }

        static void LogMissOnce(string key)
        {
            bool first;
            lock (Gate) first = Missed.Count < MaxLoggedMisses && Missed.Add(key);
            if (first) Log.Warn("text: no line for the key \"" + Clip(key) + "\"");
        }

        // For the log: a key from a translation file is untrusted, so a control character (a newline) is shown as
        // \uXXXX and cannot start a forged log line.
        static string Clip(string s)
        {
            string head = s.Length <= 60 ? s : s.Substring(0, 60);
            var sb = new System.Text.StringBuilder(head.Length);
            foreach (char c in head)
            {
                if (c < ' ' || c >= 0x7F && c <= 0x9F || c == '\u2028' || c == '\u2029')
                    sb.Append("\\u").Append(((int)c).ToString("X4"));
                else sb.Append(c);
            }
            return s.Length <= 60 ? sb.ToString() : sb + "...";
        }

        // Back to English only (the menu reads a translation once; a test needs a clean start).
        public static void ClearTranslation()
        {
            lock (Gate)
            {
                translation = new Dictionary<string, string>(StringComparer.Ordinal);
                locale = "en";
                Missed.Clear();
            }
        }

        // Loads a translation file's text: {"key": "line", ...}. Returns null when it loaded, else why the whole file
        // was refused (and logs it; English stays). Every key must be one the table has, each line must use exactly the
        // arguments the English line does (each written plainly as {n}), and no line holds a '<'.
        public static string LoadTranslation(string json, string localeName)
        {
            string why = Refusal(json, localeName, out Dictionary<string, string> lines);
            if (why != null)
            {
                Log.Warn("text: translation refused: " + why);
                return why;
            }
            lock (Gate)
            {
                translation = lines;
                locale = localeName;
            }
            Log.Info("text: translation " + localeName + " loaded (" + lines.Count + " lines)");
            return null;
        }

        static string Refusal(string json, string localeName, out Dictionary<string, string> lines)
        {
            lines = null;
            if (!LocaleOk(localeName)) return "the locale name is not usable";
            if (json == null) return "there is no file";
            if (json.Length > MaxTranslationChars) return "the file is too big";
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            string bad = Read(json, found);
            if (bad != null) return bad;
            foreach (KeyValuePair<string, string> kv in found)
            {
                string english;
                if (!UiStrings.English.TryGetValue(kv.Key, out english))
                    return "unknown key \"" + Clip(kv.Key) + "\"";
                bad = LineProblem(kv.Key, kv.Value, english);
                if (bad != null) return bad;
            }
            lines = found;
            return null;
        }

        static bool LocaleOk(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > MaxLocaleChars) return false;
            foreach (char c in s)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_'))
                    return false;
            return true;
        }

        // One flat JSON object of text values; anything else is a refusal.
        static string Read(string json, Dictionary<string, string> into)
        {
            try
            {
                using (var r = new JsonTextReader(new StringReader(json)))
                {
                    r.DateParseHandling = DateParseHandling.None;
                    r.MaxDepth = 4;
                    if (!r.Read() || r.TokenType != JsonToken.StartObject) return "the file is not one JSON object";
                    while (true)
                    {
                        if (!r.Read()) return "the file ends inside its object";
                        if (r.TokenType == JsonToken.EndObject) break;
                        if (r.TokenType != JsonToken.PropertyName) return "the file is not a plain list of lines";
                        string key = (string)r.Value;
                        if (!r.Read() || r.TokenType != JsonToken.String)
                            return "the line for \"" + Clip(key) + "\" is not text";
                        if (into.ContainsKey(key)) return "the key \"" + Clip(key) + "\" is there twice";
                        into.Add(key, (string)r.Value);
                    }
                    if (r.Read()) return "there is more after the object";
                }
            }
            catch (JsonException) { return "the file is not valid JSON"; }
            return null;
        }

        static string LineProblem(string key, string line, string english)
        {
            string name = "the line for \"" + key + "\"";
            if (line.Length == 0) return name + " is empty";
            if (line.Length > MaxLineChars) return name + " is too long";
            foreach (char c in line)
            {
                if (c == '<') return name + " holds a '<'";
                if (c < ' ' || c >= 0x7F && c <= 0x9F) return name + " holds a control character";
                if (Redirects(c)) return name + " holds a character that changes the text direction or line";
                // the JSON reader turns a lone surrogate into U+FFFD, so that mark is how half a character arrives
                if (c == '\uFFFD') return name + " holds half of a character";
            }
            HashSet<int> have = Placeholders(line);
            if (have == null) return name + " holds a brace that is not a plain {n}";
            HashSet<int> want = Placeholders(english);
            if (want == null || !have.SetEquals(want)) return name + " does not use the same arguments as English";
            return null;
        }

        // Characters that reorder what is around them (bidi embeddings, overrides and isolates), break a line, or are
        // a byte order mark. The joiners U+200C and U+200D and other format characters stay: Persian and Indic
        // text needs them.
        static bool Redirects(char c)
        {
            return c >= '\u202A' && c <= '\u202E' || c >= '\u2066' && c <= '\u2069' || c == '\u2028' || c == '\u2029'
                || c == '\uFEFF';
        }

        // The numbers of the {n} placeholders in a line, or null when a brace is anything but a plain {n}
        // (an alignment or format part, a doubled brace, a stray brace, a number past {3}).
        public static HashSet<int> Placeholders(string line)
        {
            var set = new HashSet<int>();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '}') return null;
                if (c != '{') continue;
                if (i + 2 >= line.Length || line[i + 1] < '0' || line[i + 1] > '9' || line[i + 2] != '}') return null;
                int n = line[i + 1] - '0';
                if (n > MaxPlaceholder) return null;
                set.Add(n);
                i += 2;
            }
            return set;
        }
    }
}
