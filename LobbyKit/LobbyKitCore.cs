using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppFishNet;
using MelonLoader;
using MelonLoader.NativeUtils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LobbyKit
{
    public class LobbyKitCore : MelonMod
    {
        public static LobbyKitCore Instance;
        private PlayerReference localPlayer;
        private readonly List<PlayerReference> players = new List<PlayerReference>();
        private readonly Dictionary<int, double> _playerJoinTimes = new Dictionary<int, double>();
        private readonly Dictionary<string, double> _playerJoinTimesByProductId = new Dictionary<string, double>();
        public static bool isHost = false;
        public static bool WasHosting = false;

        private static MelonPreferences_Category _preferences;
        private static MelonPreferences_Entry<bool> _enableGuestBangCommands;
        private static MelonPreferences_Entry<string> _serverName;
        private static MelonPreferences_Entry<int> _serverCapacity;
#pragma warning disable CS0649
        private static MelonPreferences_Entry<int> _searchMinPlayers;
#pragma warning restore CS0649
        private static MelonPreferences_Entry<int> _searchMaxPlayers;
        private static MelonPreferences_Entry<bool> _searchShowLocked;
        private static MelonPreferences_Entry<bool> _searchShowModded;
        private static MelonPreferences_Entry<bool> _searchShowYourLanguageOnly;
        private static MelonPreferences_Entry<bool> _searchOnlyPeacefulLobbies;
        private static MelonPreferences_Entry<bool> _searchHidePeacefulLobbies;
        private static MelonPreferences_Entry<int> _searchRegionMode;
        private static MelonPreferences_Entry<bool> _isPublicLobby;
        private static MelonPreferences_Entry<bool> _isPasswordProtected;
        private static MelonPreferences_Entry<string> _lobbyPassword;
        private static MelonPreferences_Entry<bool> _isPeacefulMode;
        private static MelonPreferences_Entry<bool> _isTextChatOnly;
        private static MelonPreferences_Entry<string> _messageOfTheDay;
        private static MelonPreferences_Entry<bool> _showJoinMessages;
        private static MelonPreferences_Entry<bool> _showLeaveMessages;
        private static MelonPreferences_Entry<int> _joinMessageSize;
        private static MelonPreferences_Entry<int> _leaveMessageSize;
        private static MelonPreferences_Entry<bool> _autoRestartOnCrash;
        private static MelonPreferences_Entry<bool> _enableAnticheat;
        private static MelonPreferences_Entry<bool> _headlessAutoHost;

        public static bool EnableGuestBangCommands => _enableGuestBangCommands?.Value ?? true;
        public static string ServerName => _serverName?.Value ?? string.Empty;
        public static int ServerCapacity => _serverCapacity?.Value ?? 8;
        public static int SearchMinPlayers => _searchMinPlayers?.Value ?? 1;
        public static int SearchMaxPlayers => _searchMaxPlayers?.Value ?? 0; // 0 == Any
        public static bool SearchShowLocked => _searchShowLocked?.Value ?? true;
        public static bool SearchShowModded => _searchShowModded?.Value ?? true;
        public static bool SearchShowYourLanguageOnly => _searchShowYourLanguageOnly?.Value ?? false;
        public static bool SearchOnlyPeacefulLobbies => _searchOnlyPeacefulLobbies?.Value ?? false;
        public static bool SearchHidePeacefulLobbies => _searchHidePeacefulLobbies?.Value ?? false;
        public static int SearchRegionMode => _searchRegionMode?.Value ?? 1;
        public static bool IsPublicLobby => _isPublicLobby?.Value ?? true;
        public static bool IsPasswordProtected => _isPasswordProtected?.Value ?? false;
        public static string LobbyPassword => _lobbyPassword?.Value ?? string.Empty;
        public static bool IsPeacefulMode => _isPeacefulMode?.Value ?? false;
        public static bool IsTextChatOnly => _isTextChatOnly?.Value ?? false;
        public static string MessageOfTheDay => _messageOfTheDay?.Value ?? string.Empty;
        public static bool ShowJoinMessages => _showJoinMessages?.Value ?? true;
        public static bool ShowLeaveMessages => _showLeaveMessages?.Value ?? true;
        public static int JoinMessageSize => _joinMessageSize?.Value ?? 75;
        public static int LeaveMessageSize => _leaveMessageSize?.Value ?? 75;
        public static bool AutoRestartOnCrash => _autoRestartOnCrash?.Value ?? false;
        public static bool EnableAnticheat => _enableAnticheat?.Value ?? false;
        public static bool HeadlessAutoHost => _headlessAutoHost?.Value ?? true;

        private PlayerReferenceManager _playerReferenceManager;

        public bool ReferencesLoaded = false;
        public void SavePrefs()
        {
            MelonPreferences.Save();
        }

        public override unsafe void OnInitializeMelon()
        {
            Instance = this;

            _preferences = MelonPreferences.CreateCategory("LobbyKit", "LobbyKit");
            _enableGuestBangCommands = _preferences.CreateEntry("EnableGuestBangCommands", true, "Enable Guest Bang Commands", "Allow non-host players to use custom bang chat commands like !tp.");
            _serverName = _preferences.CreateEntry("ServerName", string.Empty, "Server Name", "Custom default lobby/server name. Leave empty to use '<PlayerName>\'s Lobby'.");
            _serverCapacity = _preferences.CreateEntry("ServerCapacity", 8, "Server Capacity", "Saved default value for the max players slider.");
            _isPublicLobby = _preferences.CreateEntry("IsPublicLobby", true, "Public Lobby", "Saved default for public/private lobby.");
            _isPasswordProtected = _preferences.CreateEntry("IsPasswordProtected", false, "Password Protected", "Saved default for password protection.");
            _lobbyPassword = _preferences.CreateEntry("LobbyPassword", string.Empty, "Lobby Password", "Saved default lobby password.");
            _isPeacefulMode = _preferences.CreateEntry("IsPeacefulMode", false, "Peaceful Mode", "Saved default for peaceful mode.");
            _isTextChatOnly = _preferences.CreateEntry("IsTextChatOnly", false, "Text Chat Only", "Saved default for text-chat-only mode.");
            // Disabled for now. Leave the field/property/setter in place so this can be re-enabled without rewiring callers.
            // _searchMinPlayers = _preferences.CreateEntry("SearchMinPlayers", 1, "Search Min Players", "Minimum players filter for lobby search.");
            _searchMaxPlayers = _preferences.CreateEntry("SearchMaxPlayers", 0, "Search Max Players", "Maximum players filter for lobby search. 0 == Any.");
            _searchShowLocked = _preferences.CreateEntry("SearchShowLocked", true, "Search Show Locked", "Include locked lobbies in search results.");
            _searchShowModded = _preferences.CreateEntry("SearchShowModded", true, "Search Show Modded", "Include modded lobbies in search results.");
            _searchShowYourLanguageOnly = _preferences.CreateEntry("SearchShowYourLanguageOnly", false, "Search Your Language Only", "Filter to language-matching lobbies.");
            _searchOnlyPeacefulLobbies = _preferences.CreateEntry("SearchOnlyPeacefulLobbies", false, "Search Only Peaceful Lobbies", "Only show peaceful-mode lobbies in search results.");
            _searchHidePeacefulLobbies = _preferences.CreateEntry("SearchHidePeacefulLobbies", false, "Search Hide Peaceful Lobbies", "Hide peaceful-mode lobbies in search results.");
            _searchRegionMode = _preferences.CreateEntry("SearchRegionMode", 1, "Search Region Mode", "Lobby search region filter: 0 near, 1 nearby regions, 2 far, 3 worldwide.");
            _messageOfTheDay = _preferences.CreateEntry("MessageOfTheDay", string.Empty, "Message of the Day", "Private chat message sent to each player when they join your hosted lobby. Leave empty to disable.");
            _showJoinMessages = _preferences.CreateEntry("ShowJoinMessages", true, "Show Join Messages", "Broadcast a chat message when a player joins your hosted lobby.");
            _showLeaveMessages = _preferences.CreateEntry("ShowLeaveMessages", true, "Show Leave Messages", "Broadcast a chat message when a player leaves your hosted lobby.");
            _joinMessageSize = _preferences.CreateEntry("JoinMessageSize", 75, "Join Message Size", "Font size percentage for join messages (e.g. 75 for 75%).");
            _leaveMessageSize = _preferences.CreateEntry("LeaveMessageSize", 75, "Leave Message Size", "Font size percentage for leave messages (e.g. 75 for 75%).");
            _autoRestartOnCrash = _preferences.CreateEntry("AutoRestartOnCrash", false, "Auto-Restart On Crash", "Automatically re-host the lobby when it crashes unexpectedly.");
            _enableAnticheat = _preferences.CreateEntry("EnableAnticheat", false, "Enable AntiCheat", "Rate-limit and kick clients who spam server RPCs.");
            _headlessAutoHost = _preferences.CreateEntry("HeadlessAutoHost", true, "Headless Auto Host", "Automatically host a lobby on EOS login when launched with -batchmode -nographics.");
            MelonPreferences.Save();

            HarmonyInstance.PatchAll();
            Features.Anticheat.GenericServerRpcRateLimitPatch.ApplyPatches(HarmonyInstance);
            if (Application.isBatchMode)
                Features.Headless.HeadlessModePatches.ApplyPatches(HarmonyInstance);
        }

        public override void OnApplicationQuit()
        {
            if (Application.isBatchMode)
                MelonLogger.Warning($"[HeadlessMode] Application.Quit() called.\n{new System.Diagnostics.StackTrace(true)}");
        }

        private static bool _muteLogged;
        private static bool _muteErrLogged;

        // Headless audio kill. -nographics does NOT disable audio, and the game re-applies its saved
        // MasterVolume / un-pauses when the main scene loads. MelonLoader calls OnUpdate every frame
        // natively (more reliable here than a MelonCoroutine), so re-assert the global Unity audio
        // mute each frame. Surface the first exception instead of swallowing it, so we can see if the
        // AudioListener static access itself is failing.
        public override void OnUpdate()
        {
            if (!Application.isBatchMode) return;
            try
            {
                if (AudioListener.volume != 0f) AudioListener.volume = 0f;
                if (!AudioListener.pause) AudioListener.pause = true;
                if (!_muteLogged) { MelonLogger.Msg("[HeadlessMode] Audio muted via OnUpdate (AudioListener.volume=0)."); _muteLogged = true; }
            }
            catch (Exception ex)
            {
                if (!_muteErrLogged) { MelonLogger.Warning($"[HeadlessMode] OnUpdate mute FAILED: {ex.GetType().Name}: {ex.Message}"); _muteErrLogged = true; }
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            ReferencesLoaded = false;
            players.Clear();
            _playerJoinTimes.Clear();
            _playerJoinTimesByProductId.Clear();
            Patches.ChatSystem.ResetSessionState();
            MelonCoroutines.Start(LoadReferences());
        }

        [HarmonyPatch(typeof(PlayerReferenceManager), "OnPlayerReferenceAdded")]
        public static class PlayerJoinPatch
        {
            private static void Postfix(PlayerReferenceManager __instance, int index)
            {
                var refs = __instance.GetPlayerReferences();

                if (refs == null || index < 0 || index >= refs.Count)
                    return;

                var val = refs[index];

                if (val == null)
                    return;

                if (val.IsLocalPlayerInstance())
                {
                    Instance.localPlayer = val;
                    isHost = val.ConnectionID == 32767;
                }

                Instance.PlayerJoinedGame(val);
            }
        }

        [HarmonyPatch(typeof(PlayerReferenceManager), "OnPlayerReferenceRemoved")]
        public static class PlayerLeavePatch
        {
            private static void Postfix(PlayerReferenceManager __instance, int index, PlayerReference removedItem)
            {
                Instance.PlayerLeftGame(removedItem);
            }
        }
        public void PlayerJoinedGame(PlayerReference p)
        {
            if (p == null)
                return;

            bool isLocalPlayer = p.IsLocalPlayerInstance();
            bool isNewConnection = !players.Any(player => player != null && player.ConnectionID == p.ConnectionID);
            if (isNewConnection)
                players.Add(p);

            if (isNewConnection && (isHost || isLocalPlayer))
                TrackPlayerJoinTime(p, GetLobbyUptimeSeconds());

            if (isLocalPlayer)
            {
                localPlayer = p;
                isHost = p.ConnectionID == 32767;
                if (isHost)
                {
                    WasHosting = true;
                    Patches.SilentCrashDetectionPatches.StartPolling();
                }
            }
            else if (isHost && isNewConnection && ShowJoinMessages)
            {
                string username = Patches.ChatSystem.AutoCloseTmpRichText(string.IsNullOrWhiteSpace(p.Username) ? "A player" : p.Username);
                Patches.ChatSystem.BroadcastSystemMessage($"<size={JoinMessageSize}%><#FA0>{username} joined.");
            }

            if (isHost)
                Patches.ChatSystem.SendMotdToPlayer(p);
        }

        public void PlayerLeftGame(PlayerReference removedPlayer)
        {
            if (removedPlayer == null)
                return;

            bool wasTrackedPlayer = players.Any(player => player != null && player.ConnectionID == removedPlayer.ConnectionID);
            bool isLocalPlayer = removedPlayer.IsLocalPlayerInstance();
            if (isHost && wasTrackedPlayer && !isLocalPlayer)
            {
                string username = Patches.ChatSystem.AutoCloseTmpRichText(string.IsNullOrWhiteSpace(removedPlayer.Username) ? "A player" : removedPlayer.Username);
                if (Features.Anticheat.KickAnnouncer.TryConsume(removedPlayer.ConnectionID, out string kickReason))
                    Patches.ChatSystem.BroadcastSystemMessage($"<size={LeaveMessageSize}%><#F44>{username} {kickReason}.");
                else if (ShowLeaveMessages)
                    Patches.ChatSystem.BroadcastSystemMessage($"<size={LeaveMessageSize}%><#FA0>{username} left.");
            }

            players.RemoveAll(player => player == null || player.ConnectionID == removedPlayer.ConnectionID);
            _playerJoinTimes.Remove(removedPlayer.ConnectionID);
            if (!string.IsNullOrWhiteSpace(removedPlayer.ProductUserId))
                _playerJoinTimesByProductId.Remove(removedPlayer.ProductUserId);
            Patches.ChatSystem.ForgetMotdRecipient(removedPlayer.ConnectionID);
            Patches.ChatSystem.ForgetTeleportRequests(removedPlayer.ConnectionID);

            if (localPlayer != null && localPlayer.ConnectionID == removedPlayer.ConnectionID)
            {
                localPlayer = null;
                isHost = false;
                Patches.SilentCrashDetectionPatches.StopPolling();
            }
        }

        public PlayerReference GetLocalPlayer()
        {
            if (localPlayer == null)
            {
                return null;
            }
            return localPlayer;
        }

        public bool TryGetPlayerJoinTime(PlayerReference playerReference, out double joinTimeSeconds)
        {
            joinTimeSeconds = 0d;

            if (!isHost || playerReference == null)
                return false;

            return _playerJoinTimes.TryGetValue(playerReference.ConnectionID, out joinTimeSeconds);
        }

        public bool TryGetPlayerSessionDuration(PlayerReference playerReference, out double sessionSeconds)
        {
            sessionSeconds = 0d;

            if (!isHost || playerReference == null)
                return false;

            double currentUptimeSeconds = GetLobbyUptimeSeconds();
            if (!_playerJoinTimes.TryGetValue(playerReference.ConnectionID, out double joinTimeSeconds))
            {
                joinTimeSeconds = playerReference.ConnectionID == 32767 ? 0d : currentUptimeSeconds;
                TrackPlayerJoinTime(playerReference, joinTimeSeconds);
            }

            sessionSeconds = currentUptimeSeconds - joinTimeSeconds;
            return true;
        }

        public bool TryGetPlayerSessionDuration(string productUserId, out double sessionSeconds)
        {
            sessionSeconds = 0d;

            if (!isHost || string.IsNullOrWhiteSpace(productUserId))
                return false;

            double currentUptimeSeconds = GetLobbyUptimeSeconds();
            if (!_playerJoinTimesByProductId.TryGetValue(productUserId, out double joinTimeSeconds))
            {
                joinTimeSeconds = currentUptimeSeconds;
                _playerJoinTimesByProductId[productUserId] = joinTimeSeconds;
            }

            sessionSeconds = currentUptimeSeconds - joinTimeSeconds;
            return true;
        }

        public bool TryGetPlayerListTimerDuration(string productUserId, bool isLobbyOwner, out double sessionSeconds)
        {
            sessionSeconds = 0d;

            if (string.IsNullOrWhiteSpace(productUserId))
                return false;

            if (!isHost)
            {
                double currentUptimeSeconds = GetLobbyUptimeSeconds();
                if (isLobbyOwner)
                {
                    sessionSeconds = currentUptimeSeconds;
                    return true;
                }

                if (!IsLocalPlayerProductId(productUserId))
                    return false;

                if (!_playerJoinTimesByProductId.TryGetValue(productUserId, out double joinTimeSeconds))
                {
                    joinTimeSeconds = currentUptimeSeconds;
                    _playerJoinTimesByProductId[productUserId] = joinTimeSeconds;
                }

                sessionSeconds = currentUptimeSeconds - joinTimeSeconds;
                return true;
            }

            return TryGetPlayerSessionDuration(productUserId, out sessionSeconds);
        }

        private bool IsLocalPlayerProductId(string productUserId)
        {
            return localPlayer != null
                && !string.IsNullOrWhiteSpace(localPlayer.ProductUserId)
                && string.Equals(localPlayer.ProductUserId, productUserId, StringComparison.Ordinal);
        }

        private void TrackPlayerJoinTime(PlayerReference playerReference, double joinTimeSeconds)
        {
            _playerJoinTimes[playerReference.ConnectionID] = joinTimeSeconds;

            if (!string.IsNullOrWhiteSpace(playerReference.ProductUserId))
                _playerJoinTimesByProductId[playerReference.ProductUserId] = joinTimeSeconds;
        }

        public static string FormatLobbyJoinTime(double uptimeSeconds)
        {
            if (uptimeSeconds < 0d)
                uptimeSeconds = 0d;

            TimeSpan timeSpan = TimeSpan.FromSeconds(uptimeSeconds);
            if (timeSpan.TotalHours >= 1d)
                return $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";

            return $"{(int)timeSpan.TotalMinutes}:{timeSpan.Seconds:00}";
        }

        private static double GetLobbyUptimeSeconds()
        {
            var timeManager = InstanceFinder.TimeManager;
            if (timeManager == null)
                return Time.unscaledTimeAsDouble;

            if (timeManager.Tick > 0)
                return timeManager.TicksToTime(timeManager.Tick);

            if (timeManager.ServerUptime > 0f)
                return timeManager.ServerUptime;

            if (timeManager.ClientUptime > 0f)
                return timeManager.ClientUptime;

            return Time.unscaledTimeAsDouble;
        }

        public static void SetEnableGuestBangCommands(bool value)
        {
            if (_enableGuestBangCommands == null)
                return;

            _enableGuestBangCommands.Value = value;
            MelonPreferences.Save();
        }

        public static void SetServerName(string value)
        {
            if (_serverName == null)
                return;

            _serverName.Value = value ?? string.Empty;
            MelonPreferences.Save();
        }

        public static void SetServerCapacity(int value)
        {
            if (_serverCapacity == null)
                return;

            _serverCapacity.Value = Math.Clamp(value, 1, 64);
            MelonPreferences.Save();
        }

        public static void SetSearchMinPlayers(int value)
        {
            if (_searchMinPlayers == null)
                return;

            _searchMinPlayers.Value = Math.Clamp(value, 1, 64);
            MelonPreferences.Save();
        }

        public static void SetSearchMaxPlayers(int value)
        {
            if (_searchMaxPlayers == null)
                return;

            _searchMaxPlayers.Value = value == 0 ? 0 : Math.Clamp(value, 1, 64);
            MelonPreferences.Save();
        }

        public static void SetSearchShowLocked(bool value)
        {
            if (_searchShowLocked == null)
                return;

            _searchShowLocked.Value = value;
            MelonPreferences.Save();
        }

        public static void SetSearchShowModded(bool value)
        {
            if (_searchShowModded == null)
                return;

            _searchShowModded.Value = value;
            MelonPreferences.Save();
        }

        public static void SetSearchShowYourLanguageOnly(bool value)
        {
            if (_searchShowYourLanguageOnly == null)
                return;

            _searchShowYourLanguageOnly.Value = value;
            MelonPreferences.Save();
        }

        public static void SetSearchOnlyPeacefulLobbies(bool value)
        {
            if (_searchOnlyPeacefulLobbies == null)
                return;

            _searchOnlyPeacefulLobbies.Value = value;
            if (value && _searchHidePeacefulLobbies != null)
                _searchHidePeacefulLobbies.Value = false;

            MelonPreferences.Save();
        }

        public static void SetSearchHidePeacefulLobbies(bool value)
        {
            if (_searchHidePeacefulLobbies == null)
                return;

            _searchHidePeacefulLobbies.Value = value;
            if (value && _searchOnlyPeacefulLobbies != null)
                _searchOnlyPeacefulLobbies.Value = false;

            MelonPreferences.Save();
        }

        public static void SetSearchRegionMode(int value)
        {
            if (_searchRegionMode == null)
                return;

            _searchRegionMode.Value = Math.Clamp(value, 0, 3);
            MelonPreferences.Save();
        }

        public static void SetIsPublicLobby(bool value)
        {
            if (_isPublicLobby == null)
                return;
            _isPublicLobby.Value = value;
            MelonPreferences.Save();
        }

        public static void SetIsPasswordProtected(bool value)
        {
            if (_isPasswordProtected == null)
                return;
            _isPasswordProtected.Value = value;
            MelonPreferences.Save();
        }

        public static void SetLobbyPassword(string value)
        {
            if (_lobbyPassword == null)
                return;
            _lobbyPassword.Value = value ?? string.Empty;
            MelonPreferences.Save();
        }

        public static void SetIsPeacefulMode(bool value)
        {
            if (_isPeacefulMode == null)
                return;
            _isPeacefulMode.Value = value;
            MelonPreferences.Save();
        }

        public static void SetIsTextChatOnly(bool value)
        {
            if (_isTextChatOnly == null)
                return;
            _isTextChatOnly.Value = value;
            MelonPreferences.Save();
        }

        public static void SetMessageOfTheDay(string value)
        {
            if (_messageOfTheDay == null)
                return;

            _messageOfTheDay.Value = value ?? string.Empty;
            MelonPreferences.Save();
        }

        public static void SetShowJoinMessages(bool value)
        {
            if (_showJoinMessages == null)
                return;

            _showJoinMessages.Value = value;
            MelonPreferences.Save();
        }

        public static void SetShowLeaveMessages(bool value)
        {
            if (_showLeaveMessages == null)
                return;

            _showLeaveMessages.Value = value;
            MelonPreferences.Save();
        }

        public static void SetJoinMessageSize(int value)
        {
            if (_joinMessageSize == null)
                return;

            _joinMessageSize.Value = Math.Clamp(value, 50, 100);
            MelonPreferences.Save();
        }

        public static void SetLeaveMessageSize(int value)
        {
            if (_leaveMessageSize == null)
                return;

            _leaveMessageSize.Value = Math.Clamp(value, 50, 100);
            MelonPreferences.Save();
        }

        public static void SetEnableAnticheat(bool value)
        {
            if (_enableAnticheat == null)
                return;

            _enableAnticheat.Value = value;
            MelonPreferences.Save();
        }

        public static void SetAutoRestartOnCrash(bool value)
        {
            if (_autoRestartOnCrash == null)
                return;

            _autoRestartOnCrash.Value = value;
            MelonPreferences.Save();
        }

        public static void SetHeadlessAutoHost(bool value)
        {
            if (_headlessAutoHost == null)
                return;

            _headlessAutoHost.Value = value;
            MelonPreferences.Save();
        }

        private IEnumerator LoadReferences()
        {
            while ((Object)(object)_playerReferenceManager == (Object)null)
            {
                _playerReferenceManager = PlayerReferenceManager.Instance;
                yield return null;
            }
            // List<StatueSetup> statueSetups = null;
            // while (statueSetups == null || statueSetups.Count == 0)
            // {
            //     statueSetups = ((IEnumerable<StatueSetup>)Object.FindObjectsByType<StatueSetup>((FindObjectsSortMode)0)).ToList();
            //     Debug.Log("[Core] Waiting for scene references to load...");
            //     Debug.Log("Count: " + statueSetups.Count);
            //     yield return null;
            // }

            // Object.FindObjectsByType<StatueSetup>((FindObjectsSortMode)0).ToList();
            ReferencesLoaded = true;
            Debug.Log("[Core] Scene references loaded successfully.");
        }
    }
}
