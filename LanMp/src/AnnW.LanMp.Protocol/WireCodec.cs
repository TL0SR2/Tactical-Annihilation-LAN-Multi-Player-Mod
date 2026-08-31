using System;
using System.IO;
using System.Net;
using System.Text;

namespace AnnW.LanMp.Protocol
{
    public enum PeerRole
    {
        None,
        Host,
        Guest
    }

    public enum MsgType : byte
    {
        Hello = 1,
        Welcome = 2,
        Ping = 3,
        Pong = 4,
        LobbyDraft = 10,
        LobbyReady = 11,
        LobbyCanStart = 12,
        LobbyStart = 13,
        LobbyReject = 14,
        SeatEditRequest = 15,
        SeatEditNack = 16,
        Intent = 20,
        Command = 21,
        IntentNack = 22,
        StateHash = 30,
        StateSnapshot = 31,
        SnapshotRequest = 32,
        MatchEnd = 40,
        MatchAbort = 41,
        Disconnect = 255
    }

    public sealed class Envelope
    {
        public ushort ProtocolVersion = WireCodec.ProtocolVersion;
        public MsgType Type;
        public string BattleId = "";
        public uint Seq;
        public string PayloadJson = "{}";
    }

    public static class WireCodec
    {
        public const ushort ProtocolVersion = 3;

        public static byte[] EncodeFrame(Envelope env)
        {
            var json = JsonUtil.ToJson(env);
            var bytes = Encoding.UTF8.GetBytes(json);
            var len = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length));
            var frame = new byte[4 + bytes.Length];
            Buffer.BlockCopy(len, 0, frame, 0, 4);
            Buffer.BlockCopy(bytes, 0, frame, 4, bytes.Length);
            return frame;
        }

        public static bool TryDecodeFrame(byte[] buffer, int offset, int count, out Envelope env, out int consumed)
        {
            env = null;
            consumed = 0;
            if (count < 4)
                return false;
            var len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buffer, offset));
            if (len <= 0 || len > 2_000_000)
                throw new InvalidDataException("Invalid frame length " + len);
            if (count < 4 + len)
                return false;
            var json = Encoding.UTF8.GetString(buffer, offset + 4, len);
            env = JsonUtil.FromJson<Envelope>(json);
            consumed = 4 + len;
            return env != null;
        }

        public static bool TryParseEndpoint(string address, out string host, out int port)
        {
            host = null;
            port = 0;
            if (string.IsNullOrWhiteSpace(address))
                return false;
            var idx = address.LastIndexOf(':');
            if (idx <= 0 || idx == address.Length - 1)
                return false;
            host = address.Substring(0, idx).Trim();
            return int.TryParse(address.Substring(idx + 1), out port);
        }
    }
}
