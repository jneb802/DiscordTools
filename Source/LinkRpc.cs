using System;

namespace DiscordTools
{
    internal static class LinkRpc
    {
        public static bool TrySendRequest(string code, out string message)
        {
            message = "";
            if (ZNet.instance == null || ZRoutedRpc.instance == null || ZNet.instance.IsServer() ||
                ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected)
            {
                message = "You must be connected to a server before linking Discord.";
                return false;
            }

            var requestId = Guid.NewGuid().ToString("N");
            var pkg = new ZPackage();
            pkg.Write(requestId);
            pkg.Write(code);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcNames.LinkRequest, pkg);
            return true;
        }

        public static void OnRequest(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            var requestId = pkg.ReadString();
            var code = pkg.ReadString();
            var peer = PlayerResolver.FindPeerBySender(sender);
            if (peer == null)
            {
                SendResult(sender, requestId, false, "The server could not identify your Valheim connection.");
                return;
            }

            var plugin = DiscordToolsPlugin.Instance;
            if (plugin == null)
            {
                SendResult(sender, requestId, false, "DiscordTools is not ready on the server.");
                return;
            }

            var link = new LinkRequest
            {
                Sender = sender,
                RequestId = requestId,
                Code = code,
                PlayerId = PlayerResolver.StablePlayerId(peer),
                PlayerName = peer.m_playerName ?? "",
                Endpoint = PlayerResolver.SafeEndPoint(peer),
                PlatformDisplayName = PlayerResolver.PlatformDisplayName(peer),
                ReceivedAtUtc = DateTime.UtcNow
            };

            DiscordToolsPlugin.Log.LogInfo("Received Discord link code from " + PlayerResolver.DescribePeer(peer) + ".");
            plugin.StartCoroutine(BotApiClient.PostLinkRoutine(link, SendResult));
        }

        public static void OnResult(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer())
            {
                return;
            }

            var requestId = pkg.ReadString();
            var success = pkg.ReadBool();
            var message = pkg.ReadString();
            DiscordToolsPlugin.Log.LogInfo("Discord link result " + requestId + ": " + message);
            Chat.instance?.AddString(success ? message : "Discord link failed: " + message);
        }

        private static void SendResult(long target, string requestId, bool success, string message)
        {
            var pkg = new ZPackage();
            pkg.Write(requestId);
            pkg.Write(success);
            pkg.Write(message);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcNames.LinkResult, pkg);
        }
    }
}
