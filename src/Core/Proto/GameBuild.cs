using System;
using System.IO;
using System.Security.Cryptography;

namespace SolarExpanseMultiplayer
{
    // Which build of the game this is: its code decides, so "acs-" + the first 16 hex digits of Assembly-CSharp.dll's
    // SHA-256. Content changes (other mods' data) are the content checksum's job (M4).
    public static class GameBuild
    {
        public static string FromFile(string assemblyCSharpPath)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(assemblyCSharpPath))
                return "acs-" + BitConverter.ToString(sha.ComputeHash(f), 0, 8).Replace("-", "").ToLowerInvariant();
        }
    }
}
