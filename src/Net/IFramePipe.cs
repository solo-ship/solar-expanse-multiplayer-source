using System;

namespace SolarExpanseMultiplayer
{
    // What a Link reads and writes frames through: TLS over TCP (TlsPipe) or a Steam P2P connection (SteamPipe).
    public interface IFramePipe
    {
        string Kind { get; }                     // "tls" or "steam" (logs only)
        PeerIdentity Peer { get; }               // who the transport says is at the other end (the gate's view)
        byte[] ReadFrame(int maxBody);           // one frame body; null = ended cleanly; throws ProtocolException (bad frame),
                                                 // EndOfStreamException / IOException (lost)
        void WriteFrame(byte[] framed);          // framed = Frame.Wrap(body); blocks at most NetOptions.SendTimeoutMs, then throws IOException
        void ReaderDone();                       // Link: its read loop has ended, so a lingering Close may drain the socket
        void Close(bool linger);                 // idempotent; linger = a Bye was just queued (TLS: a 250 ms drain)
    }
}
