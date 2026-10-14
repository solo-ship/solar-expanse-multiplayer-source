using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SolarExpanseMultiplayer
{
    // The host's self-signed TLS certificate (RSA 2048, SHA-256), kept in the mod's data folder for this profile.
    // Made with the game's Mono.Security: in Unity's Mono, CertificateRequest throws PlatformNotSupportedException and
    // there is no EC crypto. In Game, not Core: the game's Mono.Security needs Mono's mscorlib (on desktop .NET it fails
    // with MissingMethodException Array.Reverse<T>, checked 2026-10-09), so desktop tests make their own (NetKit.Cert).
    //
    // Validity dates are NOT checked by anyone, on purpose: a client trusts only the SHA-256 of this certificate (the pin
    // in the join code), so an expiry date would only add a way to lock players out. The dates (1 day back, 20 years
    // ahead) just have to be sane for the TLS stack. See docs/SECURITY.md, "Host key storage".
    //
    // The file life cycle (create, quarantine a corrupt file, never destroy a key over a transient IO error, temp file
    // then replace, user-only permissions) is CertFile's, which desktop tests cover.
    public static class HostCert
    {
        public const string FileName = "host-cert.p12";
        const string Password = "semp";   // NOT protection (see SECURITY.md); some importers refuse an empty password

        public static X509Certificate2 LoadOrCreate(string dataDir)
        {
            bool created;
            return LoadOrCreate(dataDir, out created);
        }

        // created: a new identity was made this time (the old fingerprint, if any, is in the log at WARN).
        public static X509Certificate2 LoadOrCreate(string dataDir, out bool created)
        {
            CertFile.Result r = CertFile.LoadOrCreate(dataDir, FileName, Load, Create, Log.Warn, FingerprintOf);
            created = r.Created;
            if (!r.Created)
                Log.Info("host cert: loaded " + LogPrivacy.Path(Path.Combine(dataDir, FileName), Boot.GameRootDir));
            return r.Cert;
        }

        // Not Exportable: the private key never needs to leave the certificate object. Task 8 checked in game that UnityTls
        // serves without it (see the task report); if that ever stops being true, this is the flag to put back.
        static X509Certificate2 Load(byte[] p12)
        {
            var c = new X509Certificate2(p12, Password, X509KeyStorageFlags.DefaultKeySet);
            if (!c.HasPrivateKey) throw new CryptographicException("the certificate has no private key");
            return c;
        }

        // The old identity's fingerprint from the raw file, for the log: the certificate part needs no private key (Load
        // refuses a keyless file, so CertFile could not get it from there).
        static string FingerprintOf(byte[] p12)
        {
            return CertFile.Fingerprint(new X509Certificate2(p12, Password));
        }

        // The same load and fingerprint over plain PKCS#12 bytes, for KeyFiles (which unprotects the file first).
        public static X509Certificate2 LoadPlain(byte[] p12) { return Load(p12); }
        public static string PlainFingerprint(byte[] p12) { return FingerprintOf(p12); }

        public static byte[] Create()
        {
            using (var rsa = new RSACryptoServiceProvider(2048))
            using (var rng = new RNGCryptoServiceProvider())
            {
                var serial = new byte[16];
                rng.GetBytes(serial);
                serial[0] &= 0x7F;   // a positive serial number
                var b = new Mono.Security.X509.X509CertificateBuilder(3);
                b.SerialNumber = serial;
                b.IssuerName = "CN=host";   // a neutral name: the certificate is only ever trusted by its fingerprint
                b.SubjectName = "CN=host";
                b.NotBefore = DateTime.UtcNow.AddDays(-1);
                b.NotAfter = DateTime.UtcNow.AddYears(20);
                b.SubjectPublicKey = rsa;
                b.Hash = "SHA256";
                byte[] der = b.Sign(rsa);
                var p12 = new Mono.Security.X509.PKCS12();
                p12.Password = Password;
                p12.AddCertificate(new Mono.Security.X509.X509Certificate(der));
                p12.AddPkcs8ShroudedKeyBag(rsa);
                return p12.GetBytes();
            }
        }
    }
}
