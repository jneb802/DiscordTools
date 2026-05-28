using System;
using System.Text.RegularExpressions;

namespace DiscordTools
{
    internal static class LinkCommandHandler
    {
        private static readonly Regex CodePattern = new("^[A-Za-z0-9_-]{4,64}$", RegexOptions.Compiled);

        public static bool TryHandle(Chat chat)
        {
            var raw = chat.m_input != null ? chat.m_input.text : "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var command = DiscordToolsPlugin.LinkCommand.Value.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                command = "!link";
            }

            var trimmed = raw.Trim();
            if (!trimmed.Equals(command, StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var code = trimmed.Length > command.Length ? trimmed.Substring(command.Length).Trim() : "";
            if (!CodePattern.IsMatch(code))
            {
                chat.AddString("Usage: " + command + " CODE");
                ClearInput(chat);
                return true;
            }

            if (!LinkRpc.TrySendRequest(code, out var message))
            {
                chat.AddString(message);
                ClearInput(chat);
                return true;
            }

            chat.AddString("Sending Discord link code to the server.");
            ClearInput(chat);
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
