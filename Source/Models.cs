using System;

namespace DiscordTools
{
    internal sealed class IncomingTransfer
    {
        public long Sender;
        public ZNetPeer Peer = null!;
        public string RequestId = "";
        public string Reason = "";
        public long OriginalBytes;
        public long CompressedBytes;
        public string Sha256 = "";
        public int ChunkSize;
        public int ChunkCount;
        public string ClientPlayerName = "";
        public string LogModifiedUtc = "";
        public DateTime ReceivedAtUtc;
        public string TempPath = "";
        public bool[] ReceivedChunks = Array.Empty<bool>();
        public int ReceivedCount;
    }

    internal sealed class ArchivedLog
    {
        public string PlayerId = "";
        public string PlayerName = "";
        public string Reason = "";
        public string RequestId = "";
        public DateTime ReceivedAtUtc;
        public long OriginalBytes;
        public long CompressedBytes;
        public string Sha256 = "";
        public string LogPath = "";
        public string MetadataPath = "";
        public string RelativeLogPath = "";
        public string RelativeMetadataPath = "";
    }
}
