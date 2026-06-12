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
        // ───────────────────────── Lobby keep-alive (headless) ─────────────────────────
        // The base game keeps a hosted EOS lobby joinable two ways; NEITHER runs on a headless host, so
        // after ~1 hour the server silently drops out of the EOS lobby search list:
        //
        //   (1) EOS Connect auth-token refresh. The ProductUserId Connect token has a ~1h TTL. The base
        //       game's EOSAuthenticator registers AddNotifyAuthExpiration and re-logs-in (StartConnect(1))
        //       before it expires — but that path is bypassed on headless (PatchBootables force-passes
        //       EOSAuthenticator.IsBooted) AND its refresh re-login uses a Steam session ticket, which a
        //       headless host has no SteamManager for. We logged in with a native DeviceId credential, so
        //       we refresh the same way: periodically re-run the DeviceId EOS_Connect_Login. DeviceId is
        //       machine-stable, so the re-login returns the SAME ProductUserId — the lobby and FishyEOS
        //       transport (both keyed by PUID) are unaffected; only the underlying token is renewed. This
        //       is the primary ~1h fix.
        //
        //   (2) Lobby heartbeat. UpdateLobbyHeartbeat() is a NO-OP unless the lobby already has a HEARTBEAT
        //       attribute (which it seeds nowhere else — chicken-and-egg), so on headless it never sends a
        //       single ModifyLobby and the lobby is reaped by EOS as stale (~11h) → Lobby.Clear(). We seed
        //       the HEARTBEAT attribute ourselves so UpdateLobbyHeartbeat actually refreshes the lobby every
        //       10s. See LobbyHeartbeatLoop for the full decomp-verified root-cause writeup.
        private static bool _refreshInProgress;

        // Re-runs the native DeviceId EOS_Connect_Login to renew the Connect token (same path as the
        // initial login, minus CreateDeviceId — the device credential is already registered). Reuses the
        // existing pinned login callback, device-auth flags, and PUID injection. Mirrors the initial-login
        // style (inline FreeHGlobal, no try/finally) so it stays a valid iterator.
        private static IEnumerator RefreshConnectLogin()
        {
            if (_refreshInProgress) yield break;
            _refreshInProgress = true;

            var platform = EOSManager.Instance?.GetEOSPlatformInterface();
            var connect = platform?.GetConnectInterface();
            if (connect == null)
            {
                MelonLogger.Warning("[HeadlessMode][KeepAlive] RefreshConnectLogin: ConnectInterface null — skipping.");
                _refreshInProgress = false;
                yield break;
            }

            IntPtr handle = connect.InnerHandle;
            IntPtr loginCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedLoginCb);
            IntPtr displayNamePtr = Marshal.StringToHGlobalAnsi("HeadlessServer");
            _deviceAuthDone = false; _deviceAuthSuccess = false; _rawProductUserId = IntPtr.Zero;

            MelonLogger.Msg("[HeadlessMode][KeepAlive] Refreshing EOS Connect token (DeviceId re-login)...");
            CallEosDeviceLogin(handle, displayNamePtr, loginCbPtr);

            float t = Time.realtimeSinceStartup;
            while (!_deviceAuthDone && Time.realtimeSinceStartup - t < 15f)
                yield return new WaitForSecondsRealtime(0.25f);

            Marshal.FreeHGlobal(displayNamePtr);

            if (_deviceAuthSuccess && _rawProductUserId != IntPtr.Zero)
            {
                InjectPuidIntoEosManager(_rawProductUserId);
                MelonLogger.Msg("[HeadlessMode][KeepAlive] EOS Connect token refreshed OK.");
            }
            else
                MelonLogger.Warning($"[HeadlessMode][KeepAlive] EOS Connect token refresh FAILED (done={_deviceAuthDone}, success={_deviceAuthSuccess}).");

            _refreshInProgress = false;
        }

        // Renews the EOS Connect token every 40 minutes — comfortably under the ~1h TTL — so the
        // ProductUserId session never lapses and the lobby stays joinable / in EOS search.
        private static IEnumerator RefreshConnectTokenLoop()
        {
            if (!Application.isBatchMode) yield break;
            const float intervalSecs = 40f * 60f; // 40 min < ~60 min Connect-token TTL
            while (!_isQuitting)
            {
                float waited = 0f;
                while (waited < intervalSecs && !_isQuitting)
                {
                    yield return new WaitForSecondsRealtime(5f);
                    waited += 5f;
                }
                if (_isQuitting) yield break;
                yield return RefreshConnectLogin();
            }
        }

        // Tracks the lobby Id we've seeded the HEARTBEAT attribute for, so we seed exactly once per lobby.
        private static string _heartbeatSeededForLobbyId;

        // Keeps the EOS lobby fresh in search via a REAL heartbeat. THIS IS THE ~11h DROP-OFF ROOT CAUSE FIX.
        //
        // Decomp-verified bug (GameAssembly LobbyManager.UpdateLobbyHeartbeat @177028, GetAttributeByKey
        // @174053): UpdateLobbyHeartbeat() only writes (AddAttribute HEARTBEAT + ModifyLobby) when a HEARTBEAT
        // attribute ALREADY exists on the lobby — yet the ONLY site that adds that attribute is inside that
        // same guarded branch (@177108), and GetAttributeByKey returns null for a missing key (no lazy-create).
        // It is a chicken-and-egg: on a headless-hosted lobby the attribute is never seeded → UpdateLobbyHeartbeat
        // is a permanent NO-OP → ZERO ModifyLobby heartbeats ever reach EOS → the lobby is never refreshed →
        // EOS reaps it as stale (~11h) and fires LobbyMemberStatus(Closed/Disconnected) for the host's own PUID
        // → EOSLobbyManager.OnMemberStatusReceived (@643285) calls Lobby.Clear() (Id→"") → the server vanishes
        // from the explorer while FishNet/EOS auth stay up. (A normal client-host's lobby is kept fresh by the
        // game's own driving path; only headless hit the unseeded no-op.)
        //
        // Fix: SEED the HEARTBEAT attribute ourselves once per lobby, after which the game's own
        // UpdateLobbyHeartbeat finds it and genuinely refreshes it + ModifyLobby every tick. AddAttribute
        // upserts by key (the base game calls it every heartbeat without accumulating duplicates), so this is
        // safe. If the lobby is ever Clear()'d anyway (empty Id), we log loudly — that means the heartbeat fix
        // was insufficient and EOS evicted the host for some other reason (surfaces during >11h validation).
        private static IEnumerator LobbyHeartbeatLoop()
        {
            if (!Application.isBatchMode) yield break;
            int beats = 0;
            bool announcedFirst = false;
            bool warnedDead = false;
            while (!_isQuitting)
            {
                yield return new WaitForSecondsRealtime(10f);
                try
                {
                    var lm = LobbyManager.Instance;
                    if (lm == null) continue;
                    var lobby = _eosLobbyManager?.GetCurrentLobby();

                    // Dead-lobby detection: Lobby.Clear() leaves a NON-null object with an EMPTY Id, so the
                    // old `== null` guard never caught it (it would "heartbeat a corpse" forever). With the
                    // seed fix the lobby should never be reaped; if it still is, log once so we know.
                    string id = null;
                    try { id = lobby?.Id; } catch { }
                    if (lobby == null || string.IsNullOrWhiteSpace(id))
                    {
                        // RECOVER: an empty Id means a Lobby.Clear() got through that the self-eviction guard
                        // did NOT retain — i.e. the EOS lobby was (probably) genuinely closed server-side, not
                        // a transient host self-disconnect (those are suppressed by Lobby_Clear_Prefix). Re-host
                        // in place so the server reappears in the EOS list. RehostLobby self-guards against
                        // re-entrancy and thrash (120s cooldown), so it's safe to call every 10s tick.
                        if (SledHeadlessCore.WasHosting)
                        {
                            if (!warnedDead)
                            {
                                MelonLogger.Error("[HeadlessMode][KeepAlive] LOBBY CLEARED (empty Id) — EOS closed the host lobby and the self-eviction guard retained nothing (likely a genuine EOS-side close). Attempting in-place re-host.");
                                warnedDead = true;
                            }
                            MelonCoroutines.Start(RehostLobby());
                        }
                        continue;
                    }
                    warnedDead = false;

                    // SEED the HEARTBEAT attribute once per lobby so UpdateLobbyHeartbeat stops being a no-op.
                    if (id != _heartbeatSeededForLobbyId)
                    {
                        lm.AddAttribute(lobby, Il2Cpp_Scripts.UI.Pre_Game.LobbyAttributeType.HEARTBEAT, BoxInt64(DateTime.UtcNow.Ticks), Il2CppEpic.OnlineServices.Lobby.LobbyAttributeVisibility.Public);
                        _heartbeatSeededForLobbyId = id;
                        MelonLogger.Msg("[HeadlessMode][KeepAlive] Seeded HEARTBEAT lobby attribute — UpdateLobbyHeartbeat will now send real ModifyLobby refreshes.");
                    }

                    // Now genuinely refresh: UpdateLobbyHeartbeat finds the seeded attribute, rewrites it to
                    // UtcNow.Ticks, and calls EOSLobbyManager.ModifyLobby — a real EOS round-trip that keeps
                    // the lobby alive in search and prevents the staleness reap that triggered Lobby.Clear().
                    lm.UpdateLobbyHeartbeat();
                    beats++;
                    if (!announcedFirst) { MelonLogger.Msg("[HeadlessMode][KeepAlive] Lobby heartbeat active (10s, real ModifyLobby)."); announcedFirst = true; }
                    else if (beats % 30 == 0) MelonLogger.Msg($"[HeadlessMode][KeepAlive] Lobby heartbeat tick #{beats}.");

                    // RE-LIST after a suppressed self-eviction: the local lobby object is populated (we kept it),
                    // but EOS may have delisted it server-side. Once the server is EMPTY, re-host to guarantee a
                    // fresh, listed lobby — zero disruption (no players to drop). RehostLobby self-guards thrash.
                    if (_rehostWhenEmptyPending && RemotePlayerCount() == 0)
                    {
                        MelonLogger.Msg("[HeadlessMode][KeepAlive] Server emptied after a suppressed self-eviction — re-hosting to re-list cleanly.");
                        _rehostWhenEmptyPending = false;
                        MelonCoroutines.Start(RehostLobby());
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[HeadlessMode][KeepAlive] Lobby heartbeat error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // ───────────────────────── Lobby self-eviction guard (PREVENT) ─────────────────────────
        // ROOT CAUSE (decomp-verified, GameAssembly.dll.c:643171/643235-643285): when EOS reports the
        // HOST's OWN membership as DISCONNECTED(2)/KICKED(3)/CLOSED(5), PEWS EOSLobbyManager
        // .OnMemberStatusReceived (and the LeaveLobbyRequested-driven OnKickedFromLobby @642132) call
        // Lobby.Clear() on the lobby the host OWNS — with NO owner exception — emptying the Id. The server
        // then drops off the EOS search list while FishNet/EOS-P2P stay up (verified live: chat kept flowing
        // 25-75s after the clear). The dominant case is a TRANSIENT Disconnected(2) where the EOS lobby is
        // still alive; suppressing that Clear keeps the host in its own lobby with ZERO player disruption.
        // (A genuine server-side CLOSED leaves an empty Id that LobbyHeartbeatLoop re-hosts; see the window
        // give-up below + RehostLobby.) Patches the no-arg Lobby.Clear() — covers BOTH PEWS clear paths at
        // once and avoids the non-blittable member-status callback-info marshaling.

        // Set true around our OWN intentional teardown (shutdown / re-host sweep) so a graceful Clear proceeds.
        internal static bool _allowLobbyClear;
        private static int _suppressedEvictions;
        // Set when we suppress a self-eviction; the heartbeat loop re-hosts (re-lists) once the server empties.
        private static bool _rehostWhenEmptyPending;
        private static float _evictionWindowStart;
        private static int _evictionsInWindow;
        private const int EvictionsBeforeGivingUp = 3;   // this many self-clears in the window => assume genuine close => allow it => re-host
        private const float EvictionWindowSecs = 30f;

        // Region captured at the initial host, reused by RehostLobby so it doesn't re-run QoS region pinging.
        internal static string _lastHostRegion = "us-central1";

        // Harmony PREFIX on PlayEveryWare...Lobby.Clear(). Returns false to SKIP the clear (retain the lobby).
        private static bool Lobby_Clear_Prefix(Lobby __instance)
        {
            try
            {
                if (!Application.isBatchMode) return true;          // only the headless host
                if (_allowLobbyClear || _isQuitting) return true;   // our own intentional / shutdown teardown
                if (!SledHeadlessCore.WasHosting) return true;      // not hosting yet (e.g. startup ghost sweep)

                // Only protect OUR live hosted lobby: a populated Id matching the current lobby. Clears of
                // other / already-empty Lobby objects pass through untouched.
                string id = null;
                try { id = __instance?.Id; } catch { }
                if (string.IsNullOrWhiteSpace(id)) return true;
                string current = null;
                try { current = _eosLobbyManager?.GetCurrentLobby()?.Id; } catch { }
                if (!string.IsNullOrWhiteSpace(current) && !string.Equals(id, current, StringComparison.OrdinalIgnoreCase))
                    return true;                                    // a different lobby — don't interfere

                // Sliding window: a one-off transient Disconnect is suppressed (players kept connected). If EOS
                // insists (>= N clears within the window) the lobby is probably genuinely closed, so allow this
                // one through — the heartbeat loop's empty-Id branch then re-hosts.
                float now = Time.realtimeSinceStartup;
                if (now - _evictionWindowStart > EvictionWindowSecs) { _evictionWindowStart = now; _evictionsInWindow = 0; }
                _evictionsInWindow++;
                if (_evictionsInWindow >= EvictionsBeforeGivingUp)
                {
                    MelonLogger.Warning($"[HeadlessMode][KeepAlive] EOS cleared the host lobby {_evictionsInWindow}x in <{EvictionWindowSecs:0}s — likely a genuine close; allowing Clear so the loop re-hosts.");
                    return true;
                }

                _suppressedEvictions++;
                // A suppressed eviction keeps EXISTING players connected (their P2P transport is unaffected), but
                // EOS may have GENUINELY delisted the lobby server-side — in which case our retained object is a
                // corpse invisible in search (confirmed live: server fell off the list after a single suppressed
                // self-eviction, heartbeat ticking a dead lobby). We can't cheaply prove listing, but re-hosting
                // re-lists it. Defer that until the server is EMPTY so live players are never disrupted; the
                // heartbeat loop fires it the moment remote player count hits 0. See RemotePlayerCount / loop.
                _rehostWhenEmptyPending = true;
                // If OnKickedFromLobby fired in the last 2s this is the LeaveLobbyRequested path; otherwise it's
                // the member-status path (Disconnected/Kicked/Closed) — the exact status can't be captured (its
                // callback struct crashes the Il2CppInterop trampoline; see HeadlessPatches.LobbyEvictionDiag).
                string trigger = (_lastEvictionTrigger != null && Time.realtimeSinceStartup - _lastEvictionTriggerAt < 2f)
                    ? _lastEvictionTrigger : "member-status path (OnMemberStatusReceived: Disconnected/Kicked/Closed) — no LeaveLobbyRequested";
                MelonLogger.Warning($"[HeadlessMode][KeepAlive] Suppressed EOS lobby self-eviction (Lobby.Clear, lobby {id}) #{_suppressedEvictions} [cause: {trigger}; remotePlayers={RemotePlayerCount()}] — host stays in its own lobby (existing players unaffected). Will re-host to re-list once the server empties, in case EOS delisted it.");
                return false;   // skip Clear() -> CurrentLobby stays populated -> existing players keep playing
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode][KeepAlive] Lobby_Clear_Prefix error: {ex.GetType().Name}: {ex.Message}");
                return true;     // on any doubt, never block the game's own Clear
            }
        }

        // ───────────────────────── Lobby re-host (RECOVER) ─────────────────────────
        // The lobby genuinely went empty (a Clear the guard let through). Re-create it in place — WITHOUT
        // restarting the already-running keep-alive / host loops — so the server reappears in EOS search.
        // Self-guarded against re-entrancy and thrash (120s cooldown), so the 10s heartbeat may call it freely.
        private static bool _rehostInProgress;
        private static float _lastRehostRealtime = -9999f;
        private const float RehostCooldownSecs = 120f;

        private static IEnumerator RehostLobby()
        {
            if (_rehostInProgress) yield break;
            if (Time.realtimeSinceStartup - _lastRehostRealtime < RehostCooldownSecs) yield break;
            _rehostInProgress = true;
            _lastRehostRealtime = Time.realtimeSinceStartup;
            MelonLogger.Msg("[HeadlessMode][KeepAlive] Lobby empty — attempting in-place re-host...");

            // Allow the ghost sweep to clear the dead corpse, then re-create a fresh lobby.
            _allowLobbyClear = true;
            try { HeadlessGhostSweep.Sweep(null); }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][KeepAlive] re-host sweep: {ex.GetType().Name}: {ex.Message}"); }
            _allowLobbyClear = false;
            _heartbeatSeededForLobbyId = null;   // force re-seed of the new lobby's HEARTBEAT attribute

            string lobbyName = !string.IsNullOrWhiteSpace(SledHeadlessCore.ServerName)
                ? SledHeadlessCore.ServerName : "Headless Server";
            try
            {
                LobbyManager.Instance.CreateLobby(lobbyName, SledHeadlessCore.ServerCapacity,
                    SledHeadlessCore.IsPublicLobby, !SledHeadlessCore.IsTextChatOnly, SledHeadlessCore.IsPasswordProtected,
                    SledHeadlessCore.LobbyPassword, SledHeadlessCore.IsPeacefulMode,
                    "PC", _lastHostRegion, false);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode][KeepAlive] re-host CreateLobby threw: {ex.GetType().Name}: {ex.Message}");
                _rehostInProgress = false;
                yield break;
            }

            float t = Time.realtimeSinceStartup;
            string newId = null;
            while (Time.realtimeSinceStartup - t < 20f)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                try { newId = _eosLobbyManager?.GetCurrentLobby()?.Id; } catch { }
                if (!string.IsNullOrWhiteSpace(newId)) break;
            }

            if (!string.IsNullOrWhiteSpace(newId))
            {
                MelonLogger.Msg($"[HeadlessMode][KeepAlive] Re-host succeeded — new lobby {newId}. Re-applying host display name.");
                try { ApplyHostDisplayName(); } catch { }
                try { HeadlessGhostSweep.RememberLobby(newId); } catch { }
            }
            else
                MelonLogger.Warning("[HeadlessMode][KeepAlive] Re-host did NOT produce a new lobby within 20s — will retry after cooldown.");

            _rehostInProgress = false;
        }

        // Count of connected REMOTE players (excludes the clientHost's own connection 32767). 0 == empty server.
        private static int RemotePlayerCount()
        {
            try
            {
                var clients = InstanceFinder.ServerManager?.Clients;
                if (clients == null) return 0;
                int total = clients.Count;
                try { if (clients.ContainsKey(32767)) total--; } catch { }
                return total < 0 ? 0 : total;
            }
            catch { return 0; }
        }

        // Boxes a long into an Il2Cpp System.Object for LobbyManager.AddAttribute (value param is object).
        private static unsafe Il2CppSystem.Object BoxInt64(long v) =>
            new Il2CppSystem.Object(Il2CppInterop.Runtime.IL2CPP.il2cpp_value_box(
                Il2CppInterop.Runtime.Il2CppClassPointerStore<long>.NativeClassPtr, (IntPtr)(&v)));
    }
}
