using System.IO;

namespace SolarExpanseMultiplayer
{
    // A dev test profile's data folder: <game>\UserData\<profile>, standing in for the game's LocalLow data folder.
    public sealed class ProfilePaths
    {
        public readonly string Root;
        public readonly string Saves;

        public ProfilePaths(string gameRoot, string profile)
        {
            Root = Path.Combine(Path.Combine(gameRoot, "UserData"), profile);
            Saves = Path.Combine(Root, "Saves");
        }
    }
}
