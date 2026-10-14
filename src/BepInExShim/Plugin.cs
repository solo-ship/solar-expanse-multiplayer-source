using BepInEx;

namespace SolarExpanseMultiplayer.BepInExShim
{
    // Only used when BepInEx is installed; all the work is in SolarExpanseMultiplayer.dll.
    // BepInEx needs a plain System.Version: ModInfo.Number (ModInfo.Version has the "-beta" or "-dev" suffix).
    [BepInPlugin("com.solo-ship.solarexpansemultiplayer", ModInfo.Name, ModInfo.Number)]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            Boot.FromBepInEx(typeof(Plugin).Assembly.Location);
        }
    }
}
