using System.Collections.Generic;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // One item that differs between two content scans (DefinitionHasher.Changes).
    public sealed class ItemChange
    {
        public const int Changed = 0, OnlyBefore = 1, OnlyNow = 2;
        public int Category;
        public string Id;
        public int Kind;
        public string[] Fields;   // for a change: the top-level fields whose bytes differ
    }

    // The log text naming what changed when the content after a load is not the same as at the menu, so one run says
    // which definition and field moved instead of only a category hash. Bounded: the first maxItems items, each with
    // at most maxFields fields; ids and field names cleaned and cut, as they may come from a content mod.
    public static class ContentChanges
    {
        const int MaxIdChars = 64, MaxFieldChars = 40;

        public static string Text(IList<ItemChange> changes, int maxItems, int maxFields)
        {
            if (changes == null || changes.Count == 0) return "";
            var sb = new StringBuilder();
            int shown = 0, lastCategory = -1;
            foreach (ItemChange c in changes)
            {
                if (shown == maxItems) break;
                if (c.Category != lastCategory)
                {
                    if (lastCategory >= 0) sb.Append("; ");
                    sb.Append(ContentCategory.Name(c.Category).ToLowerInvariant()).Append(": ");
                    lastCategory = c.Category;
                }
                else sb.Append(", ");
                sb.Append(Cut(c.Id, MaxIdChars));
                if (c.Kind == ItemChange.OnlyBefore) sb.Append(" (only before)");
                else if (c.Kind == ItemChange.OnlyNow) sb.Append(" (only now)");
                else if (c.Fields != null && c.Fields.Length > 0)
                {
                    sb.Append(" (");
                    for (int f = 0; f < c.Fields.Length && f < maxFields; f++)
                        sb.Append(f > 0 ? ", " : "").Append(Cut(c.Fields[f], MaxFieldChars));
                    if (c.Fields.Length > maxFields) sb.Append(", ...");
                    sb.Append(")");
                }
                shown++;
            }
            if (changes.Count > shown) sb.Append("; ... and ").Append(changes.Count - shown).Append(" more");
            return sb.ToString();
        }

        static string Cut(string s, int max)
        {
            string c = TextSanitiser.Clean(s ?? "", 4 * max, "");
            if (c.Length == 0) return "?";
            if (c.Length <= max) return c;
            int cut = char.IsHighSurrogate(c[max - 1]) ? max - 1 : max;
            return c.Substring(0, cut) + "...";
        }
    }
}
