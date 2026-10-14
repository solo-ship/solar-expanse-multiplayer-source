using System;
using System.Collections.Generic;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // One card under the game's New Notifications tab (DD11): a notice of the queue, or the "+N more" card.
    public sealed class NoticeCard
    {
        public int Id = -1;                  // the queue's id (Dismiss); -1 for the "+N more" card
        public string Key; public object[] Args = new object[0];
        public bool Warning;                 // stays until dismissed; drawn with the game's own warning look
        public int CarryOnPlayer = -1;       // the host may carry on without this dropped player (a click asks)
        public int More;                     // the "+N more" card: how many are not on screen
        public string Agency;                // the named player's agency (its emblem on the card); null when unknown
        public int ProgressPct = -1, SecsLeft = -1;   // a joining player's download, from the view; -1 = none

        public string Text()
        {
            if (Key == JoiningKey && ProgressPct >= 0 && SecsLeft >= 0 && Args.Length > 0)
                return UiText.Get(JoiningEtaKey, Args[0], ProgressPct, SecsLeft);
            return UiText.Get(Key, Args);
        }

        public const string JoiningKey = "join.notice.joining", JoiningEtaKey = "hud.why.joiningEta";
    }

    // Which notices are on screen, from NoticeQueue.Visible and HiddenWarnings alone. The queue's order (oldest first)
    // is kept; when more are live than fit, the newest stay and one "+N more" card counts the rest, the warnings the
    // queue holds back included. A drop notice ("notice.dropped", arguments: the name, then the player's id) lets the
    // host carry on without that player while the player is still dropped (DD15), behind the CarryOn capability.
    public static class NoticeCards
    {
        public const string DroppedKey = "notice.dropped";
        public const string MoreKey = "notice.more";
        public const string CarryOnKey = "notice.carryOn";

        public static List<NoticeCard> Plan(IList<NoticeItem> visible, int hiddenWarnings, int maxCards,
                                            SessionView view, Capability caps)
        {
            var cards = new List<NoticeCard>();
            if (maxCards < 1) return cards;
            int count = visible != null ? visible.Count : 0;
            int hidden = Math.Max(0, hiddenWarnings);
            int room = count + (hidden > 0 ? 1 : 0) > maxCards ? maxCards - 1 : count;
            int skip = count - room;
            for (int i = skip; i < count; i++)
            {
                NoticeItem n = visible[i];
                if (n == null) continue;
                var c = new NoticeCard
                {
                    Id = n.Id, Key = n.Key, Args = n.Args ?? new object[0], Warning = n.Kind == NoticeQueue.Warning,
                };
                c.CarryOnPlayer = CarryOnFor(n, view, caps);
                PlayerRow who = PlayerOf(n, view);
                if (who != null)
                {
                    c.Agency = string.IsNullOrEmpty(who.Agency) ? null : who.Agency;
                    if (n.Key == NoticeCard.JoiningKey && who.State == PlayerRow.Joining && who.JoinPct >= 0)
                    {
                        c.ProgressPct = Math.Min(100, who.JoinPct);
                        c.SecsLeft = who.JoinSecsLeft;
                    }
                }
                cards.Add(c);
            }
            int more = skip + hidden;
            if (more > 0) cards.Add(new NoticeCard { Key = MoreKey, Args = new object[] { more }, More = more });
            return cards;
        }

        // The player a notice names: by the id a drop notice carries, else by its name when exactly one player has
        // it (the view's names are peer text, compared as they are); null when none or several.
        public static PlayerRow PlayerOf(NoticeItem n, SessionView view)
        {
            if (n == null || view == null || n.Args == null || n.Args.Length == 0) return null;
            if (n.Key == DroppedKey && n.Args.Length >= 2 && n.Args[1] is int) return view.Row((int)n.Args[1]);
            string name = n.Args[0] as string;
            if (string.IsNullOrEmpty(name)) return null;
            PlayerRow found = null;
            foreach (PlayerRow r in view.Players)
            {
                if (r == null || r.Name != name) continue;
                if (found != null) return null;
                found = r;
            }
            return found;
        }

        // The dropped player a drop notice names, while the host may carry on without them; else -1.
        public static int CarryOnFor(NoticeItem n, SessionView view, Capability caps)
        {
            if (n == null || n.Key != DroppedKey || n.Args == null || n.Args.Length < 2) return -1;
            if (!(n.Args[1] is int)) return -1;
            int player = (int)n.Args[1];
            return DialogRules.CarryOn(view, caps, player) != null ? player : -1;
        }

        // What a card says: its line, and for a carry-on card the hint that a click asks.
        public static string Text(NoticeCard c)
        {
            if (c == null) return "";
            string line = c.Text();
            if (c.CarryOnPlayer < 0 || c.Args.Length == 0) return line;
            return line + " " + UiText.Get(CarryOnKey, c.Args[0]);
        }

        // The cards as one string: the screen rebuilds only when it changes.
        public static string Signature(IList<NoticeCard> cards)
        {
            var sb = new StringBuilder();
            foreach (NoticeCard c in cards)
                sb.Append(c.Id).Append(c.Warning ? "w" : "r").Append(c.CarryOnPlayer).Append(c.Agency)
                  .Append(c.ProgressPct).Append(':').Append(Text(c))
                  .Append('\n');
            return sb.ToString();
        }
    }
}
