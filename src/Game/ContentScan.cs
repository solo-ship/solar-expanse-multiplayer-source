using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;
using Data.ScriptableObject;

namespace SolarExpanseMultiplayer
{
    // The content summary and the item lists (Design decision 7; plan "Game glue"), on both sides: taken once at the
    // first "main menu ready" (before any save is loaded, so the host's and a joiner's are taken in the same state) and
    // kept for the process; the definitions again after each load (PostLoad: a content mod can make definitions at
    // load). What is listed and how is Core's (ContentScanRules.cs); this class only reads the game, the assemblies and
    // the files. Owned by Session; main thread. Never throws: a scan that fails leaves no summary and a problem code,
    // so the join is refused (fail closed).
    public sealed class ContentScan
    {
        const int MaxDepth = 6, MaxElements = 4096;
        const int MaxChangedItems = 8, MaxChangedFields = 4;   // the changed-since-the-menu log line

        ContentSummary menu;
        DefinitionHasher menuDefs;
        int problem = JoinCodes.ContentUnavailable;
        bool taken;

        public bool Taken { get { return taken; } }

        // ---- at the menu ----

        public void TakeMenuOnce()
        {
            if (taken) return;
            taken = true;
            try { TakeMenu(); }
            catch (Exception e)
            {
                menu = null;
                problem = JoinCodes.ContentUnavailable;
                Log.Error("join: the content scan at the menu failed: " + e);
            }
        }

        void TakeMenu()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int fields, count;
            DefinitionHasher defs = Definitions(out fields, out count);
            long defsMs = watch.ElapsedMilliseconds;
            string managedDir = Path.Combine(Application.dataPath, "Managed");
            int managedCount;
            string managedHash = ManagedHash(managedDir, out managedCount);
            List<PluginEntry> assemblies = Assemblies(managedDir);
            List<PluginEntry> plugins = Plugins();
            string loader = Boot.Mode == "BepInEx" ? "bepinex" : "doorstop";
            int code;
            string error;
            menu = ContentScanSummary.Build(Session.GameBuildId, Application.version, ModInfo.Version, loader,
                managedHash, managedCount,
                                            plugins, assemblies, defs, out code, out error);
            menuDefs = defs;
            problem = menu == null ? code : 0;
            foreach (PluginEntry p in plugins) Log.Info("join: content: plugin " + p.Where + " " + p.Id + " "
                + p.Version + " " + Short(p.Sha256));
            foreach (PluginEntry a in assemblies)
                Log.Info("join: content: assembly " + a.Where + " " + a.Id + " " + a.Version + " "
                    + (a.Where == "memory" ? "mvid " + a.Mvid : Short(a.Sha256)));
            if (menu == null)
            {
                Log.Error("join: content scan at the menu: no summary (" + code + "): " + error);
                return;
            }
            Log.Info("join: content scan at the menu: " + count + " definitions (" + fields + " values) in " + defsMs
                + " ms; " +
                     Categories(menu.Categories) + "; " + plugins.Count + " plugins, " + assemblies.Count
                         + " assemblies; loader " + loader +
                     "; managed " + managedCount + " files " + Short(managedHash) + "; overall " + Short(menu.Overall)
                         + "; " +
                     watch.ElapsedMilliseconds + " ms in all");
        }

        // The summary a joiner sends: a copy, with a planted -semp-join-fault applied (test profiles only).
        public ContentSummary MenuSummary()
        {
            if (menu == null) return null;
            return JoinFaultSummary.Apply(menu, Fault());
        }

        // The host's own summary, never perturbed (JoinService makes its own copy).
        public ContentSummary HostSummary { get { return menu; } }

        public int MenuProblem() { return menu != null ? 0 : problem; }

        public ItemList MenuItems(int category)
        {
            if (menuDefs == null) return null;
            return JoinFaultSummary.ApplyItems(menuDefs.Items(category), category, Fault());
        }

        static string Fault() { return Boot.Dev != null ? Boot.Dev.JoinFault : null; }

        // ---- after a load ----

