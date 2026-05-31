using System;
using System.Collections;
using HarmonyLib;
using Il2Cpp;
using Il2CppFishNet;
using Il2Cpp_Scripts.Managers;
using MelonLoader;

namespace LobbyKit.Patches
{
    [HarmonyPatch]
    internal static class AutoRestartPatches
    {
        private static bool _lastProximityChatEnabled = false;
        private static string _lastPlatform = string.Empty;
        private static string _lastRegion = string.Empty;
        private static bool _lastCrossplayEnabled = true;
        private static bool _hasLastLobbyParams = false;

        private static int _pendingAutoRestartAttempts = 0;
        private const int MaxAutoRestartAttempts = 3;
        private const int AutoRestartRetryDelayFrames = 300; // 5 seconds

        private static bool _awaitingLobbyLeft = false;
        private static bool _createCompleteFired = false;
        private static bool _pendingFishNetStop = false;

        [HarmonyPatch(typeof(LobbyManager), "CreateLobby")]
        [HarmonyPrefix]
        private static void LobbyManager_CreateLobby_Prefix(
            ref bool proximityChatEnabled,
            ref string platform,
            ref string region,
            ref bool crossplayEnabled)
        {
            _lastProximityChatEnabled = proximityChatEnabled;
            _lastPlatform = platform ?? string.Empty;
            _lastRegion = region ?? string.Empty;
            _lastCrossplayEnabled = crossplayEnabled;
            _hasLastLobbyParams = true;
        }

        [HarmonyPatch(typeof(UiReferenceController), "LeaveGame")]
        [HarmonyPrefix]
        private static void UiReferenceController_LeaveGame_Prefix()
        {
            LobbyKitCore.WasHosting = false;
            ChatSystem.ResetSessionState();
        }

        [HarmonyPatch(typeof(UiReferenceController), "ReturnToMainMenu")]
        [HarmonyPrefix]
        private static void UiReferenceController_ReturnToMainMenu_Prefix(int messageType)
        {
            if (!LobbyKitCore.AutoRestartOnCrash || !LobbyKitCore.WasHosting || messageType == 0)
                return;

            LobbyKitCore.WasHosting = false;

            if (!_hasLastLobbyParams)
            {
                MelonLogger.Warning("[LobbyKit] Auto-restart: no prior lobby params recorded, skipping restart.");
                return;
            }

            _pendingAutoRestartAttempts = MaxAutoRestartAttempts;
            MelonCoroutines.Start(AutoRestartLobbyCoroutine());
        }

        [HarmonyPatch(typeof(LobbyManager), "OnCreateLobbyComplete")]
        [HarmonyPrefix]
        private static bool LobbyManager_OnCreateLobbyComplete_Prefix(int result)
        {
            // On success during auto-restart: stop any ghost FishNet server synchronously
            // before the original method calls serverMgr.StartConnection(). The ghost
            // server (recreated by Dissonance after the crash) may have set FishNet's
            // internal _started flag even if IsAnyServerStarted() returns false, causing
            // StartConnection to bail with "Server is already running."
            if (result == 0 && _pendingFishNetStop)
            {
                _pendingFishNetStop = false;
                try { InstanceFinder.ServerManager?.StopConnection(false); } catch { }
                try { InstanceFinder.ClientManager?.StopConnection(); } catch { }
            }

            if (result == 0 || _pendingAutoRestartAttempts <= 0)
            {
                _pendingAutoRestartAttempts = 0;
                _createCompleteFired = false;
                return true;
            }

            // Guard: EOSLobbyManager can fire this callback twice for one failure
            // (duplicate registration survives scene transitions). Only handle the first.
            if (_createCompleteFired)
                return false;
            _createCompleteFired = true;

            _pendingAutoRestartAttempts--;
            int attempt = MaxAutoRestartAttempts - _pendingAutoRestartAttempts;
            MelonLogger.Warning($"[LobbyKit] Auto-restart: lobby creation failed (attempt {attempt}/{MaxAutoRestartAttempts}), retrying in 5 seconds...");
            MelonCoroutines.Start(AutoRestartRetryCoroutine());
            return false;
        }

        [HarmonyPatch(typeof(UiReferenceController), "OnLobbyLeft")]
        [HarmonyFinalizer]
        private static Exception UiReferenceController_OnLobbyLeft_Finalizer(Exception __exception, int result)
        {
            if (!_awaitingLobbyLeft)
                return __exception;

            _awaitingLobbyLeft = false;

            if (__exception != null)
                MelonLogger.Warning("[LobbyKit] Auto-restart: OnLobbyLeft threw during FishNet teardown — continuing restart.");
            else if (result == 0)
                MelonLogger.Msg("[LobbyKit] Auto-restart: EOS lobby destroyed — creating new lobby...");
            else
                MelonLogger.Warning($"[LobbyKit] Auto-restart: EOS lobby destroy returned {result} — attempting create anyway...");

            MelonCoroutines.Start(AutoRestartCreateAfterLeaveCoroutine());
            return null; // suppress exception — EOS lobby is already destroyed
        }

