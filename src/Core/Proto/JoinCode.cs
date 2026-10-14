using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // Crockford base32: digits and capitals without I, L, O, U, so codes survive being read out or retyped.
    public static class Base32
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static string Encode(byte[] data)
        {
            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    sb.Append(Alphabet[(buffer >> bits) & 31]);
                }
                buffer &= (1 << bits) - 1;
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        // Null for any character outside the alphabet, or for leftover bits that aren't zero padding.
        // Lower case is fine; O reads as 0 and I or L as 1.
        public static byte[] Decode(string s)
        {
            if (s == null) return null;
            var output = new List<byte>(s.Length * 5 / 8);
            int buffer = 0, bits = 0;
            foreach (char raw in s)
            {
                int v = Value(raw);
                if (v < 0) return null;
                buffer = (buffer << 5) | v;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    output.Add((byte)(buffer >> bits));
                    buffer &= (1 << bits) - 1;
                }
            }
            if (bits >= 5 || buffer != 0) return null;
            return output.ToArray();
        }

        static int Value(char c)
        {
            if (c >= 'a' && c <= 'z') c = (char)(c - 32);
            if (c == 'O') c = '0';
            else if (c == 'I' || c == 'L') c = '1';
            return Alphabet.IndexOf(c);
        }
    }

    // What a joining player needs: the host's address and port, the SHA-256 of the host's certificate (pinned during
    // TLS) and a one-time invite secret. Text: "SEMP-" + Crockford base32 in groups of 8, with a 4-byte checksum inside
    // (bytes: version 1, address family 4 or 6, address, port (big-endian), fingerprint (32), invite (16), checksum (4)).
    public sealed class JoinCode
    {
        public const string Prefix = "SEMP-";
        public const int MaxTextLength = 200;
        const byte Version = 1;
        public const byte LanKind = Version;
        // How JoinCode.Parse refuses a Steam join code, so a caller can send it the other way.
        public const string NotLan = "this is a Steam join code, not a home-network one";
        // How every refusal of a code's address starts, so a log or a tool can tell it from a damaged code.
        public const string NotLocal = "the join code points outside this PC and the home network";

        public IPAddress Address;
        public int Port;
        public byte[] Fingerprint;   // 32 bytes
        public byte[] Invite;        // 16 bytes

        public string Encode()
        {
            if (Address == null || Fingerprint == null || Fingerprint.Length != 32 || Invite == null || Invite.Length != 16 || Port < 1 || Port > 65535)
                throw new ArgumentException("incomplete join code");
            byte[] addr = Address.GetAddressBytes();
            var b = new List<byte> { Version, (byte)(addr.Length == 4 ? 4 : 6) };
            b.AddRange(addr);
            b.Add((byte)(Port >> 8));
            b.Add((byte)Port);
            b.AddRange(Fingerprint);
            b.AddRange(Invite);
            b.AddRange(Check(b.ToArray(), b.Count));
            return Text(b.ToArray());
        }

        internal static string Text(byte[] raw)
        {
            string s = Base32.Encode(raw);
            var sb = new StringBuilder(Prefix);
            for (int i = 0; i < s.Length; i += 8)
            {
                if (i > 0) sb.Append('-');
                sb.Append(s, i, Math.Min(8, s.Length - i));
            }
            return sb.ToString();
        }

        internal static byte[] Check(byte[] data, int count)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data, 0, count);
                return new[] { h[0], h[1], h[2], h[3] };
            }
        }

        // The bytes of any join code (either kind), or null with the reason: the text checks both kinds share.
        internal static byte[] Bytes(string text, out string error)
        {
            error = null;
            if (text == null) { error = "no join code"; return null; }
            text = text.Trim();
            if (text.Length == 0 || text.Length > MaxTextLength) { error = "a join code is 1 to " + MaxTextLength + " characters"; return null; }
            if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) { error = "a join code starts with " + Prefix; return null; }
            byte[] b = Base32.Decode(text.Substring(Prefix.Length).Replace("-", ""));
            if (b == null) { error = "the join code has characters that don't belong in it"; return null; }
            if (b.Length < 2) { error = "unknown join code version"; return null; }
            return b;
        }

        // Null with a reason in error for anything that isn't a complete, intact home-network code.
        public static JoinCode Parse(string text, out string error)
        {
            byte[] b = Bytes(text, out error);
            if (b == null) return null;
            if (b[0] == SteamCode.Kind) { error = NotLan; return null; }
            if (b[0] != Version) { error = "unknown join code version"; return null; }
            int addrLen = b[1] == 4 ? 4 : b[1] == 6 ? 16 : -1;
            if (addrLen < 0) { error = "unknown address type in the join code"; return null; }
            int expected = 2 + addrLen + 2 + 32 + 16 + 4;
            if (b.Length != expected) { error = "the join code is the wrong length (incomplete copy?)"; return null; }
            byte[] check = Check(b, expected - 4);
            for (int i = 0; i < 4; i++)
                if (b[expected - 4 + i] != check[i]) { error = "the join code's checksum doesn't match (typo or incomplete copy?)"; return null; }
            var addr = new byte[addrLen];
            Buffer.BlockCopy(b, 2, addr, 0, addrLen);
            var c = new JoinCode { Address = new IPAddress(addr) };
            if (c.Address.Equals(IPAddress.Any) || c.Address.Equals(IPAddress.IPv6Any) || c.Address.Equals(IPAddress.Broadcast))
            { error = "the join code has no usable address"; return null; }
            // The joiner goes only where a host may listen (BindAddress): a code pointing anywhere else, by mistake or
            // planted, is refused here, before anything connects. Across the internet goes through Steam's relay.
            if (!BindAddress.Allowed(c.Address))
            {
                error = NotLocal + " (" + c.Address + "): the host must be on this PC, the same home network "
                        + "(127.x.x.x, 10.x.x.x, 172.16.x.x to 172.31.x.x, 192.168.x.x) or Tailscale (100.64.x.x to "
                        + "100.127.x.x); across the internet, use a Steam join code";
                return null;
            }
            int p = 2 + addrLen;
            c.Port = (b[p] << 8) | b[p + 1];
            if (c.Port == 0) { error = "port 0 in the join code"; return null; }
            c.Fingerprint = new byte[32];
            Buffer.BlockCopy(b, p + 2, c.Fingerprint, 0, 32);
            c.Invite = new byte[16];
            Buffer.BlockCopy(b, p + 34, c.Invite, 0, 16);
            return c;
        }
    }
}
