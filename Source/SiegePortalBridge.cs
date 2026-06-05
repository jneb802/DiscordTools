using System;
using HarmonyLib;

namespace DiscordTools
{
    internal static class SiegePortalBridge
    {
        private const int ProtocolVersion = 1;
        internal const string SiegeIdZdoKey = "valheimCreativeSiegeId";
        internal const string SiegeTagPrefix = "siege:";

        internal static bool TryHandle(TeleportWorld portal, Player player)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null)
            {
                return false;
            }

            if (player == null || Player.m_localPlayer == null || player != Player.m_localPlayer)
            {
                return false;
            }

            ZNetView nview = portal.GetComponent<ZNetView>();
            ZDO? portalZdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (portalZdo == null || !TryGetSiegeId(portalZdo, out string siegeId))
            {
                return false;
            }

            if (player.m_nview == null || !player.m_nview.IsValid())
            {
                player.Message(MessageHud.MessageType.Center, "Siege portal failed: character was not ready.");
                return true;
            }

            ZDO? playerZdo = player.m_nview.GetZDO();
            if (playerZdo == null)
            {
                player.Message(MessageHud.MessageType.Center, "Siege portal failed: character was not ready.");
                return true;
            }

            ZPackage package = new();
            package.Write(ProtocolVersion);
            package.Write(playerZdo.m_uid);
            package.Write(siegeId);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcNames.SiegePortalEnter, package);
            player.Message(MessageHud.MessageType.Center, $"Entering siege: {siegeId}");
            DiscordToolsPlugin.Log.LogInfo($"Requested siege portal enter for {siegeId}.");
            return true;
        }

        private static bool TryGetSiegeId(ZDO portalZdo, out string siegeId)
        {
            siegeId = portalZdo.GetString(SiegeIdZdoKey).Trim();
            if (!string.IsNullOrWhiteSpace(siegeId))
            {
                return true;
            }

            string tag = portalZdo.GetString(ZDOVars.s_tag).Trim();
            if (tag.StartsWith(SiegeTagPrefix, StringComparison.OrdinalIgnoreCase))
            {
                siegeId = tag.Substring(SiegeTagPrefix.Length).Trim();
                return !string.IsNullOrWhiteSpace(siegeId);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Teleport))]
    internal static class TeleportWorldSiegePortalPatch
    {
        private static bool Prefix(TeleportWorld __instance, Player player)
        {
            return !SiegePortalBridge.TryHandle(__instance, player);
        }
    }
}
