using System;
using System.Collections.Generic;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    // One save of the Multiplayer saves folder as the menu lists it (DD24): from the folder's index, never from the
    // save itself. Title and HostName are peer text (another player may have named them): cleaned, shown through
    // PeerText only.
    public sealed class MpSaveRow
    {
        public string Name;                   // the save's name in the folder (the actions take it)
        public string Title, HostName;        // peer text
        public string GameDate;               // the game's own date text, or null
        public string Difficulty;             // the game's difficulty id (Explorer, Pioneer, Veteran), or null
        public long LastPlayedUtcTicks;       // 0 = not known
        public List<AgencyRow> Agencies = new List<AgencyRow>();
    }

    // The Multiplayer saves the menu can list, and their delete. The full track's session (Tasks 9 and 15) offers
    // it beside ISessionActions; until then only the screens' test adapter does, and the list stays behind its
    // capabilities (MpSaves, SoloPlay).
    public interface IMpSaves
    {
        IList<MpSaveRow> MpSaves { get; }
        string DeleteMpSave(string name);     // null = deleted; else a UiText key
    }

    public static class MpSaveText
    {
        // "NASA, ESA (players); Solex (AI)": the humans, then the AI rivals, then the idle agencies; agencies not in
        // the game are left out. nameOf turns a wire id into the game's own agency name.
        public static string Agencies(MpSaveRow row, Func<string, string> nameOf)
        {
            if (row == null || row.Agencies == null) return "";
            var parts = new List<string>();
            AddGroup(parts, row, AgencyStanding.Human, "menu.saves.players", nameOf);
            AddGroup(parts, row, AgencyStanding.AiRival, "menu.saves.ai", nameOf);
            AddGroup(parts, row, AgencyStanding.Idle, "menu.saves.idle", nameOf);
            return string.Join("; ", parts.ToArray());
        }

        static void AddGroup(List<string> parts, MpSaveRow row, AgencyStanding state, string key,
                             Func<string, string> nameOf)
        {
            var names = new List<string>();
            foreach (AgencyRow a in row.Agencies)
                if (a != null && a.State == state && !string.IsNullOrEmpty(a.Id))
                    names.Add(Named(a.Id, nameOf));
            if (names.Count > 0) parts.Add(UiText.Get(key, string.Join(", ", names.ToArray())));
        }

        // The game's name for the agency, else the id itself: the id comes from the folder's index (another
        // player's copy), so it is cleaned like any peer text first.
        static string Named(string id, Func<string, string> nameOf)
        {
            string n = null;
            try { n = nameOf != null ? nameOf(id) : null; } catch (Exception) { }
            return string.IsNullOrEmpty(n) ? TextSanitiser.Clean(id, MaxIdChars, "?").ToUpperInvariant() : n;
        }

        const int MaxIdChars = 32, MaxDateChars = 32;

        // The save's game date from the index, cleaned like any peer text ("" when there is none).
        public static string Date(MpSaveRow row)
        {
            return row == null ? "" : TextSanitiser.Clean(row.GameDate, MaxDateChars, "");
        }

        // How long ago the save was last played, by calendar day in the player's own time: today, yesterday, so many
        // days ago (under a week), a week ago (under two), so many weeks ago (under five weeks), else the date. Empty
        // when not known or in the future (a clock set back).
        public static string Age(long thenUtcTicks, long nowUtcTicks, TimeSpan utcOffset)
        {
            if (thenUtcTicks <= 0 || thenUtcTicks > nowUtcTicks) return "";
            DateTime then = new DateTime(thenUtcTicks, DateTimeKind.Utc) + utcOffset;
            DateTime now = new DateTime(nowUtcTicks, DateTimeKind.Utc) + utcOffset;
            int days = (int)(now.Date - then.Date).TotalDays;
            if (days <= 0) return UiText.Get("menu.saves.today");
            if (days == 1) return UiText.Get("menu.saves.yesterday");
            if (days < 7) return UiText.Get("menu.saves.days", days);
            if (days < 14) return UiText.Get("menu.saves.oneWeek");
            if (days < 35) return UiText.Get("menu.saves.weeks", days / 7);
            return then.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        }

        // The agencies the solo picker offers (DD24 step 1): every agency in the game in that save (a former human
        // seat, an AI rival or idle) that can be played; never one not in the game, and never the World Government
        // (which is never playable).
        public static List<string> SoloOffer(MpSaveRow row)
        {
            var ids = new List<string>();
            if (row == null || row.Agencies == null) return ids;
            foreach (AgencyRow a in row.Agencies)
            {
                if (a == null || !a.Playable || string.IsNullOrEmpty(a.Id) || ids.Contains(a.Id)) continue;
                if (a.State == AgencyStanding.Human || a.State == AgencyStanding.AiRival || a.State == AgencyStanding.Idle)
                    ids.Add(a.Id);
            }
            return ids;
        }
    }
}