        private static IEnumerator AutoRestartLobbyCoroutine()
        {
            MelonLogger.Msg("[LobbyKit] Lobby crashed — restarting lobby...");
            ChatSystem.ResetSessionState();

            // Brief delay for the main menu to finish its UI transition
            for (int i = 0; i < 120; i++)
                yield return null;

            // Pre-stop FishNet before calling LeaveGame. In crash scenarios where
            // Dissonance recreates a ghost server, this primes FishNet's state so
            // OnLobbyLeft's StopConnection is more likely to complete cleanly.
            // Silently ignored if it throws (e.g., during test crashes with broken objects).
            try
            {
                var serverManager = InstanceFinder.ServerManager;
                if (serverManager != null && serverManager.IsAnyServerStarted())
                    serverManager.StopConnection(false);
                InstanceFinder.ClientManager?.StopConnection();
            }
            catch { }

            // Use the game's own leave path so the owner check runs and
            // DestroyCurrentLobby (not LeaveLobby) is called for the host.
            // LobbyManager.LeaveLobby() always calls LeaveLobby on EOSLobbyManager,
            // which EOS rejects for lobby owners — leaving the presence entry alive.
            var uiController = UiReferenceController.Instance;
            if (uiController != null)
            {
                MelonLogger.Msg("[LobbyKit] Auto-restart: destroying stale EOS lobby...");
                _awaitingLobbyLeft = true;
                uiController.LeaveGame(0);
                // CreateLobby is triggered from UiReferenceController_OnLobbyLeft_Finalizer
                // once EOS confirms the destroy completed (or if OnLobbyLeft throws).
            }
            else
            {
                MelonLogger.Warning("[LobbyKit] Auto-restart: UiReferenceController unavailable, skipping destroy.");
                InvokeCreateLobby(LobbyManager.Instance);
            }
        }

        private static IEnumerator AutoRestartCreateAfterLeaveCoroutine()
        {
            // Settle time after EOS confirms the lobby is destroyed
            for (int i = 0; i < 180; i++)
                yield return null;

            // StopConnection is async — poll until FishNet reports fully stopped.
            // Starting a new host before shutdown completes causes "Server failed to
            // start in host mode within timeout."
            int fishNetWait = 0;
            while (fishNetWait < 300)
            {
                bool stillRunning;
                try { stillRunning = InstanceFinder.ServerManager?.IsAnyServerStarted() == true; }
                catch { break; }
                if (!stillRunning) break;
                fishNetWait++;
                yield return null;
            }
            if (fishNetWait > 0)
                MelonLogger.Msg($"[LobbyKit] Auto-restart: waited {fishNetWait} extra frames for FishNet to stop.");

            InvokeCreateLobby(LobbyManager.Instance);
        }

        private static IEnumerator AutoRestartRetryCoroutine()
        {
            _createCompleteFired = false;
            for (int i = 0; i < AutoRestartRetryDelayFrames; i++)
                yield return null;

            InvokeCreateLobby(LobbyManager.Instance);
        }

        private static void InvokeCreateLobby(LobbyManager lobbyManager)
        {
            if (lobbyManager == null)
            {
                MelonLogger.Warning("[LobbyKit] Auto-restart: LobbyManager unavailable.");
                _pendingAutoRestartAttempts = 0;
                return;
            }

            _createCompleteFired = false;
            _pendingFishNetStop = true;

            string lobbyName = !string.IsNullOrWhiteSpace(LobbyKitCore.ServerName)
                ? LobbyKitCore.ServerName
                : $"{GameInfo.Instance?.PlayerName ?? "Host"}'s Lobby";

            MelonLogger.Msg("[LobbyKit] Auto-restart: creating lobby...");
            lobbyManager.CreateLobby(
                lobbyName,
                LobbyKitCore.ServerCapacity,
                LobbyKitCore.IsPublicLobby,
                _lastProximityChatEnabled,
                LobbyKitCore.IsPasswordProtected,
                LobbyKitCore.LobbyPassword,
                LobbyKitCore.IsPeacefulMode,
                _lastPlatform,
                _lastRegion,
                _lastCrossplayEnabled);
        }
    }
}