        // The categories as they are now (after a load), or null when the scan failed (the joiner then fails
        // PostLoadContent; the host's header carries no PostLoad and its own rules refuse it).
        public CategoryHash[] AfterLoad()
        {
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int fields, count;
                DefinitionHasher defs = Definitions(out fields, out count);
                CategoryHash[] atMenuRules = defs.Categories();
                bool same = menu == null || PostLoadContent.Same(menu.Categories, atMenuRules);
                // Economic holds the speed list and is not a definition, so a mod could change it unseen: its six
                // time values are hashed too (one item of Other). It lives on the game scene's GameManager, which
                // the menu does not have, so only the after-load scan (host and joiner) can include it.
                EconomicTimes.Feed(defs, Economic());
                count++;
                CategoryHash[] c = defs.Categories();
                Log.Info("join: content after the load: " + count + " definitions in " + watch.ElapsedMilliseconds
                    + " ms; " + Categories(c) +
                         (menu != null ? "; " + (same ? "the same as at the menu" : "NOT the same as at the menu")
                             : ""));
                // which definitions and fields moved, so one run shows the cause and not only a category hash
                if (!same && menuDefs != null)
                    Log.Info("join: content changed since the menu: " + ContentChanges.Text(
                        DefinitionHasher.Changes(menuDefs, defs), MaxChangedItems, MaxChangedFields));
                return c;
            }
            catch (Exception e)
            {
                Log.Error("join: the content scan after the load failed: " + e);
                return null;
            }
        }

        // ---- the parts ----

        static DefinitionHasher Definitions(out int fields, out int count)
        {
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
            if (all == null || all.AllMyIDScriptableObjects == null)
                throw new InvalidOperationException("no AllScriptableObjectManager");
            var defs = new DefinitionHasher();
            var walker = new DefinitionWalker(RefOf, MaxDepth, MaxElements);
            var chain = new List<string>(8);
            count = 0;
            foreach (MyIDScriptableObject o in all.AllMyIDScriptableObjects)
            {
                if (o == null) continue;   // the Unity null too: a destroyed definition is not content
                chain.Clear();
                for (Type t = o.GetType(); t != null; t = t.BaseType) chain.Add(t.Name);
                int category = ContentCategories.Of(chain);
                walker.Item(defs, category, ContentCategories.ItemId(category, o.GetType().Name, o.ID), o);
                count++;
            }
            fields = walker.Fields;
            return defs;
        }

        // The running game's Economic: a serialised field of the scene's GameManager (D 80822, 80921), so it exists
        // only once a game is loaded, never at the main menu.
        public static object Economic()
        {
            var gm = MonoBehaviourSingleton<Manager.GameManager>.Instance;
            if ((object)gm == null || (object)gm.Economic == null)
                throw new InvalidOperationException("no Economic asset");
            return gm.Economic;
        }

        // A game object reference inside a definition: another definition by its type and id, anything else Unity owns
        // by its type and name; null for a plain object (walked).
        static string RefOf(object v)
        {
            var u = v as UnityEngine.Object;
            if ((object)u == null) return null;
            var my = v as MyIDScriptableObject;
            if ((object)my != null) return "id:" + my.GetType().Name + "/" + my.ID;
            string name;
            try { name = u.name; } catch (Exception) { name = "(destroyed)"; }
            return "unity:" + u.GetType().Name + "/" + name;
        }

        static string ManagedHash(string dir, out int count)
        {
            string[] files = Directory.GetFiles(dir);
            var names = new string[files.Length];
            var hashes = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                names[i] = Path.GetFileName(files[i]);
                hashes[i] = Sha(files[i]);
            }
            count = files.Length;
            return ManagedHasher.Compute(names, hashes);
        }

        // Every non-dynamic assembly (I-9): a file outside Managed by name and SHA-256; one with no file as "memory".
        static List<PluginEntry> Assemblies(string managedDir)
        {
            var list = new List<PluginEntry>();
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // full paths already in the list
            string root = Boot.GameRootDir;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic) continue;
                AssemblyName n = a.GetName();
                string version = n.Version != null ? n.Version.ToString() : null;
                string loc = null;
                try { loc = a.Location; } catch (Exception) { }
                if (string.IsNullOrEmpty(loc))
                {
                    string mvid = a.ManifestModule.ModuleVersionId.ToString("N");
                    string shape = null, fileMvid = null;
                    if (MemoryAssemblies.NeedsShape(n.Name))
                    {
                        try { shape = MemoryAssemblies.ShapeOf(a); }
                        catch (Exception e) { Log.Warn("join: HarmonySharedState unreadable: " + e.Message); }
                    }
                    if (MemoryAssemblies.NeedsFileMvid(n.Name))
                    {
                        string file = Path.Combine(managedDir, n.Name + ".dll");
                        if (File.Exists(file)) fileMvid = PeMvid.Read(File.ReadAllBytes(file));
                    }
                    list.Add(MemoryAssemblies.Entry(a.FullName, n.Name, version, mvid, shape, fileMvid));
                    continue;
                }
                string full = Path.GetFullPath(loc);
                string where = AssemblyWhere.Of(full, root);
                if (where == null) continue;   // Managed: ManagedHash covers it
                if (listed.Add(full)) list.Add(DiskAssemblies.FileEntry(full, root));
            }
            // and every DLL file mods load code from, loaded yet or not (a lazily loaded dependency still counts)
            DiskAssemblies.AddFiles(list, listed, DiskAssemblies.Roots(root, DoorstopConfig(root)), root);
            list.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
            return list;
        }

        // Doorstop's config beside the game (null when there is none); read with a size cap.
        static string DoorstopConfig(string root)
        {
            var info = new FileInfo(Path.Combine(root, "doorstop_config.ini"));
            if (!info.Exists) return null;
            if (info.Length > 64 * 1024) throw new InvalidOperationException("doorstop_config.ini is over 64 KiB");
            return File.ReadAllText(info.FullName);
        }

        // BepInEx's plugins, by reflection only (the main DLL never references BepInEx): Chainloader.PluginInfos, when
        // BepInEx is the loader. Under BepInEx a list that cannot be read is a failed scan, never an empty list.
        static List<PluginEntry> Plugins()
        {
            var list = new List<PluginEntry>();
            if (Boot.Mode != "BepInEx") return list;
            Type chain = Type.GetType("BepInEx.Bootstrap.Chainloader, BepInEx");
            if (chain == null) throw new InvalidOperationException("BepInEx's Chainloader was not found");
            var infos = chain.GetProperty("PluginInfos", BindingFlags.Public | BindingFlags.Static).GetValue(null,
                null) as IDictionary;
            if (infos == null) throw new InvalidOperationException("Chainloader.PluginInfos is not readable");
            foreach (DictionaryEntry e in infos)
            {
                object info = e.Value;
                object meta = info.GetType().GetProperty("Metadata").GetValue(info, null);
                string location = info.GetType().GetProperty("Location").GetValue(info, null) as string;
                object ver = meta.GetType().GetProperty("Version").GetValue(meta, null);
                string full = string.IsNullOrEmpty(location) ? null : Path.GetFullPath(location);
                list.Add(new PluginEntry
                {
                    Id = meta.GetType().GetProperty("GUID").GetValue(meta, null) as string,
                    Name = meta.GetType().GetProperty("Name").GetValue(meta, null) as string,
                    Version = ver != null ? ver.ToString() : null,
                    Sha256 = full != null && File.Exists(full) ? Sha(full) : null,
                    Where = full != null ? (AssemblyWhere.Of(full, Boot.GameRootDir) ?? "other") : "other",
                });
            }
            list.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
            return list;
        }

        static string Sha(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream s = File.OpenRead(file))
                return Hex(sha.ComputeHash(s));
        }

        static string Hex(byte[] b)
        {
            var c = new char[b.Length * 2];
            const string D = "0123456789abcdef";
            for (int i = 0; i < b.Length; i++) { c[i * 2] = D[b[i] >> 4]; c[i * 2 + 1] = D[b[i] & 15]; }
            return new string(c);
        }

        static string Short(string hex) { return hex == null ? "-" : hex.Length > 16 ? hex.Substring(0, 16) : hex; }

        static string Categories(CategoryHash[] cats)
        {
            if (cats == null) return "no categories";
            var parts = new List<string>();
            foreach (CategoryHash c in cats) parts.Add(ContentCategory.Name(c.Category).ToLowerInvariant() + " "
                + c.Count + " " + Short(c.Hash));
            return string.Join(", ", parts.ToArray());
        }
    }
}
