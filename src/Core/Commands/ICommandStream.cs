using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // What the join driver (JoinService) calls on the host's command driver. CommandService implements it; the join
    // driver never touches the stream's inside.
    public interface ICommandStream
    {
        // Before the snapshot: a SeatClaimed entry for each new agency (money from IActionWorld.MoneyFor) is applied
        // and sent; then the stream is Held.
        void BatchStarting(IList<string> newAgencies);
        // Asked every frame after BatchStarting until true, or until the join driver's bounded wait (SnapshotReadyMs) ends and
        // the batch fails. The stream stays held meanwhile. M5: always true.
        bool ReadyToSnapshot();
        // The frame the snapshot was taken (after a true ReadyToSnapshot): the stream position the members' snapshot belongs
        // to. An entry applied before it is in the snapshot and is not sent to a member again.
        void SnapshotTaken();
        // For JoinHeader.HumanAgencies, read after BatchStarting.
        string[] HumanAgencies { get; }
        // DD5's batch end, steps 1-4; the join driver then lifts the pause (step 5); step 6 runs in a later Update.
        void BatchEnded(IList<int> loaded, IList<string> releasedAgencies);
        void ReleaseSeat(string agency); // a kick: the agency leaves the human set (SeatReleased) now
        void LinkReplaced(int player);   // Confirmed on a replacing link, or N-2's Rejoined
        void Left(int player);
        void QuickRejoined(int player);  // AgencyState; back among the recipients
        void HostLoaded();               // StreamReset to every recipient; a new stream
        uint LastSentTo(int player);     // for RejoinFacts.ResultsMissed
        // M7 (DD-W12): where M4/M5's rules would answer a full rejoin, can the dropped player be replayed what it
        // missed? declined is RejoinRules' code; nextSeq and pausedTicks fill the RejoinResult. No side effects.
        bool CanResume(int player, uint lastResult, long clientNextDay, int declined, out uint nextSeq,
            out long pausedTicks, out string why);
        // After the Resume was sent: the player is back among the recipients and the paced replay starts.
        void StartResume(int player, uint lastResult, long clientNextDay);
    }
}
