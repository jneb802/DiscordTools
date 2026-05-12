namespace DiscordTools
{
    internal static class ClientLogCommand
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            _ = new Terminal.ConsoleCommand(
                DiscordToolsPlugin.CommandName.Value,
                "[playerNameOrSteamID] requests a connected client's full BepInEx log",
                Execute,
                onlyServer: true,
                remoteCommand: true,
                optionsFetcher: GetPlayerOptions,
                alwaysRefreshTabOptions: true);
        }

        private static object Execute(Terminal.ConsoleEventArgs args)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return "This command can only run on the server.";
            }

            if (args.Length < 2 || string.IsNullOrWhiteSpace(args.ArgsAll))
            {
                return "Usage: " + DiscordToolsPlugin.CommandName.Value + " {playerNameOrSteamID}";
            }

            ClientLogRpc.Register();
            var query = args.ArgsAll.Trim();
            var matches = PlayerResolver.FindPeers(query);
            if (matches.Count == 0)
            {
                return "No connected player matched '" + query + "'.";
            }

            if (matches.Count > 1)
            {
                return "Multiple connected players matched '" + query + "': " + string.Join(", ", matches.Select(PlayerResolver.DescribePeer).ToArray());
            }

            var peer = matches[0];
            var requestId = Guid.NewGuid().ToString("N");
            var pkg = new ZPackage();
            pkg.Write(requestId);
            pkg.Write("manual");
            pkg.Write(DiscordToolsPlugin.ManualRequestTimeoutSeconds.Value);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, RpcNames.RequestLog, pkg);

            args.Context?.AddString("Requested client log from " + PlayerResolver.DescribePeer(peer) + ".");
            return true;
        }

        private static List<string> GetPlayerOptions()
        {
            if (ZNet.instance == null)
            {
                return new List<string>();
            }

            return ZNet.instance.GetConnectedPeers()
                .Where(peer => peer.IsReady())
                .Select(peer => peer.m_playerName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct()
                .OrderBy(name => name)
                .ToList();
        }
    }
}
