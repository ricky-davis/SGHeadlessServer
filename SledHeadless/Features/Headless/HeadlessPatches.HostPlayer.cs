using System;
using System.Collections;
using Il2Cpp;
using Il2CppFishNet;
using Il2CppPlayEveryWare.EpicOnlineServices;
using MelonLoader;
using UnityEngine;

namespace SledHeadless
{
    internal static partial class HeadlessPatches
    {
        // ── Real host PlayerControl + synced PlayerReference ──────────────────────────────
        //
        // Goal: the headless host appears in every client's in-game player list (incl. vanilla) under
        // FakeClientName, backed by a REAL spawned PlayerControl. A real (non-null) PlayerControl is
        // REQUIRED — not just for the list row, but because client-side systems that iterate players to
        // anchor things to them (player nametags / ragdoll head bones) break when a PlayerReference in the
        // synced list has a null PlayerControl: nametags desync and attach to the wrong players' heads.
        // (The null-PlayerControl approach renders the list row fine but causes exactly that nametag bug,
        // so it is NOT acceptable.)
        //
        // FishNet's PlayerSpawner already spawns a "Player Networked" PlayerControl NetworkObject for the
        // host connection (id 32767) at server start. We reuse THAT object: give it a valid character so it
        // serializes a resolvable model, then sync a PlayerReference (connId 32767) pointing at it. The host
        // player is NOT hidden — it must stay observed so each client resolves the reference's PlayerControl
        // to a real local object (which the nametag system then anchors correctly). It is then parked: peaceful,
        // seated on a fixed bench, and made push-immune (see SeatHostPlayerLoop / the Server_GetHitBySomething
        // prefix).

        private const int HostConnectionId = 32767;

        // True once we've successfully synced the host PlayerReference, so the poll stops re-adding it.
        private static bool _hostPlayerRefSynced;

        // True once the host has been put in peaceful mode and seated on the fixed bench (one-shot).
        private static bool _hostSeated;

        // The bench the headless host is parked on (so it's out of the way and can't grief / be hit).
        private const string HostBenchPath = "World/Benches/Bench (10)";

