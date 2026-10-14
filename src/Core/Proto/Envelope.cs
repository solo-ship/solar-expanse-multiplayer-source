using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace SolarExpanseMultiplayer
{
    [Flags]
    public enum Dir { ToHost = 1, ToClient = 2, Both = 3 }

    // One frame body: a 2-byte message type id and a 4-byte sequence number (both big-endian), then the JSON payload.
    // The id picks a class from this fixed table, so nothing from the network ever names a .NET type. The JSON reader
    // ignores "$type" and other metadata, drops unknown fields, and refuses deep nesting and trailing content.
    // Two kinds are binary (SnapshotChunk, protocol 2, and SaveCopyChunk, protocol 7): its payload is a 4-byte big-endian index and the raw bytes, so a
    // 15 MB snapshot costs no JSON parsing and no base64.
    public static class Envelope
    {
        public const int HeaderSize = 6, MaxDepth = 8;

        sealed class Kind
        {
            public ushort Id; public Type Type; public int MaxPayload; public Dir Dir; public bool Binary;
        }

        static readonly List<Kind> Kinds = new List<Kind>
        {
            new Kind { Id = 1, Type = typeof(Hello), MaxPayload = 1024, Dir = Dir.ToHost },
            new Kind { Id = 2, Type = typeof(Welcome), MaxPayload = 512, Dir = Dir.ToClient },
            new Kind { Id = 3, Type = typeof(Reject), MaxPayload = 512, Dir = Dir.ToClient },
            new Kind { Id = 4, Type = typeof(Ping), MaxPayload = 64, Dir = Dir.Both },
            new Kind { Id = 5, Type = typeof(Pong), MaxPayload = 64, Dir = Dir.Both },
            new Kind { Id = 6, Type = typeof(Clock), MaxPayload = 1024, Dir = Dir.ToClient },
            new Kind { Id = 7, Type = typeof(SpeedRequest), MaxPayload = 128, Dir = Dir.ToHost },
            new Kind { Id = 8, Type = typeof(Bye), MaxPayload = 512, Dir = Dir.Both },
            // protocol 2 (M4): the join messages
            new Kind { Id = 9, Type = typeof(ContentSummary), MaxPayload = 60 * 1024, Dir = Dir.ToHost },
            new Kind { Id = 10, Type = typeof(ContentRequest), MaxPayload = 256, Dir = Dir.ToClient },
            new Kind { Id = 11, Type = typeof(ContentDetail), MaxPayload = 60 * 1024, Dir = Dir.ToHost },
            new Kind { Id = 12, Type = typeof(JoinRefused), MaxPayload = 8 * 1024, Dir = Dir.ToClient },
            new Kind { Id = 13, Type = typeof(AgencyClaim), MaxPayload = 128, Dir = Dir.ToHost },
            new Kind { Id = 14, Type = typeof(ClaimResult), MaxPayload = 256, Dir = Dir.ToClient },
            new Kind { Id = 15, Type = typeof(JoinWait), MaxPayload = 64, Dir = Dir.ToClient },
            new Kind { Id = 16, Type = typeof(JoinHeader), MaxPayload = 8 * 1024, Dir = Dir.ToClient },
            new Kind { Id = 17, Type = typeof(SnapshotChunk), MaxPayload = 4 + JoinLimits.ChunkSize, Dir = Dir.ToClient, Binary = true },
            new Kind { Id = 18, Type = typeof(SnapshotAck), MaxPayload = 64, Dir = Dir.ToHost },
            new Kind { Id = 19, Type = typeof(JoinStage), MaxPayload = 128, Dir = Dir.ToHost },
            new Kind { Id = 20, Type = typeof(Notice), MaxPayload = 768, Dir = Dir.ToClient },
            // 48 KiB, not the plan's 16 KiB: M3's areas give 10 blocks per agency and 4 for the world, so the 24 agencies the
            // plan wants it to hold are 244 blocks, about 34 KB of JSON (three 16-hex hashes and a count each). Under the
            // 64 KiB frame cap, and the fingerprint never leaves a session.
            new Kind { Id = 21, Type = typeof(RejoinOffer), MaxPayload = 48 * 1024, Dir = Dir.ToHost },
            // protocol 6: 128, not 64: ResumeNextSeq and ResumePausedTicks (WorldCapsTests builds the worst case)
            new Kind { Id = 22, Type = typeof(RejoinResult), MaxPayload = 128, Dir = Dir.ToClient },
            // protocol 4 (M5): the command messages (CommandMessages.cs). Every ToClient kind but ActionRefused goes
            // only to the stream's recipients. The caps are derived from the field caps: CommandCapsTests builds each
            // worst case.
            // protocol 5: 32 KiB, not 16: a CyclicalSet carries two plans (FlightCapsTests derives it)
            new Kind { Id = 23, Type = typeof(ActionRequest), MaxPayload = 32 * 1024, Dir = Dir.ToHost },
            // protocol 5: 40 KiB, not 20: a finished mission with MaxRemovals rows and MaxMissionCrafts crafts; protocol 6: 56 KiB (the
            // largest frame the host sends): MaxMarketOps widest ops, a full AI entry, a full growth part (WorldCapsTests)
            new Kind { Id = 24, Type = typeof(ActionResult), MaxPayload = 56 * 1024, Dir = Dir.ToClient },
            new Kind { Id = 25, Type = typeof(ActionRefused), MaxPayload = 128, Dir = Dir.ToClient },
            // protocol 6: 56 KiB (the largest frame the host sends), not 20: the world print (32 area prints) and a value day's 1,024 block sums join the prints
            new Kind { Id = 26, Type = typeof(DayClosed), MaxPayload = 56 * 1024, Dir = Dir.ToClient },
            new Kind { Id = 27, Type = typeof(ObjectRequest), MaxPayload = 512, Dir = Dir.ToHost },
            // 56 KiB, not the plan's 48 KiB: a full piece of 160 build rows with 96-byte item ids and name keys is
            // 52,204 bytes
            new Kind { Id = 28, Type = typeof(ObjectData), MaxPayload = 56 * 1024, Dir = Dir.ToClient },
            // 56 KiB: 144 counter rows at their widest (the rivals' item counters joined the human agencies') are
            // 49,482 bytes with the rest of a full state
            new Kind { Id = 29, Type = typeof(AgencyState), MaxPayload = 56 * 1024, Dir = Dir.ToClient },
            new Kind { Id = 30, Type = typeof(StreamReset), MaxPayload = 64, Dir = Dir.ToClient },
            // protocol 6 (M7): heals and the resume (WorldMessages.cs). WorldCapsTests builds each worst case.
            new Kind { Id = 31, Type = typeof(HealRequest), MaxPayload = 256, Dir = Dir.ToHost },
            new Kind { Id = 32, Type = typeof(HealData), MaxPayload = 56 * 1024, Dir = Dir.ToClient },
            // the size of RejoinOffer's fingerprint (M4): JoinLimits.MaxFingerprintBlocks blocks
            new Kind { Id = 33, Type = typeof(ResumeProgress), MaxPayload = 48 * 1024, Dir = Dir.ToHost },
            new Kind { Id = 34, Type = typeof(ResumeCheck), MaxPayload = 64, Dir = Dir.ToClient },
            // protocol 7 (M8): the lobby and the save copy (LobbyMessages.cs, SaveCopyMessages.cs). LobbyCapsTests builds each
            // worst case.
            new Kind { Id = 35, Type = typeof(Roster), MaxPayload = LobbyLimits.MaxRosterBytes, Dir = Dir.ToClient },
            new Kind { Id = 36, Type = typeof(LobbyClaim), MaxPayload = 128, Dir = Dir.ToHost },
            new Kind { Id = 37, Type = typeof(LobbyReady), MaxPayload = 64, Dir = Dir.ToHost },
            new Kind { Id = 38, Type = typeof(PlayerKey), MaxPayload = 128, Dir = Dir.ToHost },
            new Kind { Id = 39, Type = typeof(SaveCopyHeader), MaxPayload = LobbyLimits.MaxCopyHeaderBytes, Dir = Dir.ToClient },
            new Kind { Id = 40, Type = typeof(SaveCopyChunk), MaxPayload = 4 + JoinLimits.ChunkSize, Dir = Dir.ToClient, Binary = true },
            new Kind { Id = 41, Type = typeof(SaveCopyAck), MaxPayload = 64, Dir = Dir.ToHost },
        };

        public sealed class Decoded
        {
            public ushort Type;
            public uint Seq;
            public Message Message;   // null when Error is set
            public string Error;
        }

        static Kind Find(ushort id) { foreach (Kind k in Kinds) if (k.Id == id) return k; return null; }
        static Kind Find(Type t) { foreach (Kind k in Kinds) if (k.Type == t) return k; return null; }

        public static ushort IdOf(Type t) { Kind k = Find(t); return k == null ? (ushort)0 : k.Id; }

        // The payload cap of a message class (0 for a class that is not in the table).
        public static int MaxPayloadOf(Type t) { Kind k = Find(t); return k == null ? 0 : k.MaxPayload; }

        // A fresh settings object every time: nothing shared between threads can be changed by mistake.
        static JsonSerializerSettings Settings()
        {
            return new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.None,
                MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double,
                MaxDepth = MaxDepth,
                CheckAdditionalContent = true,
            };
        }

        // For callers whose messages are of fixed size: throws ArgumentException for a message that cannot be built. Anything
        // that carries peer-derived text goes through TryEncode (Link.Send does), which says no instead of throwing (I-13).
        public static byte[] Encode(Message m, uint seq)
        {
            byte[] body; string error;
            if (!TryEncode(m, seq, out body, out error)) throw new ArgumentException(error);
            return body;
        }

        // Never throws: false (body null, error set) for a message that is null, not in the table, invalid (a binary
        // message is checked here, since it has no JSON to refuse), or over its payload cap.
        public static bool TryEncode(Message m, uint seq, out byte[] body, out string error)
        {
            body = null; error = null;
            if (m == null) { error = "no message to encode"; return false; }
            Kind k = Find(m.GetType());
            if (k == null) { error = "not a protocol message: " + m.GetType().Name; return false; }
            try
            {
                byte[] payload;
                if (k.Binary)
                {
                    var chunk = m as ChunkMessage;
                    string bad = chunk == null ? "not a binary message" : chunk.Validate();
                    if (bad != null) { error = k.Type.Name + ": " + bad; return false; }
                    if (4 + chunk.Data.Length > k.MaxPayload) { error = k.Type.Name + " payload is " + (4 + chunk.Data.Length) + " bytes, cap " + k.MaxPayload; return false; }
                    payload = new byte[4 + chunk.Data.Length];
                    payload[0] = (byte)(chunk.Index >> 24); payload[1] = (byte)(chunk.Index >> 16); payload[2] = (byte)(chunk.Index >> 8); payload[3] = (byte)chunk.Index;
                    Buffer.BlockCopy(chunk.Data, 0, payload, 4, chunk.Data.Length);
                }
                else
                {
                    payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(m, Formatting.None, Settings()));
                    if (payload.Length > k.MaxPayload) { error = k.Type.Name + " payload is " + payload.Length + " bytes, cap " + k.MaxPayload; return false; }
                }
                body = Raw(k.Id, seq, payload);
                return true;
            }
            catch (Exception e) { error = k.Type.Name + " could not be encoded (" + e.GetType().Name + ")"; return false; }
        }

        // Any type id, any payload: for Encode, and for the fake client's hostile tests.
        public static byte[] Raw(ushort type, uint seq, byte[] payload)
        {
            var body = new byte[HeaderSize + payload.Length];
            body[0] = (byte)(type >> 8); body[1] = (byte)type;
            body[2] = (byte)(seq >> 24); body[3] = (byte)(seq >> 16); body[4] = (byte)(seq >> 8); body[5] = (byte)seq;
            Buffer.BlockCopy(payload, 0, body, HeaderSize, payload.Length);
            return body;
        }

        // Never throws for bad input: Error says what was wrong, and the caller closes the connection.
        public static Decoded Decode(byte[] body, Dir arrivingAt)
        {
            var d = new Decoded();
            if (body == null || body.Length < HeaderSize) { d.Error = "frame shorter than the header"; return d; }
            d.Type = (ushort)((body[0] << 8) | body[1]);
            d.Seq = ((uint)body[2] << 24) | ((uint)body[3] << 16) | ((uint)body[4] << 8) | body[5];
            Kind k = Find(d.Type);
            if (k == null) { d.Error = "unknown message type " + d.Type; return d; }
            if ((k.Dir & arrivingAt) == 0) { d.Error = k.Type.Name + " is not allowed in this direction"; return d; }
            int len = body.Length - HeaderSize;
            if (len > k.MaxPayload) { d.Error = k.Type.Name + " payload is " + len + " bytes, cap " + k.MaxPayload; return d; }
            if (k.Binary) return DecodeChunk(d, k, body, len);
            try
            {
                // UTF-8 only (no byte-order-mark sniffing), and invalid bytes throw instead of becoming '?'.
                using (var text = new StreamReader(new MemoryStream(body, HeaderSize, len), new UTF8Encoding(false, true), false))
                using (var reader = new JsonTextReader(text))
                {
                    reader.MaxDepth = MaxDepth;
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.FloatParseHandling = FloatParseHandling.Double;
                    var m = JsonSerializer.Create(Settings()).Deserialize(reader, k.Type) as Message;
                    if (m == null) { d.Error = k.Type.Name + " payload is not a JSON object"; return d; }
                    string bad = m.Validate();
                    if (bad != null) { d.Error = k.Type.Name + ": " + bad; return d; }
                    d.Message = m;
                }
            }
            catch (Exception e) { d.Error = k.Type.Name + " payload unreadable (" + e.GetType().Name + ")"; }
            return d;
        }

        // SnapshotChunk: 4 bytes of index (big-endian), then 1..ChunkSize raw bytes. The cap was checked by the caller.
        static Decoded DecodeChunk(Decoded d, Kind k, byte[] body, int len)
        {
            if (len < 5) { d.Error = k.Type.Name + " payload is " + len + " bytes, it needs an index and at least one byte"; return d; }
            uint index = ((uint)body[HeaderSize] << 24) | ((uint)body[HeaderSize + 1] << 16) | ((uint)body[HeaderSize + 2] << 8) | body[HeaderSize + 3];
            if (index > int.MaxValue) { d.Error = k.Type.Name + ": Index is out of range"; return d; }
            var data = new byte[len - 4];
            Buffer.BlockCopy(body, HeaderSize + 4, data, 0, data.Length);
            var m = (ChunkMessage)Activator.CreateInstance(k.Type);
            m.Index = (int)index; m.Data = data;
            string bad = m.Validate();
            if (bad != null) { d.Error = k.Type.Name + ": " + bad; return d; }
            d.Message = m;
            return d;
        }
    }

    // Sequence numbers per direction: 1, 2, 3, ... TCP keeps order, so a gap or a repeat means a bug or tampering.
    public sealed class SeqCounter
    {
        uint last;
        public uint Next() { return ++last; }
        public bool Accept(uint seq)
        {
            if (seq != unchecked(last + 1)) return false;
            last = seq;
            return true;
        }
    }
}
