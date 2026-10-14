using System;

namespace SolarExpanseMultiplayer
{
    // The kept copies of printed objects and the fetches that ask for them (DD10): the part of CommandService that
    // answers an out-of-step client's ObjectRequest.
    public sealed partial class CommandService
    {
        // The host's copy of one printed object as of a day's close. Reading it must happen at the close (the game
        // moves on), but cutting it into pieces and encoding them waits for a fetch: most days nobody asks.
        sealed class Kept
        {
            public long Day; public int Kind; public string Agency; public int Body;
            public uint LastSeq;
            public int Code;            // ObjectCodes; Ok while the copy can still be sent
            public ObjectData Data;     // the raw copy, until it is cut
            public Item[] Pieces;       // cut and encoded on the first fetch
            public long Bytes;          // what the copy counts against MaxKeptCopyBytes: the most its pieces can be
        }

        // The widest JSON a row of builds, queue or facilities takes (a full piece of 160 rows is 53,004 bytes), and
        // what an object's other fields take at the most (research is 28,806 bytes with no rows at all).
        const int RowBytes = 340, SiteFixedBytes = 2 * 1024, ResearchBytes = 30 * 1024;

        void KeepCopy(long day, PrintEntry t)
        {
            var k = new Kept { Day = day, Kind = t.Kind, Agency = t.Agency, Body = t.Body, LastSeq = seq };
            ObjectData d = world.Read(t.Kind, t.Agency, t.Body);
            if (d == null) k.Code = ObjectCodes.NoSuchObject;
            else if (d.Code != ObjectCodes.Ok)
                k.Code = d.Code >= ObjectCodes.TooBig && d.Code <= ObjectCodes.NoSuchObject
                    ? d.Code : ObjectCodes.TooBig;
            else
            {
                d.Kind = t.Kind;   // what it is cut as is what was asked for
                k.Data = d;
                k.Bytes = (d.Kind == ObjectKind.Research ? ResearchBytes : SiteFixedBytes) + (long)RowBytes * Rows(d);
            }
            kept.Add(k);
            keptBytes += k.Bytes;
        }

        static long Rows(ObjectData d)
        {
            long n = 0;
            if (d.Builds != null)
            {
                if (d.Builds.Building != null) n += d.Builds.Building.Length;
                if (d.Builds.Queue != null) n += d.Builds.Queue.Length;
            }
            if (d.Facilities != null)
            {
                if (d.Facilities.Built != null) n += d.Facilities.Built.Length;
                if (d.Facilities.Mining != null) n += d.Facilities.Mining.Length;   // a key is far under a row
            }
            return n;
        }

        // The pieces, addressed and stamped, each checked and encoded; null when the copy cannot be sent (too big, or
        // not a valid object): the client then rejoins fully, which heals it through the snapshot.
        Item[] PiecesOf(Kept k)
        {
            if (k.Pieces != null || k.Data == null) return k.Pieces;
            ObjectData[] cut = ObjectPieces.Cut(k.Data);
            k.Data = null;
            var items = cut == null ? null : new Item[cut.Length];
            for (int i = 0; items != null && i < cut.Length; i++)
            {
                ObjectData p = cut[i];
                p.Day = k.Day;
                p.AfterSeq = k.LastSeq;
                p.Kind = k.Kind;
                p.Agency = k.Agency;
                p.Body = k.Body;
                string why;
                items[i] = Encode(p, out why);
                if (items[i] == null)
                {
                    Log.Warn("command: object " + k.Kind + " " + k.Agency + " " + k.Body + " cannot be sent: " + why);
                    items = null;
                }
            }
            if (items == null) k.Code = ObjectCodes.TooBig;
            k.Pieces = items;
            return items;
        }

        void TrimKept(long day)
        {
            while (kept.Count > 0 && (kept[0].Day <= day - ActionLimits.KeptCopyDays
                || keptBytes > ActionLimits.MaxKeptCopyBytes))
            {
                keptBytes -= kept[0].Bytes;
                kept.RemoveAt(0);
            }
        }

        // The copy, or why not (TooBig, TooMany, NoSuchObject), to a player in the stream: object data goes to the
        // stream's recipients only (I9). The pieces are paced apart from the stream, at ObjectBytesPerMinute, and the
        // queue of them is capped.
        public void ObjectWanted(int player, ObjectRequest r)
        {
            if (r == null || r.Validate() != null) return;
            Player p = PlayerOf(player);
            if (!p.Recipient)
            {
                if (!p.OutsideLogged) Log.Info("command: object request from player " + player + " outside the stream");
                p.OutsideLogged = true;
                return;
            }
            if (LosesFetch(player, r)) return;
            long now = nowMs();
            Kept k = null;
            Item[] pieces = null;
            int code = ObjectCodes.TooMany;
            if (MayAsk(p, now))
            {
                k = FindKept(r);
                code = k == null ? ObjectCodes.NoSuchObject : k.Code;
                if (code == ObjectCodes.Ok)
                {
                    pieces = PiecesOf(k);
                    code = pieces == null ? k.Code : ObjectCodes.Ok;
                }
                if (code == ObjectCodes.Ok && p.Objects.Count + pieces.Length > MaxObjectQueue)
                    code = ObjectCodes.TooMany;
            }
            if (code != ObjectCodes.Ok && p.FetchRefused)
            {
                // the client is already out of step and will rejoin: one answer told it, the rest cost nothing
                if (p.FetchesIgnored++ == 0)
                    Log.Info("command: player " + player + " keeps fetching after a refused fetch; ignored");
                return;
            }
            Log.Info("command: object " + r.Kind + " " + r.Agency + " " + r.Body + " for player " + player + ": "
                + code);
            if (code != ObjectCodes.Ok)
            {
                p.FetchRefused = true;
                SendMinor(p, new ObjectData { Code = code, Day = r.Day, AfterSeq = k == null ? 0 : k.LastSeq,
                    Piece = 0, Pieces = 1, Kind = r.Kind, Agency = r.Agency, Body = r.Body });
                return;
            }
            for (int i = 0; i < pieces.Length; i++) p.Objects.Enqueue(pieces[i]);
            PumpPlayer(p, now);
        }

        // At most ObjectRequestsPerMinute fetches a minute from one player, kept across its links.
        static bool MayAsk(Player p, long now)
        {
            int n = p.Asked.Length;
            if (p.AskedCount < n)
            {
                p.Asked[(p.AskedHead + p.AskedCount) % n] = now;
                p.AskedCount++;
                return true;
            }
            if (now - p.Asked[p.AskedHead] < AskWindowMs) return false;
            p.Asked[p.AskedHead] = now;
            p.AskedHead = (p.AskedHead + 1) % n;
            return true;
        }

        Kept FindKept(ObjectRequest r)
        {
            for (int i = kept.Count - 1; i >= 0; i--)
            {
                Kept k = kept[i];
                if (k.Day == r.Day && k.Kind == r.Kind && k.Body == r.Body && k.Agency == r.Agency) return k;
            }
            return null;
        }
    }
}
