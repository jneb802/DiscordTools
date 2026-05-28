using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine.Networking;

namespace DiscordTools
{
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
                fileBytes = ReadDecompressedLog(log.LogPath);
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
                new MultipartFormFileSection("file", fileBytes, GetDiscordFileName(log.LogPath), "text/plain")
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

        public static IEnumerator PostLinkRoutine(LinkRequest link, Action<long, string, bool, string> sendResult)
        {
            var linkApiUrl = DiscordToolsPlugin.GetLinkApiUrl();
            var botApiKey = DiscordToolsPlugin.GetBotApiKey();

            if (string.IsNullOrWhiteSpace(linkApiUrl))
            {
                sendResult(link.Sender, link.RequestId, false, "Link API URL is not configured on the server.");
                yield break;
            }

            if (string.IsNullOrWhiteSpace(botApiKey))
            {
                sendResult(link.Sender, link.RequestId, false, "Bot API key is not configured on the server.");
                yield break;
            }

            var body = JsonLinkRequest(link);
            var bytes = Encoding.UTF8.GetBytes(body);
            using var request = new UnityWebRequest(linkApiUrl, "POST")
            {
                uploadHandler = new UploadHandlerRaw(bytes),
                downloadHandler = new DownloadHandlerBuffer()
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-API-Key", botApiKey);
            request.SetRequestHeader("User-Agent", "DiscordTools/1.1");

            yield return request.SendWebRequest();

            var responseText = request.downloadHandler != null ? request.downloadHandler.text : "";
            if (request.result != UnityWebRequest.Result.Success || request.responseCode < 200 || request.responseCode >= 300)
            {
                var message = string.IsNullOrWhiteSpace(responseText)
                    ? (string.IsNullOrWhiteSpace(request.error) ? "HTTP " + request.responseCode : request.error + " (HTTP " + request.responseCode + ")")
                    : responseText;
                DiscordToolsPlugin.Log.LogWarning("Discord link API failed for " + link.PlayerId + ": " + message);
                sendResult(link.Sender, link.RequestId, false, message);
            }
            else
            {
                var message = string.IsNullOrWhiteSpace(responseText)
                    ? "Discord link complete."
                    : responseText;
                DiscordToolsPlugin.Log.LogInfo("Discord link API accepted " + link.PlayerId + ".");
                sendResult(link.Sender, link.RequestId, true, message);
            }
        }

        private static string JsonMetadata(ArchivedLog log)
        {
            return "{" +
                   "\"requestId\":\"" + EscapeJson(log.RequestId) + "\"," +
                   "\"playerId\":\"" + EscapeJson(log.PlayerId) + "\"," +
                   "\"playerName\":\"" + EscapeJson(log.PlayerName) + "\"," +
                   "\"playerFolder\":\"" + EscapeJson(log.PlayerFolder) + "\"," +
                   "\"reason\":\"" + EscapeJson(log.Reason) + "\"," +
                   "\"receivedAtUtc\":\"" + EscapeJson(log.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture)) + "\"," +
                   "\"originalBytes\":" + log.OriginalBytes.ToString(CultureInfo.InvariantCulture) + "," +
                   "\"compressedBytes\":" + log.CompressedBytes.ToString(CultureInfo.InvariantCulture) + "," +
                   "\"sha256\":\"" + EscapeJson(log.Sha256) + "\"," +
                   "\"serverLogPath\":\"" + EscapeJson(log.RelativeLogPath) + "\"" +
                   "}";
        }

        private static string JsonLinkRequest(LinkRequest link)
        {
            return "{" +
                   "\"requestId\":\"" + EscapeJson(link.RequestId) + "\"," +
                   "\"code\":\"" + EscapeJson(link.Code) + "\"," +
                   "\"playerId\":\"" + EscapeJson(link.PlayerId) + "\"," +
                   "\"playerName\":\"" + EscapeJson(link.PlayerName) + "\"," +
                   "\"endpoint\":\"" + EscapeJson(link.Endpoint) + "\"," +
                   "\"platformDisplayName\":\"" + EscapeJson(link.PlatformDisplayName) + "\"," +
                   "\"receivedAtUtc\":\"" + EscapeJson(link.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture)) + "\"" +
                   "}";
        }

        private static byte[] ReadDecompressedLog(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var memory = new MemoryStream();
            gzip.CopyTo(memory);
            return memory.ToArray();
        }

        private static string GetDiscordFileName(string path)
        {
            var fileName = Path.GetFileName(path);
            return fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - 3)
                : fileName;
        }

        private static string EscapeJson(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
