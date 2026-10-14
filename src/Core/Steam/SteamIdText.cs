using System;
using System.IO;
using System.Security.Cryptography;

namespace SolarExpanseMultiplayer
{
    // A Steam id names a person's account, so a log never holds one whole: Short gives "s:" and 8 hex digits of a
    // salted SHA-256, enough to tell players apart in one install's logs and useless for looking anyone up. The salt is
    // per install (a random file in the mod's data), so the same account reads differently on two PCs.
    public static class SteamIdText
    {
        public const string SaltFile = "steam-id-salt.bin";
        const int SaltBytes = 16;

        public static string Short(ulong id, byte[] salt)
        {
            if (salt == null) salt = new byte[0];
            var data = new byte[salt.Length + 8];
            Buffer.BlockCopy(salt, 0, data, 0, salt.Length);
            for (int i = 0; i < 8; i++) data[salt.Length + i] = (byte)(id >> (i * 8));
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                return "s:" + HostGate.Hex(new[] { h[0], h[1], h[2], h[3] });
            }
        }

        // 17 digits starting 7656119: what a public individual Steam id looks like in decimal (for the privacy scans).
        public static bool LooksLikeSteamId(string digits)
        {
            if (digits == null || digits.Length != 17 || !digits.StartsWith("7656119", StringComparison.Ordinal))
                return false;
            foreach (char c in digits) if (c < '0' || c > '9') return false;
            return true;
        }

        // This install's salt from <dir>\steam-id-salt.bin, made on first use. A salt that can't be read or written is
        // replaced by one for this run only: the short forms then change between runs, which is safe.
        public static byte[] LoadOrMakeSalt(string dir)
        {
            try
            {
                if (dir != null)
                {
                    string path = Path.Combine(dir, SaltFile);
                    if (File.Exists(path) && new FileInfo(path).Length == SaltBytes) return File.ReadAllBytes(path);
                    byte[] made = HostGate.SecureRandom(SaltBytes);
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, made);
                    return made;
                }
            }
            catch (Exception) { }
            return HostGate.SecureRandom(SaltBytes);
        }
    }
}
