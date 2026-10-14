namespace SolarExpanseMultiplayer
{
    public static class ModInfo
    {
        public const string Name = "Solar Expanse Multiplayer";
        public const string Number = "0.5.0";              // plain System.Version form: the BepInEx shim uses it
        // A public beta build says so in its shown version (the menu label, the Hello and the logs): "0.5.0-beta". Every other
        // build is "-dev". tools release.ps1 refuses a build whose version does not carry "beta". The label is the only thing
        // this changes: the protocol number below and the join checks are untouched (two peers still need the same Version).
        public const bool Beta = true;
        public const string Version = Number + (Beta ? "-beta" : "-dev");
        // The wire format (3 = Seq, 4 = the command messages and RejoinOffer.LastResult, 5 = the flight kinds, fields
        // and messages, 6 = the shared world: market, growth, contract, AI and heal kinds, the world print and the resume, 7 = the lobby: roster, claims,
        // keys, the save copy, and a Hello that may carry no invite on a Steam lobby link). Bumped when a message's meaning changes for an existing peer; an optional new field that an
        // older peer drops and that reads as its old default when missing needs none (SaveId, Bye.Code).
        public const int Protocol = 7;

        // The game's version string for Hello.GameVersion and the gate: never null, at most 32 characters (the Hello cap),
        // never ending inside a surrogate pair.
        public const int MaxGameVersionChars = 32;
        public static string ClipGameVersion(string raw)
        {
            if (raw == null) return "";
            if (raw.Length <= MaxGameVersionChars) return raw;
            int cut = MaxGameVersionChars;
            if (char.IsHighSurrogate(raw[cut - 1])) cut--;
            return raw.Substring(0, cut);
        }
    }
}
