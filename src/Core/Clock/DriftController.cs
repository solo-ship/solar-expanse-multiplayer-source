using System;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    public enum ClockMode { Follow, CatchUp, Hold, Locked, NoRate, Ease }

    public struct ClockDecision
    {
        public float Scale;        // the speed to run at now
        public ClockMode Mode;
        public bool AskSlower;     // behind while the host already runs at the top speed: ask it for one step slower
        public double GapSeconds;  // behind (+) or ahead (-) of the host, in real seconds at the host's speed; NaN = unknown
        public bool Landing;       // landing on the host's date: set this speed every frame (ClockRules.ApplyNow)
        public string Note;        // a settle after a drop started or ended this step (for the log); null when not
        public bool Paced;         // following below the host's speed, at its pace (see DriftController.Pace)
    }

    // Keeps a client's date with the host's without ever setting the date (a jump skips day events and fires every
    // passed scheduled action at once, docs/research/clock.md): behind -> run faster, ahead -> pause.
    // Hysteresis (start correcting at 1 s, stop at 0.1 s) stops it flapping.
    // A landing (land: the host has just answered this client's own slow-down or pause) starts at 0.1 s instead and
    // runs at LandSeconds' pace: the client slowed first, and the game time the host ran meanwhile, small at the old
    // speed, is many seconds at the new one; at the ordinary 2 s pace the client kept moving 2-8 s after a pause.
    // The same holds for any drop of the host's speed: an offset in step at the old speed (at 200000 one of the
    // engine's fixed steps is already 2 s at 1x, and a client takes a drop a step or two after the host) is many
    // seconds at the new one. It is settled, not held: behind, the client lands; ahead, it eases (runs at EaseShare
    // of the host's speed) rather than stopping while the host runs, and holds only while the host is paused (a
    // client can never go back past a stopped host). The settle starts the step after the drop step, once this
    // client's own last old-speed step is in its date (ClockRules.SpeedStart), and ends once in step; an ease ends
    // anyway once the host has run the lead / (1 - EaseShare) plus SettleSlackSeconds, so it cannot hide a real lead,
    // and an offset already out of step at the old speed is real drift, left to the ordinary rules. Each settle's
    // start and end are said in ClockDecision.Note, which the client logs and clock-compare reads.
    public sealed class DriftController
    {
        public const float MaxScale = 200000f;
        // The unit of Clock.RatePer1x ("game seconds per real second at speed 1000"), not a listed speed: the game's
        // x1/5 is 5000 and nothing runs at 1000.
        public const float BaseScale = 1000f;
        // The step a client asks for when it is behind at the top speed: the list's second highest.
        public static float StepBelowMax { get { return ClockRules.Listed[ClockRules.Listed.Length - 2]; } }
        public const double EnterSeconds = 1.0, LeaveSeconds = 0.1, CatchUpSeconds = 2.0;
        // A landing speed asks to cover frameSeconds / LandSeconds of what is left per frame, and the speed running is
        // at most 1 / (1 - ClockRules.LandResetShare) of it (it is set again once 10% off), so a frame up to
        // MaxFrameSeconds (Unity's own cap on a frame's time step is a third of a second) covers at most
        // 0.4 / 0.9 / 0.56 = 79%: it never carries the client past a stopped host. No 1x floor while landing either.
        public const double LandSeconds = 0.56, MaxFrameSeconds = 0.4;
        // An ease runs at half the host's speed: the in-game 5.9 s lead at 1x is 12 s of half-speed play, not a 5.9 s
        // stop. Its window is what the lead needs plus SettleSlackSeconds of the host's running.
        public const double EaseShare = 0.5, SettleSlackSeconds = 1.0;
        // The longest landing from an offset in step at 200000 (1 s there, 200 s at 1x) into the 0.1 s band at 16 ms
        // frames (ClockDropEdgeTests): clock-compare bounds a landing window by it.
        public const double LandWithinSeconds = 6.0;
        // What a game that keeps up runs per real second at speed 1000 (M2: 5259, steady across PCs). The rate a
        // client assumes until the host's pace or measured rate is known, and the measure of a host that falls short.
        public const double DefaultRatePer1x = 5259;
        // Pacing (Pace): a host below PaceBelow of what a game that keeps up runs at its speed is followed at its pace,
        // until it is back above PaceUntil. PaceSeconds: the gap (real s at the host's pace) that moves the share
        // from the host's to the full speed; PaceFloor: the least share.
        public const double PaceBelow = 0.9, PaceUntil = 0.95, PaceSeconds = 0.5, PaceFloor = 0.1;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public ClockMode Mode { get; private set; }
        bool landing, paced;
        float lastHostScale = -1f;
        double graceBehind;    // game seconds behind left by the command day gate's hold, not caught up (Rebase)
        float graceScale;      // the host's speed the grace was given at

        // The command day gate has just released this game's fixed step: the host ran on while it was held, so the
        // client is behind by the hold. At the top speed no catch-up can close that, and asking the host to step down
        // for it would turn every gate hold into a speed vote. So that much is not counted as behind (the gap is
        // re-based) while the host keeps this speed: it shrinks as the client gains, and ends when the host's speed
        // changes (a pause then lands the client on the host's date as before). Below the top speed the ordinary
        // catch-up closes it, so no grace is given. The line for the log, or null.
        public string Rebase(double hostNow, double client, float hostScale)
        {
            if (hostScale < MaxScale) return null;
            double behind = hostNow - client;
            if (!(behind > 0)) return null;
            graceBehind = behind;
            graceScale = hostScale;
            return string.Format(CultureInfo.InvariantCulture,
                "gate grace: {0:0} game s behind after the day gate's hold, not counted while the host stays at {1:0}",
                behind, hostScale);
        }

        public double GraceBehind { get { return graceBehind; } }
        bool dropped;          // the host's speed dropped: the step after the drop step settles
        bool dropStep;         // the next measured step is the drop step
        float dropFrom;        // the fastest old speed of the drops since the last settle started
        int settle;            // +1 behind (landing), -1 ahead (easing), 0 none
        double settleUntil;    // ahead: the host's date at which the ease ends anyway

        // The offset the drop left, measured the step after the drop step (game seconds, host - client).
        void StartSettle(double hostNow, double client, double gap, float hostScale, double ratePer1x,
                         double refRate, ref ClockDecision d)
        {
            int was = settle;
            settle = 0;
            double game = hostNow - client;
            double atOld = Math.Abs(game) / (ratePer1x * Math.Max(dropFrom, BaseScale) / BaseScale);
            if (atOld > EnterSeconds)
            {
                if (was != 0) Say(ref d, "settle over (out of step before the drop)");
                return;
            }
            if (Math.Abs(gap) <= LeaveSeconds)
            {
                if (was != 0) Say(ref d, "settled (gap " + Fmt(gap) + " s)");
                return;
            }
            settle = gap > 0 ? 1 : -1;
            string bound;
            if (settle < 0)
            {
                settleUntil = hostNow + Math.Abs(game) / (1 - EaseShare) + refRate * SettleSlackSeconds;
                bound = string.Format(Inv, "until the host reaches {0:0.0}", settleUntil);
            }
            else bound = string.Format(Inv, "landing within {0:0.#} s", LandWithinSeconds);
            Say(ref d, string.Format(Inv, "settling after the host's drop from {0:0} to {1:0}: {2:0.0} game s {3} "
                + "({4:0.000} s at {0:0}), {5}", dropFrom, hostScale, Math.Abs(game), settle > 0 ? "behind" : "ahead",
                atOld, bound));
        }

        void CheckSettle(double hostNow, double gap, ref ClockDecision d)
        {
            if (settle > 0 ? gap <= LeaveSeconds : gap >= -LeaveSeconds)
            {
                settle = 0;
                Say(ref d, "settled (gap " + Fmt(gap) + " s)");
            }
            else if (settle < 0 && hostNow > settleUntil)
            {
                settle = 0;
                Say(ref d, "settle over (its window ended)");
            }
        }

        // The settle ends without a step that would say so (the host lost or silent, a load replacing this
        // controller): the line for the log, or null when none was running. A drop not yet measured is dropped too.
        public string EndSettle(string why)
        {
            dropped = dropStep = false;
            if (settle == 0) return null;
            settle = 0;
            return "settle over (" + why + ")";
        }

        static void Say(ref ClockDecision d, string note) { d.Note = d.Note == null ? note : d.Note + "; " + note; }

        static string Fmt(double gap) { return gap.ToString("+0.000;-0.000;0.000", Inv); }

        // Where the host's date is now, from its last Clock message: ageMs since it was sent (time since it arrived
        // plus half the round trip).
        public static double PredictHost(double game, float scale, double ratePer1x, long ageMs)
        {
            if (scale <= 0 || !(ratePer1x > 0) || ageMs <= 0) return game;
            return game + ratePer1x * scale / BaseScale * ageMs / 1000.0;
        }

        // The part of `behind` (game seconds) the gate's grace covers this step: none once the host's speed changed;
        // never more than the client is behind now, so whatever the client gains is not given back.
        double Grace(double behind, float hostScale, ref ClockDecision d)
        {
            if (graceBehind <= 0) return 0;
            if (hostScale != graceScale)
            {
                graceBehind = 0;
                Say(ref d, "gate grace over (the host's speed changed)");
                return 0;
            }
            graceBehind = Math.Max(0, Math.Min(graceBehind, behind));
            return graceBehind;
        }

        // The rate a client follows with: the host's pace from its Clock stream (HostPace), else the rate the host
        // measured, else DefaultRatePer1x. Never 0, so a joiner has a gap from its first step in game.
        public static double FollowRate(double paceRatePer1x, double sentRatePer1x)
        {
            if (PaceKnown(paceRatePer1x)) return paceRatePer1x;
            return sentRatePer1x > 0 ? sentRatePer1x : DefaultRatePer1x;
        }

        // A pace far beyond any game's (the host loaded a later date) is not believed, as RateMeter does.
        public static bool PaceKnown(double paceRatePer1x)
        {
            return paceRatePer1x > 0 && paceRatePer1x <= DefaultRatePer1x * RateMeter.MaxJumpFactor;
        }

        // The engine rate Decide paces against: only while the host's own pace is known, never on the rate it
        // measured (that lags: after a drop from a host short at 200000 it would pace a host that keeps up).
        public static double PaceAgainst(double paceRatePer1x)
        {
            return PaceKnown(paceRatePer1x) ? DefaultRatePer1x : 0;
        }

        // A host whose frames cannot keep up (at the top speed, above all in its first ~20 s after a join) runs short
        // of its speed. Copying its speed, this client would run ahead and the command day gate would stop it at
        // nearly every day (the first two-device session: 105 holds of 15-770 ms at 200000, a stutter). So while the
        // host runs below PaceBelow of enginePer1x, Follow runs at its share of the host's speed, corrected by the gap,
        // and this game keeps the host's pace. The share comes and goes with a note for the log.
        void Pace(float hostScale, double ratePer1x, double enginePer1x, double gap, ref ClockDecision d)
        {
            double share = enginePer1x > 0 && hostScale > 0 ? ratePer1x / enginePer1x : double.NaN;
            bool was = paced;
            if (double.IsNaN(share)) paced = false;
            else if (share < PaceBelow) paced = true;
            else if (share > PaceUntil) paced = false;
            if (paced && !was)
                Say(ref d, string.Format(Inv, "pacing: the host runs at {0:0}% of its speed {1:0} - following at its "
                    + "pace", share * 100, hostScale));
            else if (was && !paced) Say(ref d, "pacing over");
            if (!paced || Mode != ClockMode.Follow) return;
            d.Scale = (float)(hostScale * Math.Min(1, Math.Max(PaceFloor, share + gap / PaceSeconds)));
            d.Paced = true;
        }

        // hostNow, client: game seconds. hostScale: host speed (0 = paused). ratePer1x: game seconds per real second
        // at speed 1000 (0 = not measured yet: just copy the host's speed). enginePer1x: what a game that keeps up
        // runs at speed 1000 (0 = no pacing, see Pace).
        public ClockDecision Decide(double hostNow, double client, float hostScale, double ratePer1x, bool locked,
                                    bool land = false, double enginePer1x = 0)
        {
            var d = new ClockDecision { GapSeconds = double.NaN };
            float before = lastHostScale;
            lastHostScale = hostScale;
            // Before the lock check: the client copies the host's speed while locked, so a drop then leaves the same
            // offset once the lock ends, and the first step after the lock is the drop step.
            if (hostScale < before && ratePer1x > 0)
            {
                dropFrom = dropped || settle != 0 ? Math.Max(dropFrom, before) : before;
                dropped = dropStep = true;
            }
            if (locked) { Mode = ClockMode.Locked; d.Mode = Mode; d.Scale = hostScale; return d; }
            if (!(ratePer1x > 0)) { Mode = ClockMode.NoRate; d.Mode = Mode; d.Scale = hostScale; return d; }
            if (Mode == ClockMode.Locked || Mode == ClockMode.NoRate) Mode = ClockMode.Follow;

            // Real seconds of difference at the host's speed (at 1x while the host is paused).
            double refRate = ratePer1x * Math.Max(hostScale, BaseScale) / BaseScale;
            double gap = (hostNow - client - Grace(hostNow - client, hostScale, ref d)) / refRate;
            d.GapSeconds = gap;
            // The drop step takes the host's new speed; the next one measures, with this client's own last old-speed
            // step then in its date.
            bool atDrop = dropStep;
            dropStep = false;
            if (dropped && !atDrop)
            {
                dropped = false;
                StartSettle(hostNow, client, gap, hostScale, ratePer1x, refRate, ref d);
            }
            else if (settle != 0) CheckSettle(hostNow, gap, ref d);

            if (settle > 0) { Mode = ClockMode.CatchUp; landing = true; }
            else if (settle < 0) Mode = hostScale > 0 ? ClockMode.Ease : ClockMode.Hold;
            else
            {
                if (Mode == ClockMode.Ease) Mode = ClockMode.Follow;
                switch (Mode)
                {
                    case ClockMode.Follow:
                        if (gap > EnterSeconds || (land && gap > LeaveSeconds))
                        {
                            Mode = ClockMode.CatchUp;
                            landing = land;
                        }
                        else if (gap < -EnterSeconds && !atDrop) Mode = ClockMode.Hold;
                        break;
                    case ClockMode.CatchUp:
                        if (gap <= LeaveSeconds) Mode = ClockMode.Follow;
                        else if (land) landing = true;
                        break;
                    case ClockMode.Hold:
                        if (gap >= -LeaveSeconds) Mode = ClockMode.Follow;
                        break;
                }
            }
            d.Mode = Mode;
            if (Mode == ClockMode.Hold) d.Scale = 0f;
            else if (Mode == ClockMode.Ease) d.Scale = (float)(hostScale * EaseShare);
            else if (Mode == ClockMode.CatchUp)
            {
                // Close the gap in about the pace's seconds: running at s, the gap (real seconds at the host's speed,
                // or at 1x while it is paused) shrinks by (s - hostScale) / max(hostScale, 1x) s per second.
                double pace = landing ? LandSeconds : CatchUpSeconds;
                double s = hostScale + Math.Max(hostScale, BaseScale) * gap / pace;
                double floor = landing ? hostScale : Math.Max(hostScale, BaseScale);
                d.Scale = (float)Math.Min(MaxScale, Math.Max(s, floor));
                d.Landing = landing;
                // Under a second behind while landing is its own slow-down, not a machine too slow for 200000.
                d.AskSlower = hostScale >= MaxScale && (!landing || gap > EnterSeconds);
            }
            else d.Scale = hostScale;
            Pace(hostScale, ratePer1x, enginePer1x, gap, ref d);
            return d;
        }
    }
}
