using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // One row of an object's canonical print: two texts and three numbers, whatever the object keeps in them.
    public struct PrintRow { public string A; public string B; public long C; public long D; public long E; }

    // What the host and a client compare at the day boundary to learn that an object they both changed has come out
    // differently. Callers put in ids, counts, order and flags only: never progress, money or stock, which differ by a
    // fixed step between two games without the object being wrong.
    public static class ObjectPrint
    {
        public static string Of(int kind, string agency, int body, IList<PrintRow> rows)
        {
            ulong h = Fnv64.Offset;
            h = Fnv64.Long(h, kind);
            h = Fnv64.Text(h, agency);
            h = Fnv64.Long(h, body);
            // the row count and the length-prefixed texts keep a moved field or row boundary from hashing the same
            int n = rows == null ? 0 : rows.Count;
            h = Fnv64.Long(h, n);
            for (int i = 0; i < n; i++)
            {
                PrintRow r = rows[i];
                h = Fnv64.Text(h, r.A);
                h = Fnv64.Text(h, r.B);
                h = Fnv64.Long(h, r.C);
                h = Fnv64.Long(h, r.D);
                h = Fnv64.Long(h, r.E);
            }
            return h.ToString("x16");
        }
    }
}
