namespace SolarExpanseMultiplayer
{
    // A parsed Odin type name: "Ns.Type+Nested, Asm", with generic arguments and at most one array suffix.
    // Name is the part before the arguments ("System.Collections.Generic.List`1"), Assembly the simple assembly name.
    public sealed class TypeRef { public string Name; public string Assembly; public TypeRef[] Args; public int ArrayRank; }

    // The strict grammar of docs\research\save-allowlist.md section 10.4 (M4 Task 7). Odin's own binder accepts far more than the
    // names Odin writes (Type.GetType first: single brackets, unqualified arguments, spaces, Version= parts), so a name that does
    // not match this grammar exactly is refused, whatever it would bind to:
    //
    //   Name   := Bare ", " Asm                       one comma and one space, nothing else
    //   Bare   := Simple [ "[" Arg ( "," Arg )* "]" ] [ "[]" ]
    //   Arg    := "[" Name "]"                        each generic argument is [[Name, Asm]] in the whole
    //   Simple := one or more of A-Z a-z 0-9 _ . + `  (ASCII only)
    //   Asm    := one or more of A-Z a-z 0-9 _ . -    (ASCII only)
    //
    // A generic's Simple ends in `n (n from 1, no leading zero) and has exactly n arguments; a name without a backtick has none.
    // One array rank only, nesting of argument groups at most 8 deep, the whole name at most 1024 characters. The parser never
    // throws and never recurses deeper than the nesting limit, so a hostile name costs a bounded amount of work.
    public static class TypeName
    {
        public const int MaxLength = 1024;
        public const int MaxNesting = 8;
        public const int MaxArguments = 16;   // the largest arity accepted (the BCL's largest generic here is 3)

        // true and a TypeRef for a name in the grammar; otherwise false, r = null and a short reason that never repeats the input.
        public static bool TryParse(string s, out TypeRef r, out string error)
        {
            r = null; error = null;
            if (s == null) { error = "no name"; return false; }
            if (s.Length == 0) { error = "empty name"; return false; }
            if (s.Length > MaxLength) { error = "longer than " + MaxLength + " characters"; return false; }
            var p = new Parser(s);
            TypeRef t = p.Name(0);
            if (t == null) { error = p.Error; return false; }
            if (p.Pos != s.Length) { error = "text after the assembly at " + p.Pos; return false; }
            r = t;
            return true;
        }

        static bool SimpleChar(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '+' || c == '`';
        }

        static bool AssemblyChar(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
        }

        sealed class Parser
        {
            readonly string s;
            public int Pos;
            public string Error;

            public Parser(string text) { s = text; }

            TypeRef Fail(string what) { if (Error == null) Error = what + " at " + Pos; return null; }

            // Name := Bare ", " Asm. `groups` is how many argument groups enclose this name.
            public TypeRef Name(int groups)
            {
                int start = Pos;
                while (Pos < s.Length && SimpleChar(s[Pos])) Pos++;
                if (Pos == start) return Fail("no type name");
                string simple = s.Substring(start, Pos - start);

                int arity;
                if (!Arity(simple, out arity)) return Fail("bad generic arity");

                TypeRef[] args = null;
                int rank = 0;
                if (Pos < s.Length && s[Pos] == '[')
                {
                    if (Pos + 1 < s.Length && s[Pos + 1] == '[')
                    {
                        if (arity == 0) return Fail("arguments on a name that is not generic");
                        if (groups + 1 > MaxNesting) return Fail("argument groups nested deeper than " + MaxNesting);
                        args = Arguments(arity, groups + 1);
                        if (args == null) return null;
                    }
                    else if (Pos + 1 < s.Length && s[Pos + 1] == ']')
                    {
                        Pos += 2;
                        rank = 1;
                    }
                    else return Fail("bad bracket");
                }
                if (arity > 0 && args == null) return Fail("generic name without its arguments");
                // an array suffix after the arguments: List`1[[..]][]
                if (rank == 0 && Pos + 1 < s.Length && s[Pos] == '[' && s[Pos + 1] == ']') { Pos += 2; rank = 1; }
                if (Pos < s.Length && s[Pos] == '[') return Fail("array rank above one");

                if (Pos + 1 >= s.Length || s[Pos] != ',' || s[Pos + 1] != ' ') return Fail("expected \", \" before the assembly");
                Pos += 2;
                int asm = Pos;
                while (Pos < s.Length && AssemblyChar(s[Pos])) Pos++;
                if (Pos == asm) return Fail("no assembly name");

                return new TypeRef { Name = simple, Assembly = s.Substring(asm, Pos - asm), Args = args ?? new TypeRef[0], ArrayRank = rank };
            }

            // "[" Arg ( "," Arg )* "]" with exactly `arity` arguments
            TypeRef[] Arguments(int arity, int groups)
            {
                var list = new TypeRef[arity];
                Pos++;   // the group's "["
                for (int k = 0; k < arity; k++)
                {
                    if (k > 0)
                    {
                        if (Pos >= s.Length || s[Pos] != ',') { Fail("too few generic arguments"); return null; }
                        Pos++;
                    }
                    if (Pos >= s.Length || s[Pos] != '[') { Fail("expected \"[\" before a generic argument"); return null; }
                    Pos++;
                    TypeRef a = Name(groups);
                    if (a == null) return null;
                    if (Pos >= s.Length || s[Pos] != ']') { Fail("expected \"]\" after a generic argument"); return null; }
                    Pos++;
                    list[k] = a;
                }
                if (Pos >= s.Length || s[Pos] != ']') { Fail("more generic arguments than the arity"); return null; }
                Pos++;
                return list;
            }

            // 0 for a name without a backtick; n for "...`n" (one backtick, then 1 or 2 digits to the end, no leading zero, 1..16)
            static bool Arity(string simple, out int arity)
            {
                arity = 0;
                int tick = simple.IndexOf('`');
                if (tick < 0) return true;
                if (tick == 0) return false;
                int digits = simple.Length - tick - 1;
                if (digits < 1 || digits > 2) return false;
                int n = 0;
                for (int i = tick + 1; i < simple.Length; i++)
                {
                    char c = simple[i];
                    if (c < '0' || c > '9') return false;
                    n = n * 10 + (c - '0');
                }
                if (simple[tick + 1] == '0' || n > MaxArguments) return false;
                arity = n;
                return true;
            }
        }
    }
}
