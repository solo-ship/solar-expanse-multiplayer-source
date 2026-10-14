using System;

namespace SolarExpanseMultiplayer
{
    public sealed class LoaderFile { public string Where; public string File; public string Sha256; }

    // Our own loader files, which the content comparison leaves out so a Doorstop player and a BepInEx player with no other plugins
    // match (user, DECISIONS "M4 plan questions": "Doorstop, the BepInEx core, our shim"). An entry of a summary is left out only
    // when its Where and file name match a row here AND its SHA-256 equals the pinned one; anything else counts as a plugin or
    // assembly difference, so a loader file cannot be swapped for another one or hidden under a name.
    //
    // - Our main DLL, SolarExpanseMultiplayer.dll, is NOT here: both loaders run the same file, so its hash must match.
    // - The shim is pinned by ShimPin.Sha256, which tools\build.ps1 embeds from the shim it built with this DLL (I-8).
    // - The two loaders put the same DLL in different folders (Where "mod" or "bepinex-plugin"); the comparison does not look at Where
    //   for entries that are not on this list.
    // - Two assemblies have no file (S7): HarmonySharedState (Doorstop; exact name, once, doorstop summaries only) and UnityEngine.CoreModule
    //   (BepInEx; name and the file's MVID). See below.
    public static class LoaderFiles
    {
        public const string ShimGuid = "com.solo-ship.solarexpansemultiplayer";
        const string ShimFile = "SolarExpanseMultiplayer.BepInEx.dll";

        // SHA-256s of the files tools\fetch-deps.ps1 downloads (it checks the archives): lib\harmony (Harmony 2.4.2), lib\doorstop
        // (Doorstop 4.5.0) and BepInEx 5.4.23.5's BepInEx\core. Recorded in docs\research\join.md (S7).
        static LoaderFile Row(string where, string file, string sha) { return new LoaderFile { Where = where, File = file, Sha256 = sha }; }

        public static readonly LoaderFile[] Excluded =
        {
            Row("native", "winhttp.dll", "8c6cdbc38836dee87e3368f5de1994d7c0ccebf29e4ce7aba3c0981f9375412c"),   // Doorstop's proxy: never in a managed list
            Row("mod", "0Harmony.dll", "7b9e756306fa3d7620e02a857c8927a6ab04973f9bd8a77d3866700a6deac55c"),
            Row("bepinex-core", "BepInEx.dll", "8255b28902886085c578b9e427d3073c97002db85176d2090cdeda90ef14ce70"),
            Row("bepinex-core", "BepInEx.Preloader.dll", "55d3895351a9d16b63b6f35f1c01b44ac650979e853d0bd3a442b92a082af64f"),
            Row("bepinex-core", "BepInEx.Harmony.dll", "00c8f2ac48593e4d45ce4eb26ce4e68b18113c5c489b38b8bf0850f65830d601"),
            Row("bepinex-core", "0Harmony.dll", "1a21cc03424fc82c3dd1346905d16494536b9595ae4162228d99fb7c285c1031"),
            Row("bepinex-core", "0Harmony20.dll", "c755a5ab2712915a32f9a850d580a8c1b70ff1653bc76600f2767aa8ffccc663"),
            Row("bepinex-core", "HarmonyXInterop.dll", "7ce1342d3afa0334b59a3e38c0aed15e162b9abe9d46f95f34be44af47f3b493"),
            Row("bepinex-core", "Mono.Cecil.dll", "7ae470288fff4a402899c254d0a76cefef55877f5c54f96e83c797cc5bb6e2f6"),
            Row("bepinex-core", "Mono.Cecil.Mdb.dll", "5896d1898f616701fff18f3b2c71e6b844d2390ef9f41e1c5fccce8cb27c698e"),
            Row("bepinex-core", "Mono.Cecil.Pdb.dll", "174db44a067f58561510af746f3caeb032037762c57a31c8d9ee32db25174984"),
            Row("bepinex-core", "Mono.Cecil.Rocks.dll", "54ac539fb5ddc8b44c0e9acd0fcb7324f89d1a072edf8ebc1b06dd691e3d3927"),
            Row("bepinex-core", "MonoMod.RuntimeDetour.dll", "40e49bb314391cd7bddc2644f8553eeba92c194b940836b103df16955c464e0c"),
            Row("bepinex-core", "MonoMod.Utils.dll", "9d1495f147ac93c4f81f84538c1a326e8f8a6aefc78d6289d798f3ce1162c5e9"),
            Row("bepinex-plugin", ShimFile, ShimPin.Sha256),
        };

        // Assemblies loaded from bytes (Where "memory"), the two S7 found:
        // - HarmonySharedState: Harmony's generated assembly. Doorstop only, a new MVID every launch, so it cannot be pinned by MVID. It is
        //   left out only by its exact full name, only in a summary whose Loader is doorstop, and only once per list (a second one counts).
        //   The scan-side proof that it really is Harmony's is Task 10's.
        // - UnityEngine.CoreModule: BepInEx patches it in memory and keeps the file's MVID, pinned here (docs\research\join.md S7).
        public const string HarmonySharedStateName = "HarmonySharedState, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
        public const string CoreModuleMvid = "21e6fa6562484a14a137bdef0e78a605";   // Managed\UnityEngine.CoreModule.dll of this game build
        const string CoreModuleSimpleName = "UnityEngine.CoreModule";

        // Without a loader, no memory assembly of the HarmonySharedState kind is left out.
        public static bool IsExcluded(PluginEntry e) { return IsExcluded(e, null); }

        // `loader` is the Loader of the summary the entry came from ("doorstop" or "bepinex").
        public static bool IsExcluded(PluginEntry e, string loader)
        {
            if (e == null || e.Where == null || e.Id == null) return false;
            if (e.Where == "memory") return MemoryExcluded(e, loader);
            // our shim as a BepInEx plugin: our GUID, in the plugin folder, with the pinned file hash
            if (e.Where == "bepinex-plugin" && e.Id == ShimGuid) return e.Sha256 != null && e.Sha256 == ShimPin.Sha256;
            foreach (LoaderFile f in Excluded)
                if (f.Where == e.Where && f.File == e.Id && e.Sha256 != null && f.Sha256 == e.Sha256) return true;
            return false;
        }

        // An entry that may be left out at most once per list (HarmonySharedState): the comparison leaves out the first, counts the rest.
        public static bool IsSingleton(PluginEntry e)
        {
            return e != null && e.Where == "memory" && e.Id == HarmonySharedStateName;
        }

        static bool MemoryExcluded(PluginEntry e, string loader)
        {
            if (e.Id == HarmonySharedStateName) return loader == "doorstop";
            string simple = e.Id;
            int comma = simple.IndexOf(',');
            if (comma >= 0) simple = simple.Substring(0, comma);
            return simple.Trim() == CoreModuleSimpleName && e.Mvid == CoreModuleMvid;
        }
    }
}
