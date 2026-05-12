namespace DiscordTools
{
    internal static class ServerLogReceiver
    {
        private static readonly Dictionary<string, IncomingTransfer> Transfers = new();

        public static void OnMetadata(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            var peer = PlayerResolver.FindPeerBySender(sender);
            if (peer == null)
            {
                return;
            }

            var transfer = new IncomingTransfer
            {
                Sender = sender,
                Peer = peer,
                RequestId = pkg.ReadString(),
                Reason = SafeReason(pkg.ReadString()),
                OriginalBytes = pkg.ReadLong(),
                CompressedBytes = pkg.ReadLong(),
                Sha256 = pkg.ReadString(),
                ChunkSize = pkg.ReadInt(),
                ChunkCount = pkg.ReadInt(),
                ClientPlayerName = pkg.ReadString(),
                LogModifiedUtc = pkg.ReadString(),
                ReceivedAtUtc = DateTime.UtcNow
            };

            if (transfer.OriginalBytes > DiscordToolsPlugin.MaxOriginalBytes.Value)
            {
                SendResult(sender, transfer.RequestId, false, "Original log exceeds server limit.");
                return;
            }

            if (transfer.CompressedBytes > DiscordToolsPlugin.MaxCompressedBytes.Value)
            {
                SendResult(sender, transfer.RequestId, false, "Compressed log exceeds server limit.");
                return;
            }

            if (transfer.ChunkSize <= 0 || transfer.ChunkCount <= 0 || transfer.ChunkCount > 100000)
            {
                SendResult(sender, transfer.RequestId, false, "Invalid upload metadata.");
                return;
            }

            transfer.TempPath = LogArchive.GetIncomingPath(transfer.RequestId);
            transfer.ReceivedChunks = new bool[transfer.ChunkCount];
            Transfers[transfer.RequestId] = transfer;
            DiscordToolsPlugin.Log.LogInfo("Receiving client log from " + PlayerResolver.DescribePeer(peer) + " reason=" + transfer.Reason + " request=" + transfer.RequestId);
        }

        public static void OnChunk(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            var requestId = pkg.ReadString();
            if (!Transfers.TryGetValue(requestId, out var transfer) || transfer.Sender != sender)
            {
                return;
            }

            var index = pkg.ReadInt();
            var bytes = pkg.ReadByteArray();
            if (index < 0 || index >= transfer.ChunkCount || transfer.ReceivedChunks[index])
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(transfer.TempPath)!);
            using (var stream = new FileStream(transfer.TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                stream.Seek((long)index * transfer.ChunkSize, SeekOrigin.Begin);
                stream.Write(bytes, 0, bytes.Length);
            }

            transfer.ReceivedChunks[index] = true;
            transfer.ReceivedCount++;
            if (transfer.ReceivedCount < transfer.ChunkCount)
            {
                return;
            }

            FinishTransfer(transfer);
            Transfers.Remove(requestId);
        }

        private static void FinishTransfer(IncomingTransfer transfer)
        {
            try
            {
                var fileInfo = new FileInfo(transfer.TempPath);
                if (!fileInfo.Exists || fileInfo.Length != transfer.CompressedBytes)
                {
                    SendResult(transfer.Sender, transfer.RequestId, false, "Compressed byte count did not match.");
                    return;
                }

                var sha = Sha256Hex(File.ReadAllBytes(transfer.TempPath));
                if (!string.Equals(sha, transfer.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    SendResult(transfer.Sender, transfer.RequestId, false, "Compressed file hash did not match.");
                    return;
                }

                var archived = LogArchive.Archive(transfer);
                SendResult(transfer.Sender, transfer.RequestId, true, "Saved client log to " + archived.RelativeLogPath);

                var plugin = DiscordToolsPlugin.Instance;
                if (plugin != null && DiscordToolsPlugin.PostToBotApi.Value)
                {
                    plugin.StartCoroutine(BotApiClient.PostLogRoutine(archived));
                }
            }
            catch (Exception ex)
            {
                DiscordToolsPlugin.Log.LogError("Failed to finish client log transfer: " + ex);
                SendResult(transfer.Sender, transfer.RequestId, false, "Server failed to archive log: " + ex.Message);
            }
            finally
            {
                TryDelete(transfer.TempPath);
            }
        }

        private static void SendResult(long target, string requestId, bool success, string message)
        {
            var pkg = new ZPackage();
            pkg.Write(requestId);
            pkg.Write(success);
            pkg.Write(message);
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcNames.LogResult, pkg);
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string SafeReason(string reason)
        {
            reason = (reason ?? "").Trim().ToLowerInvariant();
            return reason == "logout" || reason == "quit" || reason == "manual" ? reason : "unknown";
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Cleanup failure is not fatal; retention cleanup can remove it later.
            }
        }
    }
}
