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

        private static string EscapeJson(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
