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
        private static MelonPreferences_Category _serverSettings;   // shared with LobbyKit
        private static MelonPreferences_Entry<string> _serverName;
        private static MelonPreferences_Entry<int> _serverCapacity;
        private static MelonPreferences_Entry<bool> _isPublicLobby;
        private static MelonPreferences_Entry<bool> _isPasswordProtected;
        private static MelonPreferences_Entry<string> _lobbyPassword;
        private static MelonPreferences_Entry<bool> _isPeacefulMode;
        private static MelonPreferences_Entry<bool> _isTextChatOnly;
        private static MelonPreferences_Entry<bool> _headlessAutoHost;

        public static string ServerName => _serverName?.Value ?? string.Empty;
        public static int ServerCapacity => _serverCapacity?.Value ?? 8;
        public static bool IsPublicLobby => _isPublicLobby?.Value ?? true;
        public static bool IsPasswordProtected => _isPasswordProtected?.Value ?? false;
        public static string LobbyPassword => _lobbyPassword?.Value ?? string.Empty;
        public static bool IsPeacefulMode => _isPeacefulMode?.Value ?? false;
        public static bool IsTextChatOnly => _isTextChatOnly?.Value ?? false;
        public static bool HeadlessAutoHost => _headlessAutoHost?.Value ?? true;

        public override void OnInitializeMelon()
        {
            _prefs = MelonPreferences.CreateCategory("SledHeadless", "SledHeadless");

            // Server config lives in its own shared category so LobbyKit and SledHeadless read one source of truth.
            // Keys, display names, and descriptions match LobbyKit exactly so both bind to the same entries.
            _serverSettings = MelonPreferences.CreateCategory("ServerSettings", "Server Settings");
            _serverName = _serverSettings.CreateEntry("ServerName", string.Empty, "Server Name", "Custom default lobby/server name. Leave empty to use '<PlayerName>\'s Lobby'.");
            _serverCapacity = _serverSettings.CreateEntry("ServerCapacity", 8, "Server Capacity", "Saved default value for the max players slider.");
            _isPublicLobby = _serverSettings.CreateEntry("IsPublicLobby", true, "Public Lobby", "Saved default for public/private lobby.");
            _isPasswordProtected = _serverSettings.CreateEntry("IsPasswordProtected", false, "Password Protected", "Saved default for password protection.");
            _lobbyPassword = _serverSettings.CreateEntry("LobbyPassword", string.Empty, "Lobby Password", "Saved default lobby password.");
            _isPeacefulMode = _serverSettings.CreateEntry("IsPeacefulMode", false, "Peaceful Mode", "Saved default for peaceful mode.");
            _isTextChatOnly = _serverSettings.CreateEntry("IsTextChatOnly", false, "Text Chat Only", "Saved default for text-chat-only mode.");

            // HeadlessAutoHost is Headless-specific, so it stays in the SledHeadless category.
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
