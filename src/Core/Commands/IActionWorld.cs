using System;
using System.Collections.Generic;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The numbers one apply made: the host decides them and every client applies the entry with them. The observatory
    // draws, the after-state, a new system's name and the asteroid spawns travel like the counters.
    public class ApplyNumbers
    {
        public CounterRow[] Counters;
        public long[] NewIds;
        public ObsDraw[] Draws;
        public ObsAfter After;
        public string SystemName;
        public AsteroidSpawn[] Spawns;
        public AsteroidPull Pull;   // AsteroidPulled only (M6): the low orbit's id and the two display values
        // Only on a client applying an impact's ObjectRemoved (CommandClient.ApplyImpactNow): the game's own landing
        // handler, which the world runs inside the apply, under the entry's spawn and draw replay. null = it ran;
        // else why it did not. A world that never calls it, or whose branch says no, puts the client out of step.
        public Func<string> RunBranch;
    }

    // M7 (protocol 6): the numbers of an entry that carries market ops, rival steps or an objective chain. A subclass, so
    // an ordinary entry's numbers are no bigger than before (a client makes one for every entry it applies).
    public sealed class WorldNumbers : ApplyNumbers
    {
        public MarketOp[] Ops;
        public AiStep[] Steps;
        public ChainRow[] Chain;
    }

    // What applying one request did, on the host (numbers made) or on a client (numbers given).
    public sealed class ApplyOutcome
    {
        public int Code;                 // 0 = applied; else RefuseCodes (host) or why it could not (client)
        public ActionRequest Applied;    // normalised: R3 expanded, finished topics left out
        public ApplyNumbers Numbers;
        public bool PaymentFailed;       // a client's payment did not go through: out of step
        public IList<PrintEntry> Touched;   // the objects to print at the day's end (kind, agency, body; Print empty)
        // M6: the apply goes on over frames: on a client the load path's creation; on the host an accepted flight whose
        // own copy is still being created (DD-F2, DD-F3)
        public bool Deferred;
        public int Token;                // host: the world's handle for it, answered through CommandService.DeferredDone
    }

    // The game as the drivers see it (the host's CommandService and every client's CommandClient). The game side
    // implements it; the tests use a fake.
    public interface IActionWorld
    {
        long OriginTicks { get; }
        long NextDayTicks { get; }
        long GameTicks { get; }
        bool GameOver { get; }
        string OwnerOf(ActionRequest a);            // the agency owning the target; null = no such target
        int Check(ActionRequest a);                 // host: the windows' checks for the acting agency; 0 or RefuseCodes
        double MoneyFor(string agency);             // host: SeatClaimed's amount (DD8)
        // given is null on the host; the host's numbers on clients. The host's driver validates what it gets and sends
        // it later: the outcome, its numbers and its Touched list belong to the driver from the return, and the game
        // must not change them afterwards (a reused buffer would change what is sent). A client hands Apply a new
        // ApplyNumbers for each entry, valid for the call only.
        ApplyOutcome Apply(ActionRequest a, ApplyNumbers given);
        // may throw: the client then goes out of step
        string PrintOf(int kind, string agency, int body);
        // host; Code set when it cannot. A fresh copy the driver keeps until it is fetched or ages out.
        ObjectData Read(int kind, string agency, int body);
        string Restore(ObjectData d);                               // client: null = restored; else out of step
    }

    // Counter rows the drivers build themselves.
    public static class CounterRows
    {
        // 24 four-byte characters: 96 UTF-8 bytes, the longest content id in its widest encoding
        internal static readonly string WideId = Repeat("\U0001F600", 24);

        // The widest counter rows an entry for this agency can carry, for the encode-before-apply check: a name
        // counter with the longest key and the widest numbers is the widest kind of row.
        public static CounterRow[] Worst(string agency, int count)
        {
            var rows = new CounterRow[count];
            for (int i = 0; i < count; i++)
                rows[i] = new CounterRow { Which = CounterKind.NameCounter, Agency = agency, Key = WideId,
                    Before = int.MaxValue, After = int.MaxValue };
            return rows;
        }

        static string Repeat(string s, int n)
        {
            var sb = new StringBuilder(s.Length * n);
            for (int i = 0; i < n; i++) sb.Append(s);
            return sb.ToString();
        }
    }

    // How the host cuts a printed object into the pieces ObjectData carries (R5-4): at most MaxRowsPerPiece rows of
    // builds and queue together, the counters in the first piece only, at most MaxPieces pieces.
    public static class ObjectPieces
    {
        // The pieces of d, in order, or null when the object is over its row caps or cannot be cut into MaxPieces
        // (the host then answers TooBig). Day and AfterSeq are the caller's.
        public static ObjectData[] Cut(ObjectData d)
        {
            if (d == null || d.Code != ObjectCodes.Ok) return null;
            if (d.Kind == ObjectKind.Research) return d.Research == null ? null : new[] { Piece(d, 0, 1) };
            if (d.Kind == ObjectKind.SiteFacilities) return CutFacilities(d);
            return CutBuilds(d);
        }

        static ObjectData Piece(ObjectData from, int index, int count)
        {
            return new ObjectData { Code = ObjectCodes.Ok, Day = from.Day, AfterSeq = from.AfterSeq, Piece = index,
                Pieces = count, Kind = from.Kind, Agency = from.Agency, Body = from.Body, Research = from.Research };
        }

        static int PiecesFor(int rows)
        {
            return Math.Max(1, (rows + ActionLimits.MaxRowsPerPiece - 1) / ActionLimits.MaxRowsPerPiece);
        }

        static ObjectData[] CutFacilities(ObjectData d)
        {
            SiteFacilityState s = d.Facilities;
            if (s == null) return null;
            FacilityRow[] built = s.Built ?? new FacilityRow[0];
            if (built.Length > ActionLimits.MaxBuiltRows) return null;
            int n = PiecesFor(built.Length);
            if (n > ActionLimits.MaxPieces) return null;
            var pieces = new ObjectData[n];
            for (int i = 0; i < n; i++)
            {
                int from = i * ActionLimits.MaxRowsPerPiece;
                var rows = new FacilityRow[Math.Min(ActionLimits.MaxRowsPerPiece, built.Length - from)];
                Array.Copy(built, from, rows, 0, rows.Length);
                pieces[i] = Piece(d, i, n);
                pieces[i].Facilities = new SiteFacilityState { Built = rows, Mining = i == 0 ? s.Mining : null };
            }
            return pieces;
        }

        static ObjectData[] CutBuilds(ObjectData d)
        {
            SiteBuildState s = d.Builds;
            if (s == null) return null;
            BuildRow[] building = s.Building ?? new BuildRow[0];
            QueueRow[] queue = s.Queue ?? new QueueRow[0];
            if (building.Length > ActionLimits.MaxBuildRows || queue.Length > ActionLimits.MaxQueueRows) return null;
            int rows = building.Length + queue.Length;
            int n = PiecesFor(rows);
            if (n > ActionLimits.MaxPieces) return null;
            var pieces = new ObjectData[n];
            for (int i = 0; i < n; i++)
            {
                // a row number counts the building rows first, then the queue's
                int from = i * ActionLimits.MaxRowsPerPiece, to = Math.Min(rows, from + ActionLimits.MaxRowsPerPiece);
                var b = new List<BuildRow>();
                var q = new List<QueueRow>();
                for (int r = from; r < to; r++)
                {
                    if (r < building.Length) b.Add(building[r]);
                    else q.Add(queue[r - building.Length]);
                }
                pieces[i] = Piece(d, i, n);
                pieces[i].Builds = new SiteBuildState { Counters = i == 0 ? s.Counters : null, Building = b.ToArray(),
                    Queue = q.ToArray() };
            }
            return pieces;
        }
    }
}
