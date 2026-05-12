namespace DiscordTools
{
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
