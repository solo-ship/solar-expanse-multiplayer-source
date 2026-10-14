using System;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // The host key file's life cycle, separate from how the certificate is made (HostCert, which needs the game's
    // Mono.Security) so desktop tests can run it.
    // - A missing file is created. An unparseable one (CryptographicException) or one without a private key is moved aside
    //   to <name>.bad-<time>, never deleted, and a new identity is made (before the old file is touched) and reported.
    //   Any other exception (access denied, ...) fails the start: it says nothing about the file.
    // - A transient IO error (sharing violation ...) never destroys a good key: it is retried, then the start fails.
    // - Writes go to <name>.tmp (a stale one is removed first) and are swapped in with File.Move.
    // - The file is restricted to the current user where the platform allows it (best effort, logged when it fails).
    public static class CertFile
    {
        public sealed class Result
        {
            public X509Certificate2 Cert;
            public bool Created;
            public string OldFingerprint;   // lower-case SHA-256 hex of the certificate that was set aside, if it could be read
        }

        // fingerprintOf: the SHA-256 hex of the certificate in raw file bytes, without needing its private key (so a keyless
        // or half-readable file still has its old identity logged); null/throws/returns null: logged as unknown.
        public static Result LoadOrCreate(string dir, string fileName, Func<byte[], X509Certificate2> load, Func<byte[]> make, Action<string> warn,
                                          Func<byte[], string> fingerprintOf = null,
                                          Func<byte[], byte[]> unprotect = null, Func<byte[], byte[]> protect = null)
        {
            // unprotect/protect: the file holds protected bytes (DPAPI, M8 Task 10). A file that cannot be unprotected is as bad as
            // an unparseable one (set aside, never read as plain); load, make and fingerprintOf always see the plain bytes.
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, fileName);
            byte[] bytes = ReadWithRetry(file);   // null: no such file
            string whyBad = null;
            byte[] plain = bytes;
            if (bytes != null && unprotect != null)
            {
                try { plain = unprotect(bytes); }
                catch (CryptographicException e) { plain = null; whyBad = "it could not be unprotected (" + e.GetType().Name + ")"; }
            }
            if (bytes != null && plain != null)
            {
                try
                {
                    X509Certificate2 c = load(plain);
                    if (c != null && c.HasPrivateKey) return new Result { Cert = c };
                    whyBad = "it has no private key";
                }
                catch (CryptographicException e) { whyBad = "it could not be read (" + e.GetType().Name + ")"; }
                // Anything else (access denied, missing runtime method, out of memory ...) says nothing about the file:
                // it propagates, the host start fails and the key stays where it is.
            }

            // The new identity is made and written BEFORE the old file is touched: if making or writing fails, the old
            // file is exactly as it was.
            byte[] fresh = make();
            byte[] onDisk = protect != null ? protect(fresh) : fresh;
            string tmp = file + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);   // a stale one from a crashed write
            File.WriteAllBytes(tmp, new byte[0]);
            RestrictToCurrentUser(tmp, warn);         // before the key goes in, so it never sits there readable by others
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Write(onDisk, 0, onDisk.Length);
                fs.Flush(true);                       // on disk before it replaces anything
            }
            // Checked while the old file is still untouched: a bad new key must never cost the old one.
            X509Certificate2 made = load(fresh);
            if (made == null || !made.HasPrivateKey) throw new IOException("the new host certificate has no private key");
            string oldFp = null;
            if (bytes != null)
            {
                oldFp = plain == null ? null : FingerprintOrNull(plain, fingerprintOf);
                string aside = Quarantine(file);
                warn("host cert: " + fileName + " was set aside as " + Path.GetFileName(aside) + " because " + whyBad);
            }
            File.Move(tmp, file);
            if (bytes != null) warn("host cert: new identity made - the old certificate had fingerprint " + (oldFp ?? "unknown") + "; join codes made with it no longer work");
            else warn("host cert: new identity created (" + fileName + ")");   // not the full path: it can name the user
            return new Result { Cert = made, Created = true, OldFingerprint = oldFp };
        }

        // The SHA-256 of the certificate part of the raw file bytes: a custom reader if given, else a plain (no password)
        // load, which reads a public-only certificate. Null when neither can read it.
        static string FingerprintOrNull(byte[] bytes, Func<byte[], string> fingerprintOf)
        {
            if (fingerprintOf != null)
            {
                try { string f = fingerprintOf(bytes); if (f != null) return f; }
                catch (Exception) { }
            }
            try { return Fingerprint(new X509Certificate2(bytes)); }
            catch (Exception) { return null; }
        }

        public static string Fingerprint(X509Certificate c) { return HostGate.Hex(CertPin.Sha256(c)); }

        // Reads the file; null when it does not exist. Any other IO trouble is retried a few times, then thrown.
        static byte[] ReadWithRetry(string file)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { return File.ReadAllBytes(file); }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (Exception e)
                {
                    if (attempt >= 4) throw new IOException("the host key file could not be read (" + e.GetType().Name + ": " + e.Message + "); not replacing it", e);
                    Thread.Sleep(100 * attempt);
                }
            }
        }

        static string Quarantine(string file)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
            string target = file + ".bad-" + stamp;
            for (int n = 2; File.Exists(target); n++) target = file + ".bad-" + stamp + "-" + n;
            File.Move(file, target);   // an IO error here ends the start: never write over a key we could not move aside
            return target;
        }

        static void RestrictToCurrentUser(string file, Action<string> warn)
        {
            try
            {
                var sec = new FileSecurity();
                sec.SetAccessRuleProtection(true, false);   // no inherited permissions
                sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
                File.SetAccessControl(file, sec);
            }
            catch (Exception e) { warn("host cert: could not restrict the key file to this user (" + e.GetType().Name + "); it keeps the folder's permissions"); }
        }
    }
}
