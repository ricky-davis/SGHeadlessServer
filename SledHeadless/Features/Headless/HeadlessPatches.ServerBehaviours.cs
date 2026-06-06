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
        // On a headless (server-only) host, NetworkBehaviours that are DISABLED on the server — e.g.
        // PlayerMovement, which only the owning client simulates — never run their Awake-driven
        // NetworkInitialize_Early, so their SyncVars stay uninitialized (IsInitialized=False) and CANNOT
        // replicate to clients. FishNet normally force-initializes such behaviours via
        // NetworkInitializeIfDisabled(); under this custom (CreateLobby-based) headless start that call is
        // skipped. The visible symptom is snowball pickup never working for clients:
        // PlayerMovement.sync_CurrentFootstepCollection never replicates, so the client's
        // GetIsStandingOnSnow() is always false and the pickup prompt never shows. We replicate FishNet's
        // own call here. Verified live (RuntimeAPI /eval): NetworkInitializeIfDisabled() flips footstep
        // init False→True, the value then replicates, and YoureAllowedTo_PickupSnow() becomes true on the
        // client. The call is a guarded no-op once a behaviour is initialized, so re-running is safe; we
        // poll so players who join later are covered too.
        private static IEnumerator EnsureServerBehavioursInitializedLoop()
        {
            if (!Application.isBatchMode) yield break;
            while (!_isQuitting)
            {
                yield return new WaitForSecondsRealtime(2f);
                try { InitializeDisabledServerBehaviours(); }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][NETINIT] loop error: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        // Player NetworkObjects whose footstep value we have already force-re-sent to clients, mapped to
        // the OwnerId (connection id) we delivered it for. FishNet POOLS NetworkObjects: a rejoining player
        // can REUSE the same object (same GetInstanceID), but it always arrives with a NEW connection id
        // (FishNet increments connIds and does not immediately recycle them — verified live: client left as
        // connId 1, rejoined as connId 3). So keying on (nobId, ownerId) — not nobId alone — makes a rejoin
        // count as fresh and triggers a new cross-tick re-send, even when leave+rejoin happen inside one
        // poll interval (the old poll-based IntersectWith prune missed that fast-rejoin window → the
        // rejoiner's footstep stayed stuck at None and they couldn't pick up snowballs).
        private static readonly Dictionary<int, int> _footstepResentNobOwner = new();

        // The headless server keeps each player's PlayerMovement DISABLED (only the owning client
        // simulates it). A DISABLED NetworkBehaviour's SyncVars are excluded from FishNet's per-observer
        // delta sync, so PlayerMovement.sync_CurrentFootstepCollection (ServerOnly-write, set by the owner
        // via CmdSetFootstepCollection, read by GetIsStandingOnSnow) never reaches clients → the snowball
        // pickup prompt never appears. We must do TWO things:
        //   1. ENABLE PlayerMovement so FishNet will replicate its SyncVars at all.
        //   2. DELIVER the current footstep value. Enabling alone is not enough: the footstep changes
        //      None→Snow exactly once (owner first steps on snow); if that transition happened while the
        //      behaviour was disabled the delta is lost, and afterwards the owner keeps sending the SAME
        //      value (no change → no delta). A SAME-FRAME re-set (Snow→None→Snow in one tick) is COALESCED
        //      by FishNet into no net change and sends nothing. Trying to "catch" the natural transition by
        //      enabling early was too timing-dependent and failed in practice. The ONLY reliable delivery is
        //      a CROSS-TICK transition: set None, let a tick pass so FishNet actually transmits None, then
        //      restore the real value → a genuine None→Snow delta that reaches every client. Verified live.
        // Movement stays owner-authoritative (the server is not the owner), so this only restores SyncVar
        // replication; it does not let the server drive movement. (NOTE: SyncBase.IsDirty is a red herring —
        // every SyncVar reads dirty=True here, including enabled ones that clearly replicate.)
        private static IEnumerator EnsurePlayerMovementEnabledLoop()
        {
            if (!Application.isBatchMode) yield break;
            var resendQueue = new List<Il2Cpp.PlayerMovement>();
            while (!_isQuitting)
            {
                yield return new WaitForSecondsRealtime(0.5f);

                // Pass 1 (no yields): enable PlayerMovement everywhere, and queue any player whose footstep
                // now holds a real (non-None) value that we have not yet force-re-sent.
                resendQueue.Clear();
                var spawnedNobIds = new HashSet<int>();
                try
                {
                    var pcs = Resources.FindObjectsOfTypeAll<Il2Cpp.PlayerControl>();
                    if (pcs != null)
                    {
                        foreach (var pc in pcs)
                        {
                            try
                            {
                                if (pc == null || !pc.IsSpawned) continue;
                                var mv = pc.movement;
                                if (mv == null) continue;
                                var nob = pc.NetworkObject;
                                int owner = nob == null ? -1 : nob.OwnerId;
                                if (owner == 32767 || owner < 0) continue; // skip the host phantom

                                if (!mv.enabled)
                                {
                                    mv.enabled = true;
                                    MelonLogger.Msg($"[HeadlessMode][MVENABLE] Enabled PlayerMovement for owner={owner}.");
                                }

                                int nobId = nob == null ? 0 : nob.GetInstanceID();
                                spawnedNobIds.Add(nobId);
                                // Skip only if we have already delivered for THIS owner session. A pooled
                                // object reused by a rejoiner carries a new OwnerId, so the recorded value
                                // won't match and we re-send (treat the rejoin as fresh).
                                if (_footstepResentNobOwner.TryGetValue(nobId, out var sentOwner) && sentOwner == owner) continue;
                                var sv = mv.sync_CurrentFootstepCollection;
                                if (sv == null || sv.Value == Il2Cpp.FootstepCollectionType.None) continue; // wait for a real surface
                                resendQueue.Add(mv);
                            }
                            catch { }
                        }
                    }

                    // Drop records for objects that are no longer spawned (returned to the pool). Not
                    // strictly required for correctness now that we key on OwnerId — a rejoin with a new
                    // connId re-sends regardless — but it keeps the map from growing unbounded.
                    if (_footstepResentNobOwner.Count > 0)
                    {
                        var dead = new List<int>();
                        foreach (var kv in _footstepResentNobOwner)
                            if (!spawnedNobIds.Contains(kv.Key)) dead.Add(kv.Key);
                        foreach (var id in dead) _footstepResentNobOwner.Remove(id);
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][MVENABLE] scan error: {ex.GetType().Name}: {ex.Message}"); }

                // Pass 2 (yields): cross-tick re-send each queued player so the value is actually delivered.
                foreach (var mv in resendQueue)
                {
                    var sv = mv == null ? null : mv.sync_CurrentFootstepCollection;
                    if (sv == null) continue;
                    var cur = sv.Value;
                    if (cur == Il2Cpp.FootstepCollectionType.None) continue;

                    bool setNone = false;
                    try { sv.Value = Il2Cpp.FootstepCollectionType.None; setNone = true; } catch { }
                    if (!setNone) continue;

                    yield return new WaitForSecondsRealtime(0.2f); // let FishNet transmit None on its own tick

                    try
                    {
                        // Restore the real value (unless the owner already pushed a new one meanwhile).
                        if (sv.Value == Il2Cpp.FootstepCollectionType.None) sv.Value = cur;
                        var nob = mv.NetworkObject;
                        if (nob != null) _footstepResentNobOwner[nob.GetInstanceID()] = nob.OwnerId;
                        MelonLogger.Msg($"[HeadlessMode][MVENABLE] Cross-tick re-sent footstep={cur} for owner={(nob == null ? -1 : nob.OwnerId)} — clients can now see it (snowball pickup).");
                    }
                    catch { }
                }
            }
        }

        private static void InitializeDisabledServerBehaviours()
        {
            var pcs = Resources.FindObjectsOfTypeAll<Il2Cpp.PlayerControl>();
            if (pcs == null) return;
            foreach (var pc in pcs)
            {
                try
                {
                    if (pc == null || !pc.IsSpawned) continue;

                    var mv = pc.movement;
                    if (mv == null) continue;

                    // Only run the (cheap) NETINIT pass while a SyncVar is still uninitialized.
                    // (PlayerMovement is enabled separately and FAST in EnsurePlayerMovementEnabledLoop so
                    // its footstep SyncVar replicates in time — see that loop for the full explanation.)
                    var foot = mv.sync_CurrentFootstepCollection;
                    if (foot == null || foot.IsInitialized) continue;

                    var nob = pc.NetworkObject;
                    if (nob == null) continue;
                    var nbs = nob.NetworkBehaviours;
                    if (nbs == null) continue;

                    int n = nbs.Count;
                    for (int i = 0; i < n; i++)
                    {
                        try { nbs[i].NetworkInitializeIfDisabled(); } catch { }
                    }
                    MelonLogger.Msg($"[HeadlessMode][NETINIT] Initialized {n} disabled NetworkBehaviours for player owner={pc.OwnerId} — SyncVars (footstep/snow, etc.) can now replicate.");
                }
                catch { }
            }
        }
    }
}
