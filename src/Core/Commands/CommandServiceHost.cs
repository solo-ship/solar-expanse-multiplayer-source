using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host driver's seams for the game glue (M5 Task 10a): the game's own faults, the published entries, a day
    // event that runs twice, and the test profile's planted faults and day hold. Apart from CommandService.cs so M6's
    // and M7's additions can sit beside it.
    public sealed partial class CommandService
    {
        // The last day this stream sent DayClosed for (-1: none yet). A day is closed at most once: a day handler that
        // throws makes the game run that day again with nextDay unchanged, and a second DayClosed for it is a day
        // going back on every client.
        long closedThrough = -1;
        long reopenLogged = -1;

        // dev: the day hold (`dayhold on`): from the first DayClosed held, the stream waits, in order
        bool closesHeld;
        readonly List<Item> heldStream = new List<Item>();
        // dev: the planted faults hit the first client that joined (the tools scope their expectations to it)
        int firstMember = -1;
        string dropFault, fetchFault;

        // Every entry this stream published, after its seq and day are fixed: the game's "seat:", "bankrupt:" and
        // "craft:" lines are made from it (StreamLines), so they carry the entry's own numbers.
        public event Action<ActionResult> Published;

        // The game's own world did something the clients cannot be told (IHostDecisions.HostFault): the entry cannot
        // be refused, so the stream starts again and every client rejoins fully.
        public void HostFault(string why) { Fault(why ?? "the game reported a fault"); }

        void RaisePublished(ActionResult r)
        {
            Action<ActionResult> h = Published;
            if (h == null) return;
            try { h(r); }
            catch (Exception e) { Log.Warn("command: a listener threw on a published entry: " + e.GetType().Name); }
        }

        // The day event of a day already closed is running again: no second close, and what it raises still
        // belongs to the next day.
        bool Reopens(long day)
        {
            if (day > closedThrough) return false;
            closing = true;
            closingDay = closedThrough;
            if (reopenLogged != day)
            {
                Log.Warn("command: day " + day + " runs its day event again; it was closed once already");
                reopenLogged = day;
            }
            return true;
        }

        // A closed day whose event has not finished yet: the players' requests wait, so on the host as on every
        // client they apply after that day's event, never inside a day that is already closed. Said at most once a
        // second, so a day event that keeps failing shows in the log without flooding it.
        bool DayPending(long now)
        {
            if (closedThrough < 0) return false;
            long day;
            try
            {
                if (!DayKey.TryCurrent(world.NextDayTicks, world.OriginTicks, out day)) return false;
            }
            catch (Exception) { return false; }   // the apply's own day stamp reports it
            if (day > closedThrough) return false;
            if (now - pendingLogMs >= 1000 || now < pendingLogMs)
            {
                Log.Info("command: " + waiting.Count + " requests wait for day " + day + "'s event");
                pendingLogMs = now;
            }
            return true;
        }

        long pendingLogMs = long.MinValue / 2;

        // The running crew-death timers of the human agencies' sites, which AgencyState carries (the game does not
        // save them). Required, as StateCounters is: a state made without them would let a joiner's crew live on
        // after the host's has died.
        public Func<string[], LifeSupportRow[]> StateLifeSupport { get; set; }

        LifeSupportRow[] TimersFor(string[] agencies)
        {
            Func<string[], LifeSupportRow[]> timers = StateLifeSupport;
            if (timers == null) throw new InvalidOperationException("StateLifeSupport is not set");
            LifeSupportRow[] rows = timers(agencies);
            if (rows == null) throw new InvalidOperationException("StateLifeSupport gave no list");
            return rows.Length == 0 ? null : rows;
        }

        void StateLine(int player, uint next)
        {
            Log.Info("command: state to player " + player + " next #" + next + " stream " + epoch);
        }

        void ResetHostState()
        {
            closedThrough = -1;
            reopenLogged = -1;
            closesHeld = false;
            heldStream.Clear();
            ResetWorldState();
        }

        // ---- the test profile's day hold ----

        // `dayhold on`: DayClosed is held, and everything sent after it waits behind it, so each client keeps the
        // order and holds at its next boundary. `off`: the held messages go out in order.
        public void HoldCloses(bool on)
        {
            closesHeld = on;
            if (on) return;
            var release = new List<Item>(heldStream);
            heldStream.Clear();
            for (int i = 0; i < release.Count; i++) Deliver(release[i]);
            Pump(nowMs());
        }

        public bool ClosesHeld { get { return closesHeld; } }

        bool HoldsBack(Item it)
        {
            if (heldStream.Count == 0 && !(closesHeld && it.M is DayClosed)) return false;
            heldStream.Add(it);
            if (heldStream.Count > MaxOutQueue)
            {
                Log.Warn("command: dayhold released: more than " + MaxOutQueue + " messages waited");
                HoldCloses(false);
            }
            return true;
        }

        // ---- the planted faults (-semp-command-fault) ----

        void NoteMember(int id) { if (firstMember < 0) firstMember = id; }

        // seqgap and dropentry: the next entry meant for the first client that joined is not sent to it.
        public void DropNextEntry(string fault) { dropFault = fault; }

        // fetchlost: the next object request of the first client that joined is never answered.
        public void LoseNextFetch(string fault) { fetchFault = fault; }

        bool Drops(Player p, Item it)
        {
            if (dropFault == null || it.Seq == 0 || p.Id != firstMember) return false;
            Log.Warn("command: fault " + dropFault + ": entry #" + it.Seq + " not sent to player " + p.Id);
            dropFault = null;
            return true;
        }

        bool LosesFetch(int player, ObjectRequest r)
        {
            if (fetchFault == null || player != firstMember) return false;
            Log.Warn("command: fault " + fetchFault + ": object " + r.Kind + " " + r.Agency + " " + r.Body
                + " for player " + player + " never answered");
            fetchFault = null;
            return true;
        }
    }
}
