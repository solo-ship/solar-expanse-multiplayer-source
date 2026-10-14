using System;

namespace SolarExpanseMultiplayer
{
    // Quick-rejoin state fingerprints and the decision they feed (Design decision 16; docs/research/join.md S12).
    //
    // A quick rejoin resumes without a reload only if time stood still for everyone and neither game changed while paused.
    // Each side takes M3's fingerprint (one StateReader.Visit into an AreaHasher, keyed from the host's OriginTicks) when its drop
    // pause has settled, and again when the offer is made or answered:
    //   - FingerprintRules.Same is each side's own check (R-1): the two prints must be identical at EVERY level in every block,
    //     which catches anything that changed while time stood still;
    //   - FingerprintRules.Compare is the cross-check between the two games: Count and Discrete in every block, plus the Discrete
    //     day hash, at the same day boundary.
    // RejoinRules.Decide turns the facts into a code: Quick, or the FIRST condition that failed.
    //
    // FAIL-SAFE, by construction. Every uncertain input ends in a full rejoin (a reload from a fresh snapshot, the safe outcome):
    // a missing, malformed or over-long print, a capped block, a mismatched origin or block list, an unknown day boundary, a drop
    // record that does not make sense. Nothing here may make a quick rejoin MORE likely when the evidence is thin.
    //
    // S12 (Task 1): the cross-check fails after the first year event of an UNSEEDED session, at any game speed, because the year
    // event draws from UnityEngine.Random in both games, which diverge (population, market and the World Government's money and
    // stock). Before that event it passes in every trial. So in an unseeded session a quick rejoin is refused once the first
    // year event has passed and the player does a full rejoin. That is the safe direction and the rules stay as they are: they
    // may get stricter (strict, from Task 13's measurements), never looser without the user. In a session whose games are
    // seeded alike (-semp-random-seed) the games do not diverge and the cross-check holds.

    // A filled DaySnapshot -> the wire Fingerprint. Pure: the glue calls it after one visit, on both sides.
    public static class FingerprintBuild
    {
        // null when no fingerprint can be made: no snapshot, no origin, no blocks at all (a print of nothing proves nothing: two of
        // them would match, and I1 would send a quick rejoin on unverified state), or more blocks than a RejoinOffer may carry
        // (JoinLimits.MaxFingerprintBlocks). Null means "no print", which RejoinRules answers with a full rejoin.
        public static Fingerprint From(DaySnapshot s, long originTicks)
        {
            if (s == null || originTicks < 1 || s.Blocks == null || s.BlockCount < 1 || s.BlockCount > JoinLimits.MaxFingerprintBlocks || s.BlockCount > s.Blocks.Length)
                return null;
            var blocks = new FingerprintBlock[s.BlockCount];
            ulong discrete = Fnv64.Offset, rounded = Fnv64.Offset, exact = Fnv64.Offset;
            for (int i = 0; i < blocks.Length; i++)
            {
                BlockSum b = s.Blocks[i];
                blocks[i] = new FingerprintBlock
                {
                    Area = b.Area, Agency = b.Agency, Count = b.Count,
                    Exact = Hex(b.Exact), Rounded = Hex(b.Rounded), Discrete = Hex(b.Discrete), Capped = b.Capped != 0,
                };
                discrete = Fnv64.Long(discrete, (long)b.Discrete);
                rounded = Fnv64.Long(rounded, (long)b.Rounded);
                exact = Fnv64.Long(exact, (long)b.Exact);
            }
            return new Fingerprint { OriginTicks = originTicks, DayDiscrete = Hex(discrete), DayRounded = Hex(rounded), DayExact = Hex(exact), Blocks = blocks };
        }

        static string Hex(ulong v) { return v.ToString("x16"); }
    }

    // The cross-check's result. Level names the first decisive failure: malformed, origin, blocks, layout, capped, Count, Discrete,
    // DayDiscrete (or, only when strict, Rounded, Exact, DayRounded, DayExact); null on a match. FirstArea and FirstAgency name the
    // block (FirstAgency is DriftAreas.WorldAgency, -1, for a world area) and are -1 and -1 when the failure is not in one block.
    // DiscreteDiffers counts blocks whose Count or Discrete differ; RoundedDiffers and ExactDiffers count blocks too.
    public sealed class FingerprintDiff
    {
        public bool Match;
        public int FirstArea = -1, FirstAgency = -1;
        public string Level;
        public int DiscreteDiffers, RoundedDiffers, ExactDiffers;
    }

