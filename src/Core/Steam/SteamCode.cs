using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The Steam join code: what a player needs to join through Steam's relay. It holds the host's Steam id (the pin:
    // the joiner connects only to that account, and Steam's certificates prove the account), the same one-time invite
    // a home-network code holds, and the host's protocol and a build tag so a joiner on another version is told before
    // it connects. Never an address. Text: "SEMP-" + Crockford base32 in groups of 8, as JoinCode (bytes: kind 2, host
    // id (8, big-endian), invite (16), protocol (2, big-endian), build tag (4), checksum (4)). A home-network code
    // starts with byte 1, so neither parser ever takes the other's code.
    public sealed class SteamCode
    {
        public const byte Kind = 2;
        const int Length = 1 + 8 + 16 + 2 + 4 + 4;
        // How SteamCode.TryParse refuses a home-network code, so a caller can send it the other way.
        public const string NotSteam = "this is a home-network join code, not a Steam join code";

        public ulong HostId;      // an individual account (IsIndividual)
        public byte[] Invite;     // 16 bytes
        public int Protocol;      // the host's ModInfo.Protocol
        public byte[] BuildTag;   // 4 bytes: Tag(mod version, game build)

        // Steam's id layout: universe (8 bits, 1 = public), account type (4 bits, 1 = individual), instance (20
        // bits), account number (32 bits, never 0).
        public static bool IsIndividual(ulong id)
        {
            return (id >> 56) == 1 && ((id >> 52) & 0xF) == 1 && (id & 0xFFFFFFFFUL) != 0;
        }

        // The first 4 bytes of SHA-256 over the mod version and the game build: enough to tell versions apart, nothing
        // to keep secret (the gate checks the versions in full after the invite).
        public static byte[] Tag(string modVersion, string gameBuild)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes((modVersion ?? "") + "|" + (gameBuild ?? "")));
                return new[] { h[0], h[1], h[2], h[3] };
            }
        }

        public static string Format(SteamCode c)
        {
            if (c == null || !IsIndividual(c.HostId) || c.Invite == null || c.Invite.Length != 16
                || c.BuildTag == null || c.BuildTag.Length != 4 || c.Protocol < 0 || c.Protocol > 65535)
                throw new ArgumentException("incomplete Steam join code");
            var b = new List<byte>(Length) { Kind };
            for (int i = 7; i >= 0; i--) b.Add((byte)(c.HostId >> (i * 8)));
            b.AddRange(c.Invite);
            b.Add((byte)(c.Protocol >> 8));
            b.Add((byte)c.Protocol);
            b.AddRange(c.BuildTag);
            b.AddRange(JoinCode.Check(b.ToArray(), b.Count));
            return JoinCode.Text(b.ToArray());
        }

        // False with a reason in error for anything that isn't a complete, intact Steam join code.
        public static bool TryParse(string text, out SteamCode code, out string error)
        {
            code = null;
            byte[] b = JoinCode.Bytes(text, out error);
            if (b == null) return false;
            if (b[0] != Kind)
            {
                error = b[0] == JoinCode.LanKind ? NotSteam : "unknown join code version";
                return false;
            }
            if (b.Length != Length) { error = "the join code is the wrong length (incomplete copy?)"; return false; }
            byte[] check = JoinCode.Check(b, Length - 4);
            for (int i = 0; i < 4; i++)
                if (b[Length - 4 + i] != check[i])
                { error = "the join code's checksum doesn't match (typo or incomplete copy?)"; return false; }
            ulong id = 0;
            for (int i = 1; i <= 8; i++) id = (id << 8) | b[i];
            if (!IsIndividual(id))
            { error = "the Steam join code does not name a player's Steam account"; return false; }
            var c = new SteamCode { HostId = id, Invite = new byte[16], BuildTag = new byte[4] };
            Buffer.BlockCopy(b, 9, c.Invite, 0, 16);
            c.Protocol = (b[25] << 8) | b[26];
            Buffer.BlockCopy(b, 27, c.BuildTag, 0, 4);
            code = c;
            return true;
        }

        // The joiner's check before it connects: null when the code's host runs this protocol, mod version and build.
        public string VersionProblem(int protocol, string modVersion, string gameBuild)
        {
            if (Protocol != protocol)
                return "the host runs a different version of the mod (protocol " + Protocol + ", this game has "
                    + protocol + "): both players need the same version";
            byte[] mine = Tag(modVersion, gameBuild);
            for (int i = 0; i < 4; i++)
                if (mine[i] != BuildTag[i])
                    return "the host runs a different mod version or game build: both players need the same versions";
            return null;
        }
    }

    // Which way a pasted code goes: a Steam join code through the relay, a home-network code over TLS. Decided by the
    // code's kind byte, so a damaged code of either kind gets its own kind's reason.
    public static class JoinRoute
    {
        public static bool Parse(string text, out JoinCode lan, out SteamCode steam, out string error)
        {
            lan = null; steam = null;
            byte[] b = JoinCode.Bytes(text, out error);
            if (b == null) return false;
            if (b[0] == SteamCode.Kind) return SteamCode.TryParse(text, out steam, out error);
            lan = JoinCode.Parse(text, out error);
            return lan != null;
        }
    }
}
