using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace SolarExpanseMultiplayer
{
    // The host certificate and the player's keys on disk, through ProtectedFile (DD9). Without DPAPI there is no host certificate
    // (no hosting) and no stored key.
    public static class KeyFiles
    {
        // Not host-cert.p12: that name is the test profiles' plain file, which must never be read as a protected one.
        public const string HostCertName = HostKeyStore.ProtectedName;

        // The protected host certificate (HostKeyStore: DPAPI for the current user, an old plain host-cert.p12 removed);
        // null when protection is not available (the caller offers no hosting) or the certificate cannot be made.
        // created: a new identity was made this time (old join codes stop working).
        public static X509Certificate2 HostCertificate(string dataDir, out bool created)
        {
            return HostKeyStore.Protected(dataDir, new DpapiProtector(), HostCert.LoadPlain, HostCert.Create,
                                          HostCert.PlainFingerprint, Log.Warn, out created);
        }

        // The player's keys (secret and rejoin keys) under <mod>\data\player\keys\ (dataDir is already data\player).
        public static KeyStore Keys(string dataDir)
        {
            return new KeyStore(ProtectedFile.Shared, ModData.KeysDir(dataDir));
        }
    }
}
