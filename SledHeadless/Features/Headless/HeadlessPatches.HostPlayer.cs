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
        // serializes a resolvable model, then sync a PlayerReference (connId 32767) pointing at it. The
        // phantom is NOT hidden — it must stay observed so each client resolves the reference's PlayerControl
        // to a real local object (which the nametag system then anchors correctly).
        //
        // KNOWN ISSUE UNDER ACTIVE DIAGNOSIS: the headless-spawned phantom currently aborts a joining
        // client's spawn batch (client hangs on the loading screen). We are capturing the exact client-side
        // NRE via ClientSpawnDiagnostics (run SledHeadless on the joining client) to fix the specific
        // malformation. See [[headless-proper-host-player]].

        private const int HostConnectionId = 32767;

        // True once we've successfully synced the host PlayerReference, so the poll stops re-adding it.
        private static bool _hostPlayerRefSynced;

        // Polls for the host's spawned phantom PlayerControl, gives it a valid character, then syncs a
        // PlayerReference pointing at it. Runs as a loop because the phantom spawns slightly after the
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
