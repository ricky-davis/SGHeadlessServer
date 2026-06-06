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
        /// <summary>
        /// Main headless boot orchestrator. Runs as a MelonLoader coroutine after all patches
        /// are installed. Coordinates every step needed to create a live EOS lobby:
        ///
        ///   Phase 1 — P/Invoke DeviceId auth (no Steam, no Epic account):
        ///     Call <c>EOS_Connect_CreateDeviceId</c> then <c>EOS_Connect_Login</c> directly
        ///     via P/Invoke. PEWS's <c>StartConnectLoginWithDeviceToken</c> was tried but
        ///     internally waits for a Steam session ticket before calling EOS_Connect_Login,
        ///     which times out in headless. The P/Invoke path completes in &lt;1s.
        ///
        ///   Phase 2 — PersistentAuth fallback:
        ///     If DeviceId is disabled for this product, try <c>EOS_Auth_Login(PersistentAuth)</c>
        ///     which uses a locally cached refresh token from any prior Epic account login.
        ///
        ///   Post-login:
        ///     - Wait for <c>LobbyManager</c> to become available.
        ///     - Poll <c>LobbyManager._lobbyManager</c> until the <c>EOSLobbyManager</c> is set.
        ///     - Call <see cref="InitEosLobbyManagerState"/> to ensure post-login initialization ran.
        ///     - Wait for FishNet <c>ServerManager</c> to register (depends on NetworkManager Awake).
        ///     - Call <c>LobbyManager.CreateLobby</c> with values from <c>MelonPreferences</c>.
        ///     - Poll for join code and log it clearly so the user can enter it in-game.
        /// </summary>
        private static IEnumerator WaitForEosLoginAndAutoHost()
        {
            if (!SledHeadlessCore.HeadlessAutoHost) yield break;

            bool loggedIn = false;

            // Wait for EOSManager to initialize the EOS Platform before touching any EOS APIs.
            // Platform initialization is async and typically takes 15-20s after boot. Calling
            // EOS_Connect_CreateDeviceId before the platform is ready queues the request internally,
            // and the callback fires only when the platform finishes — adding unnecessary latency.
            MelonLogger.Msg("[HeadlessMode] Waiting for EOS Platform to initialize...");
            float eosInitStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - eosInitStart < 60f)
            {
                yield return new WaitForSecondsRealtime(0.25f);
                try { if (EOSManager.Instance?.GetEOSPlatformInterface() != null) break; } catch { }
            }
            float eosInitTime = Time.realtimeSinceStartup - eosInitStart;
            MelonLogger.Msg($"[HeadlessMode] EOS Platform ready after {eosInitTime:F1}s.");

            IntPtr authHandle = IntPtr.Zero;
            ConnectInterface connectIface = null;
            try
            {
                var platform = EOSManager.Instance?.GetEOSPlatformInterface();
                authHandle = platform?.GetAuthInterface()?.InnerHandle ?? IntPtr.Zero;
                connectIface = platform?.GetConnectInterface();
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] GetPlatformInterfaces: {ex.Message}"); }

            // Phase 1: P/Invoke DeviceId — directly call EOS_Connect_CreateDeviceId + EOS_Connect_Login.
            // PEWS's StartConnectLoginWithDeviceToken was tried first but internally waits for a Steam
            // session ticket before calling EOS_Connect_Login, which times out in headless. P/Invoke
            // bypasses PEWS entirely and completes in < 1s once EOS Platform is ready.
            if (connectIface != null)
            {
                IntPtr handle = connectIface.InnerHandle;
                IntPtr loginCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedLoginCb);
                IntPtr createCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedCreateDeviceCb);
                IntPtr modelPtr = Marshal.StringToHGlobalAnsi("HeadlessServer");

                MelonLogger.Msg("[HeadlessMode] Trying EOS_Connect_CreateDeviceId...");
                _createDeviceIdDone = false;
                _createDeviceIdSuccess = false;
                var devIdOpts = new EosCreateDeviceIdOptions { ApiVersion = 1, DeviceModel = modelPtr };
                EosSdkCreateDeviceId(handle, ref devIdOpts, IntPtr.Zero, createCbPtr);

                float tdv = Time.realtimeSinceStartup;
                while (!_createDeviceIdDone && Time.realtimeSinceStartup - tdv < 10f)
                    yield return new WaitForSecondsRealtime(0.25f);
                Marshal.FreeHGlobal(modelPtr);

                if (_createDeviceIdSuccess)
                {
                    IntPtr displayNamePtr = Marshal.StringToHGlobalAnsi("HeadlessServer");
                    _deviceAuthDone = false; _deviceAuthSuccess = false;
                    _rawProductUserId = IntPtr.Zero;
                    CallEosDeviceLogin(handle, displayNamePtr, loginCbPtr);
                    float tl = Time.realtimeSinceStartup;
                    while (!_deviceAuthDone && Time.realtimeSinceStartup - tl < 15f)
                        yield return new WaitForSecondsRealtime(0.25f);
                    Marshal.FreeHGlobal(displayNamePtr);
                    loggedIn = _deviceAuthSuccess;
                    if (loggedIn) InjectPuidIntoEosManager(_rawProductUserId);
                }
                else
                    MelonLogger.Warning("[HeadlessMode] DeviceId unavailable (disabled for this product).");

                // Phase 2: PersistentAuth — uses cached refresh token from any prior Epic account login.
                if (!loggedIn && authHandle != IntPtr.Zero)
                {
                    MelonLogger.Msg("[HeadlessMode] Trying EOS_Auth_Login (PersistentAuth)...");
                    yield return TryEosAuthAndConnect(authHandle, connectIface, 2, IntPtr.Zero);
                    loggedIn = _deviceAuthSuccess;
                }
            }
            else
                MelonLogger.Warning("[HeadlessMode] EOS Connect interface unavailable after platform init.");

            if (!loggedIn) { MelonLogger.Warning("[HeadlessMode] EOS login failed — DeviceId and PersistentAuth both failed."); yield break; }
            MelonLogger.Msg("[HeadlessMode] EOS Connect login confirmed.");

            float elapsed = 0f;
            while (LobbyManager.Instance == null && elapsed < 30f)
            {
                yield return new WaitForSecondsRealtime(0.1f);
                elapsed += 0.1f;
            }
            if (LobbyManager.Instance == null) { MelonLogger.Warning("[HeadlessMode] LobbyManager not available."); yield break; }

            var lobbyMgrProp = typeof(LobbyManager).GetProperty("_lobbyManager",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            float startCapture = Time.realtimeSinceStartup;
            MelonLogger.Msg("[HeadlessMode] Polling for LobbyManager._lobbyManager...");
            while (Time.realtimeSinceStartup - startCapture < 30f)
            {
                try
                {
                    var raw = lobbyMgrProp?.GetValue(LobbyManager.Instance);
                    if (raw != null)
                    {
                        _eosLobbyManager = (raw as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                            ?.TryCast<EOSLobbyManager>() ?? raw as EOSLobbyManager;
                        if (_eosLobbyManager != null) break;
                    }
                }
                catch { }
                yield return new WaitForSecondsRealtime(1f);
            }

            if (_eosLobbyManager == null) { MelonLogger.Warning("[HeadlessMode] _lobbyManager stayed null."); yield break; }
            MelonLogger.Msg($"[HeadlessMode] EOSLobbyManager ready after {Time.realtimeSinceStartup - startCapture:F1}s.");

            // The normal flow fires EOSLobbyManager.OnLoggedIn() after Connect login, which subscribes
            // to lobby notifications and initializes internal state. We logged in manually (P/Invoke +
            // StartConnectLoginWithDeviceToken), so that cascade may never have run — leaving fields
            // (CurrentLobby@0x10, UserInfoManager@0xA0) null and causing CreateLobby to NullRef.
            InitEosLobbyManagerState(_eosLobbyManager);

            // Wait for FishNet ServerManager to register (requires NetworkManager in scene).
            MelonLogger.Msg("[HeadlessMode] Waiting for FishNet ServerManager...");
            float fishWait = Time.realtimeSinceStartup;
            while (InstanceFinder.ServerManager == null && Time.realtimeSinceStartup - fishWait < 30f)
                yield return new WaitForSecondsRealtime(0.5f);

            if (InstanceFinder.ServerManager == null)
            {
                MelonLogger.Warning("[HeadlessMode] FishNet ServerManager never registered — cannot host.");
                yield break;
            }
            MelonLogger.Msg($"[HeadlessMode] FishNet ServerManager ready after {Time.realtimeSinceStartup - fishWait:F1}s.");

            yield return new WaitForSecondsRealtime(0.5f);

            // Resolve the EOS relay region dynamically instead of passing empty (which leaves the
            // lobby's REGION attribute unset). The game caches its ping-selected region in
            // PlayerSavedSettings.PlayerRegion, populated by InitialiseRegion() (Unity Relay
            // ListRegions + QoS ping, 10s lookup timeout). On a normal client this runs during
            // boot; on headless that path may not have fired, so we trigger it ourselves and poll
            // PlayerRegion until it resolves. "default" is the game's sentinel for "not resolved".
            string region = RegionHandler.DefaultRegion; // "default" — the client's own fallback
            var regionSettings = Il2Cpp.PlayerPrefsManager.Instance?.playerSavedSettings;
            if (regionSettings != null)
            {
                try { region = regionSettings.PlayerRegion ?? region; } catch { }

                bool RegionResolved() =>
                    !string.IsNullOrWhiteSpace(region) &&
                    !string.Equals(region, RegionHandler.DefaultRegion, StringComparison.OrdinalIgnoreCase);

                if (!RegionResolved())
                {
                    MelonLogger.Msg("[HeadlessMode] Region unresolved — running InitialiseRegion() (Unity Relay + QoS ping)...");
                    try { regionSettings.InitialiseRegion(); }
                    catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] InitialiseRegion threw: {ex.GetType().Name}: {ex.Message}"); }

                    float regionWait = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - regionWait < 12f)
                    {
                        try { region = regionSettings.PlayerRegion ?? region; } catch { }
                        if (RegionResolved()) break;
                        yield return new WaitForSecondsRealtime(0.5f);
                    }
                }

                if (RegionResolved())
                    MelonLogger.Msg($"[HeadlessMode] Resolved lobby region: '{region}'.");
                else
                    MelonLogger.Warning($"[HeadlessMode] Region did not resolve (last='{region}') — using '{RegionHandler.DefaultRegion}'.");
            }
            else
                MelonLogger.Warning($"[HeadlessMode] PlayerSavedSettings null — cannot resolve region; using '{region}'.");

            string lobbyName = !string.IsNullOrWhiteSpace(SledHeadlessCore.ServerName)
                ? SledHeadlessCore.ServerName : "Headless Server";

            MelonLogger.Msg($"[HeadlessMode] Calling LobbyManager.CreateLobby('{lobbyName}', {SledHeadlessCore.ServerCapacity}, region='{region}')...");
            try
            {
                LobbyManager.Instance.CreateLobby(lobbyName, SledHeadlessCore.ServerCapacity,
                    SledHeadlessCore.IsPublicLobby, true, SledHeadlessCore.IsPasswordProtected,
                    SledHeadlessCore.LobbyPassword, SledHeadlessCore.IsPeacefulMode,
                    "PC", region, false);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode] CreateLobby threw: {ex.GetType().Name}: {ex.Message}");
            }

            float waitStart = Time.realtimeSinceStartup;
            bool serverStarted = false;
            while (Time.realtimeSinceStartup - waitStart < 20f)
            {
                try { serverStarted = InstanceFinder.ServerManager?.IsAnyServerStarted() == true; } catch { }
                if (serverStarted) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }

            if (serverStarted)
            {
                SledHeadlessCore.isHost = true;
                SledHeadlessCore.WasHosting = true;
                MelonLogger.Msg("[HeadlessMode] Lobby hosted successfully! Launch your game and join.");
            }
            else
                MelonLogger.Warning("[HeadlessMode] FishNet server never started after 20s.");

            // The join code is generated inside CreateLobby (SledLobby.CreateNewLobbyCode) and stored
            // as a lobby attribute, populated locally once the async EOS lobby update round-trips.
            // Poll GetLobbyCode() until it appears so we can print it for the user to join with.
            string joinCode = null, inviteTarget = null, lobbyId = null;
            float codeWait = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - codeWait < 15f)
            {
                try
                {
                    joinCode = LobbyManager.Instance?.GetLobbyCode();
                    inviteTarget = LobbyManager.Instance?.GetInviteJoinTarget();
                    lobbyId = LobbyManager.Instance?.GetLobbyId();
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(joinCode)) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }

            MelonLogger.Msg("[HeadlessMode] ════════════════════════════════════════════");
            MelonLogger.Msg($"[HeadlessMode]   JOIN CODE:      {(string.IsNullOrWhiteSpace(joinCode) ? "<not set yet>" : joinCode)}");
            MelonLogger.Msg($"[HeadlessMode]   INVITE TARGET:  {(string.IsNullOrWhiteSpace(inviteTarget) ? "<none>" : inviteTarget)}");
            MelonLogger.Msg($"[HeadlessMode]   LOBBY ID:       {(string.IsNullOrWhiteSpace(lobbyId) ? "<none>" : lobbyId)}");
            MelonLogger.Msg("[HeadlessMode]   → In-game: Join Private Lobby → enter the JOIN CODE above.");
            MelonLogger.Msg("[HeadlessMode] ════════════════════════════════════════════");

            // Report the live EOS lobby + active transport so we know whether/how a client can join.
            LogHostDiagnostics();

            // Push correct lobby settings to all clients via FishNet SyncVar.
            FixAndSyncLobbySettings();

            // Enable text and voice chat on the server's PlayerSavedSettings.
            EnableHeadlessChat();

            // Register the headless host as a PlayerReference in PlayerReferenceManager.
            // In a normal hosted game, PlayerControl.InitializePlayerReferenceAsync() calls
            // Cmd_AddPlayerReference to register the host at connection ID 32767. In headless
            // this never completes (it waits for Steam session ticket / Dissonance voice ID).
            // Without the host PlayerReference in sync_PlayerReferences, client-side code that
            // checks for a valid host entry (snowball pickup gate, chat gate, etc.) fails silently.
            RegisterHostPlayerReference();

            // Register the server-side chat broadcast handler. ChatManager.OnEnable registers its
            // FishNet broadcast handlers at scene-load time, when IsServer is still false (we start
            // the server ~37s later, after EOS login), so the server-side handler is never bound.
            // Result: client chat broadcasts reach ServerManager.ParseBroadcast but are dropped with
            // no registered handler. We bind it manually now that the server is up.
            MelonCoroutines.Start(RegisterHeadlessChatServerHandler());

            // Initialize SyncVars on server-side disabled NetworkBehaviours so they can replicate to
            // clients (fixes snowball pickup and any other state carried by a disabled behaviour's SyncVars).
            MelonCoroutines.Start(EnsureServerBehavioursInitializedLoop());

            // Enable PlayerMovement FAST (250ms poll) so its footstep SyncVar replicates before the owner's
            // first CmdSetFootstepCollection — required for the snowball-pickup prompt. See the loop comment.
            MelonCoroutines.Start(EnsurePlayerMovementEnabledLoop());

            // Hide the headless host's phantom "Player Networked" object from remote clients so it is
            // never serialized into their spawn batch (it is uninitialized — no real avatar — and its
            // spawn-time callback NREs on the client, desyncing FishNet's PooledReader and aborting the
            // whole spawn packet → the client's own Sled never spawns → stuck on "Waiting for
            // Sled.LocalSledInstance"). MelonLoader clients survive because Il2CppInterop swallows the
            // callback NRE; vanilla clients have no such net, so only they hang. See HidePhantomHostPlayerLoop.
            MelonCoroutines.Start(HidePhantomHostPlayerLoop());

            // Keep the EOS lobby joinable past the ~1h Connect-token TTL and keep it fresh in EOS search.
            // The base game does both (auth-expiration re-login + a 10s lobby heartbeat); neither runs on a
            // headless host, so without these the server drops out of the lobby list after ~an hour.
            // See RefreshConnectTokenLoop / LobbyHeartbeatLoop.
            MelonCoroutines.Start(RefreshConnectTokenLoop());
            MelonCoroutines.Start(LobbyHeartbeatLoop());
        }
    }
}
