using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace SolarExpanseMultiplayer
{
    // <mod>\data\player\seats\<save-id>.json: who sat where in this save (by key hash, never the key) and who is banned. Plain JSON, read
    // with a size cap; a damaged file is moved aside and the list starts empty. The path is built from a checked save id only, never from
    // a network string (PathFor).
    public sealed class SeatsFile
    {
        public const int MaxBytes = 256 * 1024, MaxSeats = 256, MaxBans = 256;

        public sealed class Seat { public string Agency, Name, KeyHash; }
        sealed class Doc { public int Version = 1; public List<Seat> Seats = new List<Seat>(); public List<string> Banned = new List<string>(); }

        readonly List<Seat> seats = new List<Seat>();         // oldest first
        readonly List<string> bans = new List<string>();

        public IList<Seat> Seats { get { return seats; } }
        public IList<string> Banned { get { return bans; } }

        // The one place a seats path comes from: null for anything but a 16 hex save id.
        public static string PathFor(string seatsDir, string saveId)
        {
            if (string.IsNullOrEmpty(seatsDir) || !SaveNames.IsSaveId(saveId)) return null;
            return Path.Combine(seatsDir, saveId + ".json");
        }

        static bool SeatOk(Seat s)
        {
            return s != null && LobbyIds.IsContentId(s.Agency) && PlayerKeys.IsHex64(s.KeyHash)
                && s.Name != null && s.Name.Length <= PlayerNames.MaxChars;
        }

        // A missing file is an empty list (problem null). Over the cap, unparseable or holding ANY invalid entry: moved aside as
        // <name>.bad-<time>, empty result and the reason. Half-trusting a planted file could ban or seat the wrong player.
        public static SeatsFile Read(string path, out string problem)
        {
            problem = null;
            var f = new SeatsFile();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return f;
            string why = null;
            try
            {
                if (new FileInfo(path).Length > MaxBytes) why = "the seats file is over its size cap";
                else
                {
                    Doc d = JsonConvert.DeserializeObject<Doc>(File.ReadAllText(path, Encoding.UTF8));
                    if (d == null || d.Seats == null || d.Banned == null || d.Seats.Count > MaxSeats || d.Banned.Count > MaxBans) why = "the seats file is not valid";
                    else
                    {
                        foreach (Seat s in d.Seats) if (!SeatOk(s)) { why = "a seat in the seats file is not valid"; break; }
                        foreach (string b in d.Banned) if (!PlayerKeys.IsHex64(b)) { why = "a ban in the seats file is not valid"; break; }
                        if (why == null) { f.seats.AddRange(d.Seats); f.bans.AddRange(d.Banned); }
                    }
                }
            }
            catch (Exception e) { why = "the seats file could not be read (" + e.GetType().Name + ")"; }
            if (why == null) return f;
            f.seats.Clear(); f.bans.Clear();
            string aside = AtomicFile.MoveAside(path);
            problem = why + (aside != null ? "; moved aside as " + Path.GetFileName(aside) : "; it could not be moved aside");
            return f;
        }

        // One remembered seat per key hash (the newest wins); the oldest go when the cap is reached. Bad input is ignored.
        public void Remember(string agency, string name, string keyHash)
        {
            if (!LobbyIds.IsContentId(agency) || !PlayerKeys.IsHex64(keyHash)) return;
            seats.RemoveAll(s => PlayerKeys.Same(s.KeyHash, keyHash));
            seats.Add(new Seat { Agency = agency, Name = PlayerNames.Clean(name), KeyHash = keyHash });
            while (seats.Count > MaxSeats) seats.RemoveAt(0);
        }

        public void Forget(string keyHash)
        {
            seats.RemoveAll(s => PlayerKeys.Same(s.KeyHash, keyHash));
        }

        public string KeptAgency(string keyHash)
        {
            foreach (Seat s in seats) if (PlayerKeys.Same(s.KeyHash, keyHash)) return s.Agency;
            return null;
        }

        // The name of the newest player remembered on that agency, or null.
        public string LastHolder(string agency)
        {
            for (int i = seats.Count - 1; i >= 0; i--) if (seats[i].Agency == agency) return seats[i].Name;
            return null;
        }

        public void Ban(string keyHash)
        {
            if (!PlayerKeys.IsHex64(keyHash) || IsBanned(keyHash)) return;
            bans.Add(keyHash);
            while (bans.Count > MaxBans) bans.RemoveAt(0);
            Forget(keyHash);
        }

        public bool IsBanned(string keyHash)
        {
            foreach (string b in bans) if (PlayerKeys.Same(b, keyHash)) return true;
            return false;
        }

        // null = ok, else why not. Written through a temp file.
        public string Write(string path)
        {
            if (string.IsNullOrEmpty(path)) return "no path";
            var d = new Doc { Seats = seats, Banned = bans };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(d, Formatting.Indented));
            if (bytes.Length > MaxBytes) return "the seats file would be over its size cap";
            return AtomicFile.Write(path, bytes);
        }
    }
}
