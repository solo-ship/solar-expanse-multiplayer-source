using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The content scan's rules that need no game (M4 Task 10). The game glue (src/Game/ContentScan.cs) lists the
    // assemblies, reads the files and hands every definition to DefinitionWalker; what is listed, how and in which
    // category is decided here.

    // ------------------------------------------------------------------ where an assembly file lives

    public static class AssemblyWhere
    {
        // The Where of a summary entry for an assembly loaded from a file, or null for one in the game's Managed folder
        // (those are covered by ManagedHash, not listed one by one). gameRoot: the folder holding "Solar Expanse_Data".
        public static string Of(string fileFull, string gameRoot)
        {
            if (string.IsNullOrEmpty(fileFull) || string.IsNullOrEmpty(gameRoot)) return "other";
            string root = gameRoot.TrimEnd('\\', '/');
            if (PathRules.IsInside(Join(root, "Solar Expanse_Data", "Managed"), fileFull)) return null;
            if (PathRules.IsInside(Join(root, "BepInEx", "core"), fileFull)) return "bepinex-core";
            if (PathRules.IsInside(Join(root, "BepInEx", "plugins"), fileFull)) return "bepinex-plugin";
            if (PathRules.IsInside(Join(root, "SolarExpanseMultiplayer"), fileFull)) return "mod";
            return "other";
        }

        static string Join(params string[] parts) { return string.Join("\\", parts); }
    }

    // ------------------------------------------------------------------ assembly files on disk, loaded or not

    // The content check lists the assemblies the process has loaded, and also every DLL file in the folders mods load
    // code from, loaded yet or not: BepInEx\plugins, BepInEx\patchers, our own mod folder and any folder Doorstop's
    // config points at. A dependency a plugin loads only later (lazily) still counts at the join. What stays out of
    // reach is code loaded at run time from anywhere else (docs/DESIGN.md "Content checksum"). A file is listed the
    // same way loaded or not (by its own metadata), so a host that has loaded a DLL and a joiner that has not still
    // match.
    public static class DiskAssemblies
    {
        // The folders a Doorstop config points at: target_assembly's folder and every dll_search_path_override entry
        // (separated by ';'), relative ones from the game root. Empty for no config.
        public static List<string> DoorstopFolders(string iniText, string gameRoot)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(iniText)) return list;
            foreach (string raw in iniText.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 1 || line.StartsWith("#") || line.StartsWith(";")) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (value.Length == 0) continue;
                if (key == "target_assembly")
                {
                    string dir = Path.GetDirectoryName(value);
                    if (!string.IsNullOrEmpty(dir)) list.Add(Full(dir, gameRoot));
                }
                else if (key == "dll_search_path_override")
                    foreach (string part in value.Split(';'))
                        if (part.Trim().Length > 0) list.Add(Full(part.Trim(), gameRoot));
            }
            return list;
        }

        static string Full(string path, string gameRoot)
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(gameRoot, path)).TrimEnd('\\', '/');
        }

        // Every folder to list files from: the three fixed ones and Doorstop's, once each. Never the game root itself
        // or a folder above it (a config pointing there would list the whole game) and never Managed (ManagedHash
        // covers it).
        public static List<string> Roots(string gameRoot, string doorstopIni)
        {
            string root = Path.GetFullPath(gameRoot).TrimEnd('\\', '/');
            var all = new List<string>
            {
                Path.Combine(Path.Combine(root, "BepInEx"), "plugins"),
                Path.Combine(Path.Combine(root, "BepInEx"), "patchers"),
                Path.Combine(root, "SolarExpanseMultiplayer"),
            };
            all.AddRange(DoorstopFolders(doorstopIni, root));
            string managed = Path.Combine(Path.Combine(root, "Solar Expanse_Data"), "Managed");
            var list = new List<string>();
            foreach (string r in all)
            {
                string f = Path.GetFullPath(r).TrimEnd('\\', '/');
                bool rootOrAbove = PathRules.IsInside(f, root) || string.Equals(f, root,
                    StringComparison.OrdinalIgnoreCase);
                bool inManaged = PathRules.IsInside(managed, f) || string.Equals(f, managed,
                    StringComparison.OrdinalIgnoreCase);
                if (rootOrAbove || inManaged) continue;
                bool dup = false;
                foreach (string x in list) if (string.Equals(x, f, StringComparison.OrdinalIgnoreCase)) dup = true;
                if (!dup) list.Add(f);
            }
            return list;
        }

        // One file's entry: its name, its assembly name and version from its own metadata (the file name and no version
        // for a native DLL), its SHA-256 and where it lives.
        public static PluginEntry FileEntry(string file, string gameRoot)
        {
            string name = Path.GetFileNameWithoutExtension(file), version = null;
            try
            {
                AssemblyName n = AssemblyName.GetAssemblyName(file);
                name = n.Name;
                version = n.Version != null ? n.Version.ToString() : null;
            }
            catch (Exception) { }
            string sha;
            using (SHA256 h = SHA256.Create())
            using (FileStream s = File.OpenRead(file))
                sha = ContentHex.Of(h.ComputeHash(s), 32);
            return new PluginEntry
            {
                Id = Path.GetFileName(file), Name = name, Version = version, Sha256 = sha,
                Where = AssemblyWhere.Of(file, gameRoot) ?? "other",
            };
        }

        // Adds every *.dll under the roots (recursively, in ordinal path order) that `listed` does not hold yet (full
        // paths, any case). Stops one past the summary's cap, so an overfull list is refused as SummaryTooBig.
        public static void AddFiles(List<PluginEntry> into, HashSet<string> listed, IList<string> roots,
            string gameRoot)
        {
            foreach (string r in roots)
            {
                if (!Directory.Exists(r)) continue;
                string[] files = Directory.GetFiles(r, "*.dll", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    if (into.Count > JoinLimits.MaxAssemblies) return;
                    string full = Path.GetFullPath(f);
                    if (!listed.Add(full)) continue;
                    into.Add(FileEntry(full, gameRoot));
                }
            }
        }
    }

    // ------------------------------------------------------------------ assemblies loaded from bytes (I-9)

    // An assembly with no file is listed as "memory" by its full name and MVID, and always counts, except the two the
    // loaders make (docs/research/join.md S7), which LoaderFiles leaves out only in the exact forms made here:
    // - HarmonySharedState (Harmony 2.x under Doorstop): a new MVID every launch, so it is proved by its contents
    //   instead. When it holds exactly Harmony's one type and four fields, it is listed under its exact full name with
    //   a canonical MVID (the hash of that shape), stable across launches; anything else under that name is listed with
    //   a marked name and its real MVID, so it counts as a difference.
    // - UnityEngine.CoreModule (BepInEx patches it in memory): listed as is only when its in-memory MVID equals the
    //   MVID of the same-named file in Managed; otherwise with a marked name, so it counts whatever its MVID.
    public static class MemoryAssemblies
    {
        public const string HarmonyName = "HarmonySharedState";
        public const string CoreModuleName = "UnityEngine.CoreModule";
        public const string Unexpected = "[unexpected contents] ", NotTheFile = "[differs from Managed] ";

        // Harmony 2.4.2's shared-state assembly: one module with no global members, one type with four static fields,
        // as Type.ToString writes the field types (no assembly versions in it).
        public const string HarmonyShape =
            "modules 1\n" +
            "type HarmonySharedState Public, Abstract, Sealed : System.Object\n" +
            "field originals Public, Static System.Collections.Generic.Dictionary`2" +
                "[System.Reflection.MethodInfo,System.Reflection.MethodBase]\n" +
            "field originalsMono Public, Static System.Collections.Generic.Dictionary`2" +
                "[System.Int64,System.Reflection.MethodBase[]]\n" +
            "field state Public, Static System.Collections.Generic.Dictionary`2" +
                "[System.Reflection.MethodBase,System.Byte[]]\n" +
            "field version Public, Static System.Int32\n";

        // The canonical MVID of a HarmonySharedState with exactly that shape: 32 hex of SHA-256 over the shape text.
        public static string HarmonyCanonicalMvid
        {
            get
            {
                using (SHA256 sha = SHA256.Create())
                    return ContentHex.Of(sha.ComputeHash(Encoding.UTF8.GetBytes(HarmonyShape)), 16);
            }
        }

        // The entry for an assembly with no file. mvid: 32 hex; shape: ShapeOf(the assembly), asked only for
        // HarmonySharedState; fileMvid: the MVID of Managed\<simple name>.dll (null when there is none or it could not
        // be read).
        public static PluginEntry Entry(string fullName, string simpleName, string version, string mvid, string shape,
            string fileMvid)
        {
            var e = new PluginEntry { Id = fullName, Name = simpleName, Version = version, Where = "memory",
                Mvid = mvid };
            if (simpleName == HarmonyName)
            {
                if (fullName == LoaderFiles.HarmonySharedStateName
                    && shape == HarmonyShape) e.Mvid = HarmonyCanonicalMvid;
                else e.Id = Marked(fullName, Unexpected);
            }
            else if (simpleName == CoreModuleName && (fileMvid == null || fileMvid != mvid)) e.Id = Marked(fullName,
                NotTheFile);
            return e;
        }

        // The mark goes first, so the simple name (before the first comma) is no longer the excluded one either.
        static string Marked(string fullName, string mark)
        {
            string n = mark + (fullName ?? "?");
            return n.Length > 128 ? n.Substring(0, 128) : n;
        }

        // The whole assembly in a fixed order, in the form HarmonyShape uses: how many modules it has, the manifest
        // module's own (global) methods and fields, then every type with all its declared members, static and instance.
        // A member of any kind is listed (a method, a constructor, a property, a nested type), so anything Harmony's
        // state does not have breaks the match.
        public static string ShapeOf(Assembly a)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var sb = new StringBuilder();
            sb.Append("modules ").Append(a.GetModules().Length).Append('\n');
            var global = new List<string>();
            foreach (MethodInfo m in a.ManifestModule.GetMethods(All)) global.Add("module method " + m.Name);
            foreach (FieldInfo f in a.ManifestModule.GetFields(All)) global.Add("module " + Member(f));
            global.Sort(StringComparer.Ordinal);
            foreach (string l in global) sb.Append(l).Append('\n');
            var types = new List<Type>(a.GetTypes());
            types.Sort((x, y) => string.CompareOrdinal(x.FullName, y.FullName));
            foreach (Type t in types)
            {
                string kind = (t.IsPublic ? "Public" : t.IsNotPublic ? "NotPublic" : "Nested") +
                              (t.IsAbstract ? ", Abstract" : "") + (t.IsSealed ? ", Sealed" : "") +
                              (t.IsInterface ? ", Interface" : "");
                sb.Append("type ").Append(t.FullName).Append(' ').Append(kind)
                  .Append(" : ").Append(t.BaseType == null ? "-" : t.BaseType.FullName).Append('\n');
                var lines = new List<string>();
                foreach (MemberInfo m in t.GetMembers(All)) lines.Add(Member(m));
                lines.Sort(StringComparer.Ordinal);
                foreach (string l in lines) sb.Append(l).Append('\n');
            }
            return sb.ToString();
        }

        static string Member(MemberInfo m)
        {
            var f = m as FieldInfo;
            if (f == null) return m.MemberType + " " + m.Name;
            return "field " + f.Name + " " + (f.IsPublic ? "Public" : "NotPublic") + (f.IsStatic ? ", Static" : "") +
                   (f.IsInitOnly ? ", InitOnly" : "") + (f.IsLiteral ? ", Literal" : "") + " " + f.FieldType;
        }

        // Which assemblies with no file need the costly facts Entry asks for: the shape (Harmony's state, matched by
        // its simple name; Entry then wants the exact full name too) and the Managed file's MVID (CoreModule).
        public static bool NeedsShape(string simpleName) { return simpleName == HarmonyName; }
        public static bool NeedsFileMvid(string simpleName) { return simpleName == CoreModuleName; }
    }

    // ------------------------------------------------------------------ an assembly file's MVID, without loading it

    // The module version id of a .NET assembly file, read from its metadata (the Module table's first row, through the
    // #GUID heap), so the scan can compare the in-memory UnityEngine.CoreModule with the file on disk. Returns null for
    // anything that is not a well-formed managed PE file; never throws.
    public static class PeMvid
    {
        public static string Read(byte[] pe)
        {
            try { return ReadOrThrow(pe); }
            catch (Exception) { return null; }
        }

        static string ReadOrThrow(byte[] b)
        {
            if (b == null || b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return null;
            int peOff = I32(b, 0x3c);
            if (peOff < 0 || peOff + 24 > b.Length || I32(b, peOff) != 0x00004550) return null;
            int coff = peOff + 4;
            int sections = U16(b, coff + 2), optSize = U16(b, coff + 16);
            int opt = coff + 20;
            int magic = U16(b, opt);
            int dirs = magic == 0x10b ? opt + 96 : magic == 0x20b ? opt + 112 : -1;
            if (dirs < 0) return null;
            int cliRva = I32(b, dirs + 14 * 8);
            int secTable = opt + optSize;
            int cli = Offset(b, secTable, sections, cliRva);
            if (cli < 0) return null;
            int metaRva = I32(b, cli + 8);
            int meta = Offset(b, secTable, sections, metaRva);
            if (meta < 0 || I32(b, meta) != 0x424A5342) return null;   // "BSJB"
            int verLen = I32(b, meta + 12);
            int p = meta + 16 + verLen;
            int streams = U16(b, p + 2);
            p += 4;
            int tables = -1, guids = -1;
            for (int i = 0; i < streams; i++)
            {
                int off = I32(b, p), size = I32(b, p + 4);
                int q = p + 8, start = q;
                while (b[q] != 0) q++;
                string name = Encoding.ASCII.GetString(b, start, q - start);
                p = start + ((q - start + 4) & ~3);
                if (name == "#~" || name == "#-") tables = meta + off;
                else if (name == "#GUID") guids = meta + off;
                if (size < 0) return null;
            }
            if (tables < 0 || guids < 0) return null;
            int heapSizes = b[tables + 6];
            long valid = I64(b, tables + 8);
            if ((valid & 1) == 0) return null;   // no Module table
            int rows = tables + 24;
            for (int t = 0; t < 64; t++) if ((valid & (1L << t)) != 0) rows += 4;
            int stringIdx = (heapSizes & 1) != 0 ? 4 : 2, guidIdx = (heapSizes & 2) != 0 ? 4 : 2;
            int row = rows + 2 + stringIdx;   // Generation, Name, then Mvid
            int mvidIndex = guidIdx == 4 ? I32(b, row) : U16(b, row);
            if (mvidIndex < 1) return null;
            int g = guids + (mvidIndex - 1) * 16;
            if (g < 0 || g + 16 > b.Length) return null;
            var raw = new byte[16];
            Buffer.BlockCopy(b, g, raw, 0, 16);
            return new Guid(raw).ToString("N");
        }

        static int Offset(byte[] b, int secTable, int sections, int rva)
        {
            for (int i = 0; i < sections; i++)
            {
                int s = secTable + i * 40;
                int va = I32(b, s + 12), raw = I32(b, s + 16), ptr = I32(b, s + 20);
                if (rva >= va && rva < va + raw) return rva - va + ptr;
            }
            return -1;
        }

        static int U16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
        static int I32(byte[] b, int i) { return b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24); }
        static long I64(byte[] b, int i) { return (uint)I32(b, i) | ((long)I32(b, i + 4) << 32); }
    }

    // ------------------------------------------------------------------ which category a definition counts in

    public static class ContentCategories
    {
        // typeNames: the definition's type and its base types (simple names), most derived first. The seven named kinds
        // of the spec, mapped as the S7 spike measured them; Bodies are the body descriptors the game has at the menu
        // (planetary systems, generated planet types, star types); everything else is Other.
        public static int Of(IList<string> typeNames)
        {
            if (Has(typeNames, "FacilityBaseDescriptor")) return ContentCategory.Facilities;
            if (Has(typeNames, "ResearchDefinition")) return ContentCategory.Research;
            if (Has(typeNames, "ResourceDefinition")) return ContentCategory.Resources;
            if (Has(typeNames, "ContractDefinition")) return ContentCategory.Contracts;
            if (Has(typeNames, "SpacecraftType") || Has(typeNames, "LaunchVehicleType") || Has(typeNames,
                "SpaceComponent"))
                return ContentCategory.SpacecraftParts;
            if (Has(typeNames, "MyIDScriptableObjectProductionItem")) return ContentCategory.ProductionItems;
            if (Has(typeNames, "PlanetarySystemDescriptor") || Has(typeNames, "GeneratedPlanetType") || Has(typeNames,
                "StarTypeDefinition"))
                return ContentCategory.Bodies;
            return ContentCategory.Other;
        }

        // The id a definition is listed under: its own id in a named category, "Type/id" in Other (several kinds share
        // it).
        public static string ItemId(int category, string typeName, string id)
        {
            return category == ContentCategory.Other ? typeName + "/" + id : id;
        }

        static bool Has(IList<string> names, string n)
        {
            if (names == null) return false;
            foreach (string s in names) if (s == n) return true;
            return false;
        }
    }

    // ------------------------------------------------------------------ the summary, or why there is none

    public static class ContentScanSummary
    {
        // The summary, or null with code SummaryTooBig (more plugins or assemblies than a summary carries, or past its
        // byte cap) or ContentUnavailable (anything else: the scan's own result is unusable).
        public static ContentSummary Build(string gameBuild, string rawGameVersion, string modVersion, string loader,
            string managedHash,
                                           int managedCount, IList<PluginEntry> plugins, IList<PluginEntry> assemblies,
                                               DefinitionHasher defs,
                                           out int code, out string error)
        {
            code = 0;
            if ((plugins != null && plugins.Count > JoinLimits.MaxPlugins) || (assemblies != null
                && assemblies.Count > JoinLimits.MaxAssemblies))
            {
                code = JoinCodes.SummaryTooBig;
                error = "more than " + JoinLimits.MaxPlugins + " plugins or " + JoinLimits.MaxAssemblies
                    + " assemblies";
                return null;
            }
            bool tooBig;
            ContentSummary s = ContentSummaries.Build(gameBuild, rawGameVersion, modVersion, loader, managedHash,
                managedCount, plugins, assemblies,
                                                      defs, out error, out tooBig);
            if (s == null) code = tooBig ? JoinCodes.SummaryTooBig : JoinCodes.ContentUnavailable;
            return s;
        }
    }

    // ------------------------------------------------------------------ fields the game fills in during play

    // A serialised field that the game's own getter fills in place the first time it runs, during play only (it never
    // travels in a save). Hashed empty, it would make a host's played-in definitions differ from a joiner's freshly
    // loaded ones, so an empty one is hashed as the value its getter returns. A field set to anything else still counts.
    public static class LazyFields
    {
        // The reference text (as the game glue's refOf writes it) for an empty field, or null for any other field.
        public static string DefaultRef(string typeName, string field)
        {
            // LaunchVehicleType.FuelTypeOnStart: an empty field becomes the resource id_resource_fuel (a rival's
            // launch runs it, so a long game with the rivals' AI on fills it for some launch vehicles)
            if (field == "fuelTypeOnStart" && typeName == "LaunchVehicleType")
                return "id:ResourceDefinition/id_resource_fuel";
            return null;
        }
    }

    // ------------------------------------------------------------------ the definitions, by reflection

    // Walks one definition object by reflection and feeds its effective values into a DefinitionHasher (Design decision
    // 7: the fields Unity or Odin serialise, nested serialisable objects, lists and dictionaries, references to other
    // definitions by id; floats by their bits; a depth cap and a cycle guard). Deterministic: a type's fields are taken
    // base type first, each type's in ordinal name order. Never leaves an item half open: a value that cannot be read
    // is hashed as a marker.
    //
    // refOf: for a value that is a game object reference (a UnityEngine.Object), the text it is hashed as
    // ("id:<type>/<id>" for a definition, a name otherwise); null for anything that is not one, which is then walked.
    // Asked below the item's own level.
    public sealed class DefinitionWalker
    {
        public const string CycleMark = "\u0001cycle", DeepMark = "\u0001deep", UnreadableMark = "\u0001unreadable";

        readonly Func<object, string> refOf;
        readonly int maxDepth, maxElements;
        readonly Dictionary<Type, FieldInfo[]> fields = new Dictionary<Type, FieldInfo[]>();
        readonly HashSet<object> path = new HashSet<object>(RefEq.Instance);

        public int Fields { get; private set; }   // values hashed so far, for the log

        public DefinitionWalker(Func<object, string> refOf, int maxDepth, int maxElements)
        {
            if (refOf == null) throw new ArgumentNullException("refOf");
            this.refOf = refOf;
            this.maxDepth = maxDepth;
            this.maxElements = maxElements;
        }

        public void Item(DefinitionHasher h, int category, string id, object o)
        {
            h.BeginItem(category, id);
            path.Clear();
            try
            {
                if (o != null) { path.Add(o); WalkFields(h, o, 1); }
            }
            finally { path.Clear(); }
            h.EndItem();
        }

        // Unity serialises a public field and one marked [SerializeField]; Odin also one marked [OdinSerialize];
        // neither one marked [NonSerialized], nor a static one. Odin's own SerializationData (the serialised copy of
        // the Odin fields), delegates and pointers are never hashed. Matched by attribute name: Core does not reference
        // the game's assemblies.
        public static bool Serialised(FieldInfo f)
        {
            if (f.IsStatic || f.IsNotSerialized || f.IsLiteral) return false;
            Type t = f.FieldType;
            if (typeof(Delegate).IsAssignableFrom(t) || t == typeof(IntPtr) || t == typeof(UIntPtr)
                || t.IsPointer) return false;
            if (t.FullName == "Sirenix.Serialization.SerializationData") return false;
            return f.IsPublic || HasAttribute(f, "SerializeField") || HasAttribute(f, "SerializeReference")
                || HasAttribute(f, "OdinSerializeAttribute");
        }

        static bool HasAttribute(FieldInfo f, string name)
        {
            foreach (object a in f.GetCustomAttributes(true))
                if (a.GetType().Name == name) return true;
            return false;
        }

        FieldInfo[] FieldsOf(Type t)
        {
            FieldInfo[] list;
            if (fields.TryGetValue(t, out list)) return list;
            var chain = new List<Type>();
            for (Type x = t; x != null && x != typeof(object) && x != typeof(ValueType); x = x.BaseType) chain.Insert(0,
                x);
            var all = new List<FieldInfo>();
            const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly;
            foreach (Type x in chain)
            {
                var own = new List<FieldInfo>();
                foreach (FieldInfo f in x.GetFields(Declared)) if (Serialised(f)) own.Add(f);
                own.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                all.AddRange(own);
            }
            list = all.ToArray();
            fields[t] = list;
            return list;
        }

        void WalkFields(DefinitionHasher h, object o, int depth)
        {
            foreach (FieldInfo f in FieldsOf(o.GetType()))
            {
                object v;
                try { v = f.GetValue(o); }
                catch (Exception) { h.Str(f.Name, UnreadableMark); Fields++; continue; }
                string lazy = v == null ? LazyFields.DefaultRef(f.DeclaringType.Name, f.Name) : null;
                if (lazy != null) { h.Ref(f.Name, lazy); Fields++; continue; }
                Value(h, f.Name, v, depth);
            }
        }

        void Value(DefinitionHasher h, string name, object v, int depth)
        {
            Fields++;
            if (v == null) { h.Str(name, null); return; }
            Type t = v.GetType();
            if (t == typeof(string)) { h.Str(name, (string)v); return; }
            if (t == typeof(bool)) { h.Bool(name, (bool)v); return; }
            if (t == typeof(double)) { h.Num(name, (double)v); return; }
            if (t == typeof(float)) { h.Num(name, (float)v); return; }
            if (t.IsEnum) { h.Int(name, EnumValue(v, t)); return; }
            if (t.IsPrimitive) { h.Int(name, IntValue(v)); return; }
            if (t == typeof(decimal)) { h.Str(name,
                ((decimal)v).ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (t == typeof(DateTime)) { h.Int(name, ((DateTime)v).Ticks); return; }
            if (t == typeof(TimeSpan)) { h.Int(name, ((TimeSpan)v).Ticks); return; }
            if (t == typeof(Guid)) { h.Str(name, ((Guid)v).ToString("N")); return; }
            if (v is Type) { h.Str(name, ((Type)v).FullName); return; }
            if (v is Delegate || v is IntPtr || v is UIntPtr) { h.Str(name, null); return; }
            string r;
            try { r = refOf(v); }
            catch (Exception) { r = UnreadableMark; }
            if (r != null) { h.Ref(name, r); return; }
            bool byRef = !t.IsValueType;
            if (depth >= maxDepth) { h.Str(name, DeepMark); return; }
            if (byRef && path.Contains(v)) { h.Str(name, CycleMark); return; }
            if (byRef) path.Add(v);
            try
            {
                var dict = v as IDictionary;
                if (dict != null) { Dictionary(h, name, dict, depth); return; }
                var list = v as IEnumerable;
                if (list != null) { List(h, name, list, depth); return; }
                h.BeginObject(name);
                WalkFields(h, v, depth + 1);
                h.EndObject();
            }
            finally { if (byRef) path.Remove(v); }
        }

        // A collection is read into a list first (BeginList needs its count), at most maxElements entries; a longer one
        // gets its true count as one more element, so a cut list never hashes like a short one. One that cannot be read
        // is a marker.
        void Dictionary(DefinitionHasher h, string name, IDictionary d, int depth)
        {
            var entries = new List<DictionaryEntry>();
            int total = 0;
            try { foreach (DictionaryEntry e in d) { if (entries.Count < maxElements) entries.Add(e); total++; } }
            catch (Exception) { h.Str(name, UnreadableMark); return; }
            bool cut = total > entries.Count;
            h.BeginList(name, entries.Count + (cut ? 1 : 0));
            foreach (DictionaryEntry e in entries)
            {
                h.BeginObject(null);
                Value(h, "k", e.Key, depth + 1);
                Value(h, "v", e.Value, depth + 1);
                h.EndObject();
            }
            if (cut) h.Int("#count", total);
            h.EndList();
        }

        void List(DefinitionHasher h, string name, IEnumerable items, int depth)
        {
            var kept = new List<object>();
            int total = 0;
            try { foreach (object x in items) { if (kept.Count < maxElements) kept.Add(x); total++; } }
            catch (Exception) { h.Str(name, UnreadableMark); return; }
            bool cut = total > kept.Count;
            h.BeginList(name, kept.Count + (cut ? 1 : 0));
            foreach (object x in kept) Value(h, null, x, depth + 1);
            if (cut) h.Int("#count", total);
            h.EndList();
        }

        static long EnumValue(object v, Type t)
        {
            Type u = Enum.GetUnderlyingType(t);
            if (u == typeof(ulong)) return unchecked((long)Convert.ToUInt64(v,
                System.Globalization.CultureInfo.InvariantCulture));
            return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        static long IntValue(object v)
        {
            if (v is ulong) return unchecked((long)(ulong)v);
            if (v is char) return (char)v;
            return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        sealed class RefEq : IEqualityComparer<object>
        {
            public static readonly RefEq Instance = new RefEq();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }
    }
}
