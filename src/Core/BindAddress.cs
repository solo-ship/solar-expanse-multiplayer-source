using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // Where a host may listen and a joiner may go: this PC's loopback (127.0.0.0/8), a private home-network address
    // (RFC 1918: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16), or a Tailscale address (100.64.0.0/10 and
    // fd7a:115c:a1e0::/48: the fallback when Steam is down, a private network the players set up themselves). Anything
    // wider (every interface, a public address, link-local, multicast, other IPv6) would let more than those networks
    // try to join; across the internet goes through Steam's relay. Text must be the address written plainly:
    // IPAddress also reads "10.1", "012.0.0.1" or "167772161" as 10.0.0.1, and a value that means something other than
    // it seems to is refused rather than guessed at.
    public static class BindAddress
    {
        // What the refusals say the rule is. Its wording must not contain "needs a loopback address (127.x.x.x)": the
        // device kit looks for that text in a deployed DLL to tell a build that can only host on this PC.
        public const string Rule = "this PC's loopback address (127.x.x.x), a private home-network address " +
                                   "(10.x.x.x, 172.16.x.x to 172.31.x.x, 192.168.x.x) or a Tailscale address " +
                                   "(100.64.x.x to 100.127.x.x, fd7a:115c:a1e0::/48), written plainly";

        // The IPv4 rule as one pattern, for tools\run-test-game.ps1 -Bind, which cannot call this class: four decimal
        // parts 0-255 without leading zeros, in one of the five ranges. [0-9], not \d, which also takes other
        // scripts' digits. run-test-game.ps1 builds it from $octet the same way; BindAddressTests pins the two texts
        // together and checks the pattern agrees with TryParse.
        public const string Octet = "(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])";
        public const string Pattern =
            @"^(?:(?:127|10)\." + Octet + @"|172\.(?:1[6-9]|2[0-9]|3[01])|192\.168"
            + @"|100\.(?:6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7]))\." + Octet + @"\." + Octet + @"\z";
        // A Tailscale IPv6 address: the /48 prefix, then lower-case hex groups. TryParse also needs the text to be the
        // address's own short form (IPAddress.ToString), so there is one spelling only; run-test-game.ps1 does the
        // same.
        public const string Pattern6 = @"^fd7a:115c:a1e0:[0-9a-f:]{1,30}\z";

        static readonly Regex Plain = new Regex(Pattern, RegexOptions.CultureInvariant);
        static readonly Regex Plain6 = new Regex(Pattern6, RegexOptions.CultureInvariant);

        // The address in text, or false (address null). The text must match Pattern (or Pattern6 and be the short
        // form), the one form run-test-game.ps1 accepts too, so every other spelling IPAddress would also read is
        // refused; Allowed then checks the address.
        public static bool TryParse(string text, out IPAddress address)
        {
            address = null;
            IPAddress a;
            if (text == null) return false;
            bool v4 = Plain.IsMatch(text);
            if (!v4 && !Plain6.IsMatch(text)) return false;
            if (!IPAddress.TryParse(text, out a) || !Allowed(a)) return false;
            if (!v4 && a.ToString() != text) return false;
            address = a;
            return true;
        }

        // The family is checked first: an IPv6 address such as c0a8::1 starts with the bytes 192.168.
        public static bool Allowed(IPAddress a)
        {
            if (a == null) return false;
            byte[] b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
                return a.ScopeId == 0 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1
                       && b[5] == 0xe0;
            if (a.AddressFamily != AddressFamily.InterNetwork) return false;
            if (b[0] == 127 || b[0] == 10) return true;
            if (b[0] == 172) return b[1] >= 16 && b[1] <= 31;
            if (b[0] == 192) return b[1] == 168;
            if (b[0] == 100) return b[1] >= 64 && b[1] <= 127;
            return false;
        }

        // A Tailscale address (for the host's log line: it is a network the player set up, not the home network).
        public static bool IsTailscale(IPAddress a)
        {
            if (!Allowed(a)) return false;
            byte[] b = a.GetAddressBytes();
            return a.AddressFamily == AddressFamily.InterNetworkV6 || b[0] == 100;
        }
    }
}
