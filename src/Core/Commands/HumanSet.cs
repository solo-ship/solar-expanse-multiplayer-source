using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The agencies people play (M5 Task 8). Owned by the session and kept across every load (the saved world does not
    // carry it); its only writer is the seat entries' apply (ActionApply.cs, pinned by a source test), so every game
    // changes it at the same point of the stream. The rules read it. Main thread only.
    public sealed class HumanSet
    {
        readonly HashSet<string> agencies = new HashSet<string>(StringComparer.Ordinal);

        // The host's own agency (host) or JoinHeader.HumanAgencies (joiner); an empty list is a set nobody plays in.
        public HumanSet(IEnumerable<string> agencies)
        {
            if (agencies == null) throw new ArgumentNullException("agencies");
            foreach (string a in agencies) this.agencies.Add(Checked(a));
        }

        // Fired after the set changed, so a listener reading All() sees the new set (the "rules: human agencies" line).
        public event Action Changed;

        public bool Contains(string agency) { return !string.IsNullOrEmpty(agency) && agencies.Contains(agency); }

        // Sorted (ordinal) and a copy: the header and AgencyState carry the same order on every game.
        public string[] All()
        {
            var list = new List<string>(agencies);
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }

        // SeatClaimed's apply only.
        public void Add(string agency)
        {
            if (agencies.Add(Checked(agency)) && Changed != null) Changed();
        }

        // SeatReleased's apply only.
        public void Remove(string agency)
        {
            if (agencies.Remove(Checked(agency)) && Changed != null) Changed();
        }

        static string Checked(string agency)
        {
            if (string.IsNullOrEmpty(agency)) throw new ArgumentException("an agency id is needed", "agency");
            return agency;
        }
    }

    // The decisions the rule patches make, apart from the game types, so "with no session every rule is the game's own"
    // is a test and not a reading. `active` is "a multiplayer session is running with the rules on"; `human` is "the
    // agency is in the HumanSet". Each patch asks its own question here after its own early return.
    public static class HumanRules
    {
        // S1 and S3: the game serves only the local player's agency; in a session every human agency is served too.
        public static bool SkipsAgency(bool active, bool isLocalPlayer, bool human)
        {
            return !isLocalPlayer && !(active && human);
        }

        // S2: who pays for a queued item. The game: the local player. A session: a human agency pays for its own queue.
        public static bool OwnerPays(bool active, bool human) { return active && human; }

        // S4 and S5: whose research slot and global build speed count. The game: the local player's. A session: each
        // agency's own, for every agency, so the two games compute one number.
        public static bool OwnBonus(bool active) { return active; }

        // S6: who ends an agency whose money ran out. The game: the local player ends the game, any other agency is
        // switched off by the game's own branch. A session: a human agency is stopped by the host's entry only.
        public static bool StopsOnBankruptcy(bool active, bool human) { return active && human; }

        // S7 and S8: a human agency gets no +Infinity money and no AI, whichever load or switch asks. The one thing the
        // game does for a human that stays is the player change that switches the player's own AI off.
        public static bool SkipsAiOrMoney(bool active, bool human, bool playerChangeOfThePlayer)
        {
            return active && human && !playerChangeOfThePlayer;
        }
    }

    // DD11's client hold, apart from the game types: ProductionItem.UpdateBuilding adds each frame's progress to one
    // unit of a build and calls Build() when that unit reaches 1. A client keeps a craft's unit short of 1 until the
    // host's entry arrives (docs/research/commands.md, Q4), using the game's own choice of unit and its own sum.
    public static class CraftHold
    {
        // Just short of done: a unit at this value has not finished, whatever the next frame adds.
        public const float HeldProgress = 0.9999f;

        // The unit the game's UpdateBuilding adds this frame's progress to: units already finished push the index
        // along; an index that is then a unit still being built is the one, otherwise the first unit not finished.
        // -1 when every unit is finished.
        public static int NextUnit(IList<float> units, int indexBuildingUpdate)
        {
            int index = indexBuildingUpdate;
            for (int i = 0; i < units.Count && units[i] >= 1f; i++) index++;
            if (index < units.Count) return index;
            for (int j = 0; j < units.Count; j++) if (units[j] < 1f) return j;
            return -1;
        }

        // Would this frame's progress finish the unit (the game's `progress >= 1` after adding and capping at 1)?
        public static bool WouldFinish(float progress, double daysTimeDelta, float timeToBuildInDays,
            float constructionPower)
        {
            // the game keeps the progress as a float: the sum is rounded to one before it is compared
            return (float)(progress + (float)(daysTimeDelta / (double)timeToBuildInDays) * constructionPower) >= 1f;
        }

        // A client's hold decision for one UpdateBuilding call of an item it holds (a craft or a facility, started,
        // not built, with an owner and a site): the unit to keep at HeldProgress, or -1 to let the game's update run.
        // A list the game has not made yet is the game's to make on this call (its one unit starts at the item's
        // progress, which no single frame takes from 0 to 1).
        public static int HoldUnit(IList<float> units, int indexBuildingUpdate, double daysTimeDelta,
            float timeToBuildInDays, float constructionPower)
        {
            if (units == null || units.Count == 0) return -1;
            int unit = NextUnit(units, indexBuildingUpdate);
            if (unit < 0) return -1;
            return WouldFinish(units[unit], daysTimeDelta, timeToBuildInDays, constructionPower) ? unit : -1;
        }

        // Which of a site's items an entry names. Item ids repeat at a site: a save's first facilities carry ids the
        // agency's counter hands out again, so a new build can share its id with a finished facility. So the content
        // id must match too, and an unfinished item wins over a finished one (the held build is the unfinished one).
        // -1 when nothing matches.
        public static int Pick(IList<SiteItem> items, long itemId, string content)
        {
            int built = -1;
            for (int i = 0; items != null && i < items.Count; i++)
            {
                SiteItem s = items[i];
                if (s.Id != itemId || !string.Equals(s.Content, content, StringComparison.Ordinal)) continue;
                if (!s.Built) return i;
                if (built < 0) built = i;
            }
            return built;
        }
    }

    // One production item at a site, as CraftHold.Pick compares it.
    public struct SiteItem
    {
        public long Id;
        public string Content;
        public bool Built;

        public SiteItem(long id, string content, bool built) { Id = id; Content = content; Built = built; }
    }
}
