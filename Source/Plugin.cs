using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace DiscordTools
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class DiscordToolsPlugin : BaseUnityPlugin
    {
        private const string ModName = "DiscordTools";
        private const string ModVersion = "1.0.0";
        private const string Author = "warpalicious";
        private const string ModGUID = Author + "." + ModName;
        private const string BotApiUrlEnv = "VALHEIM_CLIENT_LOGS_BOT_API_URL";
        private const string BotApiKeyEnv = "VALHEIM_CLIENT_LOGS_BOT_API_KEY";

        private readonly Harmony _harmony = new(ModGUID);
        private DateTime _lastReloadTime;
        private const long ReloadDelayTicks = 10000000;

        public static DiscordToolsPlugin? Instance { get; private set; }
        public static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModName);

        internal static ConfigEntry<string> CommandName = null!;
        internal static ConfigEntry<string> OutputDirectory = null!;
        internal static ConfigEntry<int> ChunkSizeBytes = null!;
        internal static ConfigEntry<int> ManualRequestTimeoutSeconds = null!;
        internal static ConfigEntry<int> LogoutUploadTimeoutSeconds = null!;
        internal static ConfigEntry<int> QuitUploadTimeoutSeconds = null!;
        internal static ConfigEntry<int> RetentionDays = null!;
        internal static ConfigEntry<bool> DeleteOldLogsOnStartup = null!;
        internal static ConfigEntry<long> MaxOriginalBytes = null!;
        internal static ConfigEntry<long> MaxCompressedBytes = null!;
        internal static ConfigEntry<bool> PostToBotApi = null!;
        internal static ConfigEntry<string> BotApiUrl = null!;
        internal static ConfigEntry<string> BotApiKey = null!;

        internal static string GetBotApiUrl()
        {
            var envValue = Environment.GetEnvironmentVariable(BotApiUrlEnv);
            return string.IsNullOrWhiteSpace(envValue) ? BotApiUrl.Value : envValue.Trim();
        }

        internal static string GetBotApiKey()
        {
            var envValue = Environment.GetEnvironmentVariable(BotApiKeyEnv);
            return string.IsNullOrWhiteSpace(envValue) ? BotApiKey.Value : envValue.Trim();
        }

        public void Awake()
        {
            Instance = this;
            BindConfig();
            ClientLogCommand.Register();
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            SetupWatcher();
            LogArchive.EnsureDirectories();
            if (DeleteOldLogsOnStartup.Value)
            {
                LogArchive.CleanupOldLogs();
            }
        }

        private void OnDestroy()
        {
            Config.Save();
            _harmony.UnpatchSelf();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void BindConfig()
        {
            CommandName = Config.Bind("General", "CommandName", "client-logs", "Server command used by RCON to request a connected client's log.");
            OutputDirectory = Config.Bind("General", "OutputDirectory", "client-logs", "Log archive directory. Relative paths are placed under BepInEx.");
            ChunkSizeBytes = Config.Bind("General", "ChunkSizeBytes", 32768, "Compressed upload chunk size sent through Valheim networking.");
            ManualRequestTimeoutSeconds = Config.Bind("General", "ManualRequestTimeoutSeconds", 120, "How long the client waits for the server to acknowledge a manual log request.");
            LogoutUploadTimeoutSeconds = Config.Bind("General", "LogoutUploadTimeoutSeconds", 30, "How long logout waits for log upload before continuing.");
            QuitUploadTimeoutSeconds = Config.Bind("General", "QuitUploadTimeoutSeconds", 10, "How long normal quit waits for log upload before continuing.");
            RetentionDays = Config.Bind("General", "RetentionDays", 30, "Delete archived logs older than this many days. Set 0 to keep logs forever.");
            DeleteOldLogsOnStartup = Config.Bind("General", "DeleteOldLogsOnStartup", true, "Run retention cleanup when the mod loads.");

            MaxOriginalBytes = Config.Bind("Limits", "MaxOriginalBytes", 104857600L, "Largest uncompressed client log accepted, in bytes.");
            MaxCompressedBytes = Config.Bind("Limits", "MaxCompressedBytes", 52428800L, "Largest compressed client log accepted, in bytes.");
            PostToBotApi = Config.Bind("BotApi", "PostToBotApi", true, "Upload received logs to a compatible Discord bot API.");
            BotApiUrl = Config.Bind("BotApi", "ApiUrl", "", "Compatible bot client-log upload endpoint. Prefer the VALHEIM_CLIENT_LOGS_BOT_API_URL environment variable on dedicated servers.");
            BotApiKey = Config.Bind("BotApi", "ApiKey", "", "API key sent to the bot in the X-API-Key header. Prefer the VALHEIM_CLIENT_LOGS_BOT_API_KEY environment variable on dedicated servers.");
        }

        private void SetupWatcher()
        {
            _lastReloadTime = DateTime.Now;
            var watcher = new FileSystemWatcher(Paths.ConfigPath, ModGUID + ".cfg");
            watcher.Changed += ReadConfigValues;
            watcher.Created += ReadConfigValues;
            watcher.Renamed += ReadConfigValues;
            watcher.IncludeSubdirectories = true;
            watcher.EnableRaisingEvents = true;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            var now = DateTime.Now;
            var time = now.Ticks - _lastReloadTime.Ticks;
            var configPath = Path.Combine(Paths.ConfigPath, ModGUID + ".cfg");
            if (!File.Exists(configPath) || time < ReloadDelayTicks)
            {
                return;
            }

            try
            {
                Log.LogInfo("Reloading configuration.");
                Config.Reload();
                LogArchive.EnsureDirectories();
            }
            catch (Exception ex)
            {
                Log.LogError("Failed to reload configuration: " + ex.Message);
            }

            _lastReloadTime = now;
        }
    }

    internal static class RpcNames
    {
        public const string RequestLog = "VCL_RequestLog";
        public const string LogMeta = "VCL_LogMeta";
        public const string LogChunk = "VCL_LogChunk";
        public const string LogResult = "VCL_LogResult";
    }

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

    internal sealed class IncomingTransfer
    {
        public long Sender;
        public ZNetPeer Peer = null!;
        public string RequestId = "";
        public string Reason = "";
        public long OriginalBytes;
        public long CompressedBytes;
        public string Sha256 = "";
        public int ChunkSize;
        public int ChunkCount;
        public string ClientPlayerName = "";
        public string LogModifiedUtc = "";
        public DateTime ReceivedAtUtc;
        public string TempPath = "";
        public bool[] ReceivedChunks = Array.Empty<bool>();
        public int ReceivedCount;
    }

    internal sealed class ArchivedLog
    {
        public string PlayerId = "";
        public string PlayerName = "";
        public string Reason = "";
        public string RequestId = "";
        public DateTime ReceivedAtUtc;
        public long OriginalBytes;
        public long CompressedBytes;
        public string Sha256 = "";
        public string LogPath = "";
        public string MetadataPath = "";
        public string RelativeLogPath = "";
        public string RelativeMetadataPath = "";
    }

    internal static class LogArchive
    {
        public static string Root => ResolveRoot();
        private static string PlayersDir => Path.Combine(Root, "players");
        private static string IndexDir => Path.Combine(Root, "index");
        private static string IncomingDir => Path.Combine(Root, "incoming");
        private static string BotUploadFailedDir => Path.Combine(Root, "bot-upload-failed");

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(PlayersDir);
            Directory.CreateDirectory(IndexDir);
            Directory.CreateDirectory(IncomingDir);
            Directory.CreateDirectory(BotUploadFailedDir);
        }

        public static string GetIncomingPath(string requestId)
        {
            EnsureDirectories();
            return Path.Combine(IncomingDir, SafePathSegment(requestId) + ".tmp");
        }

        public static ArchivedLog Archive(IncomingTransfer transfer)
        {
            EnsureDirectories();
            var playerId = SafePathSegment(PlayerResolver.StablePlayerId(transfer.Peer));
            var playerName = string.IsNullOrWhiteSpace(transfer.Peer.m_playerName) ? transfer.ClientPlayerName : transfer.Peer.m_playerName;
            playerName = string.IsNullOrWhiteSpace(playerName) ? "unknown" : playerName;

            var month = transfer.ReceivedAtUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var stamp = transfer.ReceivedAtUtc.ToString("yyyy-MM-dd_HH-mm-ss'Z'", CultureInfo.InvariantCulture);
            var fileStem = stamp + "_" + transfer.Reason + "_" + SafePathSegment(playerName) + "_LogOutput";
            var playerDir = Path.Combine(PlayersDir, playerId);
            var logDir = Path.Combine(playerDir, "logs", month);
            Directory.CreateDirectory(logDir);

            var logPath = Path.Combine(logDir, fileStem + ".log.gz");
            var metadataPath = Path.Combine(logDir, fileStem + ".json");
            File.Copy(transfer.TempPath, logPath, overwrite: true);

            var archived = new ArchivedLog
            {
                PlayerId = playerId,
                PlayerName = playerName,
                Reason = transfer.Reason,
                RequestId = transfer.RequestId,
                ReceivedAtUtc = transfer.ReceivedAtUtc,
                OriginalBytes = transfer.OriginalBytes,
                CompressedBytes = transfer.CompressedBytes,
                Sha256 = transfer.Sha256,
                LogPath = logPath,
                MetadataPath = metadataPath,
                RelativeLogPath = ToRelative(logPath),
                RelativeMetadataPath = ToRelative(metadataPath)
            };

            File.WriteAllText(metadataPath, BuildLogMetadataJson(archived, transfer), Encoding.UTF8);
            WritePlayerFiles(playerDir, archived);
            RebuildIndexes();
            return archived;
        }

        public static void CleanupOldLogs()
        {
            var days = DiscordToolsPlugin.RetentionDays.Value;
            if (days <= 0 || !Directory.Exists(PlayersDir))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-days);
            foreach (var file in Directory.GetFiles(PlayersDir, "*", SearchOption.AllDirectories))
            {
                if ((file.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) &&
                    File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    TryDelete(file);
                }
            }

            RebuildIndexes();
        }

        public static void MarkBotUploadFailed(ArchivedLog log, string message)
        {
            Directory.CreateDirectory(BotUploadFailedDir);
            var marker = Path.Combine(BotUploadFailedDir, Path.GetFileName(log.MetadataPath));
            File.WriteAllText(marker, JsonObject(new Dictionary<string, string>
            {
                ["playerId"] = log.PlayerId,
                ["playerName"] = log.PlayerName,
                ["reason"] = log.Reason,
                ["receivedAtUtc"] = log.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["path"] = log.RelativeLogPath,
                ["error"] = message
            }), Encoding.UTF8);
        }

        private static void WritePlayerFiles(string playerDir, ArchivedLog latest)
        {
            var playerJson = Path.Combine(playerDir, "player.json");
            var knownNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { latest.PlayerName };
            if (File.Exists(playerJson))
            {
                var previous = File.ReadAllText(playerJson);
                foreach (Match match in Regex.Matches(previous, "\"knownNames\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline))
                {
                    foreach (Match nameMatch in Regex.Matches(match.Groups[1].Value, "\"(.*?)\""))
                    {
                        knownNames.Add(UnescapeJson(nameMatch.Groups[1].Value));
                    }
                }
            }

            File.WriteAllText(playerJson, BuildPlayerJson(latest, knownNames), Encoding.UTF8);
            File.WriteAllText(Path.Combine(playerDir, "latest.json"), JsonObject(new Dictionary<string, string>
            {
                ["latestLog"] = latest.RelativeLogPath,
                ["latestMetadata"] = latest.RelativeMetadataPath,
                ["receivedAtUtc"] = latest.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["reason"] = latest.Reason,
                ["playerName"] = latest.PlayerName
            }), Encoding.UTF8);
        }

        private static void RebuildIndexes()
        {
            EnsureDirectories();
            var entries = ReadAllMetadata().OrderByDescending(entry => entry.ReceivedAtUtc).ToList();
            var byId = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byName = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                byId[entry.PlayerId] = "players/" + entry.PlayerId;
                var key = entry.PlayerName.Trim().ToLowerInvariant();
                if (key.Length == 0)
                {
                    continue;
                }

                if (!byName.TryGetValue(key, out var ids))
                {
                    ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    byName[key] = ids;
                }

                ids.Add(entry.PlayerId);
            }

            File.WriteAllText(Path.Combine(IndexDir, "players.json"), BuildPlayersIndexJson(byId, byName), Encoding.UTF8);
            File.WriteAllText(Path.Combine(IndexDir, "recent.json"), BuildRecentJson(entries.Take(100).ToList()), Encoding.UTF8);
        }

        private static List<MetadataEntry> ReadAllMetadata()
        {
            var entries = new List<MetadataEntry>();
            if (!Directory.Exists(PlayersDir))
            {
                return entries;
            }

            foreach (var file in Directory.GetFiles(PlayersDir, "*.json", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).Equals("player.json", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).Equals("latest.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(file);
                    var entry = new MetadataEntry
                    {
                        PlayerId = JsonValue(json, "playerId"),
                        PlayerName = JsonValue(json, "playerName"),
                        Reason = JsonValue(json, "reason"),
                        Path = JsonValue(json, "logPath")
                    };

                    if (!DateTime.TryParse(JsonValue(json, "receivedAtUtc"), null, DateTimeStyles.RoundtripKind, out entry.ReceivedAtUtc))
                    {
                        entry.ReceivedAtUtc = File.GetLastWriteTimeUtc(file);
                    }

                    if (!string.IsNullOrWhiteSpace(entry.PlayerId))
                    {
                        entries.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    DiscordToolsPlugin.Log.LogWarning("Could not read metadata " + file + ": " + ex.Message);
                }
            }

            return entries;
        }

        private static string ResolveRoot()
        {
            var configured = DiscordToolsPlugin.OutputDirectory.Value;
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = "client-logs";
            }

            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Paths.BepInExRootPath, configured);
        }

        private static string BuildLogMetadataJson(ArchivedLog archived, IncomingTransfer transfer)
        {
            return JsonObject(new Dictionary<string, string>
            {
                ["requestId"] = archived.RequestId,
                ["reason"] = archived.Reason,
                ["playerId"] = archived.PlayerId,
                ["playerName"] = archived.PlayerName,
                ["clientPlayerName"] = transfer.ClientPlayerName,
                ["receivedAtUtc"] = archived.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["logModifiedUtc"] = transfer.LogModifiedUtc,
                ["originalBytes"] = archived.OriginalBytes.ToString(CultureInfo.InvariantCulture),
                ["compressedBytes"] = archived.CompressedBytes.ToString(CultureInfo.InvariantCulture),
                ["compression"] = "gzip",
                ["sha256"] = archived.Sha256,
                ["logPath"] = archived.RelativeLogPath,
                ["metadataPath"] = archived.RelativeMetadataPath
            });
        }

        private static string BuildPlayerJson(ArchivedLog latest, IEnumerable<string> knownNames)
        {
            var builder = new StringBuilder();
            builder.Append("{\n");
            AppendJsonProperty(builder, "playerId", latest.PlayerId, comma: true);
            AppendJsonProperty(builder, "lastKnownName", latest.PlayerName, comma: true);
            builder.Append("  \"knownNames\": [");
            builder.Append(string.Join(", ", knownNames.Select(name => "\"" + EscapeJson(name) + "\"").ToArray()));
            builder.Append("],\n");
            AppendJsonProperty(builder, "lastSeenUtc", latest.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture), comma: false);
            builder.Append("}\n");
            return builder.ToString();
        }

        private static string BuildPlayersIndexJson(SortedDictionary<string, string> byId, SortedDictionary<string, SortedSet<string>> byName)
        {
            var builder = new StringBuilder();
            builder.Append("{\n  \"byId\": {\n");
            AppendStringMap(builder, byId, 4);
            builder.Append("\n  },\n  \"byName\": {\n");
            var index = 0;
            foreach (var pair in byName)
            {
                builder.Append("    \"").Append(EscapeJson(pair.Key)).Append("\": [");
                builder.Append(string.Join(", ", pair.Value.Select(id => "\"" + EscapeJson(id) + "\"").ToArray()));
                builder.Append("]");
                if (++index < byName.Count)
                {
                    builder.Append(",");
                }
                builder.Append("\n");
            }
            builder.Append("  }\n}\n");
            return builder.ToString();
        }

        private static string BuildRecentJson(List<MetadataEntry> entries)
        {
            var builder = new StringBuilder();
            builder.Append("[\n");
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                builder.Append("  {\n");
                AppendJsonProperty(builder, "playerId", entry.PlayerId, comma: true, indent: 4);
                AppendJsonProperty(builder, "playerName", entry.PlayerName, comma: true, indent: 4);
                AppendJsonProperty(builder, "reason", entry.Reason, comma: true, indent: 4);
                AppendJsonProperty(builder, "receivedAtUtc", entry.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture), comma: true, indent: 4);
                AppendJsonProperty(builder, "path", entry.Path, comma: false, indent: 4);
                builder.Append("  }");
                if (i + 1 < entries.Count)
                {
                    builder.Append(",");
                }
                builder.Append("\n");
            }
            builder.Append("]\n");
            return builder.ToString();
        }

        private static string JsonObject(Dictionary<string, string> values)
        {
            var builder = new StringBuilder();
            builder.Append("{\n");
            var index = 0;
            foreach (var pair in values)
            {
                AppendJsonProperty(builder, pair.Key, pair.Value, ++index < values.Count);
            }
            builder.Append("}\n");
            return builder.ToString();
        }

        private static void AppendStringMap(StringBuilder builder, SortedDictionary<string, string> map, int indent)
        {
            var spaces = new string(' ', indent);
            var index = 0;
            foreach (var pair in map)
            {
                builder.Append(spaces).Append("\"").Append(EscapeJson(pair.Key)).Append("\": \"").Append(EscapeJson(pair.Value)).Append("\"");
                if (++index < map.Count)
                {
                    builder.Append(",");
                }
                builder.Append("\n");
            }
        }

        private static void AppendJsonProperty(StringBuilder builder, string key, string value, bool comma, int indent = 2)
        {
            builder.Append(new string(' ', indent))
                .Append("\"").Append(EscapeJson(key)).Append("\": ")
                .Append("\"").Append(EscapeJson(value)).Append("\"");
            if (comma)
            {
                builder.Append(",");
            }
            builder.Append("\n");
        }

        private static string JsonValue(string json, string key)
        {
            var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
            return match.Success ? UnescapeJson(match.Groups[1].Value) : "";
        }

        private static string EscapeJson(string value)
        {
            return (value ?? "")
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string UnescapeJson(string value)
        {
            return (value ?? "")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        private static string SafePathSegment(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch).ToArray();
            var safe = new string(chars);
            safe = Regex.Replace(safe, "_+", "_").Trim('_');
            return safe.Length == 0 ? "unknown" : safe;
        }

        private static string ToRelative(string path)
        {
            var root = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/')
                : path;
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
                // Retention cleanup can try again later.
            }
        }

        private sealed class MetadataEntry
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public string Reason = "";
            public string Path = "";
            public DateTime ReceivedAtUtc;
        }
    }

    internal static class BotApiClient
    {
        public static IEnumerator PostLogRoutine(ArchivedLog log)
        {
            var botApiUrl = DiscordToolsPlugin.GetBotApiUrl();
            var botApiKey = DiscordToolsPlugin.GetBotApiKey();

            if (string.IsNullOrWhiteSpace(botApiUrl))
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(botApiKey))
            {
                var message = "Bot API key is not configured. Saved locally at " + log.RelativeLogPath;
                LogArchive.MarkBotUploadFailed(log, message);
                DiscordToolsPlugin.Log.LogWarning(message);
                yield break;
            }

            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(log.LogPath);
            }
            catch (Exception ex)
            {
                LogArchive.MarkBotUploadFailed(log, ex.Message);
                DiscordToolsPlugin.Log.LogWarning("Bot API upload failed for " + log.RelativeLogPath + ": " + ex.Message);
                yield break;
            }

            var metadata = JsonMetadata(log);
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("metadata_json", metadata, Encoding.UTF8, "application/json"),
                new MultipartFormFileSection("file", fileBytes, Path.GetFileName(log.LogPath), "application/gzip")
            };

            using var request = UnityWebRequest.Post(botApiUrl, form);
            request.SetRequestHeader("X-API-Key", botApiKey);
            request.SetRequestHeader("User-Agent", "DiscordTools/1.0");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success || request.responseCode < 200 || request.responseCode >= 300)
            {
                var message = string.IsNullOrWhiteSpace(request.error) ? "HTTP " + request.responseCode : request.error + " (HTTP " + request.responseCode + ")";
                LogArchive.MarkBotUploadFailed(log, message);
                DiscordToolsPlugin.Log.LogWarning("Bot API upload failed for " + log.RelativeLogPath + ": " + message);
            }
            else
            {
                DiscordToolsPlugin.Log.LogInfo("Uploaded client log to bot API: " + log.RelativeLogPath);
            }
        }

        private static string JsonMetadata(ArchivedLog log)
        {
            return "{" +
                   "\"requestId\":\"" + EscapeJson(log.RequestId) + "\"," +
                   "\"playerId\":\"" + EscapeJson(log.PlayerId) + "\"," +
                   "\"playerName\":\"" + EscapeJson(log.PlayerName) + "\"," +
                   "\"reason\":\"" + EscapeJson(log.Reason) + "\"," +
                   "\"receivedAtUtc\":\"" + EscapeJson(log.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture)) + "\"," +
                   "\"originalBytes\":" + log.OriginalBytes.ToString(CultureInfo.InvariantCulture) + "," +
                   "\"compressedBytes\":" + log.CompressedBytes.ToString(CultureInfo.InvariantCulture) + "," +
                   "\"sha256\":\"" + EscapeJson(log.Sha256) + "\"," +
                   "\"serverLogPath\":\"" + EscapeJson(log.RelativeLogPath) + "\"" +
                   "}";
        }

        private static void WriteUtf8(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string EscapeJson(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class ZNetAwakePatch
    {
        private static void Postfix()
        {
            ClientLogRpc.Register();
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
    internal static class GameLogoutPatch
    {
        private static bool _continuing;

        private static bool Prefix(Game __instance, bool save, bool changeToStartScene)
        {
            if (_continuing || !ClientLogUploader.ShouldUpload())
            {
                return true;
            }

            ClientLogUploader.StartUpload("logout", Guid.NewGuid().ToString("N"), DiscordToolsPlugin.LogoutUploadTimeoutSeconds.Value, () =>
            {
                _continuing = true;
                try
                {
                    __instance.Logout(save, changeToStartScene);
                }
                finally
                {
                    _continuing = false;
                }
            });

            return false;
        }
    }

    [HarmonyPatch(typeof(Menu), "QuitGame")]
    internal static class MenuQuitPatch
    {
        private static bool _continuing;

        private static bool Prefix()
        {
            if (_continuing || !ClientLogUploader.ShouldUpload())
            {
                return true;
            }

            ClientLogUploader.StartUpload("quit", Guid.NewGuid().ToString("N"), DiscordToolsPlugin.QuitUploadTimeoutSeconds.Value, () =>
            {
                _continuing = true;
                try
                {
                    Gogan.LogEvent("Game", "Quit", "", 0L);
                    Application.Quit();
                }
                finally
                {
                    _continuing = false;
                }
            });

            return false;
        }
    }
}
