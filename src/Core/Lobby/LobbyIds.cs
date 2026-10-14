namespace SolarExpanseMultiplayer
{
    // The lobby's and the save files' content ids (agencies, rivals, epochs, star systems, difficulties): letters,
    // digits, '_', '-' and '.', 1 to 64 characters. Stricter than the command protocol's ActionShapes.IsContentId,
    // because these ids are read back from files on disk and from the save copy's header: anything else (spaces, path
    // separators, markup, control characters) is no id.
    public static class LobbyIds
    {
        public const int MaxContentIdChars = 64;

        public static bool IsContentId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > MaxContentIdChars) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'
                    || c == '-' || c == '.';
                if (!ok) return false;
            }
            return true;
        }
    }
}
