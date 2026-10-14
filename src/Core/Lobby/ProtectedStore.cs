using System;
using System.IO;

namespace SolarExpanseMultiplayer
{
    public interface IProtector
    {
        bool Available { get; }
        byte[] Protect(byte[] data);       // throws (CryptographicException) when it cannot
        byte[] Unprotect(byte[] data);     // throws when the data is not ours, was made by another Windows user, or is damaged
    }

    // Files protected at rest, failing closed (DD9): nothing is ever written or read as plain. No protection available: no write,
    // no read, and the caller does without (no hosting, no stored keys). A file that cannot be unprotected is moved aside as
    // <name>.bad-<time>, never deleted and never read as plain. The logic is here so desktop tests drive it with a fake
    // protector; src/Game/Session/ProtectedFile.cs is the static face over the real DPAPI one.
    public sealed class ProtectedStore
    {
        public const long MaxFileBytes = 1024 * 1024;
        readonly IProtector protector;

        public ProtectedStore(IProtector protector) { this.protector = protector; }

        public bool Available
        {
            get
            {
                try { return protector != null && protector.Available; }
                catch (Exception) { return false; }
            }
        }

        // null = written (through a temp file). Else why not; in every failure nothing was written to the path.
        public string Write(string path, byte[] data)
        {
            if (string.IsNullOrEmpty(path) || data == null) return "nothing to protect";
            if (!Available) return "protected storage is not available";
            byte[] blob;
            try
            {
                blob = protector.Protect(data);
                if (blob == null || blob.Length == 0) return "the data could not be protected";
                // A protector that hands the data back readable would put plain keys on disk: refuse it.
                if (blob.Length == data.Length && Same(blob, data)) return "the protector returned the data unchanged";
                byte[] back = protector.Unprotect(blob);
                if (back == null || !Same(back, data)) return "the protected data did not read back";
            }
            catch (Exception e) { return "the data could not be protected (" + e.GetType().Name + ")"; }
            return AtomicFile.Write(path, blob);
        }

        // The plain bytes; null with problem null when there is no file; null with a problem when it cannot be used: not available
        // (the file is left alone), over the cap or unreadable (moved aside), or not ours (moved aside).
        public byte[] Read(string path, out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(path)) { problem = "no path"; return null; }
            if (!Available) { problem = "protected storage is not available"; return null; }
            if (!File.Exists(path)) return null;
            byte[] blob = null;
            string why = null;
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) why = "the protected file is over its size cap";
                else blob = File.ReadAllBytes(path);
            }
            catch (IOException) { problem = "the protected file could not be read now"; return null; }   // busy: leave it, try later
            catch (UnauthorizedAccessException) { problem = "the protected file could not be read now"; return null; }
            if (why == null)
            {
                try
                {
                    byte[] plain = protector.Unprotect(blob);
                    if (plain != null && plain.Length > 0) return plain;
                    why = "the protected file held nothing";
                }
                catch (Exception e) { why = "the protected file could not be unprotected (" + e.GetType().Name + ")"; }
            }
            string aside = AtomicFile.MoveAside(path);
            problem = why + (aside != null ? "; moved aside as " + Path.GetFileName(aside) : "; it could not be moved aside");
            Log.Warn("protected file: " + problem);
            return null;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }
    }
}
