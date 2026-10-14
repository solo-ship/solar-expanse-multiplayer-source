using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // The frame pipe over a TCP socket and its (TLS) stream: the code that used to sit inside Link.
    public sealed class TlsPipe : IFramePipe
    {
        // linger: a Bye was just sent. Closing the socket straight away can reset the connection and throw the Bye away
        // (the other side still has unread data for us), so stop sending, give the other side a moment (<= LingerMs) to
        // read the Bye and close, and drain what it sent meanwhile.
        const int LingerMs = 250;

        readonly TcpClient tcp;
        readonly Stream stream;
        readonly ManualResetEvent readerDone = new ManualResetEvent(false);

        public TlsPipe(TcpClient tcp, Stream stream, NetOptions opt)
        {
            this.tcp = tcp; this.stream = stream;
        }

        public string Kind { get { return "tls"; } }
        public PeerIdentity Peer { get { return PeerIdentity.Tls; } }

        public byte[] ReadFrame(int maxBody)
        {
            return Frame.Read(stream, maxBody);
        }

        public void ReaderDone() { readerDone.Set(); }

        public void WriteFrame(byte[] framed)
        {
            stream.Write(framed, 0, framed.Length);
            stream.Flush();
        }

        public void Close(bool linger)
        {
            if (linger)
            {
                try
                {
                    Socket sock = tcp.Client;
                    sock.Shutdown(SocketShutdown.Send);
                    long until = NetClock.NowMs + LingerMs;
                    if (readerDone.WaitOne(LingerMs))
                    {
                        var junk = new byte[1024];
                        while (true)
                        {
                            long left = until - NetClock.NowMs;
                            if (left <= 0 || !sock.Poll((int)Math.Min(left, 1000) * 1000, SelectMode.SelectRead)) break;
                            if (sock.Receive(junk) <= 0) break;
                        }
                    }
                }
                catch (Exception) { }
            }
            try { stream.Dispose(); } catch (Exception) { }
            try { tcp.Close(); } catch (Exception) { }
        }
    }
}
