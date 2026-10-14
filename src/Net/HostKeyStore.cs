using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace SolarExpanseMultiplayer
{
    // The host's TLS certificate on a player's install: protected for the current Windows user (DPAPI through the
    // protector), never the test profiles' plain file. Once the protected file is in place, a plain host-cert.p12 that
    // an older build left beside it (and any copy of it set aside as .tmp or .bad-*) is deleted: that key sat on disk
    // readable by anyone who could read the folder, so it is not carried over and a new identity is made instead.
    // Nothing here logs key material or a full path (the player's log may be posted).
    public static class HostKeyStore
    {
        public const string ProtectedName = "host-cert.protected", PlainName = "host-cert.p12";

        // null when protection is not available or the certificate cannot be loaded or made: the caller offers no
        // home-network hosting. created: a new identity was made this time.
        public static X509Certificate2 Protected(string dataDir, IProtector protector,
                                                 Func<byte[], X509Certificate2> load, Func<byte[]> make,
                                                 Func<byte[], string> fingerprintOf, Action<string> warn,
                                                 out bool created)
        {
            created = false;
            if (warn == null) warn = s => { };
            bool available;
            try { available = protector != null && protector.Available; }
            catch (Exception) { available = false; }
            if (!available) { warn("host cert: protected storage is not available; no home-network hosting"); return null; }
            CertFile.Result r;
            try
            {
                r = CertFile.LoadOrCreate(dataDir, ProtectedName, load, make, warn, fingerprintOf,
                                          protector.Unprotect, protector.Protect);
            }
            catch (Exception e)
            {
                warn("host cert: could not load or make the protected certificate (" + e.GetType().Name + ")");
                return null;
            }
            if (r == null || r.Cert == null) return null;
            created = r.Created;
            RemovePlain(dataDir, warn);
            return r.Cert;
        }

        // The plain files an older build wrote, by name only (the folder is the profile's own data folder).
        public static List<string> PlainFiles(string dataDir)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(dataDir) || !Directory.Exists(dataDir)) return found;
            foreach (string f in Directory.GetFiles(dataDir, PlainName + "*"))
            {
                string n = Path.GetFileName(f);
                if (n == PlainName || n == PlainName + ".tmp" || n.StartsWith(PlainName + ".bad-", StringComparison.Ordinal))
                    found.Add(f);
            }
            return found;
        }

        static void RemovePlain(string dataDir, Action<string> warn)
        {
            int removed = 0, left = 0;
            foreach (string f in PlainFiles(dataDir))
            {
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                    removed++;
                }
                catch (Exception) { left++; }
            }
            if (removed > 0) warn("host cert: removed " + removed + " old unprotected key file(s)");
            if (left > 0) warn("host cert: " + left + " old unprotected key file(s) could not be removed");
        }
    }
}
