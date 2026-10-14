using System;
using System.Collections.Generic;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // Why a scan refused (ScanResult.Code). 0 is Ok. The codes are stable: tests and the SempSaveScan tool print them.
    public static class ScanCode
    {
        public const int Ok = 0;
        public const int Empty = 1;               // no data, or only whitespace
        public const int Bom = 2;                 // a UTF-8 byte-order mark (Odin's StreamReader would swallow it)
        public const int BadByte = 3;             // a byte above 0x7F or a NUL where Odin writes only ASCII
        public const int Syntax = 4;              // the document is not in the dialect
        public const int Unterminated = 5;        // the data ends inside a string, node or array
        public const int BadEscape = 6;           // a backslash escape Odin does not write
        public const int StringTooLong = 7;
        public const int DepthExceeded = 8;
        public const int TrailingContent = 9;     // something after the root node
        public const int WrongRoot = 10;
        public const int TypeNotAllowed = 11;     // a $type name that fails the grammar or the allowlist
        public const int NotCanonical = 12;       // a type id, node id, reference id or length that is not canonical decimal
        public const int TypeIdUndefined = 13;    // a numeric $type with no earlier "N|name"
        public const int TypeIdRedefined = 14;
        public const int BackslashInName = 15;    // an escape inside a $type name, a reference or a key
        public const int TypeMisplaced = 16;      // E1: every node carries $type right after "{" (or "{" and $id)
        public const int IdMisplaced = 17;        // $id anywhere but first in a node
        public const int IdRepeated = 18;         // E6
        public const int IRefUnknown = 19;        // E6: $iref to an id not yet defined
        public const int KeyMisplaced = 20;       // E2: $k and $v
        public const int ComparerNotEmpty = 21;   // E3
        public const int ObjectArrayElement = 22; // E4
        public const int ArrayTooLong = 23;       // $rlength / $plength above the ceiling
        public const int LengthMismatch = 24;     // $rlength / $plength and the elements present disagree
        public const int TooManyElements = 25;
        public const int ExternalRef = 26;        // $eref, $guidref, $strref
        public const int BadReference = 27;       // $fstrref not "Class/id" with a listed class
        public const int TooManyTypes = 28;
        public const int TooManyRefs = 29;
        public const int TooManyIds = 30;
        public const int BadToken = 31;           // a bare token that is no number, boolean, null or reference
        public const int BadKey = 32;             // an unknown "$..." key, or an empty or odd key
        public const int TypeNameRepeated = 33;   // one type name under two ids
        public const int KeyRepeated = 34;        // E7: an ordinary key twice in one node (Odin is last-wins)
        public const int TooManyKeys = 35;        // more ordinary keys in one node than MaxKeysPerNode
        public const int KeyIsAlias = 36;         // E7: a key that is a [FormerlySerializedAs] old name of the node's type (SaveAliases)
        public const int TooManyKeySteps = 37;    // the duplicate-key sets did more than MaxKeySteps of work in all
        public const int InternalError = 99;      // the scanner itself failed: counts as a refusal, and a test never allows it
    }

    // The ceilings of a scan. The defaults are the absolute bounds; FromCorpus() is what the join uses: 4x the maximum measured over
    // the corpus (docs\research\join.md S10), so a legitimate save has room and a hostile one cannot ask for more.
    public sealed class ScanLimits
    {
        // Ceilings, not generous defaults: Task 7 sets each to 4x the corpus maximum measured in S10 (never above these
        // absolute bounds). MaxTotalElements bounds what Odin may preallocate in all.
        public int MaxDepth = 128; public int MaxStringChars = 65536; public int MaxDistinctTypes = 1024;
        public int MaxRefs = 65536; public long MaxArrayLength = 4000000; public long MaxTotalElements = 64000000;
        public int MaxIds = 1000000;       // distinct $id values (nodes that can be referenced)
        public int MaxKeysPerNode = 256;   // ordinary (non-$) keys in one node; the duplicate-key check keeps that many per depth level
        public long MaxKeySteps = 64000000; // work of the duplicate-key sets in all (see KeySet.Steps): bounds what a flood of colliding keys can cost
        public string RootType = "Manager.SaveGameData, Assembly-CSharp";

        // S10 maxima (22 files): depth 14, $rlength 1,102, array elements 34,738, longest string 240 bytes, 221 distinct type
        // names in all, 398 distinct references in one file, 34,909 distinct $id values in one file (measured by this scanner),
        // 31 ordinary keys in the fullest node and 153,074 steps of key checking in one file (both measured over 32 saves, 2026-10-11: the
        // 17 of the corpus and 15 spike snapshots; tools\savescan-flood\run.ps1 -Steps).
        public static ScanLimits FromCorpus()
        {
            return new ScanLimits
            {
                MaxDepth = 4 * 14,
                MaxArrayLength = 4 * 1102,
                MaxTotalElements = 4 * 34738,
                MaxStringChars = 4 * 240,
                MaxDistinctTypes = 4 * 221,
                MaxRefs = 4 * 398,
                MaxIds = 4 * 34909,
                MaxKeysPerNode = 4 * 31,
                MaxKeySteps = 4 * 153074,
            };
        }
    }

    public sealed class ScanResult
    {
        public bool Ok; public int Code; public string Error; public string RefusedType;
        public List<string> Types = new List<string>();   // distinct names, in first-seen order
        public List<string> Refs = new List<string>();    // distinct "Class/id"
        public int MaxDepthSeen; public int TypeEntries; public long Elements;
        public int Ids;                                    // distinct $id values seen
        public long KeySteps;                              // work done by the duplicate-key sets so far (the budget is MaxKeySteps)
    }

    // Wall 4 against a stranger's save: reads the Odin JSON dialect itself, strictly, before Odin or the game sees a byte
    // (docs\research\save-allowlist.md sections 3.1 and 10.3; the plan's Design decision 2). It accepts only the dialect Odin's
    // JsonDataWriter writes and refuses everything else, because Odin's reader (JsonTextReader) is loose in ways that let one
    // document read two ways. Where this scanner and Odin could disagree, the scanner refuses.
    //
    //   - ASCII only. Odin writes every character above 0x7F as \uXXXX, so a real save has none; this also covers every
    //     byte-order mark and every invalid UTF-8 sequence.
    //   - Whitespace is space, tab, CR, LF, and only between tokens. Odin's reader drops any Unicode whitespace and joins what is
    //     on both sides, so "1 2" reads as 12 there; here it is a refusal.
    //   - Entries are separated by exactly one comma; no trailing or double comma.
    //   - Escapes: \\ \" \0 \a \b \f \n \r \t and \uXXXX only. A backslash is refused outright in a $type name, a reference and
    //     a key (a name that changes meaning when unescaped: "$type" is a $type key to Odin).
    //   - Every number that means something structural ($id, $iref, a type id, a length) is canonical decimal: digits only, no
    //     sign, no leading zero, at most 9 digits. Odin parses those with NumberStyles.Any.
    //   - Odin deserialises and registers nodes it "skips" (section 3.1), so every "{" anywhere is a node and gets the checks:
    //       E1  every node carries $type right after "{" (or after "{" and $id); a $id anywhere else is refused;
    //       E2  a { "$k", "$v" } node only as an element of a dictionary's $rcontent, and a dictionary holds nothing else;
    //       E3  a comparer node is empty (an enum comparer: an empty array);
    //       E4  the elements of an object[] are boxed strings, doubles and floats, or nodes of the two types the AI
    //           queues as behaviour-tree event arguments (EventArgument, scanned as ordinary nodes), or $iref to one;
    //       E5  $type ids canonical, defined before use, never redefined; every name passes SaveTypePolicy;
    //       E6  $id unique, every $iref names an $id already seen;
    //       E7  an ordinary (non-$) key appears once in a node, compared exactly (Odin is last-wins); is not a [FormerlySerializedAs]
    //           old name of the node's type or its bases (SaveAliases: Odin reads such a name into the member of the current one);
    //           a node holds at most MaxKeysPerNode of them, and the checking is held to MaxKeySteps of work in all.
    //   - $rlength and $plength are held to a ceiling and must equal the elements present, before Odin could preallocate from
    //     them; the sum is held to MaxTotalElements.
    //   - $eref, $guidref and $strref are refused; $fstrref must be "Class/id" with Class in the allowlist's reference classes.
    //
    // Pure and allocation-bounded by the caps; recursion is bounded by the depth ceiling (clamped to MaxDepthClamp). Scan never
    // throws: any failure is a refusal.
    public static class OdinScan
    {
        public const int MaxDepthClamp = 200;

        // Tests only: runs inside the scan's try, so a test can prove that an exception in the scanner is a refusal (InternalError).
        internal static Action TestHook;

        public static ScanResult Scan(byte[] json, ScanLimits limits)
        {
            var result = new ScanResult();
            try
            {
                Action hook = TestHook;
                if (hook != null) hook();
                if (json == null || json.Length == 0) throw new Refusal(ScanCode.Empty, "no data", null);
                var s = new Scanner(json, limits ?? ScanLimits.FromCorpus(), result);
                s.Run();
                result.Ok = true; result.Code = ScanCode.Ok; result.Error = null; result.RefusedType = null;
            }
            catch (Refusal e)
            {
                result.Ok = false; result.Code = e.Code; result.Error = e.Message; result.RefusedType = e.Type;
            }
            catch (Exception e)
            {
                result.Ok = false; result.Code = ScanCode.InternalError; result.Error = "internal error: " + e.GetType().Name; result.RefusedType = null;
            }
            return result;
        }

        sealed class Refusal : Exception
        {
            public readonly int Code; public readonly string Type;
            public Refusal(int code, string message, string type) : base(message) { Code = code; Type = type; }
        }

        enum Role { Root, Normal, DictEntry, ObjectElem }
        enum Tok { Str, Num, Word, IRef, FStrRef }
        enum ArrayKind { Plain, Primitive, Dict, Object, EnumComparer }

        struct Val { public Tok Kind; public int Start, Len; public int Id; }

        sealed class NodeState
        {
            public Role Role;
            public int Entries;
            public bool SawId, SawType;
            public int IdValue;
            public NodeKind Kind;
            public bool PendingContent, PendingPrimitive, HadArray;
            public long PendingLength;
            public int ValueEntries;
            public KeySet Keys;   // the ordinary keys of this node so far; made at the first one, emptied (in O(1)) for each new node at this depth
            public byte[][] Aliases;   // the [FormerlySerializedAs] old names of this node's type (SaveAliases), or null
            public void Reset(Role role)
            {
                Role = role; Entries = 0; SawId = false; SawType = false; IdValue = -1; Kind = NodeKind.Plain;
                PendingContent = false; PendingPrimitive = false; HadArray = false; PendingLength = 0; ValueEntries = 0;
                Aliases = null;
                if (Keys != null) Keys.Clear();
            }
        }

        // E7: the ordinary keys already seen in one node, held as (start, length) ranges of the document, never as strings, so a node
        // costs no allocation per key and nothing is kept that is not in the input. One KeySet belongs to each depth level (NodeState
        // pool) and is reused by every node at that depth, so what is held never exceeds the depth ceiling times MaxKeysPerNode
        // entries (three ints each, plus two ints for each of at most four table slots per entry).
        // Open addressing with linear probing, load at most one half. Clear is O(1): a slot counts only if its generation is the
        // current one. Keys are compared as the bytes they are: the scanner refuses a backslash in a key and any byte outside
        // printable ASCII, so there is nothing to unescape and byte equality is Odin's ordinal string equality (Odin's member map is
        // a Dictionary<string, MemberInfo> on the default comparer: case-sensitive). The hash is unsalted, so keys can be made to
        // collide: a node of MaxKeysPerNode keys in one chain costs about that number squared over two steps, which the scan counts
        // and holds to MaxKeySteps in all (measured at 36 times the normal cost without a budget).
        sealed class KeySet
        {
            int[] start = new int[8], length = new int[8], hash = new int[8];   // by entry
            int[] slotGen = new int[16], slotEntry = new int[16];               // by table slot
            int gen = 1;
            int count;
            long steps;

            public int Count { get { return count; } }

            // The work done over this set's life (Clear does not reset it): one for each Add, one for each slot an Add probes past, and
            // one for each key a rehash moves. A node's keys cost about one step each; keys that share a probe chain cost more, and the
            // scan holds the total to MaxKeySteps, so a flood of colliding keys is a refusal and not a slow scan.
            public long Steps { get { return steps; } }

            public void Clear() { gen++; count = 0; }

            // Adds the key d[s .. s+len); false when the node already has an equal key.
            public bool Add(byte[] d, int s, int len)
            {
                steps++;
                int h = HashKey(d, s, len);
                int mask = slotGen.Length - 1;
                int slot = h & mask;
                while (slotGen[slot] == gen)
                {
                    int e = slotEntry[slot];
                    if (hash[e] == h && length[e] == len && SameKey(d, start[e], s, len)) return false;
                    slot = (slot + 1) & mask;
                    steps++;
                }
                if (count == start.Length)
                {
                    Array.Resize(ref start, count * 2); Array.Resize(ref length, count * 2); Array.Resize(ref hash, count * 2);
                }
                start[count] = s; length[count] = len; hash[count] = h;
                slotGen[slot] = gen; slotEntry[slot] = count;
                count++;
                if (count * 2 > slotGen.Length) Rehash();
                return true;
            }

            static int HashKey(byte[] d, int s, int len)
            {
                unchecked
                {
                    int h = (int)2166136261;
                    for (int k = 0; k < len; k++) h = (h ^ d[s + k]) * 16777619;
                    return h ^ (int)((uint)h >> 15);
                }
            }

            static bool SameKey(byte[] d, int a, int b, int len)
            {
                for (int k = 0; k < len; k++) if (d[a + k] != d[b + k]) return false;
                return true;
            }

            void Rehash()
            {
                slotGen = new int[slotGen.Length * 2]; slotEntry = new int[slotGen.Length];
                int mask = slotGen.Length - 1;
                for (int e = 0; e < count; e++)
                {
                    int slot = hash[e] & mask;
                    steps++;
                    while (slotGen[slot] == gen) { slot = (slot + 1) & mask; steps++; }
                    slotGen[slot] = gen; slotEntry[slot] = e;
                }
            }
        }

        sealed class Scanner
        {
            readonly byte[] d;
            readonly int n;
            readonly ScanLimits L;
            readonly ScanResult R;
            readonly int maxDepth;
            int i;
            int depth;
            long keySteps;
            readonly NodeState[] pool;
            readonly Dictionary<int, int> typeById = new Dictionary<int, int>();     // type id -> index into types
            readonly Dictionary<string, int> typeByName = new Dictionary<string, int>();
            readonly List<NodeKind> kinds = new List<NodeKind>();                      // by type index
            readonly List<byte[][]> aliases = new List<byte[][]>();                    // by type index: SaveAliases.Of(name), usually null
            readonly Dictionary<int, int> nodeIds = new Dictionary<int, int>();       // $id -> type index (-1 until the node's $type is read)
            readonly HashSet<string> refs = new HashSet<string>();

            public Scanner(byte[] data, ScanLimits limits, ScanResult result)
            {
                d = data; n = data.Length; L = limits; R = result;
                maxDepth = Math.Min(limits.MaxDepth, MaxDepthClamp);
                pool = new NodeState[Math.Max(maxDepth, 1) + 2];
                for (int k = 0; k < pool.Length; k++) pool[k] = new NodeState();
            }

            // ---- the document ----

            public void Run()
            {
                if (n >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) throw new Refusal(ScanCode.Bom, "a UTF-8 byte-order mark", null);
                SkipWs();
                if (i >= n) throw new Refusal(ScanCode.Empty, "only whitespace", null);
                if (d[i] != (byte)'{') throw Unexpected("the document is not an object");
                ParseNode(Role.Root);
                SkipWs();
                if (i < n) throw new Refusal(ScanCode.TrailingContent, "content after the root node at byte " + i, null);
                R.Ids = nodeIds.Count;
            }

            // ---- small helpers ----

            void SkipWs()
            {
                while (i < n)
                {
                    byte b = d[i];
                    if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n') i++;
                    else break;
                }
            }

            Refusal Fail(int code, string what) { return new Refusal(code, what + " at byte " + i, null); }

            // the byte at i is not what the grammar allows here
            Refusal Unexpected(string expected)
            {
                if (i >= n) return new Refusal(ScanCode.Unterminated, "the data ends where " + expected + " was expected", null);
                byte b = d[i];
                if (b == 0 || b >= 0x80) return new Refusal(ScanCode.BadByte, "byte 0x" + b.ToString("x2") + " at byte " + i + " (only ASCII is written)", null);
                return new Refusal(ScanCode.Syntax, "unexpected byte 0x" + b.ToString("x2") + " at byte " + i + " where " + expected + " was expected", null);
            }

            string Ascii(int start, int len)
            {
                var chars = new char[len];
                for (int k = 0; k < len; k++) chars[k] = (char)d[start + k];
                return new string(chars);
            }

            bool Is(int start, int len, string text)
            {
                if (len != text.Length) return false;
                for (int k = 0; k < len; k++) if (d[start + k] != text[k]) return false;
                return true;
            }

            bool IsBytes(int start, int len, byte[] text)
            {
                if (len != text.Length) return false;
                for (int k = 0; k < len; k++) if (d[start + k] != text[k]) return false;
                return true;
            }

            static bool IsDigit(byte b) { return b >= (byte)'0' && b <= (byte)'9'; }
            static bool IsLetter(byte b) { return (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z'); }
            static bool IsBareChar(byte b) { return IsDigit(b) || IsLetter(b) || b == (byte)'.' || b == (byte)'+' || b == (byte)'-'; }
            static bool IsHex(byte b) { return IsDigit(b) || (b >= (byte)'a' && b <= (byte)'f') || (b >= (byte)'A' && b <= (byte)'F'); }
            static int HexValue(byte b) { return IsDigit(b) ? b - '0' : (b | 0x20) - 'a' + 10; }

            // 1 to 9 digits, no leading zero unless the whole number is "0"
            bool CanonicalInt(int start, int len, out int value)
            {
                value = 0;
                if (len < 1 || len > 9) return false;
                if (len > 1 && d[start] == (byte)'0') return false;
                int v = 0;
                for (int k = 0; k < len; k++)
                {
                    byte b = d[start + k];
                    if (!IsDigit(b)) return false;
                    v = v * 10 + (b - (byte)'0');
                }
                value = v;
                return true;
            }

            void EnterDepth()
            {
                depth++;
                if (depth > R.MaxDepthSeen) R.MaxDepthSeen = depth;
                if (depth > maxDepth) throw Fail(ScanCode.DepthExceeded, "nesting deeper than " + maxDepth);
            }

            // ---- strings and tokens ----

            // d[i] is the opening quote. Reads to the closing quote, validating bytes and escapes; start/len are the raw bytes between
            // the quotes. noEscapes: a backslash is a refusal (type names, references).
            void ReadString(out int start, out int len, bool noEscapes)
            {
                i++;
                start = i;
                int max = L.MaxStringChars;
                while (true)
                {
                    if (i >= n) throw new Refusal(ScanCode.Unterminated, "a string is not closed", null);
                    byte b = d[i];
                    if (b == (byte)'"') break;
                    if (b == (byte)'\\')
                    {
                        if (noEscapes) throw Fail(ScanCode.BackslashInName, "a backslash in a name, a reference or a key");
                        i++;
                        if (i >= n) throw new Refusal(ScanCode.Unterminated, "the data ends after a backslash", null);
                        byte e = d[i];
                        if (e == (byte)'\\' || e == (byte)'"' || e == (byte)'0' || e == (byte)'a' || e == (byte)'b' || e == (byte)'f' || e == (byte)'n' || e == (byte)'r' || e == (byte)'t') i++;
                        else if (e == (byte)'u')
                        {
                            int value = 0;
                            for (int k = 1; k <= 4; k++)
                            {
                                if (i + k >= n) { i = n; throw new Refusal(ScanCode.Unterminated, "the data ends inside a \\u escape", null); }
                                if (!IsHex(d[i + k])) { i += k; throw Fail(ScanCode.BadEscape, "a \\u escape without four hex digits"); }
                                value = value * 16 + HexValue(d[i + k]);
                            }
                            // Odin writes \u only above 0x7F, and its reader takes a decoded backslash as the start
                            // of an escape: "\u005C" then a quote keeps Odin's string open where this one closes.
                            if (value < 0x80)
                                throw Fail(ScanCode.BadEscape, "a \\u escape below 0x80, which Odin never writes");
                            i += 5;
                        }
                        else throw Fail(ScanCode.BadEscape, "an escape Odin does not write");
                    }
                    else
                    {
                        if (b == 0 || b >= 0x80) throw new Refusal(ScanCode.BadByte, "byte 0x" + b.ToString("x2") + " in a string at byte " + i + " (only ASCII is written)", null);
                        i++;
                    }
                    if (i - start > max) throw Fail(ScanCode.StringTooLong, "a string longer than " + max);
                }
                len = i - start;
                i++;
            }

            // d[i] begins a value that is not a node or an array: a string, a bare token or a $ token.
            Val ReadScalar()
            {
                byte c = d[i];
                var v = new Val();
                if (c == (byte)'"')
                {
                    ReadString(out v.Start, out v.Len, false);
                    v.Kind = Tok.Str;
                    return v;
                }
                if (c == (byte)'$') return ReadDollar();
                if (!IsBareChar(c)) throw Unexpected("a value");
                int start = i;
                while (i < n && IsBareChar(d[i])) i++;
                int len = i - start;
                v.Start = start; v.Len = len;
                if (len > 64) throw Fail(ScanCode.BadToken, "a token longer than 64 bytes");
                if (Is(start, len, "true") || Is(start, len, "false") || Is(start, len, "null")) { v.Kind = Tok.Word; return v; }
                if (IsNumber(start, len)) { v.Kind = Tok.Num; return v; }
                i = start;
                throw Fail(ScanCode.BadToken, "a token that is no number, boolean or null");
            }

            // -?digits(.digits)?([eE][+-]?digits)? , or NaN, Infinity, -Infinity (what a double's "R" format gives)
            bool IsNumber(int start, int len)
            {
                if (Is(start, len, "NaN") || Is(start, len, "Infinity") || Is(start, len, "-Infinity")) return true;
                int p = start, end = start + len;
                if (p < end && d[p] == (byte)'-') p++;
                int digits = 0;
                while (p < end && IsDigit(d[p])) { p++; digits++; }
                if (digits == 0) return false;
                if (p < end && d[p] == (byte)'.')
                {
                    p++; digits = 0;
                    while (p < end && IsDigit(d[p])) { p++; digits++; }
                    if (digits == 0) return false;
                }
                if (p < end && (d[p] == (byte)'e' || d[p] == (byte)'E'))
                {
                    p++;
                    if (p < end && (d[p] == (byte)'+' || d[p] == (byte)'-')) p++;
                    digits = 0;
                    while (p < end && IsDigit(d[p])) { p++; digits++; }
                    if (digits == 0) return false;
                }
                return p == end;
            }

            // d[i] == '$': $iref:N, $fstrref:"Class/id", or a token that is refused
            Val ReadDollar()
            {
                int s = i;
                i++;
                while (i < n && IsLetter(d[i])) i++;
                int identLen = i - s;
                if (i >= n || d[i] != (byte)':') { i = s; throw Fail(ScanCode.BadToken, "a $ token without its colon"); }
                var v = new Val();
                if (Is(s, identLen, "$iref"))
                {
                    i++;
                    int ds = i;
                    while (i < n && IsDigit(d[i])) i++;
                    int id;
                    if (!CanonicalInt(ds, i - ds, out id) || (i < n && (IsBareChar(d[i]) || d[i] == (byte)':' || d[i] == (byte)'"')))
                        throw Fail(ScanCode.NotCanonical, "a $iref that is not canonical decimal");
                    if (!nodeIds.ContainsKey(id)) throw Fail(ScanCode.IRefUnknown, "$iref:" + id + " names an $id not yet defined");
                    v.Kind = Tok.IRef; v.Id = id; v.Start = ds; v.Len = i - ds;
                    return v;
                }
                if (Is(s, identLen, "$fstrref"))
                {
                    i++;
                    if (i >= n || d[i] != (byte)'"') throw Fail(ScanCode.BadReference, "$fstrref without a quoted \"Class/id\"");
                    int start, len;
                    ReadString(out start, out len, true);
                    NoteReference(start, len);
                    v.Kind = Tok.FStrRef; v.Start = start; v.Len = len;
                    return v;
                }
                if (Is(s, identLen, "$eref") || Is(s, identLen, "$guidref") || Is(s, identLen, "$strref"))
                    throw Fail(ScanCode.ExternalRef, Ascii(s, identLen) + " is not used by the game's saves");
                i = s;
                throw Fail(ScanCode.BadToken, "an unknown $ token");
            }

            // "Class/id": one slash, Class on the list, a non-empty id of printable ASCII
            void NoteReference(int start, int len)
            {
                int slash = -1;
                for (int k = 0; k < len; k++)
                {
                    byte b = d[start + k];
                    if (b == (byte)'/') slash = k;   // the last one: a class cannot contain a slash, so a second slash fails the class check below
                    else if (b < 0x20 || b > 0x7E) throw Fail(ScanCode.BadReference, "a reference with a control character");
                }
                if (slash <= 0 || slash == len - 1) throw Fail(ScanCode.BadReference, "a reference that is not Class/id");
                string cls = Ascii(start, slash);
                if (!SaveTypePolicy.IsRefClass(cls)) throw new Refusal(ScanCode.BadReference, "an unknown reference class at byte " + i, SaveTypePolicy.Report(cls));
                string whole = Ascii(start, len);
                if (refs.Add(whole))
                {
                    if (refs.Count > L.MaxRefs) throw Fail(ScanCode.TooManyRefs, "more than " + L.MaxRefs + " distinct references");
                    R.Refs.Add(whole);
                }
            }

            // ---- nodes ----

            // d[i] == '{'
            void ParseNode(Role role)
            {
                EnterDepth();
                i++;
                NodeState ns = pool[depth];
                ns.Reset(role);
                SkipWs();
                if (i >= n) throw Unexpected("an entry");
                if (d[i] == (byte)'}') throw Fail(ScanCode.Syntax, "an empty node");
                while (true)
                {
                    ParseEntry(ns);
                    SkipWs();
                    if (i >= n) throw Unexpected("\",\" or \"}\"");
                    if (d[i] == (byte)',')
                    {
                        i++;
                        SkipWs();
                        if (i >= n) throw Unexpected("an entry");
                        if (d[i] == (byte)'}') throw Fail(ScanCode.Syntax, "a comma before the closing brace");
                        continue;
                    }
                    if (d[i] == (byte)'}') { i++; break; }
                    throw Unexpected("\",\" or \"}\"");
                }
                CloseNode(ns);
                depth--;
            }

            void CloseNode(NodeState ns)
            {
                if (ns.Role == Role.DictEntry)
                {
                    if (ns.Entries != 2) throw Fail(ScanCode.KeyMisplaced, "a dictionary entry without both $k and $v");
                    return;
                }
                if (!ns.SawType) throw Fail(ScanCode.TypeMisplaced, "a node without a $type");
                if (ns.PendingContent) throw Fail(ScanCode.Syntax, "a length without its content");
                if (ns.Role == Role.ObjectElem && ns.ValueEntries != 1) throw Fail(ScanCode.ObjectArrayElement, "a boxed element of an object[] without its one value");
            }

            void ParseEntry(NodeState ns)
            {
                byte c = d[i];
                if (c == (byte)'"')
                {
                    int start, len;
                    ReadString(out start, out len, false);
                    SkipWs();
                    if (i < n && d[i] == (byte)':')
                    {
                        i++;
                        SkipWs();
                        if (i >= n) throw Unexpected("a value");
                        HandleKey(ns, start, len);
                        return;
                    }
                    BeginUnnamed(ns, false);
                    if (ns.Role == Role.ObjectElem) CheckBoxedValue(ns, Tok.Str);
                    ns.Entries++;
                    return;
                }
                if (c == (byte)'{')
                {
                    BeginUnnamed(ns, true);
                    ParseNode(Role.Normal);
                    ns.Entries++;
                    return;
                }
                if (c == (byte)'[') throw Fail(ScanCode.Syntax, "an array outside $rcontent or $pcontent");
                Val v = ReadScalar();
                BeginUnnamed(ns, false);
                if (ns.Role == Role.ObjectElem) CheckBoxedValue(ns, v.Kind);
                ns.Entries++;
            }

            // the checks every unnamed entry (a struct's number, a boxed value) passes
            void BeginUnnamed(NodeState ns, bool isNode)
            {
                if (ns.Role == Role.DictEntry) throw Fail(ScanCode.KeyMisplaced, "an unnamed entry in a dictionary entry");
                if (ns.PendingContent) throw Fail(ScanCode.Syntax, "a length without its content");
                // an entry before the node's $type is refused when the node closes (or at its $type, which is no longer first)
                if (ns.Kind == NodeKind.EmptyComparer || ns.Kind == NodeKind.EnumComparer) throw Fail(ScanCode.ComparerNotEmpty, "an entry in a comparer node");
                if (isNode && ns.Role == Role.ObjectElem) throw Fail(ScanCode.ObjectArrayElement, "a node inside a boxed element of an object[]");
            }

            // a boxed element of an object[] holds exactly one value, a string or a number
            void CheckBoxedValue(NodeState ns, Tok kind)
            {
                if (ns.Kind == NodeKind.BoxedString && kind != Tok.Str) throw Fail(ScanCode.ObjectArrayElement, "a boxed string that does not hold a string");
                if (ns.Kind == NodeKind.BoxedDouble && kind != Tok.Num) throw Fail(ScanCode.ObjectArrayElement, "a boxed double that does not hold a number");
                if (ns.Kind == NodeKind.BoxedFloat && kind != Tok.Num) throw Fail(ScanCode.ObjectArrayElement, "a boxed float that does not hold a number");
                ns.ValueEntries++;
            }

            // E4: the kinds of node an object[] element, or a reference in one, may be
            static bool IsElementKind(NodeKind k)
            {
                return k == NodeKind.BoxedString || k == NodeKind.BoxedDouble || k == NodeKind.BoxedFloat
                    || k == NodeKind.EventArgument;
            }

            // d[i] is the first byte of the key's value
            void HandleKey(NodeState ns, int start, int len)
            {
                // keys: no escapes (Odin would unescape "$type" into a metadata key), printable ASCII only
                if (len == 0) throw Fail(ScanCode.BadKey, "an empty key");
                for (int k = 0; k < len; k++)
                {
                    byte b = d[start + k];
                    if (b == (byte)'\\') throw Fail(ScanCode.BackslashInName, "a backslash in a key");
                    if (b < 0x20 || b > 0x7E) throw Fail(ScanCode.BadKey, "a control character in a key");
                }
                bool meta = d[start] == (byte)'$';

                // a dictionary entry is exactly "$k" then "$v"
                if (ns.Role == Role.DictEntry)
                {
                    if (!(ns.Entries == 0 && Is(start, len, "$k")) && !(ns.Entries == 1 && Is(start, len, "$v")))
                        throw Fail(ScanCode.KeyMisplaced, "a dictionary entry must be $k then $v");
                    ParseValueNoArray();
                    ns.Entries++;
                    return;
                }
                if (meta && (Is(start, len, "$k") || Is(start, len, "$v"))) throw Fail(ScanCode.KeyMisplaced, Ascii(start, len) + " outside a dictionary's content");

                if (meta && Is(start, len, "$id")) { DoId(ns); return; }
                if (meta && Is(start, len, "$type")) { DoType(ns); return; }

                // every other entry needs the node's type first
                bool isRContent = meta && Is(start, len, "$rcontent"), isPContent = meta && Is(start, len, "$pcontent");
                bool isRLength = meta && Is(start, len, "$rlength"), isPLength = meta && Is(start, len, "$plength");
                if (ns.PendingContent)
                {
                    bool matches = ns.PendingPrimitive ? isPContent : isRContent;
                    if (!matches) throw Fail(ScanCode.Syntax, "a length must be followed at once by its content");
                }
                else if (isRContent || isPContent) throw Fail(ScanCode.Syntax, "content without a length before it");
                if (!ns.SawType) throw Fail(ScanCode.TypeMisplaced, "an entry before the node's $type");
                if (meta && !isRContent && !isPContent && !isRLength && !isPLength) throw Fail(ScanCode.BadKey, "an unknown metadata key");

                if (ns.Kind == NodeKind.EmptyComparer) throw Fail(ScanCode.ComparerNotEmpty, "an entry in a comparer node");
                if (ns.Kind == NodeKind.EnumComparer && !(isRLength || isRContent)) throw Fail(ScanCode.ComparerNotEmpty, "an entry in an enum comparer node other than its empty array");
                if (ns.Role == Role.ObjectElem) throw Fail(ScanCode.ObjectArrayElement, "a named entry in a boxed element of an object[]");

                if (isRLength || isPLength) { DoLength(ns, isPLength); return; }
                if (isRContent || isPContent) { DoContent(ns, isPContent); return; }

                // E7: what is left is an ordinary key (every unknown "$..." key is refused above). An old name of the node's type is
                // refused outright: Odin reads it into the same member as the current name and keeps the last of the two.
                byte[][] old = ns.Aliases;
                if (old != null)
                    for (int a = 0; a < old.Length; a++)
                        if (IsBytes(start, len, old[a])) throw Fail(ScanCode.KeyIsAlias, "a key that is an old name of the node's type");
                // and Odin is last-wins on a repeated key
                if (ns.Keys == null) ns.Keys = new KeySet();
                long before = ns.Keys.Steps;
                bool added = ns.Keys.Add(d, start, len);
                keySteps += ns.Keys.Steps - before;
                R.KeySteps = keySteps;
                if (!added) throw Fail(ScanCode.KeyRepeated, "a key repeated in one node");
                if (keySteps > L.MaxKeySteps) throw Fail(ScanCode.TooManyKeySteps, "more than " + L.MaxKeySteps + " steps of key checking in all");
                if (ns.Keys.Count > L.MaxKeysPerNode) throw Fail(ScanCode.TooManyKeys, "more than " + L.MaxKeysPerNode + " keys in one node");

                ParseValueNoArray();
                ns.Entries++;
            }

            // a value that is a node or a scalar, never an array
            void ParseValueNoArray()
            {
                byte c = d[i];
                if (c == (byte)'{') ParseNode(Role.Normal);
                else if (c == (byte)'[') throw Fail(ScanCode.Syntax, "an array outside $rcontent or $pcontent");
                else ReadScalar();
            }

            // ---- $id ----

            void DoId(NodeState ns)
            {
                if (ns.Entries != 0) throw Fail(ScanCode.IdMisplaced, "$id anywhere but first in a node");
                byte c = d[i];
                if (c == (byte)'{' || c == (byte)'[') throw Fail(ScanCode.NotCanonical, "$id is not a number");
                Val v = ReadScalar();
                int id;
                if (v.Kind != Tok.Num || !CanonicalInt(v.Start, v.Len, out id)) throw Fail(ScanCode.NotCanonical, "$id is not canonical decimal");
                if (nodeIds.Count >= L.MaxIds) throw Fail(ScanCode.TooManyIds, "more than " + L.MaxIds + " ids");
                if (nodeIds.ContainsKey(id)) throw Fail(ScanCode.IdRepeated, "$id " + id + " is used twice");
                nodeIds[id] = -1;
                ns.SawId = true; ns.IdValue = id;
                ns.Entries++;
            }

            // ---- $type ----

            void DoType(NodeState ns)
            {
                if (!(ns.Entries == 0 || (ns.Entries == 1 && ns.SawId))) throw Fail(ScanCode.TypeMisplaced, "$type anywhere but first in a node (or right after $id)");
                R.TypeEntries++;
                byte c = d[i];
                int index;
                if (c == (byte)'{') throw Fail(ScanCode.TypeMisplaced, "$type is a node");
                if (c == (byte)'[') throw Fail(ScanCode.Syntax, "$type is an array");
                if (c == (byte)'"')
                {
                    int start, len;
                    ReadString(out start, out len, true);
                    int bar = -1;
                    for (int k = 0; k < len; k++) if (d[start + k] == (byte)'|') { bar = k; break; }
                    int id;
                    if (bar < 0 || !CanonicalInt(start, bar, out id)) throw Fail(ScanCode.NotCanonical, "a $type string must start with a canonical decimal id and a bar");
                    string name = Ascii(start + bar + 1, len - bar - 1);
                    index = DefineType(ns, id, name);
                }
                else
                {
                    Val v = ReadScalar();
                    int id;
                    if (v.Kind != Tok.Num) throw Fail(ScanCode.TypeMisplaced, "$type is neither a string nor a number");
                    if (!CanonicalInt(v.Start, v.Len, out id)) throw Fail(ScanCode.NotCanonical, "a $type id is not canonical decimal");
                    if (!typeById.TryGetValue(id, out index)) throw Fail(ScanCode.TypeIdUndefined, "$type " + id + " is used before it is defined");
                }
                ns.SawType = true;
                ns.Kind = kinds[index];
                ns.Aliases = aliases[index];
                if (ns.SawId) nodeIds[ns.IdValue] = index;
                // an AI event argument has members (IdForOffer's id): from its $type on it gets the ordinary node scan
                if (ns.Role == Role.ObjectElem && ns.Kind == NodeKind.EventArgument) ns.Role = Role.Normal;
                if (ns.Role == Role.ObjectElem && ns.Kind != NodeKind.BoxedString && ns.Kind != NodeKind.BoxedDouble && ns.Kind != NodeKind.BoxedFloat)
                    throw Fail(ScanCode.ObjectArrayElement,
                        "an element of an object[] must be a boxed string, double or float, or an AI event argument");
                ns.Entries++;
            }

            int DefineType(NodeState ns, int id, string name)
            {
                if (typeById.ContainsKey(id)) throw Fail(ScanCode.TypeIdRedefined, "type id " + id + " is defined twice");
                if (ns.Role == Role.Root && name != L.RootType) throw new Refusal(ScanCode.WrongRoot, "the root node is not " + L.RootType, SaveTypePolicy.Report(name));
                if (typeByName.ContainsKey(name)) throw new Refusal(ScanCode.TypeNameRepeated, "a type name is defined under two ids", SaveTypePolicy.Report(name));
                TypeRef parsed; string part;
                if (!SaveTypePolicy.Check(name, out parsed, out part)) throw new Refusal(ScanCode.TypeNotAllowed, "a type that is not allowed: " + part, part);
                if (kinds.Count >= L.MaxDistinctTypes) throw Fail(ScanCode.TooManyTypes, "more than " + L.MaxDistinctTypes + " distinct types");
                int index = kinds.Count;
                kinds.Add(SaveTypePolicy.KindOf(parsed));
                aliases.Add(SaveAliases.Of(name));
                typeById[id] = index;
                typeByName[name] = index;
                R.Types.Add(name);
                return index;
            }

            // ---- $rlength / $plength and their content ----

            void DoLength(NodeState ns, bool primitive)
            {
                if (ns.HadArray || ns.PendingContent) throw Fail(ScanCode.Syntax, "a second array in one node");
                byte c = d[i];
                if (c == (byte)'{' || c == (byte)'[') throw Fail(ScanCode.NotCanonical, "a length is not a number");
                Val v = ReadScalar();
                int count;
                if (v.Kind != Tok.Num || !CanonicalInt(v.Start, v.Len, out count)) throw Fail(ScanCode.NotCanonical, "a length is not canonical decimal");
                if (ns.Kind == NodeKind.EnumComparer && count != 0) throw Fail(ScanCode.ComparerNotEmpty, "an enum comparer with elements");
                if (count > L.MaxArrayLength) throw Fail(ScanCode.ArrayTooLong, "a length of " + count + " is above " + L.MaxArrayLength);
                R.Elements += count;
                if (R.Elements > L.MaxTotalElements) throw Fail(ScanCode.TooManyElements, "more than " + L.MaxTotalElements + " array elements in all");
                ns.PendingContent = true; ns.PendingPrimitive = primitive; ns.PendingLength = count;
                ns.Entries++;
            }

            void DoContent(NodeState ns, bool primitive)
            {
                if (d[i] != (byte)'[') throw Fail(ScanCode.Syntax, "content that is not an array");
                ArrayKind kind = primitive ? ArrayKind.Primitive
                    : ns.Kind == NodeKind.Dictionary ? ArrayKind.Dict
                    : ns.Kind == NodeKind.ObjectArray ? ArrayKind.Object
                    : ns.Kind == NodeKind.EnumComparer ? ArrayKind.EnumComparer
                    : ArrayKind.Plain;
                long declared = ns.PendingLength;
                ns.PendingContent = false; ns.HadArray = true;
                ParseArray(kind, declared);
                ns.Entries++;
            }

            // d[i] == '['
            void ParseArray(ArrayKind kind, long declared)
            {
                EnterDepth();
                i++;
                long count = 0;
                SkipWs();
                if (i >= n) throw Unexpected("an element or \"]\"");
                if (d[i] == (byte)']') i++;
                else
                {
                    while (true)
                    {
                        ParseElement(kind);
                        count++;
                        SkipWs();
                        if (i >= n) throw Unexpected("\",\" or \"]\"");
                        if (d[i] == (byte)',')
                        {
                            i++;
                            SkipWs();
                            if (i >= n) throw Unexpected("an element");
                            if (d[i] == (byte)']') throw Fail(ScanCode.Syntax, "a comma before the closing bracket");
                            continue;
                        }
                        if (d[i] == (byte)']') { i++; break; }
                        throw Unexpected("\",\" or \"]\"");
                    }
                }
                if (count != declared) throw Fail(ScanCode.LengthMismatch, "fewer elements (" + count + ") than the declared " + declared);
                depth--;
            }

            void ParseElement(ArrayKind kind)
            {
                byte c = d[i];
                switch (kind)
                {
                    case ArrayKind.EnumComparer:
                        throw Fail(ScanCode.ComparerNotEmpty, "an element in an enum comparer");
                    case ArrayKind.Primitive:
                        if (c == (byte)'{' || c == (byte)'[' || c == (byte)'$') throw Fail(ScanCode.Syntax, "a primitive array holds only numbers, booleans and strings");
                        {
                            Val v = ReadScalar();
                            if (v.Kind == Tok.Word && Is(v.Start, v.Len, "null")) throw Fail(ScanCode.Syntax, "null in a primitive array");
                        }
                        return;
                    case ArrayKind.Dict:
                        if (c != (byte)'{') throw Fail(ScanCode.KeyMisplaced, "a dictionary's content holds only { \"$k\", \"$v\" } entries");
                        ParseNode(Role.DictEntry);
                        return;
                    case ArrayKind.Object:
                        if (c == (byte)'{') { ParseNode(Role.ObjectElem); return; }
                        if (c == (byte)'[') throw Fail(ScanCode.Syntax, "an array in an array");
                        {
                            Val v = ReadScalar();
                            if (v.Kind != Tok.IRef)
                                throw Fail(ScanCode.ObjectArrayElement, "an element of an object[] must be a node or a reference");
                            int type = nodeIds[v.Id];
                            if (type < 0 || !IsElementKind(kinds[type]))
                                throw Fail(ScanCode.ObjectArrayElement,
                                    "a reference in an object[] must name a boxed string, double or float, or an AI event argument");
                        }
                        return;
                    default:
                        if (c == (byte)'{') { ParseNode(Role.Normal); return; }
                        if (c == (byte)'[') throw Fail(ScanCode.Syntax, "an array in an array");
                        ReadScalar();
                        return;
                }
            }
        }
    }
}
