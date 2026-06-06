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
        // Instance IDs of phantom host NetworkObjects we have already made host-only, so the 2s
        // poll doesn't re-process (and re-log) them every tick.
        private static readonly HashSet<int> _hostOnlyPhantomNobs = new();

        // Polls for the headless host's phantom "Player Networked" object and makes it host-only
        // (visible to the server, never serialized to remote clients). Runs on a loop because the
        // phantom is spawned slightly after the server starts and we want it hidden before any
        // client joins. Idempotent — each phantom is processed once (see _hostOnlyPhantomNobs).
        private static IEnumerator HidePhantomHostPlayerLoop()
        {
            if (!Application.isBatchMode) yield break;
            while (!_isQuitting)
            {
                yield return new WaitForSecondsRealtime(2f);
                try { HidePhantomHostPlayersFromClients(); }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HIDE] loop error: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        private static void HidePhantomHostPlayersFromClients()
        {
            var serverMgr = InstanceFinder.ServerManager;
            if (serverMgr == null || !serverMgr.Started) return;
            var serverObjects = serverMgr.Objects;
            if (serverObjects == null) return;

            var pcs = Resources.FindObjectsOfTypeAll<Il2Cpp.PlayerControl>();
            if (pcs == null) return;
            foreach (var pc in pcs)
            {
                try
                {
                    if (pc == null || !pc.IsSpawned) continue;
                    var nob = pc.NetworkObject;
                    if (nob == null) continue;

                    // FishNet assigns real remote clients connection IDs in [0, 32766]. The headless
                    // host's phantom player is server-owned (-1) or carries the reserved clientHost id
                    // (32767). Only hide those — never touch a real client's own player object.
                    int ownerId = nob.OwnerId;
                    bool isPhantom = ownerId == 32767 || ownerId < 0;
                    if (!isPhantom) continue;

                    int nobId = nob.GetInstanceID();
                    if (_hostOnlyPhantomNobs.Contains(nobId)) continue;

                    if (MakeNetworkObjectHostOnly(nob, serverObjects))
                    {
                        _hostOnlyPhantomNobs.Add(nobId);
                        MelonLogger.Msg($"[HeadlessMode][HIDE] Phantom host PlayerControl (ownerId={ownerId}) is now host-only — " +
                                        "it will no longer be serialized to remote clients, so vanilla clients can finish loading.");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HIDE] {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        // Attaches a FishNet HostOnlyCondition to a NetworkObject's NetworkObserver so only the host
        // (the server-as-client) observes it; remote connections fail the condition (FishNet ANDs all
        // conditions) and the object is never spawned to them. Rebuilds observers so the change takes
        // effect immediately (and despawns it from any already-connected remote client).
        private static bool MakeNetworkObjectHostOnly(
            Il2CppFishNet.Object.NetworkObject nob,
            Il2CppFishNet.Managing.Server.ServerObjects serverObjects)
        {
            var observer = nob.NetworkObserver;
            if (observer == null)
            {
                // No NetworkObserver on the prefab — the object is observed via the global
                // ObserverManager. Add our own and tell it to ignore the manager so our condition
                // is authoritative.
                observer = nob.gameObject.AddComponent<Il2CppFishNet.Observing.NetworkObserver>();
                observer.OverrideType = Il2CppFishNet.Observing.NetworkObserver.ConditionOverrideType.IgnoreManager;
                nob.NetworkObserver = observer;
                observer.Initialize(nob);
            }

            // Ensure a conditions list exists.
            var conds = observer.ObserverConditionsInternal;
            if (conds == null)
            {
                conds = new Il2CppSystem.Collections.Generic.List<Il2CppFishNet.Observing.ObserverCondition>();
                observer.ObserverConditionsInternal = conds;
            }

            // Skip if a HostOnlyCondition is already present (idempotent / pre-existing).
            for (int i = 0; i < conds.Count; i++)
            {
                var existing = conds[i];
                if (existing != null && existing.TryCast<Il2CppFishNet.Component.Observing.HostOnlyCondition>() != null)
                {
                    serverObjects.RebuildObservers(nob, false);
                    return true;
                }
            }

            var cond = ScriptableObject
                .CreateInstance(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFishNet.Component.Observing.HostOnlyCondition>())
                .TryCast<Il2CppFishNet.Component.Observing.HostOnlyCondition>();
            if (cond == null)
            {
                MelonLogger.Warning("[HeadlessMode][HIDE] Failed to create HostOnlyCondition instance.");
                return false;
            }

            cond.Initialize(nob);
            conds.Add(cond);

            // Re-evaluate observers now so remote clients drop it (or never receive it).
            serverObjects.RebuildObservers(nob, false);
            return true;
        }

        // Registers the headless server as the host PlayerReference (connection ID 32767) in
        // PlayerReferenceManager.sync_PlayerReferences. Normally PlayerControl.InitializePlayerReferenceAsync
        // calls Cmd_AddPlayerReference from the host's client side, but in headless that async
        // method never completes. Without this entry, client-side checks for a valid host
        // PlayerReference block snowball pickup, chat, and other gameplay features.
        private static void RegisterHostPlayerReference()
        {
            try
            {
                var prm = Il2Cpp.PlayerReferenceManager.Instance;
                if (prm == null) { MelonLogger.Warning("[HeadlessMode] PlayerReferenceManager.Instance is null — host PlayerReference not registered."); return; }

                // Connection ID 32767 is FishNet's host/server-as-client connection ID.
                const int HostConnectionId = 32767;

                // Check if already registered (shouldn't be, but guard against double-call).
                Il2Cpp.PlayerReference existing = null;
                bool alreadyExists = prm.TryGetPlayer(HostConnectionId, out existing);
                if (alreadyExists) { MelonLogger.Msg("[HeadlessMode] Host PlayerReference already registered — skipping."); return; }

                string puid = "";
                try { puid = EOSManager.Instance?.GetProductUserId()?.ToString() ?? ""; } catch { }

                string username = !string.IsNullOrWhiteSpace(SledHeadlessCore.ServerName)
                    ? SledHeadlessCore.ServerName : "HeadlessServer";

                // Pass null for PlayerControl — a headless server has no physical player body.
                long platformId = (long)FakeSteamId;

                // Register the host reference into the SERVER-SIDE lookup dicts ONLY — never into the
                // synced sync_PlayerReferences SyncList (which Server_AddPlayerReference would do).
                // A null-PlayerControl host entry replicated to clients makes the game's CLIENT-side
                // PlayerReferenceManager.OnPlayerReferenceAdded dereference the null PlayerControl and
                // throw an NRE during the join spawn batch; that desyncs FishNet's PooledReader and hangs
                // every client that joins while another player is already present. Confirmed via a
                // client-side ClientSpawnDiag finalizer: BOTH synced entries throw, because the per-call
                // loop in OnPlayerReferenceAdded chokes on the null-PC host entry regardless of which
                // index is being added. The server only needs the dict entries —
                // OnServerReceivedChatBroadcastFromClient resolves senders from them and the null-safe
                // GetAllConnectionIdsNearPosition reimpl reads them — so we populate the dicts directly
                // with a constructed reference and keep the host out of every client's synced list.
                var hostRef = new Il2Cpp.PlayerReference(puid, platformId, HostConnectionId, username,
                    "" /* voiceId — no Dissonance in headless */, AuthPlatform.Steam, null);

                var connDict = prm._playerConnectionIdToPlayerReference;
                if (connDict != null) connDict[HostConnectionId] = hostRef;
                try { var pidDict = prm._playerPlatformIdToPlayerReference; if (pidDict != null && !string.IsNullOrEmpty(puid)) pidDict[puid] = hostRef; } catch { }
                try { var puidDict = prm._playerPlatformUserIdToPlayerReference; if (puidDict != null && platformId > 0) puidDict[platformId] = hostRef; } catch { }

                MelonLogger.Msg($"[HeadlessMode] Registered host PlayerReference (dict-only, NOT synced to clients):" +
                    $" connId={HostConnectionId}, puid={puid}, name={username}, platformId={platformId}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode] RegisterHostPlayerReference: {ex.GetType().Name}: {ex.Message}");
            }
        }

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
