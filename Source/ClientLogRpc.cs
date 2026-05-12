namespace DiscordTools
{
    internal static class ClientLogRpc
    {
        private static ZRoutedRpc? _registeredRpc;

        public static void Register()
        {
            if (ZRoutedRpc.instance == null || ReferenceEquals(_registeredRpc, ZRoutedRpc.instance))
            {
                return;
            }

            _registeredRpc = ZRoutedRpc.instance;
            ZRoutedRpc.instance.Register<ZPackage>(RpcNames.RequestLog, OnRequestLog);
            ZRoutedRpc.instance.Register<ZPackage>(RpcNames.LogMeta, ServerLogReceiver.OnMetadata);
            ZRoutedRpc.instance.Register<ZPackage>(RpcNames.LogChunk, ServerLogReceiver.OnChunk);
            ZRoutedRpc.instance.Register<ZPackage>(RpcNames.LogResult, ClientLogUploader.OnResult);
            DiscordToolsPlugin.Log.LogInfo("Registered client log RPC handlers.");
        }

        private static void OnRequestLog(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer())
            {
                return;
            }

            var requestId = pkg.ReadString();
            var reason = pkg.ReadString();
            var timeoutSeconds = pkg.ReadInt();
            ClientLogUploader.StartUpload(reason, requestId, timeoutSeconds, continueAfter: null);
        }
    }
}
