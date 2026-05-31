using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppFishNet.Managing.Server;
using Il2CppFishNet.Transporting;
using Il2Cpp_Scripts.Managers;
using MelonLoader;

namespace LobbyKit.Patches
{
    [HarmonyPatch]
    internal static class SilentCrashDetectionPatches
    {
        // Route 1: FishNet server connection state change
        // Fires whenever the server transitions between Stopped/Starting/Started/Stopping.
        // If it reaches Stopped while WasHosting is still true, LeaveGame was never called
        // — strong indicator of a silent crash.
        [HarmonyPatch(typeof(ServerManager), "Transport_OnServerConnectionState")]
        [HarmonyPostfix]
        private static void ServerManager_Transport_OnServerConnectionState_Postfix(ServerConnectionStateArgs args)
        {
            MelonLogger.Msg($"[CrashDetect] Route1 — ServerConnectionState: {args.ConnectionState} | WasHosting={LobbyKitCore.WasHosting} | isHost={LobbyKitCore.isHost}");

            if (args.ConnectionState == LocalConnectionState.Stopped && LobbyKitCore.WasHosting)
                MelonLogger.Warning("[CrashDetect] Route1 — Server stopped while WasHosting=true: possible silent crash.");
        }

        // Route 2: Remote client connection state change
        // Tracks each client connecting/disconnecting. A burst of disconnects in quick
        // succession (many clients leaving within a short window) while still hosting
        // suggests a crash rather than voluntary leaves.
        private static readonly Dictionary<float, int> _recentDisconnects = [];
        private const float DisconnectBurstWindow = 2f;
        private const int DisconnectBurstThreshold = 2;

        [HarmonyPatch(typeof(ServerManager), "Transport_OnRemoteConnectionState")]
        [HarmonyPostfix]
        private static void ServerManager_Transport_OnRemoteConnectionState_Postfix(RemoteConnectionStateArgs args)
        {
            MelonLogger.Msg($"[CrashDetect] Route2 — RemoteConnectionState: clientId={args.ConnectionId} state={args.ConnectionState} | WasHosting={LobbyKitCore.WasHosting}");

            if (args.ConnectionState != RemoteConnectionState.Stopped || !LobbyKitCore.WasHosting)
                return;

            float now = UnityEngine.Time.realtimeSinceStartup;
            _recentDisconnects[now] = args.ConnectionId;

            int burstCount = 0;
            foreach (var ts in _recentDisconnects.Keys)
            {
                if (now - ts <= DisconnectBurstWindow)
                    burstCount++;
            }

            MelonLogger.Msg($"[CrashDetect] Route2 — Remote client {args.ConnectionId} disconnected. {burstCount} disconnect(s) in last {DisconnectBurstWindow}s.");

            if (burstCount >= DisconnectBurstThreshold)
                MelonLogger.Warning($"[CrashDetect] Route2 — {burstCount} clients disconnected within {DisconnectBurstWindow}s: possible silent crash.");
        }

        // Routes 3 & 4: Polling coroutine
        // Periodically checks FishNet server state and EOS lobby state while hosting.
        // Route 3: IsAnyServerStarted() returning false while WasHosting is true.
        // Route 4: EOS lobby still active (GetCurrentLobby non-null) but FishNet server stopped.
        private static bool _pollingActive = false;

        internal static void StartPolling()
        {
            if (_pollingActive)
                return;
            _pollingActive = true;
            MelonCoroutines.Start(PollHostStateCoroutine());
        }

        internal static void StopPolling()
        {
            _pollingActive = false;
        }

        private static IEnumerator PollHostStateCoroutine()
        {
            MelonLogger.Msg("[CrashDetect] Polling coroutine started.");

            while (_pollingActive)
            {
                for (int i = 0; i < 120; i++)
                    yield return null; // poll every ~2 seconds

                if (!LobbyKitCore.WasHosting)
                    continue;

                bool serverRunning;
                try { serverRunning = InstanceFinder.ServerManager?.IsAnyServerStarted() == true; }
                catch { serverRunning = false; }

                bool eosLobbyActive;
                string lobbyId;
                try
                {
                    var lobby = LobbyManager.Instance?.GetCurrentLobby();
                    lobbyId = LobbyManager.Instance?.GetLobbyId() ?? "<none>";
                    eosLobbyActive = lobby != null && !string.IsNullOrEmpty(lobbyId);
                }
                catch
                {
                    eosLobbyActive = false;
                    lobbyId = "<error>";
                }

                MelonLogger.Msg($"[CrashDetect] Route3/4 poll — serverRunning={serverRunning} | eosLobbyActive={eosLobbyActive} | lobbyId={lobbyId} | WasHosting={LobbyKitCore.WasHosting}");

                if (!serverRunning)
                    MelonLogger.Warning("[CrashDetect] Route3 — IsAnyServerStarted()=false while WasHosting=true: possible silent crash.");

                if (!serverRunning && eosLobbyActive)
                    MelonLogger.Warning("[CrashDetect] Route4 — EOS lobby active but FishNet server stopped: mismatch detected.");
            }

            MelonLogger.Msg("[CrashDetect] Polling coroutine stopped.");
        }
    }
}
