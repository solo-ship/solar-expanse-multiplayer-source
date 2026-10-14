using System;

namespace SolarExpanseMultiplayer
{
    // One game build this mod version has been verified on.
    public sealed class KnownBuild
    {
        public readonly string GameBuild;      // "acs-" + the first 16 hex digits of Assembly-CSharp.dll's SHA-256 (GameBuild.FromFile)
        public readonly string AssemblySha256; // the full SHA-256 of Assembly-CSharp.dll, 64 lower-case hex digits
        public readonly string ManagedHash;    // the game's whole Managed folder, as ManagedHasher folds it (ContentHash.cs): one SHA-256 over
                                               // "name\nfilehash\n" of every file in Solar Expanse_Data\Managed, sorted by name (ordinal)
        public readonly string GameVersion;    // Application.version, as the mod sends it in Hello (ModInfo.ClipGameVersion)
        public readonly string SteamBranch;    // the Steam beta branch the build came from ("experimental"), "public" for stable
        public readonly string SteamBuildId;   // the Steam build id (appmanifest_1369700.acf "buildid")
        public readonly string Verified;       // the date it was last verified in the game, yyyy-MM-dd

        public KnownBuild(string gameBuild, string assemblySha256, string managedHash, string gameVersion, string steamBranch, string steamBuildId, string verified)
        {
            GameBuild = gameBuild; AssemblySha256 = assemblySha256; ManagedHash = managedHash; GameVersion = gameVersion;
            SteamBranch = steamBranch; SteamBuildId = steamBuildId; Verified = verified;
        }
    }

    // The single list of game builds this mod version is verified for (docs/GAME-UPDATES.md). Policy (user, 2026-10-11): only the
    // builds listed here are supported; the mod refuses any other at join. Today that is the beta branch only; stable is not
    // supported.
    //
    // This file is also read by tools\game-update-check.ps1, which parses the string literals of the entries below (the
    // "new KnownBuild(" lines), so keep one entry per "new KnownBuild(" call with seven plain string literals, in this order
    // (the script refuses to run if it cannot read every entry).
    //
    // Pins that must agree with an entry here: SaveAliases.GameAssemblyCSharpSha256 (src\Core\Join\SaveAliases.cs) is the full
    // Assembly-CSharp SHA-256 its alias table was generated from, so it must equal the AssemblySha256 of an entry in this list.
    // After a game update, re-pin both in one commit.
    public static class KnownBuilds
    {
        public static readonly KnownBuild[] All =
        {
            // 0.26.10.9.13 BETA, Steam branch "experimental", build 25829824. Verified in the game on a Doorstop test copy and a BepInEx test copy.
            // ManagedHash: 157 files, the same value the S7 spike measured (docs/research/join.md) and the main install gives.
            new KnownBuild("acs-d7cc4b107d686b46", "d7cc4b107d686b46be260b5066699314af3ba8b4ce3ad21b5ec0a4da0754ca0b",
                           "bf3fef0f9dccf9fe10f9f1bb28d46e9c8b6dd716b792d86855068b83b821d799",
                           "0.26.10.9.13 BETA", "experimental", "25829824", "2026-10-11"),
        };

        // The entry for a GameBuild id ("acs-<16 hex>", as GameBuild.FromFile makes it), or null for an unknown, empty or null id.
        // Exact match, case-sensitive: the id is always lower-case.
        public static KnownBuild Find(string gameBuild)
        {
            if (string.IsNullOrEmpty(gameBuild)) return null;
            for (int i = 0; i < All.Length; i++)
                if (string.Equals(All[i].GameBuild, gameBuild, StringComparison.Ordinal)) return All[i];
            return null;
        }
    }
}
