using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppPlayEveryWare.EpicOnlineServices;   // EOSManager
using MelonLoader;
using UnityEngine;

namespace SledHeadless
{
    internal static partial class HeadlessPatches
    {
        // ──────────── Lobby self-eviction DIAGNOSTICS (capture WHY EOS evicts the host) ────────────
        // We suppress the downstream Lobby.Clear() (the self-eviction guard), so we never see which of the TWO
        // PEWS clear paths fired it. We can only safely instrument ONE of them:
        //   • OnKickedFromLobby(string lobbyId)  -> the EOS_Lobby_AddNotifyLeaveLobbyRequested notification.
        //     SAFE to patch (plain string param) -> sets _lastEvictionTrigger, read by the Clear guard.
        //   • OnMemberStatusReceived(ref struct) -> member status Disconnected(2)/Kicked(3)/Closed(5).
        //     NOT PATCHABLE: the LobbyMemberStatusReceivedCallbackInfo struct is non-blittable (ProductUserId /
        //     Utf8String fields) and Il2CppInterop's native->managed trampoline NREs marshaling it on EVERY
        //     member event. So we INFER it: a suppressed Clear with NO recent OnKickedFromLobby == member-status
        //     path (exact 2/3/5 not captured here — would need the EOS SDK log callback). Behaviour unchanged.
        internal static string _lastEvictionTrigger = null;
        internal static float _lastEvictionTriggerAt = -9999f;

        internal static void ApplyLobbyEvictionDiagnostics(HarmonyLib.Harmony harmony)
        {
            string[] eosLobbyMgr =
            {
                "Il2CppPlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager",
                "PlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager",
            };
            TryPatch(harmony, eosLobbyMgr, "OnKickedFromLobby",
                prefix: nameof(EvictionDiag_OnKickedFromLobby_Prefix), label: "EvictionDiag.OnKickedFromLobby");
            // OnMemberStatusReceived(ref LobbyMemberStatusReceivedCallbackInfo) is deliberately NOT patched —
            // the ref-struct param crashes the Il2CppInterop trampoline (NRE) on every member event.
        }

        // Simple string param — binds reliably. Fires only when EOS asks the host to leave (LeaveLobbyRequested).
        private static void EvictionDiag_OnKickedFromLobby_Prefix(string lobbyId)
        {
            if (!Application.isBatchMode) return;
            _lastEvictionTrigger = $"OnKickedFromLobby/LeaveLobbyRequested (lobby={lobbyId})";
            _lastEvictionTriggerAt = Time.realtimeSinceStartup;
            MelonLogger.Warning($"[HeadlessMode][EvictionDiag] OnKickedFromLobby fired — EOS asked the host to LEAVE lobby {lobbyId} " +
                "(LeaveLobbyRequested notification). THIS is the evict trigger.");
        }
    }
}
