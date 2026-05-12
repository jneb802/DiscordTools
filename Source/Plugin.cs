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
}