    public static class FingerprintRules
    {
        // A print that is present, within the wire's caps, well formed (the same rules a received RejoinOffer passes) and holds at
        // least one block (I1: an empty print is no evidence, so it is never well formed).
        public static bool WellFormed(Fingerprint f)
        {
            return f != null && f.Blocks != null && f.Blocks.Length >= 1 && f.Validate() == null;
        }

        // The cross-check between the games (Design decision 16 (d)): same origin, the same block list, Count and Discrete equal
        // in every block and DayDiscrete equal; a Capped block never matches. Rounded and Exact differences are counted, not
        // decisive, unless strict is set (only the orchestrator may set it, from S12 and Task 13): M3 showed those levels differ
        // through the sampling phase alone at a pause edge (drift.md sections 5 and 9). The decisive failure reported is the first
        // block (in block order) that is capped or differs in Count or Discrete; the strict-only levels never outrank it.
        public static FingerprintDiff Compare(Fingerprint host, Fingerprint client, bool strict)
        {
            var d = new FingerprintDiff();
            if (!WellFormed(host) || !WellFormed(client)) return Fail(d, "malformed");
            if (host.OriginTicks != client.OriginTicks) return Fail(d, "origin");
            if (host.Blocks.Length != client.Blocks.Length) return Fail(d, "blocks");
            for (int i = 0; i < host.Blocks.Length; i++)
            {
                FingerprintBlock h = host.Blocks[i], c = client.Blocks[i];
                if (h.Area != c.Area || h.Agency != c.Agency)
                {
                    d.FirstArea = h.Area; d.FirstAgency = h.Agency;
                    return Fail(d, "layout");
                }
            }

            string level = null, strictLevel = null;
            int area = -1, agency = -1, strictArea = -1, strictAgency = -1;
            for (int i = 0; i < host.Blocks.Length; i++)
            {
                FingerprintBlock h = host.Blocks[i], c = client.Blocks[i];
                string here = null;
                if (h.Capped || c.Capped) here = "capped";
                else if (h.Count != c.Count) here = "Count";
                else if (h.Discrete != c.Discrete) here = "Discrete";
                if (h.Count != c.Count || h.Discrete != c.Discrete) d.DiscreteDiffers++;
                if (here != null && level == null) { level = here; area = h.Area; agency = h.Agency; }
                bool rounded = h.Rounded != c.Rounded, exact = h.Exact != c.Exact;
                if (rounded) d.RoundedDiffers++;
                if (exact) d.ExactDiffers++;
                if ((rounded || exact) && strictLevel == null) { strictLevel = rounded ? "Rounded" : "Exact"; strictArea = h.Area; strictAgency = h.Agency; }
            }
            if (level != null) { d.FirstArea = area; d.FirstAgency = agency; return Fail(d, level); }
            if (host.DayDiscrete != client.DayDiscrete) return Fail(d, "DayDiscrete");
            if (strict)
            {
                if (strictLevel != null) { d.FirstArea = strictArea; d.FirstAgency = strictAgency; return Fail(d, strictLevel); }
                if (host.DayRounded != client.DayRounded) return Fail(d, "DayRounded");
                if (host.DayExact != client.DayExact) return Fail(d, "DayExact");
            }
            d.Match = true;
            return d;
        }

        static FingerprintDiff Fail(FingerprintDiff d, string level)
        {
            d.Match = false;
            d.Level = level;
            return d;
        }

