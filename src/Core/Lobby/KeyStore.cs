using System;
using System.IO;

namespace SolarExpanseMultiplayer
{
    // The player's stored keys (DD9): the 256-bit secret behind every per-save key, and the rejoin keys
    // (<keys>\rejoin-<pin>.bin, pin = the host certificate's SHA-256 in hex). Everything goes through ProtectedStore. Without
    // protection nothing is stored: the secret lives in memory for this run only (joining works, a restarted game cannot rejoin)
    // and a rejoin key is not kept.
    public sealed class KeyStore
    {
        readonly ProtectedStore store;
        readonly string dir;
        byte[] secret;

        public KeyStore(ProtectedStore store, string keysFolder) { this.store = store; dir = keysFolder; }

        public bool Persistent { get { return store.Available; } }

        string SecretPath { get { return Path.Combine(dir, "secret.bin"); } }

        // The secret, made on first use. A stored one that is unreadable was moved aside by the store: a new secret follows
        // (the host sees a new player). Never returns null.
        public byte[] Secret()
        {
            if (secret != null) return secret;
            if (store.Available)
            {
                string problem;
                byte[] stored = store.Read(SecretPath, out problem);
                if (stored != null && stored.Length == PlayerKeys.SecretBytes) { secret = stored; return secret; }
                if (stored != null)
                {
                    // Our file with the wrong length is no secret of ours: set it aside like an unreadable one.
                    AtomicFile.MoveAside(SecretPath);
                    Log.Warn("keys: the stored secret had the wrong length; a new one is made");
                }
                secret = PlayerKeys.NewSecret();
                string e = store.Write(SecretPath, secret);
                if (e != null) Log.Warn("keys: the secret could not be stored (" + e + "); it is kept for this run only");
                return secret;
            }
            secret = PlayerKeys.NewSecret();
            return secret;
        }

        string RejoinPath(string pinHex) { return Path.Combine(dir, "rejoin-" + pinHex + ".bin"); }

        // The rejoin key (64 hex) kept for that host pin, or null (none, unreadable, or no protection).
        public string LoadRejoinKey(string pinHex)
        {
            if (!PlayerKeys.IsHex64(pinHex) || !store.Available) return null;
            string problem;
            byte[] b = store.Read(RejoinPath(pinHex), out problem);
            if (b == null) return null;
            string k = System.Text.Encoding.ASCII.GetString(b);
            return PlayerKeys.IsHex64(k) ? k : null;
        }

        // True when kept. False: no protection (the key stays in memory with the caller), a bad key or pin, or a write failure.
        public bool StoreRejoinKey(string pinHex, string keyHex)
        {
            if (!PlayerKeys.IsHex64(pinHex) || !PlayerKeys.IsHex64(keyHex) || !store.Available) return false;
            return store.Write(RejoinPath(pinHex), System.Text.Encoding.ASCII.GetBytes(keyHex)) == null;
        }
    }
}
