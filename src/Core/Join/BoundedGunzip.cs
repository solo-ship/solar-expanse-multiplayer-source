using System;

namespace SolarExpanseMultiplayer
{
    // The joiner's bounded decompression (spec section 3, Design decision 2 step 3). Input is hostile until the scan has passed it:
    //   - the declared size is checked against the cap BEFORE anything is allocated, and the output buffer is exactly that size and
    //     never grows (a bomb stops at declared + 1 bytes);
    //   - the gzip header is read here (flags, optional parts, the optional header CRC); the single member is inflated by the
    //     inflater below, which reports where the deflate data ended; that must be exactly 8 bytes before the end of the input
    //     (so a second member, trailing bytes, junk before the trailer, or a forged trailer after junk are all refused);
    //   - the trailer's CRC-32 must equal the CRC of the output and its ISIZE the declared size.
    // The inflater is our own (a small, table-driven port of the reference algorithm) and not DeflateStream/GZipStream: Mono's and
    // desktop .NET's differ on multiple members, trailers and truncation, and neither reports where the deflate data ended, so
    // neither can be trusted to refuse trailing bytes. This one behaves the same on both runtimes. It allocates two 64 KiB code tables
    // once, and spends no more than a fixed multiple of the output it has written on building tables (the work budget below), so a
    // stream of empty blocks with 15-bit codes cannot burn the CPU of a joiner.
    public static class BoundedGunzip
    {
        // Decompresses gz into new byte[declared] (declared 1..JoinLimits.MaxUncompressed). On success: the bytes, code 0, error null.
        // Otherwise null and a JoinCodes value: OverCap (declared outside the caps), SizeMismatch (the output is not exactly declared
        // bytes, or the trailer's ISIZE is not), Decompress (not gzip, corrupt, truncated, a bad CRC, anything after the member).
        public static byte[] Decompress(byte[] gz, long declared, out int code, out string error)
        {
            code = 0; error = null;
            if (declared < 1 || declared > JoinLimits.MaxUncompressed) return Fail(JoinCodes.OverCap, "the declared size is outside 1.." + JoinLimits.MaxUncompressed, out code, out error);
            try { return Run(gz, (int)declared, out code, out error); }
            catch (Exception e) { return Fail(JoinCodes.Decompress, "the stream could not be read (" + e.GetType().Name + ")", out code, out error); }
        }

        static byte[] Fail(int c, string why, out int code, out string error) { code = c; error = why; return null; }

        static byte[] Run(byte[] gz, int declared, out int code, out string error)
        {
            string why;
            int start = HeaderEnd(gz, out why);
            if (start < 0) return Fail(JoinCodes.Decompress, why, out code, out error);
            int trailerAt = gz.Length - 8;
            // Deflate cannot expand by more than about 1032:1 (a match of 258 bytes costs at least 2 bits), so a declared size past that
            // cannot be true: refused before the buffer exists.
            if (declared > (long)(trailerAt - start) * 1032 + 1032)
                return Fail(JoinCodes.SizeMismatch, "the data cannot hold " + declared + " bytes", out code, out error);

            var result = new byte[declared];
            var inf = new Inflater(gz, start, gz.Length, result);
            try { inf.Run(); }
            catch (InflateException e)
            {
                return Fail(e.Overflow ? JoinCodes.SizeMismatch : JoinCodes.Decompress, e.Message, out code, out error);
            }
            if (inf.EndPos != trailerAt) return Fail(JoinCodes.Decompress, "the compressed data is not followed by exactly the 8-byte gzip trailer", out code, out error);
            if (inf.Written != declared) return Fail(JoinCodes.SizeMismatch, "the data ends after " + inf.Written + " bytes, fewer than declared", out code, out error);
            uint crc = ReadLe32(gz, trailerAt), isize = ReadLe32(gz, trailerAt + 4);
            if (crc != Crc32.Of(result, 0, result.Length)) return Fail(JoinCodes.Decompress, "the gzip trailer's CRC-32 is wrong", out code, out error);
            if (isize != (uint)declared) return Fail(JoinCodes.SizeMismatch, "the gzip trailer's size is not the declared size", out code, out error);
            code = 0; error = null;
            return result;
        }

