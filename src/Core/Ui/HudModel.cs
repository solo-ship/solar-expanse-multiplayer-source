using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // One name under a speed button: a player (Me: the "You" chip), or the "+N" chip that stands for the rest of a
    // crowded button (More > 0, no player).
    public sealed class HudChip
    {
        public int Player = -1; public bool Me; public string Name; public int More;
    }

    // What the in-game strip under the speed buttons shows (DD10, DD15, DD17), worked out from the session view only:
    // each loaded player's choice under the button chosen, the slowest running choice lit, the pause reasons with
    // names, the speed play runs (or resumes) at and whose choice it is, one status line, a joiner's progress and, for
    // the host, Carry on for a dropped player. Every line comes from the text table; a line that holds a name is peer
    // text, so the screen puts it on with PeerText. The game side builds and lays it out (SpeedHud).
    public sealed class HudModel
    {
        public const int Columns = 6;
        public const int MaxChips = 3;   // a button with more players shows MaxChips - 1 names and "+N"

        // The game's six speeds (DD10: the Economic asset's 0, 5000, 25000, 50000, 100000, 200000); the screen reads
        // the live ones from the game and passes them in.
        public static readonly float[] DefaultScales = { 0f, 5000f, 25000f, 50000f, 100000f, 200000f };

        // The game's own labels for the six buttons.
        static readonly string[] SpeedKeys =
            { "hud.speed.0", "hud.speed.1", "hud.speed.2", "hud.speed.3", "hud.speed.4", "hud.speed.5" };

        public bool Show;                     // the strip is on screen
        public readonly List<HudChip>[] Chips = new List<HudChip>[Columns];
        public int Lit = -1;                  // the column of the slowest running choice, -1 for none
        public string PauseLine;              // "Paused: Sam is planning a mission (1:12)." or null
        public string PauseHead, PauseRest;   // the same line in two parts: "Paused:" (in the game's warning
                                              // colour on screen) and "Sam is planning a mission (1:12)."
        public string RunLine;                // "Running at x2: <name>'s choice, the slowest." or null
        public string RunLead, RunWho;        // the same line around the lit button's own image: "Running at",
                                              // then "<name>'s choice, the slowest."
        public string SlowestCaption;         // under the lit column, or null
        public string LockLine;               // the end-game speed lock, or null
        public string StatusText;             // the one status line (StatusLine.Pick), or null
        public int ProgressPct = -1;          // a joiner's progress, 0-100, or -1 for no bar
        public int CarryOnPlayer = -1;        // the host's Carry on for this dropped player, or -1
        public string CarryOnText, CarryOnHint;

        public HudModel()
        {
            for (int i = 0; i < Columns; i++) Chips[i] = new List<HudChip>();
        }

        // Everything the strip shows as one string: the screen redraws only when this changes.
        public string Signature()
        {
            var sb = new StringBuilder(ChipSignature());
            foreach (string s in new[] { PauseLine, PauseHead, PauseRest, RunLine, RunLead, RunWho, SlowestCaption,
                                         LockLine, StatusText, CarryOnText, CarryOnHint })
                sb.Append('\n').Append(s ?? "\u0001");
            sb.Append('\n').Append(Show).Append(Lit).Append(',').Append(ProgressPct).Append(',').Append(CarryOnPlayer);
            return sb.ToString();
        }

        // Drawn on screen: the model says show, and neither the pause menu nor a mod dialog covers the game.
        public static bool Drawn(HudModel m, bool pauseMenuOpen, bool dialogOpen)
        {
            return m != null && m.Show && !pauseMenuOpen && !dialogOpen;
        }

        // The chips' layout as one string: the screen rebuilds its chips only when this changes.
        public string ChipSignature()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Columns; i++)
            {
                sb.Append('|');
                foreach (HudChip c in Chips[i]) sb.Append(c.Player).Append(c.Me ? "m" : "").Append('+').Append(c.More)
                    .Append(':').Append(c.Name).Append(',');
            }
            return sb.ToString();
        }

        // The button a speed belongs under: 0 for paused, else the fastest button not faster than the speed, and at
        // least the first running one (a speed between two buttons sits under the slower; M4 snaps x1/5 to 1000, which
        // still shows under x1/5). scales: the six speeds in the panel's order; DefaultScales when it is not six.
        public static int Column(float scale, IList<float> scales)
        {
            if (scales == null || scales.Count != Columns) scales = DefaultScales;
            if (float.IsNaN(scale) || scale <= 0f) return 0;
            int col = 1;
            for (int i = 1; i < Columns; i++) if (scales[i] <= scale) col = i;
            return col;
        }

        public static string SpeedLabel(int column)
        {
            return column >= 0 && column < Columns ? UiText.Get(SpeedKeys[column]) : "";
        }

        // "1:12": a pause's age, minutes and seconds (hours fold into the minutes).
        public static string Age(long sinceMs, long nowMs)
        {
            long s = Math.Max(0L, nowMs - sinceMs) / 1000L;
            return (s / 60L).ToString(CultureInfo.InvariantCulture) + ":" +
                   (s % 60L).ToString("00", CultureInfo.InvariantCulture);
        }

        // hover: the pointer is on the speed buttons or the strip (DD10: the strip shows any time then).
        public static HudModel From(SessionView v, Capability caps, bool hover, long nowMs, IList<float> scales)
        {
            var m = new HudModel();
            if (v == null || v.Speed == null) return m;
            SpeedMarks s = v.Speed;
            bool paused = s.InEffect <= 0f;

            // the chips: one per loaded player's choice, under its button, slowest button first
            var running = new List<int>();   // players with a running choice
            var pausers = new List<int>();   // players whose choice is the pause button
            float slowest = float.MaxValue;
            var byColumn = new List<int>[Columns];
            for (int i = 0; i < Columns; i++) byColumn[i] = new List<int>();
            int distinct = 0;
            foreach (SpeedCluster c in s.Marks)
            {
                if (c == null || c.Players == null) continue;
                int col = Column(c.Scale, scales);
                foreach (int p in c.Players) byColumn[col].Add(p);
                if (c.Scale > 0f)
                {
                    distinct++;
                    running.AddRange(c.Players);
                    if (c.Scale < slowest) slowest = c.Scale;
                }
                else pausers.AddRange(c.Players);
            }
            for (int i = 0; i < Columns; i++) FillColumn(m.Chips[i], byColumn[i], v);
            if (running.Count > 0) m.Lit = Column(slowest, scales);

            // the pause reasons: the session's own reasons first, then each plain pause choice
            var reasons = new List<string>();
            foreach (PauseLine p in v.Pauses)
                if (p != null) reasons.Add(Reason(p, v, nowMs));
            pausers.Sort();
            foreach (int p in pausers)
                reasons.Add(IsMe(v, p) ? UiText.Get("hud.why.youPaused") : UiText.Get("hud.why.paused", NameOf(v, p)));
            if (paused)
            {
                string joined = string.Join(UiText.Get("hud.paused.sep"), reasons.ToArray());
                m.PauseLine = reasons.Count > 0 ? UiText.Get("hud.paused", joined) : UiText.Get("hud.paused.plain");
                m.PauseHead = reasons.Count > 0 ? UiText.Get("hud.paused.head") : UiText.Get("hud.paused.plain");
                m.PauseRest = reasons.Count > 0 ? UiText.Get("hud.paused.rest", joined) : null;
            }

            // the speed play runs at, and whose choice set it
            if (running.Count > 0)
            {
                var setters = new List<int>();
                foreach (SpeedCluster c in s.Marks)
                    if (c != null && c.Players != null && c.Scale == slowest) setters.AddRange(c.Players);
                setters.Sort();
                string who = Who(v, setters, running.Count + pausers.Count, distinct > 1);
                string speed = SpeedLabel(m.Lit);
                m.RunLine = paused ? UiText.Get("hud.run.resume", speed, who) : UiText.Get("hud.run.now", speed, who);
                m.RunLead = paused ? UiText.Get("hud.run.leadResume") : UiText.Get("hud.run.leadNow");
                m.RunWho = who;
                m.SlowestCaption = UiText.Get("hud.chip.slowest");
            }
            if (v.ClockLocked) m.LockLine = UiText.Get("hud.locked");

            // one status line: the view's own, then this player's link
            var candidates = new List<StatusLine>();
            if (v.Status != null) candidates.Add(v.Status);
            PlayerRow me = v.Row(v.MyId);
            if (me != null && me.State == PlayerRow.Dropped) candidates.Add(StatusLine.Make(StatusLine.Dropped));
            if (me != null && me.State == PlayerRow.Rejoining) candidates.Add(StatusLine.Make(StatusLine.Rejoining));
            StatusLine st = StatusLine.Pick(candidates);
            if (st != null) m.StatusText = UiText.Get(st.Key, st.Args);

            // a joiner's progress bar: the first joining player with a known share
            foreach (PauseLine p in v.Pauses)
            {
                if (p == null || p.Why != PauseWhy.Joining) continue;
                PlayerRow r = v.Row(p.Player);
                if (r != null && r.JoinPct >= 0) { m.ProgressPct = Math.Min(100, r.JoinPct); break; }
            }

            // the host carries on without the first dropped player (DD15), when the session can
            if (v.IsHost && CapabilityRules.Has(caps, Capability.CarryOn))
                foreach (PauseLine p in v.Pauses)
                {
                    if (p == null || p.Why != PauseWhy.Dropped) continue;
                    string n = NameOf(v, p.Player);
                    m.CarryOnPlayer = p.Player;
                    m.CarryOnText = UiText.Get("hud.carryOn", n);
                    m.CarryOnHint = UiText.Get("hud.carryOn.hint", n);
                    break;
                }

            m.Show = s.ShowStrip || hover || m.StatusText != null || m.PauseLine != null;
            return m;
        }

        static void FillColumn(List<HudChip> chips, List<int> players, SessionView v)
        {
            if (players.Count == 0) return;
            players.Sort((a, b) => IsMe(v, a) != IsMe(v, b) ? (IsMe(v, a) ? -1 : 1) : a.CompareTo(b));
            int named = players.Count > MaxChips ? MaxChips - 1 : players.Count;
            for (int i = 0; i < named; i++)
            {
                bool me = IsMe(v, players[i]);
                chips.Add(new HudChip
                {
                    Player = players[i], Me = me, Name = me ? UiText.Get("hud.chip.you") : NameOf(v, players[i]),
                });
            }
            int rest = players.Count - named;
            if (rest > 0) chips.Add(new HudChip { More = rest, Name = UiText.Get("hud.chip.more", rest) });
        }

        static string Reason(PauseLine p, SessionView v, long nowMs)
        {
            bool me = IsMe(v, p.Player);
            string n = NameOf(v, p.Player);
            switch (p.Why)
            {
                case PauseWhy.Planning:
                    string age = Age(p.SinceMs, nowMs);
                    return me ? UiText.Get("hud.why.youPlanning", age) : UiText.Get("hud.why.planning", n, age);
                case PauseWhy.Dropped:
                    return UiText.Get("hud.why.dropped", n);
                case PauseWhy.Joining:
                    PlayerRow r = v.Row(p.Player);
                    int pct = r != null ? r.JoinPct : -1, secs = r != null ? r.JoinSecsLeft : -1;
                    if (me) return pct >= 0 ? UiText.Get("hud.why.youJoiningPct", Math.Min(100, pct))
                        : UiText.Get("hud.why.youJoining");
                    if (pct >= 0 && secs >= 0) return UiText.Get("hud.why.joiningEta", n, Math.Min(100, pct), secs);
                    if (pct >= 0) return UiText.Get("hud.why.joiningPct", n, Math.Min(100, pct));
                    return UiText.Get("hud.why.joining", n);
                default:
                    return UiText.Get("hud.why.other", n);
            }
        }

        // Whose choice the speed is: everyone's, yours, one player's, or two or more players' (the slowest when
        // the choices differ).
        static string Who(SessionView v, List<int> setters, int voters, bool differ)
        {
            if (setters.Count == voters && voters > 1) return UiText.Get("hud.who.everyone");
            if (setters.Count == 1)
            {
                if (IsMe(v, setters[0])) return UiText.Get(differ ? "hud.who.youSlowest" : "hud.who.you");
                string n = NameOf(v, setters[0]);
                return differ ? UiText.Get("hud.who.oneSlowest", n) : UiText.Get("hud.who.one", n);
            }
            // you first, then by id
            setters.Sort((a, b) => IsMe(v, a) != IsMe(v, b) ? (IsMe(v, a) ? -1 : 1) : a.CompareTo(b));
            string first = IsMe(v, setters[0]) ? UiText.Get("hud.who.youName") : NameOf(v, setters[0]);
            string second = NameOf(v, setters[1]);
            if (setters.Count == 2)
                return differ ? UiText.Get("hud.who.twoSlowest", first, second)
                    : UiText.Get("hud.who.two", first, second);
            int more = setters.Count - 2;
            return differ ? UiText.Get("hud.who.manySlowest", first, second, more)
                : UiText.Get("hud.who.many", first, second, more);
        }

        static bool IsMe(SessionView v, int player)
        {
            PlayerRow r = v.Row(player);
            return r != null ? r.Me : player == v.MyId && v.MyId >= 0;
        }

        // A player's name as the view knows it; "Player N" for one it has no row for (the view's unknown name).
        public static string NameOf(SessionView v, int player)
        {
            PlayerRow r = v != null ? v.Row(player) : null;
            return r != null && !string.IsNullOrEmpty(r.Name) ? r.Name : PlayerRow.Unknown(player).Name;
        }
    }
}
