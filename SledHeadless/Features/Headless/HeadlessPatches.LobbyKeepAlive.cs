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
                        if (SledHeadlessCore.WasHosting && !warnedDead)
                        {
                            MelonLogger.Error("[HeadlessMode][KeepAlive] LOBBY CLEARED (empty Id) despite heartbeat fix — EOS evicted the host. Investigate (member-status self-disconnect).");
                            warnedDead = true;
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
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[HeadlessMode][KeepAlive] Lobby heartbeat error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Boxes a long into an Il2Cpp System.Object for LobbyManager.AddAttribute (value param is object).
        private static unsafe Il2CppSystem.Object BoxInt64(long v) =>
            new Il2CppSystem.Object(Il2CppInterop.Runtime.IL2CPP.il2cpp_value_box(
                Il2CppInterop.Runtime.Il2CppClassPointerStore<long>.NativeClassPtr, (IntPtr)(&v)));
    }
}