        // Polls for the host's spawned PlayerControl, gives it a valid character, then syncs a
        // PlayerReference pointing at it. Runs as a loop because the player spawns slightly after the
        // server starts; one-shot once the reference is synced.
        private static IEnumerator SetupHostPlayerLoop()
        {
            if (!Application.isBatchMode) yield break;
            while (!_isQuitting && !_hostPlayerRefSynced)
            {
                yield return new WaitForSecondsRealtime(2f);
                try { TrySetupHostPlayer(); }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HOSTPLR] loop error: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        private static void TrySetupHostPlayer()
        {
            if (_hostPlayerRefSynced) return;

            var serverMgr = InstanceFinder.ServerManager;
            if (serverMgr == null || !serverMgr.Started) return;

            var prm = Il2Cpp.PlayerReferenceManager.Instance;
            if (prm == null) return;

            // Already registered (e.g. re-entry)? Then we're done.
            Il2Cpp.PlayerReference existing = null;
            if (prm.TryGetPlayer(HostConnectionId, out existing)) { _hostPlayerRefSynced = true; return; }

            // Find the host's own spawned player object (FishNet clientHost connection 32767).
            var hostPc = FindHostPlayerControl();
            if (hostPc == null) return; // not spawned yet — try again next tick

            // Give the host a valid character so its PlayerControl serializes a resolvable model.
            GiveHostPlayerValidCharacter(hostPc);

            // A valid (non-null) PUID is REQUIRED: it's the Dictionary key in client-side
            // OnPlayerReferenceAdded (Dictionary.Add(ProductUserId, ...) throws on a null key).
            string puid = "";
            try { puid = EOSManager.Instance?.GetProductUserId()?.ToString() ?? ""; } catch { }
            if (string.IsNullOrEmpty(puid))
            {
                MelonLogger.Warning("[HeadlessMode][HOSTPLR] Host PUID not available yet — deferring host PlayerReference sync.");
                return;
            }

            string username = SledHeadlessCore.FakeClientName;
            long platformId = (long)FakeSteamId;

            // Sync a host PlayerReference backed by the REAL host PlayerControl. The existing
            // PlayerReferenceManager_Server_AddPlayerReference_Postfix also mirrors it into the server-side
            // lookup dicts (chat sender-resolution etc.), replacing the old dict-only RegisterHostPlayerReference.
            try
            {
                prm.Server_AddPlayerReference(puid, platformId, HostConnectionId, username,
                    "" /* voiceId — no Dissonance in headless */, (int)Il2Cpp.AuthPlatform.Steam, hostPc);
                _hostPlayerRefSynced = true;
                MelonLogger.Msg($"[HeadlessMode][HOSTPLR] Synced host PlayerReference: connId={HostConnectionId}, " +
                                $"puid={puid}, name='{username}', platformId={platformId}, playerControl=<real>. " +
                                "Host should now appear in every client's player list (incl. vanilla).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode][HOSTPLR] Server_AddPlayerReference failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Disables FishNet's RemoteClientTimeout on the headless server. FishNet's ServerManager defaults to
        // RemoteTimeoutType.Development with a ~30s duration: it Kicks any connection whose PacketTick hasn't
        // advanced within the window. On this headless host clients are dropped ~30s after joining ("X timed
        // out"), so a dedicated server must not enforce the aggressive dev timeout. The EOS P2P transport still
        // detects genuinely-dead links (ClosedRemotely), so disabling FishNet's tick-based timeout only removes
        // the spurious drops. Called once after the server starts.
        internal static void RelaxRemoteClientTimeout()
        {
            try
            {
                var sm = InstanceFinder.ServerManager;
                if (sm == null) { MelonLogger.Warning("[HeadlessMode] No ServerManager — cannot relax client timeout."); return; }
                sm.SetRemoteClientTimeout(Il2CppFishNet.Managing.RemoteTimeoutType.Disabled, (ushort)60);
                MelonLogger.Msg("[HeadlessMode] Disabled FishNet RemoteClientTimeout (was Development/~30s) — headless dedicated server keeps live clients connected; EOS P2P still detects dead links.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] RelaxRemoteClientTimeout: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Puts the headless host in peaceful mode and seats it on a fixed bench, so the avatar-less host is
        // parked out of the way (can't be hit, can't grief). Both are server-authoritative: peaceful is a
        // SyncVar on the host PlayerControl; sitting goes through Seat.Server_SitDown(seatIndex, ownerId).
        // Runs as a poll because the host PlayerControl and the world bench both spawn after the server starts;
        // one-shot once seated.
        // Remembered seat so the loop can keep re-asserting the host's position onto it.
        private static Il2Cpp.Seat _hostSeat;
        private static int _hostSeatIndex;

        private static IEnumerator SeatHostPlayerLoop()
        {
            if (!Application.isBatchMode) yield break;
            // Keeps running (does NOT one-shot): the one-time setup happens once, but we re-assert the host's
            // transform onto the seat each tick. Verified live: setting sync_CurrentSeat alone is NOT enough —
            // clients render a remote player at its replicated TRANSFORM, and the server never moves the host
            // there. The host rigidbody is kinematic, so a teleport sticks; re-asserting covers any drift.
            while (!_isQuitting)
            {
                yield return new WaitForSecondsRealtime(3f);
                try { TrySeatHostPlayer(); }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HOSTPLR] seat loop error: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        // Puts the host avatar onto its seat (position + rotation) and aims its head. The host's NetworkTransform
        // is client-authoritative and the host owns it, so a normal server-side transform set is pinned at spawn
        // and never replicates — flipping it to SERVER-authoritative (once) makes a plain transform set both hold
        // and replicate. Re-asserted each tick so drift, state resets, and newly-joined clients are all covered.
        private static void SnapHostToSeat(Il2Cpp.PlayerControl hostPc, Il2Cpp.Seat seat, int seatIndex)
        {
            try
            {
                var sp = seat?.GetSeatPosition(seatIndex);
                if (sp == null || sp.transform == null) return;

                // The host's avatar NetworkTransform is CLIENT-authoritative and the host owns it, so a normal
                // server-side transform set is pinned at spawn and never replicates. Flip it to SERVER-auth once,
                // then a plain transform set both holds and replicates — no packet forging needed. Also pin the
                // rigidbody kinematic so physics can't drift the parked avatar off the seat.
                var nt = hostPc.GetComponent<Il2CppFishNet.Component.Transforming.NetworkTransform>();
                if (nt == null) return;
                try { if (nt._clientAuthoritative) nt._clientAuthoritative = false; } catch { }
                try { var rb = hostPc.GetComponent<UnityEngine.Rigidbody>(); if (rb != null && !rb.isKinematic) rb.isKinematic = true; } catch { }

                hostPc.transform.position = sp.transform.position;
                hostPc.transform.rotation = sp.transform.rotation;

                // Head look-at IK (_lookAtIKPosition SyncVar) is a WORLD point; with no camera feeding it the
                // host's defaults to (0,0,0) — the head stares at the map origin. A seated player looks OUT from
                // the bench, i.e. along -SeatForward (matched to a real seated client), ~20 units, slightly down.
                var cc = hostPc.cameraControl;
                if (cc != null && cc._lookAtIKPosition != null)
                {
                    var lookTarget = sp.transform.position + Vector3.up * 1.2f - sp.transform.forward * 20f;
                    if ((cc._lookAtIKPosition.Value - lookTarget).sqrMagnitude > 0.01f)
                        cc._lookAtIKPosition.Value = lookTarget;
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HOSTPLR] SnapHostToSeat: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void TrySeatHostPlayer()
        {
            // Already set up — keep re-asserting the seat position/rotation AND the Sit state each tick (so any
            // drift, a state reset, or a newly-joined client gets corrected).
            if (_hostSeated)
            {
                var hp = FindHostPlayerControl();
                if (hp != null && _hostSeat != null)
                {
                    try { if (hp.sync_PlayerState != null && hp.sync_PlayerState.Value != Il2Cpp.PlayerState.Sit) hp.sync_PlayerState.Value = Il2Cpp.PlayerState.Sit; } catch { }
                    SnapHostToSeat(hp, _hostSeat, _hostSeatIndex);
                }
                return;
            }

            var hostPc = FindHostPlayerControl();
            if (hostPc == null) return; // host player not spawned yet — retry

            // Peaceful mode: server-authoritative SyncVar that PlayerControl.IsPeaceful reads.
            try
            {
                var peaceful = hostPc.sync_PeacefulModeTurnedOn;
                if (peaceful != null && !peaceful.Value)
                {
                    peaceful.Value = true;
                    MelonLogger.Msg("[HeadlessMode][HOSTPLR] Host set to peaceful mode.");
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HOSTPLR] set peaceful: {ex.GetType().Name}: {ex.Message}"); }

            // Seat on the fixed bench (Bench : Seat). GameObject.Find needs the world scene loaded.
            var benchGo = GameObject.Find(HostBenchPath);
            if (benchGo == null) return; // world/bench not loaded yet — retry (peaceful already applied)

            // Bench : Seat; GetComponent<Seat> resolves the Bench instance. We drive the FULL server-side seat
            // path (PlayerControl.Server_SitInSeat is private, so we inline its body): Seat.Server_SitDown sets
            // the seat dict, AND we set the player's sync_CurrentSeat / sync_SeatIndex SyncVars — those are what
            // clients read to actually anchor the avatar onto the bench (Server_SitDown alone never moves it).
            var seat = benchGo.GetComponent<Il2Cpp.Seat>();
            if (seat == null)
            {
                MelonLogger.Warning($"[HeadlessMode][HOSTPLR] '{HostBenchPath}' has no Seat/Bench component — cannot seat host.");
                _hostSeated = true; // stop retrying a bench that can't seat
                return;
            }

            // Pick the first available seat position (benches hold several); fall back to 0.
            int seatIndex = 0;
            try
            {
                var positions = seat.GetSeatPositions();
                int count = positions != null ? positions.Count : 1;
                for (int i = 0; i < count; i++)
                {
                    try { if (seat.IsSeatAvailable(i)) { seatIndex = i; break; } } catch { }
                }
            }
            catch { }

            int ownerId;
            try { ownerId = hostPc.OwnerId; } catch { return; }

            try
            {
                if (!seat.Server_SitDown(seatIndex, ownerId))
                {
                    MelonLogger.Warning($"[HeadlessMode][HOSTPLR] Server_SitDown returned false (seat {seatIndex}) — will retry.");
                    return;
                }

                // The bits Seat.Server_SitDown does NOT do (the avatar-anchoring state clients read).
                try { if (hostPc.sync_CurrentSeat != null) hostPc.sync_CurrentSeat.Value = seat; } catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HOSTPLR] set sync_CurrentSeat: {ex.Message}"); }
                try { if (hostPc.sync_SeatIndex != null) hostPc.sync_SeatIndex.Value = seatIndex; } catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HOSTPLR] set sync_SeatIndex: {ex.Message}"); }
                // PlayerState.Sit is what makes the avatar adopt the SITTING POSE on clients (the seat-change
                // handler + ApplySeat read it). Without it the host is positioned at the seat but stands.
                try { if (hostPc.sync_PlayerState != null) hostPc.sync_PlayerState.Value = Il2Cpp.PlayerState.Sit; } catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HOSTPLR] set sync_PlayerState: {ex.Message}"); }

                // Physically move the avatar onto the seat — clients render a remote player at its replicated
                // transform, so without this the host stays at spawn despite sync_CurrentSeat being set.
                _hostSeat = seat; _hostSeatIndex = seatIndex;
                SnapHostToSeat(hostPc, seat, seatIndex);

                _hostSeated = true;
                MelonLogger.Msg($"[HeadlessMode][HOSTPLR] Seated host (owner={ownerId}) on '{HostBenchPath}' seat {seatIndex} — sync vars set + avatar moved to seat.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HOSTPLR] seat host: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Makes the parked headless host immune to being pushed / knocked down. A player pushing the host calls
        // hostPc.Server_GetHitBySomething(...), which TargetRpcs a ragdoll/velocity to the host's owner (32767).
        // Peaceful mode does NOT gate this (Server_GetHitBySomething has no peaceful check), so we skip the hit
        // entirely when the target is the host's own player. Server-only; returns false to skip the original.
        private static bool PlayerControl_Server_GetHitBySomething_Prefix(Il2Cpp.PlayerControl __instance)
        {
            if (!Application.isBatchMode) return true;
            try { if (__instance != null && __instance.OwnerId == HostConnectionId) return false; }
            catch { }
            return true;
        }

        // Locates the headless host's own spawned PlayerControl (FishNet clientHost connection 32767).
        private static Il2Cpp.PlayerControl FindHostPlayerControl()
        {
            var pcs = Resources.FindObjectsOfTypeAll<Il2Cpp.PlayerControl>();
            if (pcs == null) return null;
            foreach (var pc in pcs)
            {
                try
                {
                    if (pc == null || !pc.IsSpawned) continue;
                    var nob = pc.NetworkObject;
                    if (nob == null) continue;
                    if (nob.OwnerId == HostConnectionId) return pc;
                }
                catch { }
            }
            return null;
        }

        // Gives the host player a valid default character (ragdoll=Default, model=Frog_Default) so its
        // PlayerControl serializes a resolvable model. Setting the SyncVar fires OnPlayerModelUpdate on the
        // server too, which NREs in GetRagdollAnimator (no models loaded headless) — but we skip that handler
        // in headless (PlayerControl.OnPlayerModelUpdate → SkipInHeadless) so the set completes cleanly and
        // the value still replicates. Best-effort: a failure here does not block syncing the PlayerReference.
        private static void GiveHostPlayerValidCharacter(Il2Cpp.PlayerControl pc)
        {
            try
            {
                var ragdoll = pc.sync_EquippedCharacterRagdoll;
                if (ragdoll != null && ragdoll.Value != Il2Cpp.CharacterRagdollType.Default)
                    ragdoll.Value = Il2Cpp.CharacterRagdollType.Default;
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HOSTPLR] set ragdoll Default: {ex.GetType().Name}: {ex.Message}"); }

            try
            {
                var name = pc.sync_EquippedCharacterName;
                if (name != null && name.Value != Il2Cpp.CharacterModelName.Frog_Default)
                    name.Value = Il2Cpp.CharacterModelName.Frog_Default;
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][HOSTPLR] set character Frog_Default: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
