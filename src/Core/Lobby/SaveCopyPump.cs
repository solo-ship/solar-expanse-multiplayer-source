using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Where the host's save copies go: the session's link to its members (Task 6 wires it to the real messages).
    public interface ISaveCopyPeers
    {
        bool SendHeader(int player, SaveCopyHeader header);
        bool SendChunk(int player, int index, ArraySegment<byte> data);
    }

    // The host's save copies for every member (DD8 "When"): one transfer per member at a time, a newer save replaces a queued or
    // running one, and a few chunks per call only, so play is never held up. The glue calls Pump every frame and Ack when a
    // member acks.
    public sealed class SaveCopyPump
    {
        public const int ChunksPerPump = 4;

        sealed class Member
        {
            public SaveCopy Copy = new SaveCopy();
            public SaveCopyHeader Header, QueuedHeader;
            public bool HeaderSent;
        }

        readonly Dictionary<int, Member> members = new Dictionary<int, Member>();

        public int Superseded
        {
            get { int n = 0; foreach (Member m in members.Values) n += m.Copy.Superseded; return n; }
        }

        // True when no member has a transfer queued or running.
        public bool Idle
        {
            get { foreach (Member m in members.Values) if (m.Copy.Running || m.Copy.Queued) return false; return true; }
        }

        // The whole of what is left to send, in bytes (for the End session deadline).
        public long BytesLeft
        {
            get
            {
                long n = 0;
                foreach (Member m in members.Values)
                {
                    if (m.Copy.Queued && m.QueuedHeader != null) n += m.QueuedHeader.CompressedSize;
                    if (m.Copy.Running && m.Header != null) n += m.Header.CompressedSize;
                }
                return n;
            }
        }

        // The save is queued for each player (a newer one replaces what was queued or running). The header is made once: its sizes
        // and hash describe these bytes.
        public void Offer(IEnumerable<int> players, SaveCopyHeader header, byte[] gz, long nowMs)
        {
            if (header == null || gz == null || header.Validate() != null || header.CompressedSize != gz.Length) return;
            foreach (int p in players)
            {
                Member m;
                if (!members.TryGetValue(p, out m)) { m = new Member(); members[p] = m; }
                m.Copy.Offer(header.SaveId, gz, nowMs);
                m.QueuedHeader = header;
                if (!m.Copy.Running) { m.Header = null; m.HeaderSent = false; }
            }
        }

        // Starts queued saves and sends a few chunks per member. A send that fails drops that member's transfer.
        public void Pump(ISaveCopyPeers peers, long nowMs)
        {
            foreach (KeyValuePair<int, Member> kv in members)
            {
                Member m = kv.Value;
                if (!m.Copy.Running && m.Copy.TryStart(nowMs)) { m.Header = m.QueuedHeader; m.QueuedHeader = null; m.HeaderSent = false; }
                if (!m.Copy.Running) continue;
                if (!m.HeaderSent)
                {
                    if (!peers.SendHeader(kv.Key, m.Header)) { Abandon(m); continue; }
                    m.HeaderSent = true;
                }
                SnapshotSender s = m.Copy.Sender;
                for (int i = 0; i < ChunksPerPump && s.CanSend(JoinLimits.Window); i++)
                {
                    int index = s.Next;
                    if (!peers.SendChunk(kv.Key, index, s.Take())) { Abandon(m); break; }
                }
            }
        }

        // A member's ack (the next chunk it expects). False when the ack is not honest (the caller counts a violation).
        public bool Ack(int player, int nextReceived, long nowMs)
        {
            Member m;
            if (!members.TryGetValue(player, out m) || !m.Copy.Running) return false;
            if (m.Copy.Sender.Ack(nextReceived, nowMs) != 0) return false;
            if (m.Copy.Sender.Done) { m.Copy.Finish(); m.Header = null; m.HeaderSent = false; }
            return true;
        }

        // A member left or its link dropped: its queue goes with it (a rejoin gets the next save).
        public void Drop(int player) { members.Remove(player); }

        static void Abandon(Member m) { m.Copy.Finish(); m.Header = null; m.HeaderSent = false; }
    }

    // The header of a copy: sizes and hash from the bytes themselves; the plain size is the gzip trailer's own (ISIZE).
    public static class SaveCopyHeaders
    {
        public static SaveCopyHeader For(string saveId, string title, string hostName, byte[] gz, SaveInfoData info,
                                         IDictionary<string, string> lastHolders)
        {
            if (gz == null || gz.Length < 18) return null;
            long plain = (uint)(gz[gz.Length - 4] | (gz[gz.Length - 3] << 8) | (gz[gz.Length - 2] << 16) | (gz[gz.Length - 1] << 24));
            return new SaveCopyHeader
            {
                SaveId = saveId, Title = TextSanitiser.Clean(title, SaveCopyHeader.MaxTitleChars, ""),
                HostName = TextSanitiser.Clean(hostName, SaveCopyHeader.MaxHostNameChars, ""),
                CompressedSize = gz.Length, PlainSize = plain, Chunks = ChunkMath.Count(gz.Length), Sha256 = Sha256Hex.Of(gz),
                Info = info, LastHolders = lastHolders == null ? new Dictionary<string, string>() : new Dictionary<string, string>(lastHolders)
            };
        }
    }
}
