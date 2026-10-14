using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SolarExpanseMultiplayer
{
    // Windows DPAPI for the current user (CryptProtectData, no machine scope), called directly: the same code runs on the desktop
    // test runner and in the game's Mono, and it is the real DPAPI on both, not a managed stand-in. The entropy binds a blob to this
    // mod (another program's DPAPI blob is not accepted). Available only on Windows and only after a round trip worked.
    public sealed class DpapiProtector : IProtector
    {
        static readonly byte[] Entropy = System.Text.Encoding.ASCII.GetBytes("SolarExpanseMultiplayer protected file v1");
        const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        struct Blob { public int Size; public IntPtr Data; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref Blob dataIn, string description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr mem);

        bool probed, works;

        public bool Available
        {
            get
            {
                if (probed) return works;
                probed = true;
                try
                {
                    if (Environment.OSVersion.Platform != PlatformID.Win32NT) return works = false;
                    byte[] probe = { 1, 2, 3, 4, 5, 6, 7, 8 };
                    byte[] back = Unprotect(Protect(probe));
                    works = back != null && back.Length == probe.Length;
                    for (int i = 0; works && i < probe.Length; i++) works = back[i] == probe[i];
                }
                catch (Exception) { works = false; }
                return works;
            }
        }

        public byte[] Protect(byte[] data) { return Run(data, true); }
        public byte[] Unprotect(byte[] data) { return Run(data, false); }

        static byte[] Run(byte[] data, bool protect)
        {
            if (data == null || data.Length == 0) throw new CryptographicException("nothing to process");
            Blob input = Make(data), entropy = Make(Entropy), output = new Blob();
            try
            {
                bool ok = protect
                    ? CryptProtectData(ref input, "semp", ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
                if (output.Size < 0 || output.Data == IntPtr.Zero) throw new CryptographicException("DPAPI gave nothing");
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                Free(input, true);
                Free(entropy, false);
                if (output.Data != IntPtr.Zero)
                {
                    if (!protect && output.Size > 0) { var z = new byte[output.Size]; Marshal.Copy(z, 0, output.Data, output.Size); }   // plain bytes
                    LocalFree(output.Data);
                }
            }
        }

        static Blob Make(byte[] b)
        {
            IntPtr p = Marshal.AllocHGlobal(b.Length);
            Marshal.Copy(b, 0, p, b.Length);
            return new Blob { Size = b.Length, Data = p };
        }

        static void Free(Blob b, bool wipe)
        {
            if (b.Data == IntPtr.Zero) return;
            if (wipe) { var z = new byte[b.Size]; Marshal.Copy(z, 0, b.Data, b.Size); }
            Marshal.FreeHGlobal(b.Data);
        }
    }
}