        static uint ReadLe32(byte[] b, int at) { return b[at] | ((uint)b[at + 1] << 8) | ((uint)b[at + 2] << 16) | ((uint)b[at + 3] << 24); }

        // The offset of the first deflate byte, or -1 with why set. The header is 10 bytes, then (by flag) an extra field, a name, a
        // comment and a 16-bit header CRC; reserved flag bits and any method but deflate are refused. Everything must leave room for at
        // least 2 deflate bytes and the 8-byte trailer.
        static int HeaderEnd(byte[] gz, out string why)
        {
            why = null;
            if (gz == null || gz.Length < 10 + 2 + 8) { why = "the data is too short to be gzip"; return -1; }
            if (gz[0] != 0x1F || gz[1] != 0x8B) { why = "the data is not gzip (wrong magic bytes)"; return -1; }
            if (gz[2] != 8) { why = "the gzip method is not deflate"; return -1; }
            int flags = gz[3];
            if ((flags & 0xE0) != 0) { why = "the gzip header sets reserved flags"; return -1; }
            int limit = gz.Length - 8 - 2;   // the header must end here at the latest
            int p = 10;
            if ((flags & 4) != 0)
            {
                if (p + 2 > limit) { why = "the gzip header is cut short"; return -1; }
                p += 2 + (gz[p] | (gz[p + 1] << 8));
                if (p > limit) { why = "the gzip extra field runs past the data"; return -1; }
            }
            for (int bit = 8; bit <= 16; bit <<= 1)   // the name, then the comment: zero-terminated
            {
                if ((flags & bit) == 0) continue;
                while (p < limit && gz[p] != 0) p++;
                if (p >= limit) { why = "the gzip name or comment never ends"; return -1; }
                p++;
            }
            if ((flags & 2) != 0)
            {
                if (p + 2 > limit) { why = "the gzip header is cut short"; return -1; }
                if ((Crc32.Of(gz, 0, p) & 0xFFFF) != (uint)(gz[p] | (gz[p + 1] << 8))) { why = "the gzip header's CRC is wrong"; return -1; }
                p += 2;
            }
            if (p > limit) { why = "the gzip header leaves no room for data"; return -1; }
            return p;
        }

        // ---- CRC-32 (the gzip one, polynomial 0xEDB88320) ----

        static class Crc32
        {
            static readonly uint[] Table = Make();

            static uint[] Make()
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                return t;
            }

            public static uint Of(byte[] data, int offset, int count)
            {
                uint crc = 0xFFFFFFFFu;
                uint[] t = Table;
                for (int i = offset, end = offset + count; i < end; i++) crc = t[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
                return crc ^ 0xFFFFFFFFu;
            }
        }

        // ---- the inflater ----

        sealed class InflateException : Exception
        {
            public readonly bool Overflow;   // the output would pass the declared size
            public InflateException(string why, bool overflow) : base(why) { Overflow = overflow; }
        }

