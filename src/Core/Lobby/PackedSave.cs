using System;

namespace SolarExpanseMultiplayer
{
    // Walls 2 to 4 for a Multiplayer save file read from the folder (a solo load, hosting a save): the bytes are the ones the
    // index recorded, the gunzip is bounded by the size the trailer declares, and the strict scan passes, all before Odin or
    // the game sees a byte. CopyInstaller runs the same walls on a held copy.
    public static class PackedSave
    {
        // null + json on success, else a fixed reason. entry: the index's record of the save.
        public static string Unpack(byte[] gz, MpSaveEntry entry, out byte[] json)
        {
            json = null;
            if (gz == null || entry == null) return "no save";
            if (gz.Length < 18 || gz.Length > JoinLimits.MaxCompressed) return "the save's size is outside the cap";
            if (!PlayerKeys.Same(Sha256Hex.Of(gz), entry.JsonSha)) return "the save has changed since it was written";
            long declared = GzipFacts.DeclaredSize(gz);
            int code; string error;
            byte[] plain = BoundedGunzip.Decompress(gz, declared, out code, out error);
            if (plain == null) return "the save could not be unpacked: " + error;
            ScanResult scan = OdinScan.Scan(plain, ScanLimits.FromCorpus());
            if (!scan.Ok)
                return "the save was refused by the scan (" + (scan.Code == ScanCode.TypeNotAllowed ? "a type that is not allowed" : "malformed") + ")";
            json = plain;
            return null;
        }
    }
}