        // The self-check of one side (R-1): the print taken when the drop pause settled and the print now must be identical at
        // EVERY level (Count, Discrete, Rounded, Exact) in every block, with the same origin and block list, and the day hashes
        // equal. Paused state is deterministic, so any difference is a change made while time stood still (UI actions, the host
        // snapshot's side effects, a behaviour-tree tick, a host reload at the same date, in M5 a command). A Capped block cannot
        // show that nothing changed beyond the cut, so it fails the check, even when both prints are alike. firstDifference is
        // null when they are the same.
        public static bool Same(Fingerprint settled, Fingerprint now, out string firstDifference)
        {
            if (!WellFormed(settled) || !WellFormed(now)) { firstDifference = "a print is missing or malformed"; return false; }
            if (settled.OriginTicks != now.OriginTicks) { firstDifference = "the origin differs"; return false; }
            if (settled.Blocks.Length != now.Blocks.Length) { firstDifference = "the block lists differ in length"; return false; }
            for (int i = 0; i < settled.Blocks.Length; i++)
            {
                FingerprintBlock a = settled.Blocks[i], b = now.Blocks[i];
                string what = null;
                if (a.Area != b.Area || a.Agency != b.Agency) what = "is a different area or agency";
                else if (a.Capped || b.Capped) what = "is capped";
                else if (a.Count != b.Count) what = "Count differs";
                else if (a.Discrete != b.Discrete) what = "Discrete differs";
                else if (a.Rounded != b.Rounded) what = "Rounded differs";
                else if (a.Exact != b.Exact) what = "Exact differs";
                if (what != null)
                {
                    firstDifference = "block " + i + " (area " + a.Area + ", agency " + a.Agency + ") " + what;
                    return false;
                }
            }
            if (settled.DayDiscrete != now.DayDiscrete) { firstDifference = "DayDiscrete differs"; return false; }
            if (settled.DayRounded != now.DayRounded) { firstDifference = "DayRounded differs"; return false; }
            if (settled.DayExact != now.DayExact) { firstDifference = "DayExact differs"; return false; }
            firstDifference = null;
            return true;
        }
    }

    // What the host knows when a player comes back (JoinService fills it; RejoinRules.Decide reads it).
    public struct RejoinFacts
    {
        public bool WasInGame;          // the player had loaded before the drop
        public bool HaveDrop;           // the host recorded the drop (time and ticks)
        public long DropMs, NowMs;      // the old link's close; the new link's Confirmed
        public bool TimeMoved;          // the host's GameTicks changed at any frame since the drop
        public bool HostReloaded;       // a save loaded on the host during the drop
        public bool HostSelfCheckHeld;  // the host's settled print equals its print now (FingerprintRules.Same)
        public bool ClientInSession;    // RejoinOffer.InSession
        public bool ClientSelfCheckHeld;// RejoinOffer.SelfCheckHeld
        public bool HavePrints;         // both prints present and well formed
        public long HostNextDay, ClientNextDay;
        public FingerprintDiff Print;   // the cross-check
        public bool ResultsMissed;      // M5: RejoinOffer.LastResult is not the last seq the host sent this player
    }

    public static class RejoinRules
    {
        // Quick only if every condition holds; otherwise the FIRST failing condition's RejoinCodes value, in the order:
        // NotInGameBefore, NoDropRecord, HostReloaded, TimeMoved, NotInSession, DropTooLong, NoPrint, HostChangedDuringDrop,
        // ClientChangedDuringDrop, BoundaryDiffers, FingerprintDiffers, CommandsMissed.
        //
        // Judgement calls, all toward "full":
        //   - the drop record must make sense (a recorded drop, not negative, and the new link not before the old one's close),
        //     else NoDropRecord; the elapsed time is compared without a subtraction that could wrap;
        //   - a drop is short only if it is UNDER JoinLimits.QuickRejoinMaxDropMs (119,999 ms is quick, 120,000 ms is full);
        //   - the two sides' next-day keys must be equal AND known (a key of zero or less means "none" and proves nothing);
        //   - a cross-check that was never made (Print null) is a failure.
        public static int Decide(RejoinFacts f)
        {
            if (!f.WasInGame) return RejoinCodes.NotInGameBefore;
            if (!f.HaveDrop || f.DropMs < 0 || f.NowMs < f.DropMs) return RejoinCodes.NoDropRecord;
            if (f.HostReloaded) return RejoinCodes.HostReloaded;
            if (f.TimeMoved) return RejoinCodes.TimeMoved;
            if (!f.ClientInSession) return RejoinCodes.NotInSession;
            if (f.NowMs - f.DropMs >= JoinLimits.QuickRejoinMaxDropMs) return RejoinCodes.DropTooLong;
            if (!f.HavePrints) return RejoinCodes.NoPrint;
            if (!f.HostSelfCheckHeld) return RejoinCodes.HostChangedDuringDrop;
            if (!f.ClientSelfCheckHeld) return RejoinCodes.ClientChangedDuringDrop;
            if (f.HostNextDay < 1 || f.HostNextDay != f.ClientNextDay) return RejoinCodes.BoundaryDiffers;
            if (f.Print == null || !f.Print.Match) return RejoinCodes.FingerprintDiffers;
            if (f.ResultsMissed) return RejoinCodes.CommandsMissed;   // last, so M4's reasons still show first
            return RejoinCodes.Quick;
        }
    }
}
