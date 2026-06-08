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
        private static MelonPreferences_Entry<string> _fakeClientName;
        private static MelonPreferences_Entry<string> _serverInstanceId;

        public static string ServerName => _serverName?.Value ?? string.Empty;
        public static int ServerCapacity => _serverCapacity?.Value ?? 8;
        public static bool IsPublicLobby => _isPublicLobby?.Value ?? true;
        public static bool IsPasswordProtected => _isPasswordProtected?.Value ?? false;
        public static string LobbyPassword => _lobbyPassword?.Value ?? string.Empty;
        public static bool IsPeacefulMode => _isPeacefulMode?.Value ?? false;
        public static bool IsTextChatOnly => _isTextChatOnly?.Value ?? false;
        public static bool HeadlessAutoHost => _headlessAutoHost?.Value ?? true;

        // Optional explicit id for this headless instance's EOS identity. Lets you run multiple servers on
        // ONE machine, each with its own persistent EOS DeviceId/PUID (otherwise they share the machine's
        // device fingerprint → same PUID → EOS P2P routes a joiner's packets across both → ~60s timeouts).
        // Blank = auto-generate a GUID and store it in UserData/SledHeadless-instance.id (reused thereafter).
        public static string ServerInstanceId => _serverInstanceId?.Value ?? string.Empty;

        // Persists an auto-generated EOS instance id back into the ServerInstanceId preference so it shows up
        // in UserData/MelonPreferences.cfg and is reused on every restart.
        internal static void SetServerInstanceId(string id)
        {
            try
            {
                if (_serverInstanceId == null) return;
                _serverInstanceId.Value = id;
                MelonPreferences.Save();
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Could not persist ServerInstanceId: {ex.Message}"); }
        }

        // The headless host owns the EOS lobby, so it can't be hidden from the member list, but this controls
        // the name it shows as. Falls back to the server name, then "Server", if left blank.
        public static string FakeClientName
        {
            get
            {
                string n = _fakeClientName?.Value;
                if (!string.IsNullOrWhiteSpace(n)) return n;
                return !string.IsNullOrWhiteSpace(ServerName) ? ServerName : "Server";
            }
        }

        public override void OnInitializeMelon()
        {
            _prefs = MelonPreferences.CreateCategory("SledHeadless", "SledHeadless");

            // Server config lives in its own shared category so LobbyKit and SledHeadless read one source of truth.
            // Keys, display names, and descriptions match LobbyKit exactly so both bind to the same entries.
            // GetOrCreate (not CreateEntry): LobbyKit and SledHeadless share this category, and CreateEntry
            // throws if the entry already exists, so the second mod to load must reuse the existing entries.
            _serverSettings = MelonPreferences.CreateCategory("ServerSettings", "Server Settings");
            _serverName = GetOrCreate(_serverSettings, "ServerName", string.Empty, "Server Name", "Custom default lobby/server name. Leave empty to use '<PlayerName>\'s Lobby'.");
            _serverCapacity = GetOrCreate(_serverSettings, "ServerCapacity", 8, "Server Capacity", "Saved default value for the max players slider.");
            _isPublicLobby = GetOrCreate(_serverSettings, "IsPublicLobby", true, "Public Lobby", "Saved default for public/private lobby.");
            _isPasswordProtected = GetOrCreate(_serverSettings, "IsPasswordProtected", false, "Password Protected", "Saved default for password protection.");
            _lobbyPassword = GetOrCreate(_serverSettings, "LobbyPassword", string.Empty, "Lobby Password", "Saved default lobby password.");
            _isPeacefulMode = GetOrCreate(_serverSettings, "IsPeacefulMode", false, "Peaceful Mode", "Saved default for peaceful mode.");
            _isTextChatOnly = GetOrCreate(_serverSettings, "IsTextChatOnly", false, "Text Chat Only", "Saved default for text-chat-only mode.");

            // HeadlessAutoHost is Headless-specific, so it stays in the SledHeadless category.
            _headlessAutoHost = _prefs.CreateEntry("HeadlessAutoHost", true, "Headless Auto Host");
            _fakeClientName = _prefs.CreateEntry("FakeClientName", string.Empty, "Fake Client Name",
                "Name the headless host shows as in the lobby/player list. The server owns the EOS lobby so it can't be hidden; this sets its display name. Blank = use the server name (or 'Server').");
            _serverInstanceId = _prefs.CreateEntry("ServerInstanceId", string.Empty, "Server Instance Id",
                "Unique id for this headless instance's EOS identity, so you can run multiple servers on one machine each with its own PUID. Blank = auto-generate + store a GUID in UserData/SledHeadless-instance.id.");
            MelonPreferences.Save();

            if (Application.isBatchMode)
            {
                HeadlessPatches.ApplyPatches(HarmonyInstance);
                HeadlessShutdown.Install();
            }
            else
                ClientSpawnDiagnostics.Install(HarmonyInstance);
        }

        // GetEntry if it already exists (e.g. LobbyKit created it on the shared ServerSettings category),
        // otherwise CreateEntry. MelonLoader's CreateEntry throws on a duplicate identifier.
        private static MelonPreferences_Entry<T> GetOrCreate<T>(MelonPreferences_Category category, string identifier, T defaultValue, string displayName, string description = null)
        {
            return category.HasEntry(identifier)
                ? category.GetEntry<T>(identifier)
                : category.CreateEntry(identifier, defaultValue, displayName, description);
        }

        public override void OnApplicationQuit()
        {
            if (Application.isBatchMode)
            {
                MelonLogger.Warning($"[HeadlessMode] Application.Quit() called.\n{new System.Diagnostics.StackTrace(true)}");
                HeadlessShutdown.OnQuit();   // destroy the EOS lobby before we go (main thread)
            }
        }

        private static bool _muteLogged;
        private static bool _muteErrLogged;

        public override void OnUpdate()
        {
            if (!Application.isBatchMode) return;
            HeadlessShutdown.Tick();   // run a requested shutdown destroy on the main thread
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