        // One deflate stream (RFC 1951) from src[pos .. end) into dst, which it never grows. Run() throws InflateException on anything
        // wrong. Afterwards Written is the output length and EndPos the offset of the first byte after the stream.
        sealed class Inflater
        {
            static readonly short[] LenBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
            static readonly byte[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
            static readonly int[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
            static readonly byte[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
            static readonly byte[] ClOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

            // The fixed codes (block type 1), built once.
            static readonly ushort[] FixedLit = new ushort[1 << 9];
            static readonly ushort[] FixedDist = new ushort[1 << 5];
            static readonly int FixedLitMask, FixedDistMask;

            static Inflater()
            {
                var l = new byte[288];
                for (int i = 0; i < 144; i++) l[i] = 8;
                for (int i = 144; i < 256; i++) l[i] = 9;
                for (int i = 256; i < 280; i++) l[i] = 7;
                for (int i = 280; i < 288; i++) l[i] = 8;
                int maxLen;
                if (Build(l, 0, 288, FixedLit, out maxLen, new int[16], new int[16]) != null) throw new InvalidOperationException("the fixed literal code is invalid");
                FixedLitMask = (1 << maxLen) - 1;
                var d = new byte[32];
                for (int i = 0; i < 32; i++) d[i] = 5;
                if (Build(d, 0, 32, FixedDist, out maxLen, new int[16], new int[16]) != null) throw new InvalidOperationException("the fixed distance code is invalid");
                FixedDistMask = (1 << maxLen) - 1;
            }

            readonly byte[] src;
            readonly int end;
            int pos;
            uint bits;
            int nbits;
            readonly byte[] dst;
            int outPos;
            readonly ushort[] lit = new ushort[1 << 15], dist = new ushort[1 << 15];
            readonly byte[] lens = new byte[320];
            readonly ushort[] clt = new ushort[1 << 7];          // the code-length code's table, reused by every block
            readonly int[] cnt = new int[16], nxt = new int[16];   // Build's scratch, reused by every block
            long work;                                           // table entries built so far (the work budget)

            public Inflater(byte[] src, int start, int end, byte[] dst)
            {
                this.src = src; pos = start; this.end = end; this.dst = dst;
            }

            public int Written { get { return outPos; } }
            // The first byte after the stream: whole bytes still waiting in the bit buffer were read ahead and are given back; the
            // 0-7 bits left in the last byte are padding.
            public int EndPos { get { return pos - nbits / 8; } }

            static InflateException Corrupt(string why) { return new InflateException("the compressed data is corrupt (" + why + ")", false); }
            static InflateException Truncated() { return new InflateException("the compressed data ends too soon", false); }
            static InflateException Overflowed() { return new InflateException("the data is longer than the declared size", true); }

            void Refill()
            {
                while (nbits <= 24 && pos < end) { bits |= (uint)src[pos++] << nbits; nbits += 8; }
            }

            int GetBits(int n)   // 0..16 bits
            {
                if (n == 0) return 0;
                if (nbits < n) { Refill(); if (nbits < n) throw Truncated(); }
                int v = (int)(bits & ((1u << n) - 1));
                bits >>= n;
                nbits -= n;
                return v;
            }

            int Decode(ushort[] table, int mask, bool distance)
            {
                if (nbits < 15) Refill();
                int e = table[(int)(bits & (uint)mask)];
                int l = e & 15;
                if (l == 0) throw Corrupt(distance ? "an invalid distance code" : "an invalid code");
                if (l > nbits) throw Truncated();
                bits >>= l;
                nbits -= l;
                return e >> 4;
            }

            public void Run()
            {
                bool last;
                do
                {
                    last = GetBits(1) == 1;
                    switch (GetBits(2))
                    {
                        case 0: Stored(); break;
                        case 1: Codes(FixedLit, FixedLitMask, FixedDist, FixedDistMask); break;
                        case 2: Dynamic(); break;
                        default: throw Corrupt("a block of reserved type");
                    }
                } while (!last);
            }

            void Stored()
            {
                // Drop the bits up to the byte boundary, then give back the whole bytes the buffer holds ahead.
                bits = 0; pos -= nbits / 8; nbits = 0;
                if (pos + 4 > end) throw Truncated();
                int len = src[pos] | (src[pos + 1] << 8), nlen = src[pos + 2] | (src[pos + 3] << 8);
                if (len != (~nlen & 0xFFFF)) throw Corrupt("a stored block's length check fails");
                pos += 4;
                if (pos + len > end) throw Truncated();
                if (len > dst.Length - outPos) throw Overflowed();
                Buffer.BlockCopy(src, pos, dst, outPos, len);
                pos += len; outPos += len;
            }

            void Dynamic()
            {
                int nlen = GetBits(5) + 257, ndist = GetBits(5) + 1, ncode = GetBits(4) + 4;
                if (nlen > 286 || ndist > 30) throw Corrupt("too many codes");
                for (int i = 0; i < 19; i++) lens[i] = 0;
                for (int i = 0; i < ncode; i++) lens[ClOrder[i]] = (byte)GetBits(3);
                int clBits;
                string bad = Build(lens, 0, 19, clt, out clBits, cnt, nxt);
                if (bad != null) throw Corrupt("the code length code is " + bad);
                int clMask = (1 << clBits) - 1;
                int total = nlen + ndist, idx = 0;
                while (idx < total)
                {
                    int sym = Decode(clt, clMask, false);
                    if (sym < 16) { lens[idx++] = (byte)sym; continue; }
                    int rep; byte val = 0;
                    if (sym == 16)
                    {
                        if (idx == 0) throw Corrupt("a repeat with nothing before it");
                        val = lens[idx - 1];
                        rep = 3 + GetBits(2);
                    }
                    else if (sym == 17) rep = 3 + GetBits(3);
                    else rep = 11 + GetBits(7);
                    if (idx + rep > total) throw Corrupt("code lengths run past the table");
                    while (rep-- > 0) lens[idx++] = val;
                }
                if (lens[256] == 0) throw Corrupt("no end-of-block code");
                int litBits, distBits;
                bad = Build(lens, 0, nlen, lit, out litBits, cnt, nxt);
                if (bad != null) throw Corrupt("the literal code is " + bad);
                bad = Build(lens, nlen, ndist, dist, out distBits, cnt, nxt);
                if (bad != null) throw Corrupt("the distance code is " + bad);
                // The work budget: building the two tables costs 1 << maxLen entries each, however little the block then writes. A
                // stream of empty blocks with 15-bit codes would otherwise cost 65536 entries per few dozen input bytes.
                work += (1L << litBits) + (1L << distBits);
                if (work > 16L * outPos + 1048576) throw Corrupt("too much table-building work for the data it produces");
                Codes(lit, (1 << litBits) - 1, dist, (1 << distBits) - 1);
            }

            void Codes(ushort[] lt, int lmask, ushort[] dt, int dmask)
            {
                byte[] d = dst;
                while (true)
                {
                    int sym = Decode(lt, lmask, false);
                    if (sym < 256)
                    {
                        if (outPos >= d.Length) throw Overflowed();
                        d[outPos++] = (byte)sym;
                        continue;
                    }
                    if (sym == 256) return;
                    sym -= 257;
                    if (sym >= 29) throw Corrupt("an invalid length code");
                    int len = LenBase[sym] + GetBits(LenExtra[sym]);
                    int ds = Decode(dt, dmask, true);
                    if (ds >= 30) throw Corrupt("an invalid distance code");
                    int back = DistBase[ds] + GetBits(DistExtra[ds]);
                    if (back > outPos) throw Corrupt("a distance reaching before the start");
                    if (len > d.Length - outPos) throw Overflowed();
                    if (back >= len) { Buffer.BlockCopy(d, outPos - back, d, outPos, len); outPos += len; }
                    else { int from = outPos - back; for (int i = 0; i < len; i++) d[outPos++] = d[from++]; }
                }
            }

            // Fills table (1 << maxLen entries, each (symbol << 4) | length; 0 = no code) from the code lengths lens[off .. off + n).
            // Null = fine; otherwise why the lengths are not a usable prefix code. An incomplete code is accepted only as a single code
            // of length 1 (which deflate allows for a lone distance code).
            static string Build(byte[] lens, int off, int n, ushort[] table, out int maxLen, int[] count, int[] next)
            {
                Array.Clear(count, 0, 16);
                for (int i = 0; i < n; i++) count[lens[off + i]]++;
                count[0] = 0;
                maxLen = 0;
                int codes = 0;
                for (int l = 1; l < 16; l++) { if (count[l] != 0) maxLen = l; codes += count[l]; }
                int left = 1;
                for (int l = 1; l < 16; l++)
                {
                    left = (left << 1) - count[l];
                    if (left < 0) { maxLen = 0; return "over-subscribed"; }
                }
                if (left > 0 && !(codes == 1 && maxLen == 1)) { if (codes != 0) { maxLen = 0; return "incomplete"; } }
                int code = 0;
                for (int l = 1; l < 16; l++) { code = (code + count[l - 1]) << 1; next[l] = code; }
                int size = 1 << maxLen;
                Array.Clear(table, 0, size);
                for (int sym = 0; sym < n; sym++)
                {
                    int l = lens[off + sym];
                    if (l == 0) continue;
                    int c = next[l]++, rev = 0;
                    for (int k = 0; k < l; k++) { rev = (rev << 1) | (c & 1); c >>= 1; }
                    ushort entry = (ushort)((sym << 4) | l);
                    for (int j = rev; j < size; j += 1 << l) table[j] = entry;
                }
                return null;
            }
        }
    }
}
