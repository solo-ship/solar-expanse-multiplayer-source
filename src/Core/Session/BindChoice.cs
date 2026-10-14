using System;
using System.Collections.Generic;
using System.Net;

namespace SolarExpanseMultiplayer
{
    public sealed class AdapterInfo
    {
        public string Name, Description;
        public int Type;             // System.Net.NetworkInformation.NetworkInterfaceType as a number
        public bool Up, HasGateway;
        public List<IPAddress> V4;
    }

    public sealed class BindOption
    {
        public string AdapterName;
        public IPAddress Address;
    }

    // This PC's local-network addresses a host may offer: BindAddress's rule, loopback left out, adapters that are up,
    // virtual ones (VMs, WSL, containers, VPNs) left out by type and by description, the adapter with a default
    // gateway first. Empty means hosting is refused.
    public static class BindChoice
    {
        // NetworkInterfaceType numbers that are never a real local network
        static readonly int[] RefusedTypes = { 1, 23, 24, 28, 131 };   // Unknown, Ppp, Loopback, Slip, Tunnel

        // Lower case; a match anywhere in the name or description leaves the adapter out
        static readonly string[] VirtualWords =
        {
            "virtual", "vethernet", "hyper-v", "wsl", "vmware", "vmnet", "vpn", "tap-", "wireguard",
            "tailscale", "zerotier", "docker", "loopback", "pseudo", "bluetooth", "npcap", "hamachi",
        };

        public static List<BindOption> From(IList<AdapterInfo> adapters)
        {
            var withGateway = new List<BindOption>();
            var others = new List<BindOption>();
            var seen = new HashSet<string>();
            if (adapters == null) return withGateway;
            foreach (AdapterInfo a in adapters)
            {
                if (a == null || !a.Up || a.V4 == null || Array.IndexOf(RefusedTypes, a.Type) >= 0) continue;
                if (IsVirtual(a)) continue;
                foreach (IPAddress ip in a.V4)
                {
                    if (!BindAddress.Allowed(ip) || IPAddress.IsLoopback(ip) || !seen.Add(ip.ToString())) continue;
                    var option = new BindOption { AdapterName = a.Name ?? "", Address = ip };
                    if (a.HasGateway) withGateway.Add(option); else others.Add(option);
                }
            }
            withGateway.AddRange(others);
            return withGateway;
        }

        static bool IsVirtual(AdapterInfo a)
        {
            string text = ((a.Name ?? "") + " " + (a.Description ?? "")).ToLowerInvariant();
            foreach (string w in VirtualWords) if (text.Contains(w)) return true;
            return false;
        }
    }
}
