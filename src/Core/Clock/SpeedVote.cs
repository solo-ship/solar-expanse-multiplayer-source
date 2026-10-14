using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The slowest requested speed wins; pause (0) is a request like any other. Player 0 is the host.
    // A player who never asked has no vote; MaxScale means "any speed is fine".
    //
    // Three things pause everyone on top of the requests, each with a reason the other players are shown:
    //  - the mission planner: while a player has it open they are "planning a mission" and the game is paused; closing
    //    it takes only that pause away (the session then runs at the slowest remaining request). Several players can be
    //    planning at once, each named, and the pause lifts when the last of them closes. One player's several planner
    //    windows are counted on that player's own side (PlannerCount), which sends a single flag. The player's own speed
    //    request is untouched.
    //  - the drop rule (DECISIONS.md, M2 scope choices): when a player drops, the game pauses once and a "drop hold"
    //    keeps it paused until a player chooses a speed (a click or hotkey: Set, or Choose with fromPlayer). A call the
    //    game makes itself (a popup, the tutorial) is still that player's vote but leaves the hold alone (Choose with
    //    !fromPlayer, Restore). Drop is never "remove the vote and look again": that would resume the game the moment a
    //    player who had asked for a pause dropped.
    //  - a join (M4): every player in the batch being joined is "joining" from the batch's start until it ends, one pause
    //    per joiner. Lifting one keeps the others; a drop hold neither ends it nor is ended by it; a player's own speed
    //    choice does not lift it. Only the join service sets and lifts it (a joiner who drops or fails before it has
    //    loaded leaves the batch with a notice and never causes a drop hold).
    public sealed class SpeedVote
    {
        readonly SortedDictionary<int, float> votes = new SortedDictionary<int, float>();
        readonly Dictionary<int, int> seqs = new Dictionary<int, int>();   // the last request acted on, per player
        readonly SortedSet<int> planning = new SortedSet<int>();
        readonly SortedSet<int> joining = new SortedSet<int>();
        readonly SortedSet<int> catching = new SortedSet<int>();
        bool dropHold;
        int dropped;

        // A speed the player chose (a click or hotkey), or the host's own start-up speed: sets the vote and ends a drop
        // hold. Game-made calls and rejoins go through Choose / Restore instead and leave a hold alone.
        public void Set(int player, float scale) { votes[player] = scale; dropHold = false; }

        // A counted speed choice. Only one made by the player (a click or hotkey: fromPlayer) may end a drop hold; a call
        // the game makes itself (a popup, the tutorial) is still this player's vote but leaves the hold alone.
        public void Choose(int player, float scale, bool fromPlayer)
        {
            if (fromPlayer) Set(player, scale); else Restore(player, scale);
        }

        // Puts a player's request back without counting as a new choice: a rejoining player's earlier speed, resent when
        // the link is made again. Unlike Set it leaves a drop hold alone (a rejoin must never end it).
        public void Restore(int player, float scale) { votes[player] = scale; }

        // Takes a vote back without pausing anything (no caller in M2; a player who is still there but has no preference).
        public void Remove(int player) { votes.Remove(player); }

        // ClockHost.Act once a request is admitted and classified (not Ignore): its Seq goes back in the Clock as the
        // answer, for a restore too. A rejoin from a new process starts its Seq again at 1 while this still holds the
        // old process's last one: echoing that would answer every new slow-down at once. Then a new choice sets the
        // vote (ending a drop hold only if the player made it), anything else puts it back; the planner flag follows.
        public void Take(int player, SpeedRequest r, RequestKind kind)
        {
            seqs[player] = r.Seq;
            if (kind == RequestKind.NewChoice) Choose(player, r.Scale, r.Player);
            else if (!Has(player, r.Scale)) Restore(player, r.Scale);
            SetPlanning(player, r.Planning);
        }

        // A player dropped: their vote goes and everybody is paused until someone asks for a speed.
        public void Drop(int player) { votes.Remove(player); planning.Remove(player); dropHold = true; dropped = player; }

        // The host pressed Carry on: the drop hold ends without any player picking a speed. Their vote stays as it was
        // (the dropped player's is already gone); the session then runs at the slowest remaining request.
        public void CarryOn() { dropHold = false; }

        // A dropped player is back in the running game (a full or quick rejoin finished). Whether that alone ends their
        // drop hold is DropRules.RejoinLiftsDropHold; the other overload lets a test try both. Another player's hold stays.
        public void Rejoined(int player) { Rejoined(player, DropRules.RejoinLiftsDropHold); }

        public void Rejoined(int player, bool lifts)
        {
            if (lifts && dropHold && dropped == player) dropHold = false;
        }

        // A player left for good (not a drop): every hold and request of theirs goes, so they cannot keep the speed down.
        // If the drop hold was theirs it ends too: nobody is coming back to resume it.
        public void Leave(int player)
        {
            votes.Remove(player);
            seqs.Remove(player);
            planning.Remove(player);
            joining.Remove(player);
            catching.Remove(player);
            if (dropHold && dropped == player) dropHold = false;
        }

        // A player opened (true) or closed (false) the mission planner. Does not touch the drop hold or their request.
        public void SetPlanning(int player, bool open)
        {
            if (open) planning.Add(player); else planning.Remove(player);
        }

        public bool IsPlanning(int player) { return planning.Contains(player); }

        // A player joined the batch being joined (true) or its batch ended for them (false: loaded, failed or gone). Does
        // not touch the drop hold, the planners or the player's vote.
        public void SetJoining(int player, bool on)
        {
            if (on) joining.Add(player); else joining.Remove(player);
        }

        public bool IsJoining(int player) { return joining.Contains(player); }

        // A dropped player's game is catching up on the host's replay (M7 DD-W12): everyone is paused, named, until the
        // host releases it. No speed choice ends it. Turning it on replaces that player's own drop hold in the same
        // call, so the session never runs between the two; another player's drop hold stays.
        public void SetCatchingUp(int player, bool on)
        {
            if (on)
            {
                catching.Add(player);
                if (dropHold && dropped == player) dropHold = false;
            }
            else catching.Remove(player);
        }

        public bool IsCatchingUp(int player) { return catching.Contains(player); }

        // Who is holding the game paused beyond plain requests, and why (planners first, then joiners, then a drop).
        public Pause[] Pauses()
        {
            var list = new List<Pause>();
            foreach (int p in planning) list.Add(new Pause { Player = p, Reason = PauseWhy.Planning });
            foreach (int p in joining) list.Add(new Pause { Player = p, Reason = PauseWhy.Joining });
            foreach (int p in catching) list.Add(new Pause { Player = p, Reason = PauseWhy.CatchingUp });
            if (dropHold) list.Add(new Pause { Player = dropped, Reason = PauseWhy.Dropped });
            return list.ToArray();
        }

        // True when this player's current vote is exactly this speed.
        public bool Has(int player, float scale) { float v; return votes.TryGetValue(player, out v) && v == scale; }

        public bool DropHold { get { return dropHold; } }

        // -1 when nobody has asked for anything; 0 while a planner is open, a joiner is joining or a drop hold is on.
        public float Effective()
        {
            if (dropHold || planning.Count > 0 || joining.Count > 0 || catching.Count > 0) return 0f;
            float min = -1f;
            foreach (float v in votes.Values) if (min < 0 || v < min) min = v;
            return min;
        }

        public Vote[] Snapshot()
        {
            var list = new List<Vote>();
            foreach (var kv in votes)
            {
                int seq;
                if (!seqs.TryGetValue(kv.Key, out seq)) seq = 0;
                list.Add(new Vote { Player = kv.Key, Scale = kv.Value, Seq = seq });
            }
            return list.ToArray();
        }

        // A client's vote as the host last reported it (MaxScale when it has none).
        public static float VoteOf(Vote[] votes, int player)
        {
            if (votes != null) foreach (Vote v in votes) if (v != null && v.Player == player) return v.Scale;
            return Clock.MaxScale;
        }

        // The last of this client's requests the host acted on, as its last Clock said (-1 when it lists no vote).
        public static int SeqOf(Vote[] votes, int player)
        {
            if (votes != null) foreach (Vote v in votes) if (v != null && v.Player == player) return v.Seq;
            return -1;
        }

        // A client while its request is on the way to the host: never faster than what its player just asked for.
        public static float ClientCap(float target, float ownRequest, float ackedRequest)
        {
            if (ownRequest == ackedRequest) return target;
            return ownRequest < target ? ownRequest : target;
        }
    }
}
