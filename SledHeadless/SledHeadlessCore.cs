using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SledHeadless
{
    public class SledHeadlessCore : MelonMod
    {
        public static bool isHost = false;
        public static bool WasHosting = false;

        private static MelonPreferences_Category _prefs;
        private static MelonPreferences_Entry<string> _serverName;
        private static MelonPreferences_Entry<int> _serverCapacity;
        private static MelonPreferences_Entry<bool> _isPublicLobby;
        private static MelonPreferences_Entry<bool> _isPasswordProtected;
        private static MelonPreferences_Entry<string> _lobbyPassword;
        private static MelonPreferences_Entry<bool> _isPeacefulMode;
        private static MelonPreferences_Entry<bool> _headlessAutoHost;

        public static string ServerName => _serverName?.Value ?? string.Empty;
        public static int ServerCapacity => _serverCapacity?.Value ?? 8;
        public static bool IsPublicLobby => _isPublicLobby?.Value ?? true;
        public static bool IsPasswordProtected => _isPasswordProtected?.Value ?? false;
        public static string LobbyPassword => _lobbyPassword?.Value ?? string.Empty;
        public static bool IsPeacefulMode => _isPeacefulMode?.Value ?? false;
        public static bool HeadlessAutoHost => _headlessAutoHost?.Value ?? true;

        public override void OnInitializeMelon()
        {
            _prefs = MelonPreferences.CreateCategory("SledHeadless", "SledHeadless");
            _serverName = _prefs.CreateEntry("ServerName", string.Empty, "Server Name");
            _serverCapacity = _prefs.CreateEntry("ServerCapacity", 8, "Server Capacity");
            _isPublicLobby = _prefs.CreateEntry("IsPublicLobby", true, "Public Lobby");
            _isPasswordProtected = _prefs.CreateEntry("IsPasswordProtected", false, "Password Protected");
            _lobbyPassword = _prefs.CreateEntry("LobbyPassword", string.Empty, "Lobby Password");
            _isPeacefulMode = _prefs.CreateEntry("IsPeacefulMode", false, "Peaceful Mode");
            _headlessAutoHost = _prefs.CreateEntry("HeadlessAutoHost", true, "Headless Auto Host");
            MelonPreferences.Save();

            if (Application.isBatchMode)
                HeadlessPatches.ApplyPatches(HarmonyInstance);
            else
                ClientSpawnDiagnostics.Install(HarmonyInstance);
        }

        public override void OnApplicationQuit()
        {
            if (Application.isBatchMode)
                MelonLogger.Warning($"[HeadlessMode] Application.Quit() called.\n{new System.Diagnostics.StackTrace(true)}");
        }

        private static bool _muteLogged;
        private static bool _muteErrLogged;

        public override void OnUpdate()
        {
            if (!Application.isBatchMode) return;
            try
            {
                if (AudioListener.volume != 0f) AudioListener.volume = 0f;
                if (!AudioListener.pause) AudioListener.pause = true;
                if (!_muteLogged) { MelonLogger.Msg("[HeadlessMode] Audio muted."); _muteLogged = true; }
            }
            catch (Exception ex)
            {
                if (!_muteErrLogged) { MelonLogger.Warning($"[HeadlessMode] Mute failed: {ex.GetType().Name}: {ex.Message}"); _muteErrLogged = true; }
            }
        }
    }
}
