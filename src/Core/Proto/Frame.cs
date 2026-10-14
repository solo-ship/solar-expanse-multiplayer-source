using System;
using System.IO;

namespace SolarExpanseMultiplayer
{
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    // Wire framing: a 4-byte big-endian body length, then the body. A length of 0 or over the cap is refused before
    // anything is allocated, so a hostile length can't make us reserve memory (a ProtocolException: the peer broke a rule).
    // A stream that ends inside a header or a body is the connection being lost, not a rule broken: EndOfStreamException.
    public static class Frame
    {
        public const int DefaultMaxBody = 64 * 1024;

        public static byte[] Wrap(byte[] body)
        {
            var f = new byte[4 + body.Length];
            f[0] = (byte)(body.Length >> 24);
            f[1] = (byte)(body.Length >> 16);
            f[2] = (byte)(body.Length >> 8);
            f[3] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, f, 4, body.Length);
            return f;
        }

        // Blocks until one whole frame has arrived. Null = the stream ended cleanly between frames.
        // Throws ProtocolException for a bad length, EndOfStreamException when the stream ends inside a frame.
        public static byte[] Read(Stream s, int maxBody)
        {
            var head = new byte[4];
            int got = ReadUpTo(s, head, 4);
            if (got == 0) return null;
            if (got < 4) throw new EndOfStreamException("the stream ended inside a frame header");
            int len = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];   // negative when the top bit is set
            if (len <= 0 || len > maxBody) throw new ProtocolException("frame length " + (uint)len + " outside 1.." + maxBody);
            var body = new byte[len];
            if (ReadUpTo(s, body, len) < len) throw new EndOfStreamException("the stream ended inside a frame");
            return body;
        }

        // One frame that arrived as one whole message (a message transport such as Steam keeps the boundaries): the
        // 4-byte prefix must say exactly the bytes behind it, within 1..maxBody. Anything else is the peer breaking a
        // rule (ProtocolException); a message can't end "inside" a frame, so there is no lost-connection case here.
        public static byte[] Unwrap(byte[] message, int maxBody)
        {
            int size = message == null ? 0 : message.Length;
            if (size < 4) throw new ProtocolException("a message of " + size + " bytes is shorter than a frame prefix");
            int len = (message[0] << 24) | (message[1] << 16) | (message[2] << 8) | message[3];
            if (len <= 0 || len > maxBody)
                throw new ProtocolException("frame length " + (uint)len + " outside 1.." + maxBody);
            if (len != size - 4)
                throw new ProtocolException("frame length " + len + " disagrees with the message's " + (size - 4) + " bytes");
            var body = new byte[len];
            Buffer.BlockCopy(message, 4, body, 0, len);
            return body;
        }

        static int ReadUpTo(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }
}
