using System;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // Dev-only command-line flags. Values become folder and file names, so only plain names are accepted.
    public static class LaunchArgs
    {
        public const string ProfileFlag = "-semp-profile";
        public const string SeedFlag = "-semp-seed";
        public const string AutoLoadFlag = "-semp-autoload";

        // \z, not $: "$" also matches before a trailing newline.
        static readonly Regex Safe = new Regex(@"^[A-Za-z0-9_-]{1,32}\z");
        // Windows device names: a folder called CON or NUL is not a normal folder.
        static readonly Regex Device = new Regex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase);

        // A leading '-' is refused too, so a missing value can't swallow the next flag ("-semp-profile -semp-seed").
        public static bool SafeName(string s)
        {
            return s != null && Safe.IsMatch(s) && s[0] != '-' && !Device.IsMatch(s);
        }

        public static bool Has(string[] args, string flag)
        {
            return Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) >= 0;
        }

        public static string Value(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        static string SafeValue(string[] args, string flag)
        {
            string v = Value(args, flag);
            return SafeName(v) ? v : null;
        }

        public static string Profile(string[] args) { return SafeValue(args, ProfileFlag); }
        public static string Seed(string[] args) { return SafeValue(args, SeedFlag); }
        public static string AutoLoad(string[] args) { return SafeValue(args, AutoLoadFlag); }
    }
}
