using System.Text;

namespace SolarExpanseMultiplayer
{
    public enum JoinCodeKind { Empty, Home, Steam, Invalid }

    // The join screen's two fields. The code is a secret (it holds the host's address and a one-time invite): it is
    // never logged and never in ToString, and the screen shows it only as a password field's dots.
    public sealed class JoinForm
    {
        string code = "";
        string name = "";

        public string Name { get { return name; } set { name = value ?? ""; } }

        public string Code { get { return code; } }

        public bool HasCode { get { return code.Length > 0; } }

        // What the field holds after the player types: kept as it is, only cut to the field's own limit.
        public void SetCode(string typed)
        {
            string t = typed ?? "";
            code = t.Length > JoinCode.MaxTextLength ? t.Substring(0, JoinCode.MaxTextLength) : t;
        }

        // A paste from the clipboard: the code may come split over lines or with spaces from a chat window, so all
        // white space goes. Null when the clipboard holds nothing that could be a code (empty, or longer than any
        // code), and then the field keeps what it had.
        public static string CleanPaste(string clip)
        {
            if (string.IsNullOrEmpty(clip) || clip.Length > JoinCode.MaxTextLength * 4) return null;
            var sb = new StringBuilder(clip.Length);
            foreach (char c in clip)
                if (!char.IsWhiteSpace(c) && !char.IsControl(c)) sb.Append(c);
            if (sb.Length == 0 || sb.Length > JoinCode.MaxTextLength) return null;
            return sb.ToString();
        }

        // What the field may hold, whatever reached it (a Ctrl+V into the field, typing): no white space or control
        // characters (a code copied from a chat window comes split over lines), at most the field's limit.
        public static string Normalise(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            var sb = new StringBuilder(System.Math.Min(field.Length, JoinCode.MaxTextLength));
            foreach (char c in field)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) continue;
                if (sb.Length == JoinCode.MaxTextLength) break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Which kind of code the field holds: a whole home-network code, a whole Steam join code, nothing, or neither.
        public JoinCodeKind Kind
        {
            get
            {
                string t = code.Trim();
                if (t.Length == 0) return JoinCodeKind.Empty;
                string error;
                JoinCode lan;
                SteamCode steam;
                if (t.Length > JoinCode.MaxTextLength || !JoinRoute.Parse(t, out lan, out steam, out error))
                    return JoinCodeKind.Invalid;
                return steam != null ? JoinCodeKind.Steam : JoinCodeKind.Home;
            }
        }

        // Why Join is refused before anything connects (a UiText key), or null: the code must parse as a whole code,
        // of either kind (a Steam join code is as good as a home-network one).
        public string Check()
        {
            JoinCodeKind k = Kind;
            return k == JoinCodeKind.Empty ? "join.noCode" : k == JoinCodeKind.Invalid ? "join.badCode" : null;
        }

        // The line under the field for what it holds (a table key), or null while it holds nothing or a partial code.
        public string KindKey
        {
            get
            {
                JoinCodeKind k = Kind;
                return k == JoinCodeKind.Steam ? "join.screen.kind.steam"
                    : k == JoinCodeKind.Home ? "join.screen.kind.home" : null;
            }
        }

        // Never the code: a form may end up in a log line.
        public override string ToString()
        {
            return "JoinForm(name " + name.Length + " chars, code " + (HasCode ? "entered" : "empty") + ")";
        }
    }
}
