using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;

namespace DiscordTools
{
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
            var playerFolderName = BuildPlayerFolderName(playerName, playerId);
            var relativePlayerFolder = "players/" + playerFolderName;

            var month = transfer.ReceivedAtUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var stamp = transfer.ReceivedAtUtc.ToString("yyyy-MM-dd_HH-mm-ss'Z'", CultureInfo.InvariantCulture);
            var fileStem = stamp + "_" + transfer.Reason + "_" + SafePathSegment(playerName) + "_LogOutput";
            var playerDir = Path.Combine(PlayersDir, playerFolderName);
            var logDir = Path.Combine(playerDir, "logs", month);
            Directory.CreateDirectory(logDir);

            var logPath = Path.Combine(logDir, fileStem + ".log.gz");
            var metadataPath = Path.Combine(logDir, fileStem + ".json");
            File.Copy(transfer.TempPath, logPath, overwrite: true);

            var archived = new ArchivedLog
            {
                PlayerId = playerId,
                PlayerName = playerName,
                PlayerFolder = relativePlayerFolder,
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
                if (IsArchivedLogFile(file) &&
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
                ["playerFolder"] = log.PlayerFolder,
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
                ["playerId"] = latest.PlayerId,
                ["playerFolder"] = latest.PlayerFolder,
                ["playerName"] = latest.PlayerName
            }), Encoding.UTF8);
        }

        private static void RebuildIndexes()
        {
            EnsureDirectories();
            var entries = ReadAllMetadata().OrderByDescending(entry => entry.ReceivedAtUtc).ToList();
            var byId = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byName = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            var byIdFolders = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            var byNameFolders = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (!byId.ContainsKey(entry.PlayerId))
                {
                    byId[entry.PlayerId] = entry.PlayerFolder;
                }

                AddToStringSetMap(byIdFolders, entry.PlayerId, entry.PlayerFolder);

                var key = entry.PlayerName.Trim().ToLowerInvariant();
                if (key.Length == 0)
                {
                    continue;
                }

                AddToStringSetMap(byName, key, entry.PlayerId);
                AddToStringSetMap(byNameFolders, key, entry.PlayerFolder);
            }

            File.WriteAllText(Path.Combine(IndexDir, "players.json"), BuildPlayersIndexJson(byId, byName, byIdFolders, byNameFolders), Encoding.UTF8);
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
                    entry.PlayerFolder = JsonValue(json, "playerFolder");
                    if (string.IsNullOrWhiteSpace(entry.PlayerFolder))
                    {
                        entry.PlayerFolder = PlayerFolderFromRelativeLogPath(entry.Path, entry.PlayerId);
                    }

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

        private static bool IsArchivedLogFile(string path)
        {
            var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var logsSegment = Path.DirectorySeparatorChar + "logs" + Path.DirectorySeparatorChar;
            return normalized.IndexOf(logsSegment, StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (path.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildLogMetadataJson(ArchivedLog archived, IncomingTransfer transfer)
        {
            return JsonObject(new Dictionary<string, string>
            {
                ["requestId"] = archived.RequestId,
                ["reason"] = archived.Reason,
                ["playerId"] = archived.PlayerId,
                ["playerName"] = archived.PlayerName,
                ["playerFolder"] = archived.PlayerFolder,
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
            AppendJsonProperty(builder, "playerFolder", latest.PlayerFolder, comma: true);
            builder.Append("  \"knownNames\": [");
            builder.Append(string.Join(", ", knownNames.Select(name => "\"" + EscapeJson(name) + "\"").ToArray()));
            builder.Append("],\n");
            AppendJsonProperty(builder, "lastSeenUtc", latest.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture), comma: false);
            builder.Append("}\n");
            return builder.ToString();
        }

        private static string BuildPlayersIndexJson(
            SortedDictionary<string, string> byId,
            SortedDictionary<string, SortedSet<string>> byName,
            SortedDictionary<string, SortedSet<string>> byIdFolders,
            SortedDictionary<string, SortedSet<string>> byNameFolders)
        {
            var builder = new StringBuilder();
            builder.Append("{\n  \"byId\": {\n");
            AppendStringMap(builder, byId, 4);
            builder.Append("\n  },\n  \"byName\": {\n");
            AppendStringSetMap(builder, byName, 4);
            builder.Append("  },\n  \"byIdFolders\": {\n");
            AppendStringSetMap(builder, byIdFolders, 4);
            builder.Append("  },\n  \"byNameFolders\": {\n");
            AppendStringSetMap(builder, byNameFolders, 4);
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
                AppendJsonProperty(builder, "playerFolder", entry.PlayerFolder, comma: true, indent: 4);
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

        private static void AddToStringSetMap(SortedDictionary<string, SortedSet<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var values))
            {
                values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                map[key] = values;
            }

            values.Add(value);
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

        private static void AppendStringSetMap(StringBuilder builder, SortedDictionary<string, SortedSet<string>> map, int indent)
        {
            var spaces = new string(' ', indent);
            var index = 0;
            foreach (var pair in map)
            {
                builder.Append(spaces).Append("\"").Append(EscapeJson(pair.Key)).Append("\": [");
                builder.Append(string.Join(", ", pair.Value.Select(value => "\"" + EscapeJson(value) + "\"").ToArray()));
                builder.Append("]");
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

        private static string PlayerFolderFromRelativeLogPath(string relativeLogPath, string playerId)
        {
            var path = (relativeLogPath ?? "").Replace('\\', '/');
            var logsIndex = path.IndexOf("/logs/", StringComparison.OrdinalIgnoreCase);
            if (path.StartsWith("players/", StringComparison.OrdinalIgnoreCase) && logsIndex > "players/".Length)
            {
                return path.Substring(0, logsIndex);
            }

            return "players/" + SafePathSegment(playerId);
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

        private static string BuildPlayerFolderName(string playerName, string playerId)
        {
            return SafePathSegment(playerName) + "_" + SafePathSegment(playerId);
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
            public string PlayerFolder = "";
            public string Reason = "";
            public string Path = "";
            public DateTime ReceivedAtUtc;
        }
    }
}
