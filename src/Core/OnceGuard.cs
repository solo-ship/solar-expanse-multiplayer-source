using System;

namespace SolarExpanseMultiplayer
{
    // Process-wide "already started" flag, so the Doorstop and BepInEx entry points can't both start the mod.
    public static class OnceGuard
    {
        static readonly object Gate = new object();

        public static bool TryClaim(string key, string owner)
        {
            lock (Gate)
            {
                if (AppDomain.CurrentDomain.GetData(key) != null) return false;
                AppDomain.CurrentDomain.SetData(key, owner ?? "");   // a null owner would read as "not claimed"
                return true;
            }
        }

        public static string Owner(string key)
        {
            return AppDomain.CurrentDomain.GetData(key) as string;
        }
    }
}
