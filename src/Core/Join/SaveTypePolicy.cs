using System.Collections.Generic;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // What OdinScan needs to know about a node's type beyond "is it allowed": the few types whose node shape the scanner holds to
    // a rule (docs\research\save-allowlist.md section 10.3).
    public enum NodeKind
    {
        Plain,
        Dictionary,       // E2: $rcontent holds { "$k", "$v" } entries (Dictionary`2, and the one subclass, ResourceBalanceSingle)
        EmptyComparer,    // E3: ObjectEqualityComparer`1 and GenericEqualityComparer`1 have nothing after $id and $type
        EnumComparer,     // E3: EnumEqualityComparer`1 is an empty array (or empty)
        ObjectArray,      // E4: object[] holds boxed strings, doubles, floats, AI event arguments, or references to them
        BoxedString,      // System.String as a node (a boxed string, one unnamed string)
        BoxedDouble,      // System.Double as a node (a boxed double, one unnamed number)
        BoxedFloat,       // System.Single as a node (a boxed float, one unnamed number; Economic.KillCrewAfterDays in a notification)
        EventArgument,    // AI.IdForOffer, AI.IdForContract: what AIPlayer sends its behaviour tree; they wait in an object[]
                          // in the saved event queue (HasReceivedEventWithQueue) until the tree reads them. Ordinary nodes.
    }

    // The allowlist applied to a whole Odin type name (M4 Task 7; docs\research\save-allowlist.md sections 10.4 and 10.5). The
    // name is parsed with the strict grammar first (TypeName), then every part must be on the list:
    //   - a node's own type: a closed non-generic type from Types, or a generic whose definition is in GenericDefinitions and
    //     whose arguments are all allowed, or exactly a name in ClosedGenerics, or an array of an allowed element;
    //   - a generic argument or an array element may also be an ArgumentOnlyTypes entry (System.Object, a content class);
    //     ArgumentOnlyTypes never serve as a node's whole type.
    // The checks are exact string matches on "Name, Assembly": no case folding, no version, no prefix.
    public static class SaveTypePolicy
    {
        const string Sys = "System.Collections.Generic.";

        // The names the scanner's structural rules (NodeKind) are written against. A test checks every one is on the list, so a
        // rename in the audit cannot leave the scanner holding a rule for a type that no longer exists.
        public static readonly string[] StructuralNames =
        {
            Sys + "Dictionary`2, mscorlib",
            "Extensions.MyExtensions+ResourceBalanceSingle, Assembly-CSharp",
            Sys + "ObjectEqualityComparer`1, mscorlib",
            Sys + "GenericEqualityComparer`1, mscorlib",
            Sys + "EnumEqualityComparer`1, mscorlib",
            "System.Object, mscorlib",
            "System.String, mscorlib",
            "System.Double, mscorlib",
            "System.Single, mscorlib",
            "AI.IdForOffer, Assembly-CSharp",
            "AI.IdForContract, Assembly-CSharp",
        };

        static readonly HashSet<string> Types = new HashSet<string>(SaveTypeAllowlist.Types);
        static readonly HashSet<string> Generics = new HashSet<string>(SaveTypeAllowlist.GenericDefinitions);
        static readonly HashSet<string> ArgumentOnly = new HashSet<string>(SaveTypeAllowlist.ArgumentOnlyTypes);
        static readonly HashSet<string> Closed = new HashSet<string>(SaveTypeAllowlist.ClosedGenerics);
        static readonly HashSet<string> RefClasses = new HashSet<string>(SaveTypeAllowlist.RefClasses);

        public const int MaxReportChars = 120;

        // Allowed as a node's type? When it is not, refusedPart names the part that failed ("System.Type, mscorlib" for
        // List<Type>; the definition "...SortedList`2, System" for a refused generic), clipped and printable.
        public static bool Allowed(string odinTypeName, out string refusedPart)
        {
            TypeRef parsed;
            return Check(odinTypeName, out parsed, out refusedPart);
        }

        // The same, and the parsed name for an allowed one (null for a refused one).
        public static bool Check(string odinTypeName, out TypeRef parsed, out string refusedPart)
        {
            parsed = null; refusedPart = null;
            TypeRef t; string error;
            if (!TypeName.TryParse(odinTypeName, out t, out error)) { refusedPart = Printable(odinTypeName); return false; }
            if (Closed.Contains(odinTypeName)) { parsed = t; return true; }
            string bad;
            bool ok = t.ArrayRank == 1 ? ArgumentOk(Element(t), out bad) : NodeOk(t, out bad);
            if (!ok) { refusedPart = Printable(bad); return false; }
            parsed = t;
            return true;
        }

        public static bool IsRefClass(string cls) { return cls != null && RefClasses.Contains(cls); }

        // The structural rule that applies to a node of this (allowed) type.
        public static NodeKind KindOf(TypeRef t)
        {
            if (t == null) return NodeKind.Plain;
            if (t.ArrayRank == 1)
                return t.Name == "System.Object" && t.Assembly == "mscorlib" && t.Args.Length == 0 ? NodeKind.ObjectArray : NodeKind.Plain;
            string key = t.Name + ", " + t.Assembly;
            if (key == StructuralNames[0] || key == StructuralNames[1]) return NodeKind.Dictionary;
            if (key == StructuralNames[2] || key == StructuralNames[3]) return NodeKind.EmptyComparer;
            if (key == StructuralNames[4]) return NodeKind.EnumComparer;
            if (key == StructuralNames[6]) return NodeKind.BoxedString;
            if (key == StructuralNames[7]) return NodeKind.BoxedDouble;
            if (key == StructuralNames[8]) return NodeKind.BoxedFloat;
            if (key == StructuralNames[9] || key == StructuralNames[10]) return NodeKind.EventArgument;
            return NodeKind.Plain;
        }

        // an array's element type: the same name without the suffix
        static TypeRef Element(TypeRef t) { return new TypeRef { Name = t.Name, Assembly = t.Assembly, Args = t.Args, ArrayRank = 0 }; }

        static string Key(TypeRef t) { return t.Name + ", " + t.Assembly; }

        // a non-array node type: closed and listed, or a listed generic with allowed arguments
        static bool NodeOk(TypeRef t, out string bad)
        {
            bad = null;
            if (t.Args.Length > 0) return GenericOk(t, out bad);
            string key = Key(t);
            if (Types.Contains(key)) return true;
            bad = key;   // unlisted, or argument-only
            return false;
        }

        // a generic argument or an array element: also argument-only types
        static bool ArgumentOk(TypeRef t, out string bad)
        {
            bad = null;
            if (t.ArrayRank == 1) return ArgumentOk(Element(t), out bad);
            if (t.Args.Length > 0) return GenericOk(t, out bad);
            string key = Key(t);
            if (Types.Contains(key) || ArgumentOnly.Contains(key)) return true;
            bad = key;
            return false;
        }

        static bool GenericOk(TypeRef t, out string bad)
        {
            bad = null;
            string definition = Key(t);   // "System.Collections.Generic.List`1, mscorlib"
            if (!Generics.Contains(definition)) { bad = definition; return false; }
            foreach (TypeRef a in t.Args)
                if (!ArgumentOk(a, out bad)) return false;
            return true;
        }

        // A name for a report: printable ASCII only, clipped. The text is peer-derived and goes to a log.
        static string Printable(string s)
        {
            if (s == null) return "(null)";
            if (s.Length == 0) return "(empty)";
            var sb = new StringBuilder(System.Math.Min(s.Length, MaxReportChars) + 3);
            for (int i = 0; i < s.Length && i < MaxReportChars; i++)
            {
                char c = s[i];
                sb.Append(c >= 0x20 && c < 0x7F ? c : '?');
            }
            if (s.Length > MaxReportChars) sb.Append("...");
            return sb.ToString();
        }

        internal static string Report(string s) { return Printable(s); }
    }
}
