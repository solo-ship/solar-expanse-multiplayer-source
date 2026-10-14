using System;
using System.Runtime.InteropServices;
using Steamworks;
using Value = Steamworks.ESteamNetworkingConfigValue;
using Scope = Steamworks.ESteamNetworkingConfigScope;
using DataType = Steamworks.ESteamNetworkingConfigDataType;
using GetResult = Steamworks.ESteamNetworkingGetConfigValueResult;

namespace SolarExpanseMultiplayer
{
    // Steam's per-socket and per-connection settings, and reading them back. Every listen socket and connection of ours
    // is made with ICE off (relay only: no IP address reaches the other side), our send rates and our send buffer
    // (SteamWireRules). A setting is trusted only once read back from the object itself. Stateless: callers hold any
    // locks, and check SteamReady before calling.
    public static class SteamConfig
    {
        const Value Ice = Value.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable;
        const Value RateMin = Value.k_ESteamNetworkingConfig_SendRateMin;
        const Value RateMax = Value.k_ESteamNetworkingConfig_SendRateMax;
        const Value Buffer = Value.k_ESteamNetworkingConfig_SendBufferSize;
        const DataType Int32 = DataType.k_ESteamNetworkingConfig_Int32;

        public static SteamNetworkingConfigValue_t[] Options()
        {
            return new[]
            {
                Int(Ice, SteamWireRules.IceDisable),
                Int(RateMin, SteamWireRules.SendRateMin),
                Int(RateMax, SteamWireRules.SendRateMax),
                Int(Buffer, SteamWireRules.SendBufferSize),
            };
        }

        static SteamNetworkingConfigValue_t Int(Value v, int value)
        {
            var o = new SteamNetworkingConfigValue_t();
            o.m_eValue = v;
            o.m_eDataType = Int32;
            o.m_val.m_int32 = value;
            return o;
        }

        // The one-PC test's socket pair, made without options: our send rates and buffer, one by one. Not ICE: a pair
        // is not a P2P connection (Steam refuses ICE on it and it has no route to share an address over).
        public static bool SetOnPair(HSteamNetConnection c)
        {
            bool ok = true;
            foreach (SteamNetworkingConfigValue_t o in Options())
                if (o.m_eValue != Ice)
                    ok &= SetInt(o.m_eValue, Scope.k_ESteamNetworkingConfig_Connection,
                                 new IntPtr(c.m_HSteamNetConnection), o.m_val.m_int32);
            return ok;
        }

        static bool SetInt(Value v, Scope scope, IntPtr obj, int value)
        {
            IntPtr buf = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buf, value);
                return SteamNetworkingUtils.SetConfigValue(v, scope, obj, Int32, buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // The value as the object has it (its own or inherited); false if it could not be read as an int.
        public static bool ReadInt(Value v, Scope scope, IntPtr obj, out int value)
        {
            value = 0;
            IntPtr buf = Marshal.AllocHGlobal(8);
            try
            {
                ulong size = 8;
                DataType t;
                GetResult r = SteamNetworkingUtils.GetConfigValue(v, scope, obj, out t, buf, ref size);
                if (r != GetResult.k_ESteamNetworkingGetConfigValue_OK
                    && r != GetResult.k_ESteamNetworkingGetConfigValue_OKInherited) return false;
                if (t != Int32) return false;
                value = Marshal.ReadInt32(buf);
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static bool ReadListenIce(HSteamListenSocket s, out int ice)
        {
            var obj = new IntPtr(s.m_HSteamListenSocket);
            return ReadInt(Ice, Scope.k_ESteamNetworkingConfig_ListenSocket, obj, out ice);
        }

        // ICE (a P2P connection's) and the send rates on one connection, read back and judged. null = all as they must
        // be; line = what was read, for the log.
        public static string CheckConnection(HSteamNetConnection c, bool p2p, out string line)
        {
            var obj = new IntPtr(c.m_HSteamNetConnection);
            Scope scope = Scope.k_ESteamNetworkingConfig_Connection;
            int ice, min, max, buffer;
            bool iceRead = ReadInt(Ice, scope, obj, out ice);
            bool minRead = ReadInt(RateMin, scope, obj, out min);
            bool maxRead = ReadInt(RateMax, scope, obj, out max);
            bool bufRead = ReadInt(Buffer, scope, obj, out buffer);
            line = "ICE " + (!p2p ? "n/a (not P2P)" : iceRead ? ice.ToString() : "unread")
                + ", send rate min " + (minRead ? min.ToString() : "unread")
                + " max " + (maxRead ? max.ToString() : "unread")
                + " B/s, send buffer " + (bufRead ? buffer.ToString() : "unread");
            string iceProblem = p2p ? SteamWireRules.IceProblem(iceRead, ice) : null;
            return iceProblem ?? SteamWireRules.RateProblem(minRead && maxRead, min, max);
        }

        public static string Flags(int f)
        {
            return "0x" + f.ToString("x")
                + ((f & SteamWireRules.FlagUnauthenticated) != 0 ? " unauthenticated" : "")
                + ((f & SteamWireRules.FlagUnencrypted) != 0 ? " unencrypted" : "")
                + ((f & SteamWireRules.FlagRelayed) != 0 ? " relayed" : "");
        }
    }
}
