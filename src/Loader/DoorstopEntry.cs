namespace Doorstop
{
    // Unity Doorstop 4 calls exactly this: static void Doorstop.Entrypoint.Start()
    public static class Entrypoint
    {
        public static void Start()
        {
            SolarExpanseMultiplayer.Boot.FromDoorstop();
        }
    }
}
