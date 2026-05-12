using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace DiscordTools
{
    internal static class ClientLogUploader
    {
        private static readonly Dictionary<string, UploadResult> Results = new();
        private static readonly HashSet<string> ActiveReasons = new();

        public static void StartUpload(string reason, string requestId, int timeoutSeconds, Action? continueAfter)
        {
            var plugin = DiscordToolsPlugin.Instance;
            if (plugin == null)
            {
                continueAfter?.Invoke();
                return;
            }

            if (!ShouldUpload())
            {
                continueAfter?.Invoke();
                return;
            }

            if (ActiveReasons.Contains(reason))
            {
                continueAfter?.Invoke();
                return;
            }

            ActiveReasons.Add(reason);
            plugin.StartCoroutine(UploadRoutine(reason, requestId, timeoutSeconds, continueAfter));
        }

        public static void OnResult(long sender, ZPackage pkg)
        {
            var requestId = pkg.ReadString();
            Results[requestId] = new UploadResult
            {
                Success = pkg.ReadBool(),
                Message = pkg.ReadString()
            };
        }

        public static bool ShouldUpload()
        {
            return ZNet.instance != null &&
                   ZRoutedRpc.instance != null &&
                   !ZNet.instance.IsServer() &&
                   ZNet.GetConnectionStatus() == ZNet.ConnectionStatus.Connected;
        }

        private static IEnumerator UploadRoutine(string reason, string requestId, int timeoutSeconds, Action? continueAfter)
        {
            PreparedLog? prepared = null;
            Exception? error = null;
            var prepareTask = Task.Run(() => PrepareLog(reason, requestId));
            while (!prepareTask.IsCompleted)
            {
                yield return null;
            }

            if (prepareTask.IsFaulted)
            {
                error = prepareTask.Exception?.GetBaseException();
            }
            else
            {
                prepared = prepareTask.Result;
            }

            if (error != null || prepared == null)
            {
                DiscordToolsPlugin.Log.LogWarning("Could not prepare client log: " + (error?.Message ?? "unknown error"));
                ActiveReasons.Remove(reason);
                continueAfter?.Invoke();
                yield break;
            }

            if (prepared.OriginalBytes > DiscordToolsPlugin.MaxOriginalBytes.Value)
            {
                DiscordToolsPlugin.Log.LogWarning("Client log is larger than MaxOriginalBytes. Upload skipped.");
                ActiveReasons.Remove(reason);
                continueAfter?.Invoke();
                yield break;
            }

            if (prepared.CompressedBytes > DiscordToolsPlugin.MaxCompressedBytes.Value)
            {
                DiscordToolsPlugin.Log.LogWarning("Compressed client log is larger than MaxCompressedBytes. Upload skipped.");
                ActiveReasons.Remove(reason);
                continueAfter?.Invoke();
                yield break;
            }

            SendMetadata(prepared);

            for (var i = 0; i < prepared.ChunkCount; i++)
            {
                var offset = i * prepared.ChunkSize;
                var count = Math.Min(prepared.ChunkSize, prepared.CompressedBytes - offset);
                var bytes = new byte[count];
                Buffer.BlockCopy(prepared.CompressedBytesArray, offset, bytes, 0, count);

                var chunk = new ZPackage();
                chunk.Write(prepared.RequestId);
                chunk.Write(i);
                chunk.Write(bytes);
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcNames.LogChunk, chunk);

                if (i % 8 == 7)
                {
                    yield return null;
                }
            }

            var deadline = Time.realtimeSinceStartup + Math.Max(1, timeoutSeconds);
            while (!Results.ContainsKey(prepared.RequestId) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (Results.TryGetValue(prepared.RequestId, out var result))
            {
                DiscordToolsPlugin.Log.LogInfo("Client log upload result: " + result.Message);
                Results.Remove(prepared.RequestId);
            }
            else
            {
                DiscordToolsPlugin.Log.LogWarning("Client log upload timed out waiting for server acknowledgement.");
            }

            ActiveReasons.Remove(reason);
            continueAfter?.Invoke();
        }

        private static PreparedLog PrepareLog(string reason, string requestId)
        {
            var logPath = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
            if (!File.Exists(logPath))
            {
                var parentLog = Path.GetFullPath(Path.Combine(Paths.BepInExRootPath, "..", "LogOutput.log"));
                if (File.Exists(parentLog))
                {
                    logPath = parentLog;
                }
            }

            if (!File.Exists(logPath))
            {
                throw new FileNotFoundException("Could not find LogOutput.log.", logPath);
            }

            byte[] original;
            using (var input = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var memory = new MemoryStream())
            {
                input.CopyTo(memory);
                original = memory.ToArray();
            }

            byte[] compressed;
            using (var memory = new MemoryStream())
            {
                using (var gzip = new GZipStream(memory, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                {
                    gzip.Write(original, 0, original.Length);
                }

                compressed = memory.ToArray();
            }

            var chunkSize = Mathf.Clamp(DiscordToolsPlugin.ChunkSizeBytes.Value, 4096, 262144);
            var fileInfo = new FileInfo(logPath);
            return new PreparedLog
            {
                RequestId = requestId,
                Reason = reason,
                LogPath = logPath,
                OriginalBytes = original.Length,
                CompressedBytes = compressed.Length,
                CompressedBytesArray = compressed,
                Sha256 = Sha256Hex(compressed),
                ChunkSize = chunkSize,
                ChunkCount = (int)Math.Ceiling(compressed.Length / (double)chunkSize),
                ClientPlayerName = GetLocalPlayerName(),
                LogModifiedUtc = fileInfo.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)
            };
        }

        private static void SendMetadata(PreparedLog prepared)
        {
            var pkg = new ZPackage();
            pkg.Write(prepared.RequestId);
            pkg.Write(prepared.Reason);
            pkg.Write(prepared.OriginalBytes);
            pkg.Write((long)prepared.CompressedBytes);
            pkg.Write(prepared.Sha256);
            pkg.Write(prepared.ChunkSize);
            pkg.Write(prepared.ChunkCount);
            pkg.Write(prepared.ClientPlayerName);
            pkg.Write(prepared.LogModifiedUtc);
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcNames.LogMeta, pkg);
        }

        private static string GetLocalPlayerName()
        {
            try
            {
                return Game.instance != null ? Game.instance.GetPlayerProfile().GetName() : "";
            }
            catch
            {
                return "";
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private sealed class PreparedLog
        {
            public string RequestId = "";
            public string Reason = "";
            public string LogPath = "";
            public long OriginalBytes;
            public int CompressedBytes;
            public byte[] CompressedBytesArray = Array.Empty<byte>();
            public string Sha256 = "";
            public int ChunkSize;
            public int ChunkCount;
            public string ClientPlayerName = "";
            public string LogModifiedUtc = "";
        }

        private sealed class UploadResult
        {
            public bool Success;
            public string Message = "";
        }
    }
}
