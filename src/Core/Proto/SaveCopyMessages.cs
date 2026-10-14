using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // A message that travels as a 4-byte big-endian index and then raw bytes, with no JSON and no base64 (Envelope encodes
    // and decodes it itself). A 15 MB world or save costs no JSON parsing.
    public abstract class ChunkMessage : Message
    {
        public int Index;
        public byte[] Data;   // 1..MaxData bytes; every chunk but the last is exactly MaxData (the receiver checks)

        protected abstract int MaxData { get; }

        public override string Validate()
        {
            if (Index < 0) return "Index is negative";
            if (Data == null || Data.Length < 1) return "Data is empty";
            return Data.Length > MaxData ? "Data is longer than a chunk" : null;
        }
    }

    // Host -> client, before the chunks of a save copy: what is coming, so the receiver can judge it before storing a byte.
    public sealed class SaveCopyHeader : Message
    {
        public const int MaxTitleChars = 120, MaxHostNameChars = 24, MaxHolders = 64;

        public string SaveId, Title, HostName;
        public long CompressedSize, PlainSize;
        public int Chunks;
        public string Sha256;
        public SaveInfoData Info;
        public Dictionary<string, string> LastHolders = new Dictionary<string, string>();   // agency id -> name

        public override string Validate()
        {
            if (!SaveNames.IsSaveId(SaveId)) return "the copy's save id is not 16 lower-case hex digits";
            if (Title == null || Title.Length > MaxTitleChars) return "the copy's title is missing or too long";
            if (HostName == null || HostName.Length > MaxHostNameChars) return "the copy's host name is missing or too long";
            if (CompressedSize < 1 || CompressedSize > JoinLimits.MaxCompressed) return "the copy's size is outside the cap";
            if (PlainSize < 1 || PlainSize > JoinLimits.MaxUncompressed) return "the copy's plain size is outside the cap";
            if (Chunks != ChunkMath.Count(CompressedSize)) return "the copy's chunk count does not fit its size";
            if (!PlayerKeys.IsHex64(Sha256)) return "the copy's hash is not 64 lower-case hex digits";
            if (Info == null) return "the copy has no save info";
            string why = Info.Validate();
            if (why != null) return why;
            if (LastHolders == null || LastHolders.Count > MaxHolders) return "the copy's last-holder list is missing or too long";
            foreach (KeyValuePair<string, string> kv in LastHolders)
                if (!LobbyIds.IsContentId(kv.Key) || kv.Value == null || !LobbyLimits.NameFits(kv.Value)) return "a last holder is not valid";
            return null;
        }
    }

    // Host -> client: one chunk of the compressed save (BINARY, as SnapshotChunk).
    public sealed class SaveCopyChunk : ChunkMessage
    {
        protected override int MaxData { get { return JoinLimits.ChunkSize; } }
    }

    // Client -> host: how many chunks have arrived, so the host sends no further than the window.
    public sealed class SaveCopyAck : Message
    {
        public int Next;
        public override string Validate()
        {
            int most = (int)(JoinLimits.MaxCompressed / JoinLimits.ChunkSize);
            return Next < 0 || Next > most ? "Next outside 0.." + most : null;
        }
    }
}
