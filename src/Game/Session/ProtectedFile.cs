namespace SolarExpanseMultiplayer
{
    // DPAPI for the current user, failing closed (DD9). The logic is ProtectedStore's (Core, tested with a fake protector); this is
    // the static face over the real one. Available false: no hosting, no stored keys, never a plain file.
    public static class ProtectedFile
    {
        static readonly ProtectedStore Store = new ProtectedStore(new DpapiProtector());

        public static bool Available { get { return Store.Available; } }

        public static string Write(string path, byte[] data) { return Store.Write(path, data); }

        public static byte[] Read(string path, out string problem) { return Store.Read(path, out problem); }

        public static ProtectedStore Shared { get { return Store; } }
    }
}
