using System;

namespace DiscordTools
{
    internal static class CreativeCommandForwarder
    {
        private const int ProtocolVersion = 1;
        private const string CreativeCommand = "!creative";
        private const string ReturnCommand = "!return";

        public static bool TryHandle(Chat chat)
        {
            string raw = chat.m_input != null ? chat.m_input.text : "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            string trimmed = raw.Trim();
            if (!IsCreativeCommand(trimmed))
            {
                return false;
            }

            if (!TrySend(trimmed, out string message))
            {
                chat.AddString("[Creative] " + message);
                ClearInput(chat);
                return true;
            }

            chat.AddString("[Creative] Sending command to the server.");
            ClearInput(chat);
            return true;
        }

        private static bool IsCreativeCommand(string text)
        {
            return text.Equals(CreativeCommand, StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith(CreativeCommand + " ", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals(ReturnCommand, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TrySend(string text, out string message)
        {
            message = string.Empty;
            if (ZRoutedRpc.instance == null)
            {
                message = "Network is not ready yet.";
                return false;
            }

            Player player = Player.m_localPlayer;
            if (player == null || player.m_nview == null || !player.m_nview.IsValid())
            {
                message = "Local player is not ready yet.";
                return false;
            }

            ZDO playerZdo = player.m_nview.GetZDO();
            if (playerZdo == null)
            {
                message = "Local player character is not ready yet.";
                return false;
            }

            UserInfo userInfo = UserInfo.GetLocalUser();
            ZPackage pkg = new();
            pkg.Write(ProtocolVersion);
            pkg.Write(playerZdo.m_uid);
            userInfo.Serialize(ref pkg);
            pkg.Write(text);

            ZRoutedRpc.instance.InvokeRoutedRPC(RpcNames.CreativeCommand, pkg);
            return true;
        }

        private static void ClearInput(Chat chat)
        {
            if (chat.m_input == null)
            {
                return;
            }

            chat.m_input.text = "";
            chat.m_input.gameObject.SetActive(false);
        }
    }
}
