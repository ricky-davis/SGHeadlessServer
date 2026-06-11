using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using Il2CppFishNet;
using Il2CppEpic.OnlineServices;
using Il2CppEpic.OnlineServices.Auth;
using Il2CppEpic.OnlineServices.Connect;
using Il2CppPlayEveryWare.EpicOnlineServices;
using Il2CppPlayEveryWare.EpicOnlineServices.Samples;
using Il2Cpp_Scripts.Managers;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SledHeadless
{
    internal static partial class HeadlessPatches
    {

        // Writes the just-added PlayerReference into PlayerReferenceManager's lookup dictionaries, which the
        // native TryGetPlayer / *.Server_Interact paths read by plain native field access. These dicts are
        // directly reachable through Il2CppInterop (verified live via the RuntimeAPI /eval endpoint), so we
        // mirror exactly the writes OnPlayerReferenceAdded performs, minus its EOS/host-only tail that NREs on
        // a headless host. This is the whole chat fix: once _playerConnectionIdToPlayerReference[connId] is set,
        // OnServerReceivedChatBroadcastFromClient resolves the sender and re-broadcasts normally.
        private static void PlayerReferenceManager_Server_AddPlayerReference_Postfix(Il2Cpp.PlayerReferenceManager __instance)
        {
            if (!Application.isBatchMode || _isQuitting || __instance == null) return;
            try
            {
                var list = __instance.GetPlayerReferences();
                if (list == null || list.Count == 0) return;
                var r = list[list.Count - 1];
                if (r == null) return;

                int connId = r.ConnectionID;

                var connDict = __instance._playerConnectionIdToPlayerReference;
                if (connDict != null) connDict[connId] = r;

                try { var pidDict = __instance._playerPlatformIdToPlayerReference; if (pidDict != null && !string.IsNullOrEmpty(r.ProductUserId)) pidDict[r.ProductUserId] = r; } catch { }
                try { var puidDict = __instance._playerPlatformUserIdToPlayerReference; if (puidDict != null && r.PlatformUserId > 0) puidDict[r.PlatformUserId] = r; } catch { }

                MelonLogger.Msg($"[HeadlessMode][PRM] Populated lookup dicts for connId={connId} user='{r.Username}' (connDict={(connDict == null ? -1 : connDict.Count)}, refs={list.Count}).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode][PRM] Server_AddPlayerReference postfix: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Null-safe reimplementation of PlayerReferenceManager.GetAllConnectionIdsNearPosition.
        // The native method dereferences each PlayerReference.PlayerControl.transform.position
        // without guarding against a null PlayerControl. The headless host reference (connId 32767)
        // has a null PlayerControl, so the native loop NREs — and because this runs inside the
        // Cmd_InitialiseRace ServerRpc reader, FishNet kicks the client that started the race.
        // We replace the body entirely (return false) on headless, skipping null-PlayerControl refs.
        private static bool PlayerReferenceManager_GetAllConnectionIdsNearPosition_Prefix(
            Il2Cpp.PlayerReferenceManager __instance,
            UnityEngine.Vector3 position,
            float radius,
            ref Il2CppSystem.Collections.Generic.List<int> __result)
        {
            if (!Application.isBatchMode || _isQuitting) return true; // run native on non-headless

            var result = new Il2CppSystem.Collections.Generic.List<int>();
            try
            {
                var list = __instance?.GetPlayerReferences();
                if (list != null)
                {
                    int n = list.Count;
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            var pr = list[i];
                            if (pr == null) continue;
                            var pc = pr.PlayerControl;
                            if (pc == null) continue;           // headless host (32767) has no avatar — skip
                            var tr = pc.transform;
                            if (tr == null) continue;
                            var p = tr.position;
                            float dx = p.x - position.x;
                            float dy = p.y - position.y;
                            float dz = p.z - position.z;
                            if (Mathf.Sqrt(dx * dx + dy * dy + dz * dz) <= radius)
                                result.Add(pr.ConnectionID);
                        }
                        catch { /* one bad reference must not abort the whole scan */ }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode][RACE] GetAllConnectionIdsNearPosition reimpl error: {ex.GetType().Name}: {ex.Message}");
            }

            __result = result;
            return false; // skip the unguarded native method
        }
    }
}
