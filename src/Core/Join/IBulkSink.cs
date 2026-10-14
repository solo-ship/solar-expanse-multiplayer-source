using System;

namespace SolarExpanseMultiplayer
{
    // The joiner's side of the snapshot transfer, as Net's Link sees it (plan "Interfaces, Transfer"). Core's SnapshotReceiver
    // implements it; the Link calls it on its READER thread, so the transfer never waits for the main thread and chunks never
    // pile up in the inbox (I-4, I-5). Every method must be quick and must not throw for hostile input: false means "the peer
    // broke a rule" and the link is closed as a violation.
    public interface IBulkSink
    {
        // Every decoded message except SnapshotChunk, BEFORE it goes to the inbox: ClaimResult(Ok) arms the sink; JoinRefused,
        // ClaimResult(!Ok) and Bye disarm it (I-4).
        void OnControl(Message m);
        // A JoinHeader (it goes to the inbox as well). False = refuse: unarmed, a second header, or a header that breaks the caps.
        bool OnHeader(JoinHeader h);
        // One chunk, straight from the frame (the raw bytes at frame[offset .. offset + count)). False = violation: unarmed, out of
        // order, past the count, or the wrong size.
        bool OnChunk(int index, byte[] frame, int offset, int count);
        // True: a chunk frame of this many bytes (the 4-byte length prefix included) skips both rate buckets (armed, and within the
        // declared count).
        bool Exempt(int frameBytes);
        // True once the transfer has ended: complete, or failed at the end (a hash mismatch). The Link puts the final progress
        // event in the inbox when this turns true.
        bool Finished { get; }
        // Wired by Link.Bulk's setter to that link's Send, so acks always go out on the current link.
        Func<Message, bool> SendAck { set; }
    }
}
