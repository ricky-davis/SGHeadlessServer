using System;
using System.Collections;
using HarmonyLib;
using Il2Cpp;
using Il2CppFishNet;
using MelonLoader;

namespace LobbyKit.Patches
{
    [HarmonyPatch]
    internal static class CrashProtectionPatches
    {
        // On awake, validate that the TrinketPack's player SyncVar is properly
        // initialized. If it's not (e.g. a test-spawned object without a real FishNet
        // owner), the SyncVar throws on .Value access and causes continuous spam.
        // Wait until the object is server-initialized, then despawn it if invalid.
        [HarmonyPatch(typeof(TrinketPack), "Awake")]
        [HarmonyPostfix]
        private static void TrinketPack_Awake_Postfix(TrinketPack __instance)
        {
            MelonCoroutines.Start(ValidateTrinketPackCoroutine(__instance));
        }

        private static IEnumerator ValidateTrinketPackCoroutine(TrinketPack trinketPack)
        {
            // Wait until FishNet has initialized this object on the server
            int waited = 0;
            while (!trinketPack.IsServerInitialized && waited < 300)
            {
                yield return null;
                waited++;
            }

            if (!trinketPack.IsServerInitialized)
                yield break;

            string reason = GetTrinketInvalidReason(trinketPack);
            if (reason == null)
                yield break;

            MelonLogger.Warning($"[LobbyKit] Despawning invalid TrinketPack: {reason}");
            try { InstanceFinder.ServerManager?.Despawn(trinketPack.NetworkObject); }
            catch (Exception ex) { MelonLogger.Warning($"[LobbyKit] TrinketPack despawn failed: {ex.GetType().Name}"); }
        }

        // Returns a reason string if the trinket is in an invalid state, null if healthy.
        // Validates the same property chain that TrinketEffectPosition accesses, so any
        // state that would cause it to throw is caught here first.
        private static string GetTrinketInvalidReason(TrinketPack trinketPack)
        {
            try
            {
                var syncVar = trinketPack.sync_PlayerThatPickedUpObject;
                if (syncVar == null)
                    return "sync_PlayerThatPickedUpObject is null";

                // Access .Value without ?. to force the throw if the SyncVar is uninitialized
                var player = syncVar.Value;
                if (player == null)
                    return null; // not held by anyone — valid idle state

                // Player reference exists but their network connection is gone — stale owner
                if (player.Owner == null)
                    return $"player owner NetworkConnection is null (player disconnected while holding)";

                // Validate holdingController — what TrinketEffectPosition accesses
                var holdingController = player.holdingController;
                if (holdingController == null)
                    return "player.holdingController is null";

                return null; // all good
            }
            catch (Exception ex)
            {
                return $"exception during validation: {ex.GetType().Name}";
            }
        }

        // Sled.FollowOwnerWhileInactive is called every FixedUpdate and spams
        // "Owner is null!" errors after a crash or unexpected disconnect. The method
        // already returns early for a null owner, but Unity logs the error first.
        // Skip the method silently when there's no valid owner to follow.
        [HarmonyPatch(typeof(Sled), "FollowOwnerWhileInactive")]
        [HarmonyPrefix]
        private static bool Sled_FollowOwnerWhileInactive_Prefix(Sled __instance)
        {
            try
            {
                var ownerSync = __instance.sync_Owner;
                if (ownerSync == null || ownerSync.Value == null)
                    return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[LobbyKit] Sled.FollowOwnerWhileInactive guard threw: {ex.GetType().Name}");
                return false;
            }
            return true;
        }

        // Any NetworkObject whose stop callback throws (e.g. a held object with a null
        // owner after a crash) will abort FishNet's despawn loop, leaving objects stuck
        // and spamming NullReferenceExceptions every time the pool resets them. Suppress
        // exceptions here so teardown and object pooling continue past any broken callback.
        [HarmonyPatch(typeof(Il2CppFishNet.Object.NetworkObject), "InvokeStopCallbacks")]
        [HarmonyFinalizer]
        private static Exception NetworkObject_InvokeStopCallbacks_Finalizer(Exception __exception)
        {
            if (__exception != null)
                MelonLogger.Warning($"[LobbyKit] NetworkObject stop callback threw (suppressed): {__exception.GetType().Name}");
            return null;
        }
    }
}
