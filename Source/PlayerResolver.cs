using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DiscordTools
{
    internal static class PlayerResolver
    {
        public static List<ZNetPeer> FindPeers(string query)
        {
            var result = new List<ZNetPeer>();
            if (ZNet.instance == null)
            {
                return result;
            }

            var normalized = Normalize(query);
            foreach (var peer in ZNet.instance.GetConnectedPeers())
            {
                if (!peer.IsReady())
                {
                    continue;
                }

                var hostName = SafeHostName(peer);
                var stableId = StablePlayerId(peer);
                if (Normalize(peer.m_playerName) == normalized ||
                    Normalize(hostName) == normalized ||
                    Normalize(stableId) == normalized ||
                    DigitsOnly(hostName) == DigitsOnly(query) && DigitsOnly(query).Length > 0)
                {
                    result.Add(peer);
                }
            }

            if (result.Count > 0)
            {
                return result;
            }

            foreach (var peer in ZNet.instance.GetConnectedPeers())
            {
                if (peer.IsReady() && Normalize(peer.m_playerName).Contains(normalized))
                {
                    result.Add(peer);
                }
            }

            return result;
        }

        public static ZNetPeer? FindPeerBySender(long sender)
        {
            if (ZNet.instance == null)
            {
                return null;
            }

            return ZNet.instance.GetConnectedPeers().FirstOrDefault(peer => peer.m_uid == sender);
        }

        public static string DescribePeer(ZNetPeer peer)
        {
            return peer.m_playerName + " (" + StablePlayerId(peer) + ")";
        }

        public static string StablePlayerId(ZNetPeer peer)
        {
            var hostName = SafeHostName(peer);
            return string.IsNullOrWhiteSpace(hostName) ? peer.m_uid.ToString(CultureInfo.InvariantCulture) : hostName;
        }

        public static string SafeHostName(ZNetPeer peer)
        {
            try
            {
                return peer.m_socket?.GetHostName() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim().ToLowerInvariant();
        }

        private static string DigitsOnly(string value)
        {
            return new string((value ?? "").Where(char.IsDigit).ToArray());
        }
    }
}
