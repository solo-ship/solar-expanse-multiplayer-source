using System;
using System.Security.Cryptography;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The per-save player key (DD9, I5): HMAC-SHA256(secret, pin || saveId), where pin is the SHA-256 of the host certificate the
    // player connected to and saveId the host's save id. A host that announces another game's save id learns a key bound to its
    // OWN pin, useless against the real host. The host stores only SHA-256(key) and compares in constant time.
    // Fails closed: any input of the wrong shape gives null, never a key made from less.
    public static class PlayerKeys
    {
        public const int SecretBytes = 32, PinBytes = 32;

        public static byte[] Derive(byte[] secret, byte[] pin, string saveId)
        {
            if (secret == null || secret.Length != SecretBytes) return null;
            if (pin == null || pin.Length != PinBytes) return null;
            if (!SaveNames.IsSaveId(saveId)) return null;
            byte[] id = Encoding.ASCII.GetBytes(saveId);
            var msg = new byte[pin.Length + id.Length];
            Buffer.BlockCopy(pin, 0, msg, 0, pin.Length);
            Buffer.BlockCopy(id, 0, msg, pin.Length, id.Length);
            using (var h = new HMACSHA256(secret)) return h.ComputeHash(msg);
        }

        // SHA-256 of the key, 64 lower-case hex digits (what the host stores). Null for no key or a key of another length.
        public static string HashForHost(byte[] key)
        {
            if (key == null || key.Length != 32) return null;
            return Sha256Hex.Of(key);
        }

        // Constant time over the longer length; false for null. Both are compared whole, so a short string costs the same.
        public static bool Same(string a, string b)
        {
            if (a == null || b == null) return false;
            int n = Math.Max(a.Length, b.Length);
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < n; i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                diff |= x ^ y;
            }
            return diff == 0;
        }

        public static bool IsHex64(string s)
        {
            if (s == null || s.Length != 64) return false;
            for (int i = 0; i < 64; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        public static byte[] NewSecret()
        {
            var b = new byte[SecretBytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }
    }
}
