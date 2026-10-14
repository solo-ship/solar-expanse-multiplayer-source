using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Per Steam account at the host's door (owned by HostSteamDoor, under its lock): at most one connection on its way
    // in, from the door's "ask again" or "yes" until its handshake ends, and a cooldown for an account whose handshakes
    // keep failing. Any Steam account can reach the door while a code is open, so without this one account could hold
    // every handshake slot and turn every friend and rejoin away. In memory only; nothing is logged with the id.
    public sealed class DoorAccounts
    {
        public const long HoldMs = 10000;          // held at the door: the grace (3 s) and the transport's 8 s limit
        public const long InFlightMs = 20000;      // admitted: Steam's connect plus the 10 s handshake deadline
        public const int StrikesToCool = 3;
        public const long StrikeWindowMs = 60000, CooldownMs = 5 * 60000;
        public const int MaxAccounts = 256;

        sealed class Entry
        {
            public long BusyUntil = long.MinValue, CoolUntil = long.MinValue, Touched;
            public readonly List<long> Strikes = new List<long>();
        }

        readonly Dictionary<ulong, Entry> accounts = new Dictionary<ulong, Entry>();

        public bool Busy(ulong id, long now)
        {
            Entry e;
            return accounts.TryGetValue(id, out e) && now < e.BusyUntil;
        }

        public bool Cooling(ulong id, long now)
        {
            Entry e;
            return accounts.TryGetValue(id, out e) && now < e.CoolUntil;
        }

        // The door holds a connection from this account and asks again shortly.
        public void Holding(ulong id, long now) { Mark(id, now, HoldMs); }

        // The door let a connection from this account through to its handshake.
        public void Admitted(ulong id, long now) { Mark(id, now, InFlightMs); }

        // The account's handshake ended: it is free to connect again. failed: it closed before the host's Welcome (a
        // deadline, a refused Hello, a protocol breach), which counts towards the cooldown.
        public void Ended(ulong id, bool failed, long now)
        {
            Entry e;
            if (accounts.TryGetValue(id, out e)) e.BusyUntil = long.MinValue;
            if (failed) Strike(id, now);
        }

        // A refusal that is the account's own doing: StrikesToCool of them within StrikeWindowMs, and it is turned away
        // for CooldownMs.
        public void Strike(ulong id, long now)
        {
            Entry e = Get(id, now);
            e.Strikes.RemoveAll(t => now - t >= StrikeWindowMs);
            e.Strikes.Add(now);
            if (e.Strikes.Count >= StrikesToCool)
            {
                e.CoolUntil = now + CooldownMs;
                e.Strikes.Clear();
            }
        }

        // A ban or an unban starts the account afresh.
        public void Forget(ulong id) { accounts.Remove(id); }

        void Mark(ulong id, long now, long forMs)
        {
            Entry e = Get(id, now);
            e.BusyUntil = now + forMs;
        }

        Entry Get(ulong id, long now)
        {
            Entry e;
            if (!accounts.TryGetValue(id, out e))
            {
                if (accounts.Count >= MaxAccounts) Prune(now);
                e = new Entry();
                accounts[id] = e;
            }
            e.Touched = now;
            return e;
        }

        // Entries with nothing left to remember go first; if all still matter, the least recently touched.
        void Prune(long now)
        {
            var idle = new List<ulong>();
            foreach (KeyValuePair<ulong, Entry> kv in accounts)
            {
                Entry e = kv.Value;
                if (now >= e.BusyUntil && now >= e.CoolUntil && e.Strikes.TrueForAll(t => now - t >= StrikeWindowMs))
                    idle.Add(kv.Key);
            }
            foreach (ulong id in idle) accounts.Remove(id);
            while (accounts.Count >= MaxAccounts)
            {
                ulong oldest = 0;
                long at = long.MaxValue;
                foreach (KeyValuePair<ulong, Entry> kv in accounts)
                    if (kv.Value.Touched < at) { at = kv.Value.Touched; oldest = kv.Key; }
                accounts.Remove(oldest);
            }
        }
    }
}
