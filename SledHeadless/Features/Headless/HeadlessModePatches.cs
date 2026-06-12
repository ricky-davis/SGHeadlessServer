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
    /// <summary>
    /// All Harmony patches and coroutines that make the game run as a headless EOS dedicated server.
    ///
    /// The game was never designed for headless operation — it assumes Steam is running, a human
    /// is clicking through the boot screen, and a full Unity scene (UI, audio, input) is live.
    /// This class surgically removes each of those assumptions so the process can:
    ///   1. Boot past the DRM/preflight checkers without Steam or an Epic account.
    ///   2. Authenticate with EOS using a DeviceId credential (machine-local, no user login).
    ///   3. Create an EOS lobby via FishyEOS P2P so real game clients can join by code.
    ///   4. Run silently (no audio, no input, no UI crashes) for the lifetime of the session.
    /// </summary>
    internal static partial class HeadlessPatches
    {
        /// <summary>
        /// Registers every Harmony patch and starts every background coroutine needed for
        /// headless operation. Called once from <see cref="SledHeadlessCore.OnInitializeMelon"/>
        /// when <c>Application.isBatchMode</c> is true.
        ///
        /// Patch order matters in a few places:
        ///   - Boot bypass must be registered before <c>BootSceneManager</c> ticks.
        ///   - SteamManager.Initialized spoof must be in place before the EOSAuthenticator
        ///     coroutine polls it; that coroutine starts when the boot scene loads.
        ///   - Native SteamID hooks must be installed before Unity's Steam integration wakes
        ///     (handled by <see cref="PatchNativeSteamId"/> at registration time, before Awake).
        /// </summary>
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            MelonLogger.Msg("[HeadlessMode] v104 Applying headless suppression patches...");

            // Silence Harmony's per-patch "WARNING AccessTools.GetTypesFromAssembly" spam.
            // Every harmony.Patch() call internally scans assemblies; UnityEngine.CoreModule
            // has a broken IdentityAttributes type that always throws ReflectionTypeLoadException,
            // and Harmony logs the full exception. We patch the scanner itself to swallow it silently.
            SuppressHarmonyAssemblyScanWarnings(harmony);

            // Give this headless instance its own EOS identity (per-instance CacheDirectory → own DeviceId/PUID)
            // so multiple servers can run on one machine without sharing a PUID (shared PUID splits a joiner's
            // EOS P2P packets across instances → ~60s client timeouts). Registered FIRST so it is in place
            // before the game's EOS boot step creates the platform.
            PatchEosPlatformCacheDirectory(harmony);

            // Self-eviction guard: stop PEWS from Clear()ing our OWN hosted lobby when EOS reports the host's
            // own membership Disconnected/Kicked/Closed (the ~6h "server fell off the list" root cause). The
            // no-arg Lobby.Clear() covers both PEWS clear paths (OnMemberStatusReceived + OnKickedFromLobby).
            TryPatch(harmony,
                typeNames: new[] { "Il2CppPlayEveryWare.EpicOnlineServices.Samples.Lobby" },
                methodName: "Clear",
                prefix: nameof(Lobby_Clear_Prefix),
                label: "Lobby.Clear self-eviction guard");

            // Read-only: name WHICH EOS callback (+ member-status code) drives a self-eviction, so we can learn
            // the root cause from a live log the next time one fires. See HeadlessPatches.LobbyEvictionDiag.cs.
            ApplyLobbyEvictionDiagnostics(harmony);

            TryPatch(harmony,
                typeNames: new[] { "Il2CppRewired.InputManager_Base", "Il2CppRewired.InputManager" },
                methodName: "Awake",
                prefix: nameof(SkipInHeadless),
                label: "Rewired.InputManager_Base.Awake");

            TryPatch(harmony,
                typeNames: new[] { "Il2CppDissonance.DissonanceComms" },
                methodName: "Awake",
                prefix: nameof(SkipInHeadless),
                label: "DissonanceComms.Awake");

            TryPatch(harmony,
                typeNames: new[] { "Il2CppPlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton" },
                methodName: "InitializeOverlay",
                prefix: nameof(SkipInHeadless),
                label: "EOSManager.EOSSingleton.InitializeOverlay");

            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.UiReferenceController" },
                methodName: "Update",
                finalizer: nameof(SuppressNullRefInHeadless),
                label: "UiReferenceController.Update NullRef suppressor");

            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.UiReferenceController" },
                methodName: "ReturnToMainMenu",
                prefix: nameof(SkipInHeadless),
                label: "UiReferenceController.ReturnToMainMenu headless skip");

            // CreateLobby calls EnableLoading() to show a "Creating lobby..." screen, which localizes
            // text via Addressables/Localization — uninitialised in headless, so it throws "invalid
            // operation handle" and aborts CreateLobby. No UI exists headless, so skip it entirely.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.UiReferenceController" },
                methodName: "EnableLoading",
                prefix: nameof(SkipInHeadless),
                label: "UiReferenceController.EnableLoading headless skip");

            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.UiReferenceController" },
                methodName: "DisableLoading",
                prefix: nameof(SkipInHeadless),
                label: "UiReferenceController.DisableLoading headless skip");

            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp_Scripts.Managers.LobbyManager" },
                methodName: "CreateLobby",
                prefix: nameof(LobbyManager_CreateLobby_PreInit),
                finalizer: nameof(LobbyManager_CreateLobby_Finalizer),
                label: "LobbyManager.CreateLobby null-field init");

            // Any NetworkObject stop callback that throws aborts FishNet's despawn loop,
            // leaving objects stuck and clients unable to finish loading. Suppress so teardown
            // always completes past any broken callback.
            TryPatch(harmony,
                typeNames: new[] { "Il2CppFishNet.Object.NetworkObject" },
                methodName: "InvokeStopCallbacks",
                finalizer: nameof(NetworkObject_InvokeStopCallbacks_Finalizer),
                label: "NetworkObject.InvokeStopCallbacks crash guard");

            // Sled.FollowOwnerWhileInactive spams errors every FixedUpdate when the owner
            // disconnects mid-game. Skip silently when there is no valid owner.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.Sled" },
                methodName: "FollowOwnerWhileInactive",
                prefix: nameof(Sled_FollowOwnerWhileInactive_Prefix),
                label: "Sled.FollowOwnerWhileInactive null-owner guard");

            // PlayerControl.IsPeaceful is a computed readonly property backed by the
            // sync_PeacefulModeTurnedOn SyncVar. Patching the getter covers the server-side
            // RPC processing path, but clients already have the SyncVar value synced from the
            // server's player prefab default (which may be true). We patch OnSpawnServer to
            // set sync_PeacefulModeTurnedOn.Value on every player spawn so clients receive the
            // correct value. The getter patch is kept as belt-and-suspenders for server-side code.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.PlayerControl" },
                methodName: "get_IsPeaceful",
                prefix: nameof(PlayerControl_IsPeaceful_Prefix),
                label: "PlayerControl.IsPeaceful → headless peaceful mode preference");

            // PlayerControl.OnPlayerModelUpdate is the OnChange handler for the sync_EquippedCharacterRagdoll
            // SyncVar. It instantiates/updates the avatar's ragdoll model, which requires the character
            // models to be loaded. On a headless server no models are loaded, so whenever a client switches
            // character the server-side handler NREs in PlayerRagdollTypeHandler.GetRagdollAnimator() (the
            // throw is swallowed by Il2CppInterop, but it's noise and risk). The server renders no visuals,
            // so there is nothing to update — skip it entirely in headless for every player.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.PlayerControl" },
                methodName: "OnPlayerModelUpdate",
                prefix: nameof(SkipInHeadless),
                label: "PlayerControl.OnPlayerModelUpdate → skip in headless (no models to update; avoids GetRagdollAnimator NRE)");

            // Make the parked headless host immune to pushes/knockdowns: skip Server_GetHitBySomething when the
            // target is the host's own player (conn 32767). Peaceful mode does not gate this in the base game.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.PlayerControl" },
                methodName: "Server_GetHitBySomething",
                prefix: nameof(PlayerControl_Server_GetHitBySomething_Prefix),
                label: "PlayerControl.Server_GetHitBySomething → headless host is push-immune");

            // ── CHAT FIX: server-side connection→player resolution ─────────────────────────────
            // PlayerReferenceManager.TryGetPlayer(connectionId, out) — used by the server to attribute an
            // incoming chat broadcast (OnServerReceivedChatBroadcastFromClient) and by EVERY *.Server_Interact
            // to identify the acting player — reads the _playerConnectionIdToPlayerReference dictionary. That
            // dictionary is normally built by OnPlayerReferenceAdded, wired to the SyncList ONLY in client-side
            // PlayerReferenceManager.OnStartClient (never runs on a headless/server-only host), and it NREs here
            // anyway (its tail touches EOS/host-only objects). So the dict stays empty, TryGetPlayer(connId)
            // returns false, and the server silently drops chat re-broadcasts + ignores world interactions —
            // even though sync_PlayerReferences (the SyncList) is fully populated.
            //
            // Fix: after each Server_AddPlayerReference, write the new reference straight into the lookup
            // dictionaries (the same writes OnPlayerReferenceAdded does, minus its EOS/host-only tail). The
            // native TryGetPlayer then reads them via plain native field access — no managed marshaling. We do
            // NOT patch TryGetPlayer itself: a Harmony postfix on its (int, out PlayerReference&) signature
            // NREs ~per frame inside Il2CppInterop's native→managed trampoline for the by-ref reference param.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.PlayerReferenceManager" },
                methodName: "Server_AddPlayerReference",
                postfix: nameof(PlayerReferenceManager_Server_AddPlayerReference_Postfix),
                label: "PlayerReferenceManager.Server_AddPlayerReference → populate connectionId lookup dicts (headless)");

            // Fix (race start disconnect): RaceManager.InitialiseRace calls
            // PlayerReferenceManager.GetAllConnectionIdsNearPosition, which walks sync_PlayerReferences
            // and dereferences each reference's PlayerControl.transform.position WITHOUT a null guard.
            // On headless the host reference (connId 32767) is registered with a null PlayerControl
            // (the server has no avatar), so the native loop NREs. A ServerRpc reader that throws makes
            // FishNet kick the sending client — so any client starting a race gets disconnected.
            // Reimplement the method null-safely in managed code (skip references with a null PlayerControl).
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.PlayerReferenceManager" },
                methodName: "GetAllConnectionIdsNearPosition",
                prefix: nameof(PlayerReferenceManager_GetAllConnectionIdsNearPosition_Prefix),
                label: "PlayerReferenceManager.GetAllConnectionIdsNearPosition → null-safe reimpl (headless race-start fix)");

            // Fix (fishing disconnect): casting a line sets the rod's sync_IsCasted SyncVar, whose
            // OnChange handler FishingRod.CheckCastLineOnAllPlayers calls the static
            // SoundEffectManager.PlayClipAtPoint. On a headless server SoundEffectManager.Instance is null
            // (the manager comes from a persistent-managers prefab that the headless boot never instantiates
            // — confirmed live: zero SoundEffectManager components in the scene), and PlayClipAtPoint
            // dereferences Instance, so it NREs — inside the Cmd_CastLine / Cmd_ReelInLine ServerRpc reader,
            // so FishNet kicks the casting client (confirmed v62 stack: SyncVar.set_Value →
            // CheckCastLineOnAllPlayers → SoundEffectManager.PlayClipAtPoint → NRE).
            // Two complementary fixes: (1) the SoundEffectManager.PlayClipAtPoint no-op registered below
            // (the server has no audio, so skipping is correct and covers every positional-sound call); and
            // (2) a silent SoundEffectManager.Instance stub created at boot (EnsureSoundEffectManagerInstance)
            // so OnChange handlers that read Instance.<clip> BEFORE calling PlayClipAtPoint (e.g. the statue
            // system — see below) don't NRE on the null Instance itself. We keep a finalizer on the fishing
            // RpcReaders as a logged safety net so any *other* null in the fishing path is swallowed (logged,
            // not silently) rather than disconnecting the client while we iterate.
            TryPatchFinalizeByPrefix(harmony,
                typeNames: new[] { "Il2Cpp.FishingRod" },
                namePrefix: "RpcReader___",
                finalizer: nameof(Fishing_RpcReader_Finalizer),
                label: "FishingRod.RpcReader___* → log/swallow safety net (headless fishing fix)");

            // Real fix for the fishing disconnect (and any positional-sound NRE): the static
            // SoundEffectManager.PlayClipAtPoint dereferences the (headless-null) SoundEffectManager.Instance.
            // Skip it entirely in headless — the server renders no audio.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.SoundEffectManager", "Il2Cpp_Scripts.SoundEffectManager" },
                methodName: "PlayClipAtPoint",
                prefix: nameof(SkipInHeadless),
                label: "SoundEffectManager.PlayClipAtPoint → skip in headless (null Instance NRE / fishing fix)");

            // ChatManager.OnCanCommunicateOverNetworkChanged fires when the platform privilege
            // check (Steam/EOS) determines whether network communication is allowed. In headless
            // without a real Steam session this fires with false, which shuts down all chat.
            // Force true so chat stays enabled regardless of platform privilege state.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp_Scripts.Systems.Chat.ChatManager" },
                methodName: "OnCanCommunicateOverNetworkChanged",
                prefix: nameof(ChatManager_OnCanCommunicateOverNetworkChanged_Prefix),
                label: "ChatManager.OnCanCommunicateOverNetworkChanged → force true in headless");

            // ── Audio suppression ─────────────────────────────────────────────────────
            // -nographics does NOT suppress audio. Silence the game's audio managers.
            // Note: on headless the real SoundEffectManager never spawns, so this Awake patch is normally
            // inert. It matters only for the stub we create in EnsureSoundEffectManagerInstance: keeping
            // Awake suppressed means the stub never runs the manager's audio setup (we set its Instance
            // singleton manually instead).
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.SoundEffectManager", "Il2Cpp_Scripts.SoundEffectManager" },
                methodName: "Awake",
                prefix: nameof(SkipInHeadless),
                label: "SoundEffectManager.Awake");

            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.MusicController", "Il2Cpp_Scripts.MusicController" },
                methodName: "Awake",
                prefix: nameof(SkipInHeadless),
                label: "MusicController.Awake");

            // ── Boot sequence bypass ──────────────────────────────────────────────────
            // BootSceneManager gates main scene loading on IBootable completion.
            // EOSAuthenticator, DLLChecker, EosPreflightBootable all block on Steam/EOS auth.
            // In headless we bypass them so the main scene (with FishNet NetworkManager) loads.
            PatchBootables(harmony);

            // ── Steam-free EOS auth ───────────────────────────────────────────────────
            // We let SteamManager.Awake run — it sets Instance even if SteamAPI.Init fails
            // (Steam not running). Instance is needed so callers don't NullRef.

            // The EOSAuthenticator boot coroutine checks SteamManager.Initialized before
            // proceeding. Fake it as true so the coroutine doesn't hang waiting for Steam.
            PatchSteamManagerInitialized(harmony);

            // DeviceId auth has no EpicAccountId (Auth service not used). Patch GetLocalUserId()
            // to return a dummy non-null value so game code that dereferences it doesn't NullRef.
            // The actual EOS lobby creation uses ProductUserId (from Connect), not EpicAccountId.
            PatchGetLocalUserId(harmony);

            // Hook SteamAPI_ISteamUser_GetSteamID at the native level so the headless
            // reports a fake SteamID to both the game and EOS, avoiding occupying the
            // real user's account slot in Steam.
            PatchNativeSteamId();

            // Enable the non-Steam NetworkManager immediately so FishNet registers with
            // InstanceFinder before CreateLobby is called. In headless without Steam we
            // want the KCP/Tugboat or EOS P2P NetworkManager, not the Steam one.
            Application.quitting += (Il2CppSystem.Action)(() =>
            {
                _isQuitting = true;
                foreach (var detach in _hookDetachActions) { try { detach(); } catch { } }
            });

            MelonCoroutines.Start(SilenceAudio());
            MelonCoroutines.Start(EnsureSoundEffectManagerInstance());
            MelonCoroutines.Start(EnableHeadlessNetworkManager());
            MelonCoroutines.Start(WaitForEosLoginAndAutoHost());
            MelonLogger.Msg("[HeadlessMode] Done.");
        }

        /// <summary>
        /// Harmony prefix used as a blanket "skip this method in headless" guard.
        /// Returns false (skip original) only in batch mode; returns true (run original) otherwise
        /// so normal game clients are completely unaffected.
        /// Used for methods that are safe to no-op: input managers, voice comms, overlay init, etc.
        /// </summary>
        private static bool SkipInHeadless() => !Application.isBatchMode && !_isQuitting;

        /// <summary>
        /// Harmony finalizer that silently swallows any exception thrown by the patched method,
        /// but only in headless. Used on <c>UiReferenceController.Update</c> which continuously
        /// polls UI state that is never initialized in batch mode, producing a wall of NullRef spam.
        /// Returning null from a finalizer tells Harmony "no exception to propagate."
        /// </summary>
        private static Exception SuppressNullRefInHeadless(Exception __exception)
        {
            if (!Application.isBatchMode || _isQuitting) return __exception;
            return null;
        }

        // ── SteamManager.Initialized spoof ───────────────────────────────────────────

        /// <summary>
        /// Patches <c>SteamManager.Initialized</c> to always return true in headless.
        ///
        /// Why: The <c>EOSAuthenticator</c> boot coroutine polls <c>SteamManager.Initialized</c>
        /// in a loop before it considers the EOS auth step complete. When the headless process
        /// is started without Steam open (the normal case for a dedicated server), SteamAPI.Init
        /// fails and <c>Initialized</c> stays false forever — so the boot loop never advances and
        /// the main scene never loads. Returning true unblocks the coroutine immediately.
        ///
        /// This is safe because we don't actually need Steam auth; we use EOS DeviceId credentials
        /// instead (see <see cref="WaitForEosLoginAndAutoHost"/>).
        /// </summary>
        private static void PatchSteamManagerInitialized(HarmonyLib.Harmony harmony)
        {
            string[] typeNames = {
                "Il2CppPlayEveryWare.EpicOnlineServices.Samples.Steam.SteamManager",
                "Il2CppPlayEveryWare.EpicOnlineServices.SteamManager"
            };
            Type t = null;
            foreach (var name in typeNames) { t = AccessTools.TypeByName(name); if (t != null) break; }
            if (t == null) { MelonLogger.Warning("[HeadlessMode] SteamManager type not found — Initialized spoof skipped."); return; }

            var getter = AccessTools.Property(t, "Initialized")?.GetGetMethod();
            if (getter == null) { MelonLogger.Warning("[HeadlessMode] SteamManager.Initialized getter not found."); return; }

            try
            {
                harmony.Patch(getter, prefix: new HarmonyMethod(typeof(HeadlessPatches), nameof(SteamManager_Initialized_Prefix)));
                MelonLogger.Msg("[HeadlessMode] Patched 'SteamManager.Initialized → true'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] SteamManager.Initialized patch failed: {ex.Message}"); }
        }

        // Harmony prefix for SteamManager.get_Initialized — short-circuits the getter.
        private static bool SteamManager_Initialized_Prefix(ref bool __result)
        {
            if (!Application.isBatchMode) return true;
            __result = true;
            return false;
        }

        // ── Boot sequence bypass ──────────────────────────────────────────────────────

        /// <summary>
        /// Patches the <c>IsBooted()</c> and <c>FailReason()</c> methods on every IBootable
        /// checked by <c>BootSceneManager</c> so they all instantly report success in headless.
        ///
        /// How the game's boot flow works:
        ///   <c>BootSceneManager</c> holds a list of <c>IBootable</c> objects and loops until
        ///   every one returns <c>IsBooted() == true</c> and <c>FailReason() == None</c>.
        ///   Only then does it load the main game scene (which contains <c>NetworkManager</c>).
        ///
        /// The bootables that would block a headless server:
        ///   - <c>EOSAuthenticator</c>  — waits for Steam session ticket → EOS Connect auth.
        ///   - <c>DLLChecker</c>        — verifies game integrity (fails in modded envs).
        ///   - <c>EosPreflightBootable</c> — runs EOS preflight checks that require a display.
        ///   - <c>PlayerPrefsManager</c> — reads/writes prefs that may be missing in batch mode.
        ///
        /// We bypass all of them so the main scene loads immediately, then handle EOS auth
        /// ourselves in <see cref="WaitForEosLoginAndAutoHost"/>.
        /// </summary>
        private static void PatchBootables(HarmonyLib.Harmony harmony)
        {
            string[] bootableTypes = {
                "Il2Cpp_Scripts.Boot.EOSAuthenticator",
                "Il2Cpp_Scripts.Boot.DLLChecker",
                "Il2Cpp_Scripts.Boot.EosPreflightBootable",
                "Il2Cpp.PlayerPrefsManager",
            };
            foreach (var typeName in bootableTypes)
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) continue;

                TryPatchBootable(harmony, t, "IsBooted", nameof(Bootable_IsBooted_Prefix), $"{typeName}.IsBooted");
                TryPatchBootable(harmony, t, "FailReason", nameof(Bootable_FailReason_Prefix), $"{typeName}.FailReason");
            }
        }

        // Patches a single method on a bootable type, logging a warning if the type or method
        // no longer exists (game update renamed it) rather than crashing the mod entirely.
        private static void TryPatchBootable(HarmonyLib.Harmony harmony, Type t, string methodName, string prefix, string label)
        {
            var m = AccessTools.Method(t, methodName);
            if (m == null) return;
            try
            {
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(HeadlessPatches), prefix));
                MelonLogger.Msg($"[HeadlessMode] Patched '{label} → instant-pass in headless'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Patch {label} failed: {ex.Message}"); }
        }

        // Prefix for IBootable.IsBooted() — reports "done" immediately so BootSceneManager advances.
        private static bool Bootable_IsBooted_Prefix(ref bool __result)
        {
            if (!Application.isBatchMode) return true;
            __result = true;
            return false;
        }

        // Prefix for IBootable.FailReason() — reports no failure so BootSceneManager doesn't
        // show an error screen. Cast to int 0 avoids a hard dependency on the enum value name.
        private static bool Bootable_FailReason_Prefix(ref Il2Cpp_Scripts.Boot.FailReason __result)
        {
            if (!Application.isBatchMode) return true;
            __result = (Il2Cpp_Scripts.Boot.FailReason)0; // None
            return false;
        }

        // ── GetLocalUserId stub for DeviceId auth ─────────────────────────────────────

        /// <summary>
        /// Patches <c>EOSSingleton.GetLocalUserId()</c> to return a dummy <c>EpicAccountId</c>
        /// in headless instead of null.
        ///
        /// Why: The EOS Auth service (which issues <c>EpicAccountId</c>) is a separate system
        /// from the EOS Connect service (which issues <c>ProductUserId</c>). DeviceId auth only
        /// uses the Connect service, so there is never a real <c>EpicAccountId</c> available.
        /// Several call sites in the game (notably <c>LobbyManager.CreateLobby</c>) call
        /// <c>GetLocalUserId()</c> and immediately dereference the result without null-checking.
        /// Returning a well-formed but semantically invalid ID prevents those NullReferenceExceptions
        /// while being harmless — EOS ignores an invalid AccountId for DeviceId-authenticated lobbies.
        /// </summary>
        private static void PatchGetLocalUserId(HarmonyLib.Harmony harmony)
        {
            var singletonType = AccessTools.TypeByName("Il2CppPlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton")
                             ?? typeof(EOSManager);
            var method = AccessTools.Method(singletonType, "GetLocalUserId");
            if (method == null) { MelonLogger.Warning("[HeadlessMode] GetLocalUserId method not found — skipping patch."); return; }
            try
            {
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(HeadlessPatches), nameof(GetLocalUserId_Prefix)));
                MelonLogger.Msg("[HeadlessMode] Patched 'EOSSingleton.GetLocalUserId → dummy in headless'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] GetLocalUserId patch failed: {ex.Message}"); }
        }

        // Cached once so we don't allocate a new EpicAccountId object on every call.
        private static EpicAccountId _dummyEpicAccountId;

        // Prefix for EOSSingleton.GetLocalUserId(). The dummy value is a valid hex string
        // (32 hex chars) that EpicAccountId.FromString accepts, but it will never correspond
        // to a real account, so EOS treats it as an anonymous/unauthenticated identity.
        private static bool GetLocalUserId_Prefix(ref EpicAccountId __result)
        {
            if (!Application.isBatchMode) return true;
            // If there's already a real value, use it
            if (__result != null && __result.IsValid()) return false;
            // Return cached dummy or create one
            if (_dummyEpicAccountId == null || !_dummyEpicAccountId.IsValid())
            {
                try { _dummyEpicAccountId = EpicAccountId.FromString("0000000000000000000000000000000f"); }
                catch { return true; }
            }
            __result = _dummyEpicAccountId;
            return false;
        }

        // ── Native SteamID spoof ─────────────────────────────────────────────────────
        // Steam registers any process that calls SteamAPI_Init as "currently playing" on the
        // account that owns the game. If the headless server and the real game share the same
        // Steam account on the same machine, Steam would show two sessions and may refuse the
        // second launch. We solve this at the native level by hooking the C functions that
        // report the SteamID before the game (or EOS) has a chance to read or register them.

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPStr)] string lpFileName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPStr)] string lpModuleName);

        // A fake but structurally valid Steam64 ID (universe=1, type=Individual, instance=1, accountID=1).
        // Chosen to be distinct from any real user account so Steam does not conflate the headless
        // process with the human playing the real game on the same machine.
        private const ulong FakeSteamId = 76561197960265729UL;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong GetSteamIdDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void UseBreakpadCrashHandlerDelegate(
            IntPtr pchVersion, IntPtr pchDate, IntPtr pchTime, int bFullMemoryDumps, IntPtr pvContext, IntPtr m_pfnPreMinidumpCallback);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetBreakpadAppIdDelegate(uint appId);

        // Breakpad_SteamSetSteamID is the direct setter that registers the process SteamID
        // with the Breakpad crash reporter — the upstream caller of SteamInternal_SetMinidumpSteamID.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void BreakpadSetSteamIdDelegate(ulong steamId);

        // Set to true when Unity begins quitting. Harmony patch prefixes check this so they
        // return cleanly instead of executing during teardown, which prevents the flood of
        // "During invoking native->managed trampoline" errors that fill the shutdown log.
        private static bool _isQuitting = false;

        // Holds every NativeHook object and its associated delegate so the GC cannot collect
        // them. A collected delegate whose function pointer is still installed in the native
        // hook table causes a crash on the next native call through that hook.
        private static readonly System.Collections.Generic.List<object> _steamIdHooks = new();

        // Detach callbacks for each installed native hook. Called on Application.quitting so
        // the native detour pointers are removed before the CLR tears down managed delegates,
        // eliminating the "During invoking native->managed trampoline" error at shutdown.
        private static readonly System.Collections.Generic.List<Action> _hookDetachActions = new();

        // Handle to steam_api64.dll — loaded explicitly so we can call GetProcAddress on it
        // for hooking and (optionally) for calling SteamAPI_Shutdown without a static import.
        private static IntPtr _steamApiModule = IntPtr.Zero;


        /// <summary>
        /// Installs native hooks into <c>steam_api64.dll</c> to prevent the headless process
        /// from appearing on the real user's Steam account.
        ///
        /// Three categories of hooks are installed:
        ///   1. <c>GetSteamID</c> getters — return <see cref="FakeSteamId"/> instead of the
        ///      real account's ID. This makes Steam (and EOS, which reads it for session tickets)
        ///      see the headless as a different user entirely.
        ///   2. Breakpad crash handler registration — hooked as no-ops because registering a
        ///      crash handler calls <c>SteamInternal_SetMinidumpSteamID</c> with the real ID,
        ///      which would overwrite our fake ID and register the process on the real account.
        ///   3. <c>Breakpad_SteamSetSteamID</c> — the direct upstream of
        ///      <c>SteamInternal_SetMinidumpSteamID</c>; also no-oped for belt-and-suspenders.
        ///
        /// All hooks are stored in <see cref="_steamIdHooks"/> to prevent GC collection of the
        /// delegate objects whose function pointers live in the native hook table.
        /// </summary>
        private static void PatchNativeSteamId()
        {
            if (!Application.isBatchMode) return;

            // Find steam_api64.dll — same search order as PEWS SteamManager
            string pluginsDir = Path.Combine(MelonEnvironment.UnityGameDataDirectory, "Plugins");
            string[] candidates = {
                Path.Combine(pluginsDir, "steam_api64.dll"),
                Path.Combine(pluginsDir, "x86_64", "steam_api64.dll"),
            };

            IntPtr library = IntPtr.Zero;
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                library = LoadLibrary(path);
                if (library != IntPtr.Zero) { MelonLogger.Msg($"[HeadlessMode] Loaded steam_api64 from: {path}"); break; }
            }
            if (library == IntPtr.Zero) { MelonLogger.Warning("[HeadlessMode] steam_api64.dll not found — SteamID spoof skipped."); return; }
            _steamApiModule = library;

            // Getters: return a fake SteamID instead of the real one.
            string[] getters = { "SteamAPI_ISteamUser_GetSteamID", "SteamAPI_ISteamGameServer_GetSteamID", "SteamGameServer_GetSteamID" };
            foreach (var name in getters)
            {
                IntPtr addr = GetProcAddress(library, name);
                if (addr == IntPtr.Zero) { MelonLogger.Msg($"[HeadlessMode] {name} not found (skip)."); continue; }
                try
                {
                    GetSteamIdDelegate detour = _ => FakeSteamId;
                    IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(detour);
                    var hook = new MelonLoader.NativeUtils.NativeHook<GetSteamIdDelegate>(addr, detourPtr);
                    hook.Attach();
                    _steamIdHooks.Add(hook); _steamIdHooks.Add(detour);
                    _hookDetachActions.Add(() => hook.Detach());
                    MelonLogger.Msg($"[HeadlessMode] Native hook: {name} → FakeSteamId ({FakeSteamId})");
                }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Hook {name} failed: {ex.Message}"); }
            }

            // Both of these register the process with Steam's crash reporter which is what
            // causes SteamInternal_SetMinidumpSteamID to be called. Hook both as no-ops.
            void HookVoid<T>(string name, T detour) where T : Delegate
            {
                IntPtr addr = GetProcAddress(library, name);
                if (addr == IntPtr.Zero) { MelonLogger.Msg($"[HeadlessMode] {name} not found (skip)."); return; }
                try
                {
                    IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(detour);
                    var hook = new MelonLoader.NativeUtils.NativeHook<T>(addr, detourPtr);
                    hook.Attach();
                    _steamIdHooks.Add(hook); _steamIdHooks.Add(detour);
                    _hookDetachActions.Add(() => hook.Detach());
                    MelonLogger.Msg($"[HeadlessMode] Native hook: {name} → no-op");
                }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Hook {name} failed: {ex.Message}"); }
            }

            HookVoid<UseBreakpadCrashHandlerDelegate>("SteamAPI_UseBreakpadCrashHandler",
                (ver, date, time, full, ctx, cb) => { });
            HookVoid<SetBreakpadAppIdDelegate>("SteamAPI_SetBreakpadAppID",
                appId => MelonLogger.Msg($"[HeadlessMode] Suppressed SteamAPI_SetBreakpadAppID({appId})"));
            // Breakpad_SteamSetSteamID is what passes the real SteamID into the crash reporter,
            // which is the direct upstream of SteamInternal_SetMinidumpSteamID.
            HookVoid<BreakpadSetSteamIdDelegate>("Breakpad_SteamSetSteamID",
                id => MelonLogger.Msg($"[HeadlessMode] Suppressed Breakpad_SteamSetSteamID({id})"));
        }

        // ── Steam disconnect after EOS auth ──────────────────────────────────────────────

        // ── LobbyManager.CreateLobby null-field patches ───────────────────────────────

        /// <summary>
        /// Harmony prefix for <c>LobbyManager.CreateLobby</c>. Runs immediately before
        /// <c>CreateLobby</c> to initialize fields that the normal UI-driven flow would have
        /// set up but that are null in headless because no human went through the menus.
        ///
        /// Fields pre-initialized here:
        ///   - <c>LobbyJoiningEvent</c> (UnityEvent) — null if no UI has subscribed to it.
        ///   - <c>LobbyHeartbeat</c> — null if the lobby system was never UI-activated.
        ///   - <c>GameInfo.PlayerId</c> / <c>PlayerName</c> — null because our manual EOS login
        ///     bypassed the code path that normally populates GameInfo from the Connect callback.
        /// </summary>
        private static void LobbyManager_CreateLobby_PreInit(LobbyManager __instance)
        {
            if (!Application.isBatchMode) return;

            EnsureUnityEvent(__instance, "LobbyJoiningEvent");
            EnsureLobbyHeartbeat(__instance);
            EnsureGameInfoState(__instance);

            // Check EOS interface availability and FishNet state
            try
            {
                var platform = EOSManager.Instance?.GetEOSPlatformInterface();
                var lobbyIface = platform != null ? (object)platform.GetLobbyInterface() : null;
                var puid = EOSManager.Instance?.GetProductUserId();
                var serverMgr = InstanceFinder.ServerManager;
                MelonLogger.Msg($"[HeadlessMode] PreInit: platform={platform!=null}, lobbyIface={lobbyIface!=null}, puid={puid?.IsValid()}, serverMgr={serverMgr!=null}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] PreInit diag: {ex.Message}"); }
        }

        /// <summary>
        /// Calls <c>EOSLobbyManager.OnLoggedIn()</c> to replicate the post-login initialization
        /// that the normal PEWS callback chain would have triggered.
        ///
        /// Why needed: <c>OnLoggedIn</c> subscribes the manager to EOS lobby update and member
        /// change notifications and resets <c>CurrentLobby</c> to a default state. Without it,
        /// <c>CurrentLobby</c> (field@0x10) and <c>UserInfoManager</c> (field@0xA0) can remain
        /// null, causing <c>CreateLobby</c> to NullRef deep inside the EOS SDK wrapper. The field
        /// offsets are read from the Il2Cpp dump (EOSLobbyManager TypeDefIndex 21513) for
        /// diagnostic logging before and after the call to confirm the state changed.
        /// </summary>
        private static void InitEosLobbyManagerState(EOSLobbyManager mgr)
        {
            try
            {
                IntPtr basePtr = (mgr as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
                if (basePtr != IntPtr.Zero)
                {
                    // Field offsets from dump.cs (EOSLobbyManager, TypeDefIndex 21513)
                    IntPtr curLobby   = Marshal.ReadIntPtr(basePtr, 0x10); // CurrentLobby
                    IntPtr userInfo   = Marshal.ReadIntPtr(basePtr, 0xA0); // UserInfoManager
                    IntPtr lobbyUpd   = Marshal.ReadIntPtr(basePtr, 0x40); // LobbyUpdateNotification
                    MelonLogger.Msg($"[HeadlessMode] EOSLobbyManager fields BEFORE OnLoggedIn: CurrentLobby={curLobby!=IntPtr.Zero}, UserInfoManager={userInfo!=IntPtr.Zero}, LobbyUpdateNotify={lobbyUpd!=IntPtr.Zero}");
                }

                // OnLoggedIn() subscribes to lobby notifications and resets internal state.
                try
                {
                    mgr.OnLoggedIn();
                    MelonLogger.Msg("[HeadlessMode] EOSLobbyManager.OnLoggedIn() invoked.");
                }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] OnLoggedIn threw: {ex.GetType().Name}: {ex.Message}"); }

                if (basePtr != IntPtr.Zero)
                {
                    IntPtr curLobby = Marshal.ReadIntPtr(basePtr, 0x10);
                    IntPtr userInfo = Marshal.ReadIntPtr(basePtr, 0xA0);
                    MelonLogger.Msg($"[HeadlessMode] EOSLobbyManager fields AFTER OnLoggedIn: CurrentLobby={curLobby!=IntPtr.Zero}, UserInfoManager={userInfo!=IntPtr.Zero}");
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] InitEosLobbyManagerState: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>
        /// Populates <c>GameInfo.PlayerId</c>, <c>PlayerName</c>, and <c>LobbyManager</c> from
        /// our EOS Connect PUID before <c>CreateLobby</c> runs.
        ///
        /// Why needed: Disassembly of <c>LobbyManager.CreateLobby</c> at offset 0xCFD40 shows
        /// a crash at offset 0xD0D53 that reads <c>GameInfo_TypeInfo → GameInfo.Instance →
        /// PlayerId@0x30</c> and dereferences the result (calls a virtual on it). The normal
        /// EOS login flow sets <c>GameInfo.PlayerId</c> from the Connect callback; our manual
        /// login bypasses that, so <c>PlayerId</c> is null and <c>CreateLobby</c> NullRefs.
        /// </summary>
        private static void EnsureGameInfoState(LobbyManager lm)
        {
            try
            {
                var gameInfo = Il2Cpp_Scripts.Managers.GameInfo.Instance;
                if (gameInfo == null)
                {
                    // Fall back to scanning loaded objects (covers inactive GameObjects).
                    var all = Resources.FindObjectsOfTypeAll<Il2Cpp_Scripts.Managers.GameInfo>();
                    if (all != null && all.Length > 0) gameInfo = all[0];
                }
                if (gameInfo == null)
                {
                    MelonLogger.Warning("[HeadlessMode] GameInfo.Instance is null — cannot set PlayerId.");
                    return;
                }

                var puid = EOSManager.Instance?.GetProductUserId();
                bool puidValid = puid != null && puid.IsValid();
                MelonLogger.Msg($"[HeadlessMode] GameInfo before: PlayerId set={gameInfo.PlayerId != null}, PlayerName={gameInfo.PlayerName ?? "<null>"}, puidValid={puidValid}");

                if (puidValid && gameInfo.PlayerId == null)
                    gameInfo.PlayerId = puid;
                if (string.IsNullOrEmpty(gameInfo.PlayerName))
                    gameInfo.PlayerName = !string.IsNullOrWhiteSpace(SledHeadlessCore.ServerName)
                        ? SledHeadlessCore.ServerName : "HeadlessServer";
                try { if (gameInfo.LobbyManager == null) gameInfo.LobbyManager = lm; } catch { }

                MelonLogger.Msg($"[HeadlessMode] GameInfo after: PlayerId set={gameInfo.PlayerId != null}, PlayerName={gameInfo.PlayerName ?? "<null>"}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] EnsureGameInfoState: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Creates a dummy LobbyHeartbeat wrapping an empty Lobby if the field is null.
        // LobbyHeartbeat is normally set when the player first opens the lobby menu;
        // in headless that UI never opens, so CreateLobby finds it null and NullRefs.
        private static void EnsureLobbyHeartbeat(LobbyManager instance)
        {
            try
            {
                var prop = typeof(LobbyManager).GetProperty("LobbyHeartbeat",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null || !prop.CanRead || !prop.CanWrite) return;
                if (prop.GetValue(instance) != null) return;

                // LobbyHeartbeat is null — create a dummy one with an empty Lobby
                // so code that accesses LobbyHeartbeat.IsValid (or similar) doesn't NullRef.
                var heartbeat = new Il2Cpp_Scripts.Managers.LobbyHeartbeat(
                    new Il2CppPlayEveryWare.EpicOnlineServices.Samples.Lobby());
                prop.SetValue(instance, heartbeat);
                MelonLogger.Msg("[HeadlessMode] Pre-initialized LobbyHeartbeat.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] EnsureLobbyHeartbeat: {ex.Message}"); }
        }


        // Creates an empty UnityEvent on a LobbyManager property if it is null.
        // The Il2CppInterop null check (val is Object o && o == null) is required because
        // Il2Cpp wrapped objects can pass a C# null-check but still be a null native pointer.
        private static void EnsureUnityEvent(LobbyManager instance, string propName)
        {
            try
            {
                var prop = typeof(LobbyManager).GetProperty(propName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null || !prop.CanRead || !prop.CanWrite) return;
                var val = prop.GetValue(instance);
                if (val != null && !(val is Object o && o == null)) return;
                prop.SetValue(instance, new UnityEngine.Events.UnityEvent());
                MelonLogger.Msg($"[HeadlessMode] Pre-initialized LobbyManager.{propName}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] EnsureUnityEvent {propName}: {ex.Message}"); }
        }

        // Harmony finalizer for CreateLobby — logs any exception with full stack trace then
        // returns null to suppress it. Without suppression a throw inside CreateLobby would
        // propagate through PEWS and FishNet, leaving the server in a partially started state
        // with no useful error message. With suppression we get the full trace in the log and
        // the server continues (FishNet may still start even if the EOS lobby creation failed).
        private static Exception LobbyManager_CreateLobby_Finalizer(Exception __exception)
        {
            if (!Application.isBatchMode) return __exception;
            if (__exception != null)
            {
                MelonLogger.Warning($"[HeadlessMode] LobbyManager.CreateLobby threw: {__exception.GetType().Name}: {__exception.Message}");
                if (!string.IsNullOrEmpty(__exception.StackTrace))
                    MelonLogger.Warning($"[HeadlessMode] Stack: {__exception.StackTrace}");
                var inner = __exception.InnerException;
                if (inner != null)
                    MelonLogger.Warning($"[HeadlessMode] Inner: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
            }
            return null;
        }

        // ── steamclient64 late hook ───────────────────────────────────────────────────

        // ── Headless NetworkManager activation ───────────────────────────────────────

        /// <summary>
        /// Coroutine: activates the "Network Manager (EOS)" GameObject so FishNet registers
        /// with <c>InstanceFinder</c> before <c>CreateLobby</c> is called.
        ///
        /// Why needed: The game ships two NetworkManagers — one wired to FishySteamworks (Steam P2P)
        /// and one wired to FishyEOS (EOS P2P). Only the active one registers with InstanceFinder.
        /// Normally a human clicking "Host" calls <c>EnableCorrectNetworkManager</c> which picks
        /// the right one. In headless there is no UI interaction, so we activate the EOS one directly.
        ///
        /// Why polling: The main scene loads asynchronously after the boot sequence completes.
        /// A fixed wait is too short; we poll until NetworkManagers appear in the loaded scene.
        /// </summary>
        private static IEnumerator EnableHeadlessNetworkManager()
        {
            if (!SledHeadlessCore.HeadlessAutoHost) yield break;

            // Poll until the main scene loads and NetworkManagers appear.
            // Resources.FindObjectsOfTypeAll includes inactive GameObjects (which the EOS NM starts as).
            Il2CppFishNet.Managing.NetworkManager[] allNMs = null;
            float nmPollStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - nmPollStart < 120f)
            {
                yield return new WaitForSecondsRealtime(1f);
                allNMs = Resources.FindObjectsOfTypeAll<Il2CppFishNet.Managing.NetworkManager>();
                if (allNMs != null && allNMs.Length > 0) break;
            }

            MelonLogger.Msg($"[HeadlessMode] Found {(allNMs?.Length ?? 0)} NetworkManager(s) after {Time.realtimeSinceStartup - nmPollStart:F1}s.");

            if (allNMs == null || allNMs.Length == 0) { MelonLogger.Warning("[HeadlessMode] No NetworkManager found — scene may not have loaded."); yield break; }

            foreach (var nm in allNMs)
                try { MelonLogger.Msg($"[HeadlessMode] NetworkManager: '{nm.gameObject.name}' active={nm.gameObject.activeSelf}"); } catch { }

            // Prefer "Network Manager (EOS)" by name — that's the FishyEOS transport NM.
            // Fall back to any non-Steam inactive NM, then any inactive NM, then whatever exists.
            Il2CppFishNet.Managing.NetworkManager target = null;
            foreach (var nm in allNMs)
                try { if (nm.gameObject.name?.Contains("EOS") == true) { target = nm; break; } } catch { }

            if (target == null)
                foreach (var nm in allNMs)
                    try { if (!nm.gameObject.name?.ToLower().Contains("steam") == true && !nm.gameObject.activeSelf) { target = nm; break; } } catch { }

            if (target == null)
                foreach (var nm in allNMs)
                    try { if (!nm.gameObject.activeSelf) { target = nm; break; } } catch { }

            if (target == null && allNMs.Length > 0) target = allNMs[0];

            if (target != null)
            {
                MelonLogger.Msg($"[HeadlessMode] Enabling NetworkManager: {target.gameObject.name}");
                target.gameObject.SetActive(true);
            }
            else
                MelonLogger.Warning("[HeadlessMode] No suitable NetworkManager to enable.");
        }

        // ── Auto-host coroutine ───────────────────────────────────────────────────────

        // Cached reference to the EOSLobbyManager retrieved from LobbyManager._lobbyManager.
        // Kept alive to support LogHostDiagnostics queries after CreateLobby completes.
        private static EOSLobbyManager _eosLobbyManager;


        // Binds ChatManager.OnServerReceivedChatBroadcastFromClient as a FishNet server broadcast
        // handler for the ChatMessage broadcast type. Confirmed root cause of broken text chat: the
        // game registers this in ChatManager.OnEnable while IsServer is still false on headless, so
        // it never binds and incoming chat broadcasts are dropped. requireAuthentication=false so
        // delivery doesn't depend on FishNet's authenticated-flag (our EOS clients may not carry it).
        private static IEnumerator RegisterHeadlessChatServerHandler()
        {
            if (!Application.isBatchMode) yield break;

            // Wait for the server + ChatManager.Instance to exist.
            Il2Cpp_Scripts.Systems.Chat.ChatManager cm = null;
            Il2CppFishNet.Managing.Server.ServerManager sm = null;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 30f && !_isQuitting)
            {
                try
                {
                    cm = Il2Cpp_Scripts.Systems.Chat.ChatManager.Instance;
                    sm = InstanceFinder.ServerManager;
                }
                catch { }
                if (cm != null && sm != null && sm.IsAnyServerStarted()) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }

            if (cm == null || sm == null)
            {
                MelonLogger.Warning($"[HeadlessMode] Chat handler not registered: ChatManager={(cm != null)}, ServerManager={(sm != null)}.");
                yield break;
            }

            try
            {
                // Build the Action<NetworkConnection, ChatMessage, Channel> by binding directly to the
                // EXISTING native ChatManager.OnServerReceivedChatBroadcastFromClient method. A C#
                // lambda can't be used here: Il2CppInterop can't marshal the non-blittable ChatMessage
                // struct across a managed trampoline. Il2CppSystem.Delegate.CreateDelegate binds the
                // native method to a native delegate with no managed marshaling, sidestepping that.
                var actionType = Il2CppInterop.Runtime.Il2CppType.Of<Il2CppSystem.Action<
                    Il2CppFishNet.Connection.NetworkConnection,
                    Il2Cpp_Scripts.Systems.Chat.ChatMessage,
                    Il2CppFishNet.Transporting.Channel>>();

                var del = Il2CppSystem.Delegate.CreateDelegate(
                    actionType,
                    cm.Cast<Il2CppSystem.Object>(),
                    "OnServerReceivedChatBroadcastFromClient");

                var handler = del.Cast<Il2CppSystem.Action<
                    Il2CppFishNet.Connection.NetworkConnection,
                    Il2Cpp_Scripts.Systems.Chat.ChatMessage,
                    Il2CppFishNet.Transporting.Channel>>();

                sm.RegisterBroadcast<Il2Cpp_Scripts.Systems.Chat.ChatMessage>(handler, false);

                MelonLogger.Msg("[HeadlessMode] Registered server-side chat broadcast handler (ChatMessage).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode] RegisterHeadlessChatServerHandler failed: {ex.GetType().Name}: {ex.Message}");
            }
        }



        // Enables text and voice chat on the server's PlayerSavedSettings so the server-side
        // ChatManager processes and forwards messages from clients. Also calls YesNo_TextChat /
        // YesNo_VoiceChat on UiReferenceController which is the same code path the normal UI uses.
        private static void EnableHeadlessChat()
        {
            // Set on PlayerPrefsManager.playerSavedSettings (the persistent save data layer)
            try
            {
                var savedSettings = Il2Cpp.PlayerPrefsManager.Instance?.playerSavedSettings;
                if (savedSettings != null)
                {
                    savedSettings.TextChatEnabledGeneral = true;
                    savedSettings.VoiceChatEnabledGeneral = true;
                    MelonLogger.Msg("[HeadlessMode] Enabled text + voice chat on PlayerSavedSettings.");
                }
                else
                    MelonLogger.Warning("[HeadlessMode] PlayerPrefsManager.playerSavedSettings is null — chat defaults unchanged.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] EnableHeadlessChat (saved settings): {ex.Message}"); }

            // We intentionally do NOT call UiReferenceController.YesNo_TextChat/VoiceChat here.
            // Those are the in-game setup-dialog handlers; they call OpenMenu() internally, which
            // dereferences menu UI that does not exist on a headless server -> NullReferenceException
            // (the IL2CPP runtime logs the whole stack even though we catch it). They are redundant:
            // the PlayerSavedSettings flags set above enable chat, and LobbyKit's ChatManager patches
            // handle the server-side forwarding. So there's nothing to gain and only log noise to lose.
        }

        /// <summary>
        /// Reads the current <c>LobbySettings</c> from <c>LobbySettingsManager</c>, overwrites the
        /// fields that default to wrong values in headless (peaceful mode, chat mode), then pushes
        /// the corrected struct to all connected clients via <c>Server_UpdateLobbySettings</c>.
        ///
        /// Why this is necessary: <c>CreateLobby</c> stores peaceful mode / chat settings as EOS
        /// lobby attributes (visible in the lobby browser) but the in-game behaviour is driven by
        /// a FishNet-synced <c>LobbySettings</c> struct managed by <c>LobbySettingsManager</c>.
        /// In the normal hosted flow the UI calls <c>Server_UpdateLobbySettings</c> to sync the
        /// struct after the host clicks "Create". We bypass that UI, so without this call clients
        /// receive the struct's default value — which leaves <c>peacefulModeOn = true</c> (blocking
        /// snowball pickup) and <c>textChatOnly = false</c> may be misinterpreted if other fields
        /// are zeroed.
        /// </summary>
        private static void FixAndSyncLobbySettings()
        {
            try
            {
                var lsm = Il2Cpp.LobbySettingsManager.Instance;
                if (lsm == null) { MelonLogger.Warning("[HeadlessMode] LobbySettingsManager.Instance is null — skipping lobby settings fix."); return; }

                var settings = lsm.GetLobbySettings();
                MelonLogger.Msg($"[HeadlessMode] LobbySettings before fix: peaceful={settings.peacefulModeOn}, textChatOnly={settings.textChatOnly}");

                settings.peacefulModeOn = SledHeadlessCore.IsPeacefulMode;
                settings.textChatOnly   = false; // allow both voice and text chat

                lsm.Server_UpdateLobbySettings(settings);
                MelonLogger.Msg($"[HeadlessMode] LobbySettings fixed: peaceful={settings.peacefulModeOn}, textChatOnly={settings.textChatOnly}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode] FixAndSyncLobbySettings: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs post-host diagnostic information to help confirm the server is correctly set up:
        ///   1. The EOS lobby — verifies the lobby is live and advertised on EOS (clients can
        ///      discover and join it). If <c>GetCurrentLobby()</c> returns null here, the EOS
        ///      lobby creation failed silently and clients won't be able to find the server.
        ///   2. The active FishNet transport — tells us whether clients will connect via EOS P2P
        ///      (FishyEOS, keyed on our valid PUID — this works) or Steam P2P (FishySteamworks,
        ///      keyed on our fake SteamID — this will not route correctly). If Multipass is active,
        ///      enumerates sub-transports to find the concrete one.
        /// </summary>
        private static void LogHostDiagnostics()
        {
            // EOS lobby — confirms the lobby is actually advertised on EOS (discoverable/joinable).
            try
            {
                var lobby = _eosLobbyManager?.GetCurrentLobby();
                if (lobby != null)
                {
                    bool valid = false; try { valid = lobby.IsValid(); } catch { }
                    string owner = "<null>"; try { owner = lobby.LobbyOwner?.IsValid() == true ? lobby.LobbyOwner.ToString() : "<invalid>"; } catch { }
                    MelonLogger.Msg($"[HeadlessMode] EOS Lobby: Id={lobby.Id ?? "<null>"}, IsValid={valid}, Bucket={lobby.BucketId ?? "<null>"}, Owner={owner}");
                }
                else MelonLogger.Warning("[HeadlessMode] EOS Lobby: GetCurrentLobby() returned null.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] EOS lobby diag: {ex.GetType().Name}: {ex.Message}"); }

            // Active FishNet transport — tells us whether clients reach us via EOS P2P (our valid PUID,
            // works) or Steam P2P (our fake SteamID, won't route).
            try
            {
                var tm = InstanceFinder.TransportManager;
                var transport = tm?.Transport;
                if (transport != null)
                {
                    // GetType() returns the Il2CppInterop wrapper (base Transport). Read the native
                    // runtime class name to get the concrete transport (FishyEOS/Multipass/Tugboat/Yak).
                    string concrete = NativeIl2CppClassName(transport);
                    MelonLogger.Msg($"[HeadlessMode] Active FishNet transport (concrete): {concrete}");

                    // If Multipass, enumerate sub-transports to find the actual networking one.
                    try
                    {
                        var mp = (transport as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                            ?.TryCast<Il2CppFishNet.Transporting.Multipass.Multipass>();
                        if (mp != null)
                        {
                            for (int i = 0; i < 8; i++)
                            {
                                Il2CppFishNet.Transporting.Transport sub = null;
                                try { sub = mp.GetTransport(i); } catch { break; }
                                if (sub == null) break;
                                MelonLogger.Msg($"[HeadlessMode]   Multipass sub[{i}] = {NativeIl2CppClassName(sub)}");
                            }
                        }
                    }
                    catch { }
                }
                else MelonLogger.Warning("[HeadlessMode] TransportManager.Transport is null.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] transport diag: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>
        /// Returns the concrete runtime class name of an Il2Cpp object by reading it from the
        /// native il2cpp metadata, bypassing Il2CppInterop's managed wrapper layer.
        ///
        /// Why: <c>Il2CppInterop</c> wraps every Il2Cpp class with a C# proxy class. For types
        /// that have no specific proxy (e.g. a third-party transport assembly we didn't generate
        /// bindings for), <c>GetType()</c> returns the base wrapper type (e.g. <c>Transport</c>)
        /// not the concrete runtime type (<c>FishyEOS</c>). Calling <c>il2cpp_class_get_name</c>
        /// directly on the native pointer always returns the correct runtime class name.
        /// </summary>
        private static string NativeIl2CppClassName(object il2cppObj)
        {
            try
            {
                IntPtr ptr = (il2cppObj as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
                if (ptr == IntPtr.Zero) return "<null ptr>";
                IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(ptr);
                if (klass == IntPtr.Zero) return "<null class>";
                IntPtr namePtr = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(klass);
                return Marshal.PtrToStringAnsi(namePtr) ?? "<null name>";
            }
            catch (Exception ex) { return $"<err {ex.GetType().Name}>"; }
        }


        // ── Peaceful mode + chat fixes ────────────────────────────────────────────────

        // Prefix for PlayerControl.get_IsPeaceful. In headless there is no host player character,
        // so sync_PeacefulModeTurnedOn is never written and may default to true in the prefab.
        // Return the preference directly so all client ServerRpcs (Cmd_StartPickingUpSnow etc.)
        // see the correct peaceful mode without needing a live host PlayerControl.
        private static bool PlayerControl_IsPeaceful_Prefix(ref bool __result)
        {
            if (!Application.isBatchMode || _isQuitting) return true;
            __result = SledHeadlessCore.IsPeacefulMode;
            return false;
        }

        // Prefix for ChatManager.OnCanCommunicateOverNetworkChanged. Forces the argument to true
        // so the ChatManager always considers network communication available. In headless without
        // a real Steam/EOS session this is called with false, which disables all network chat.
        private static bool ChatManager_OnCanCommunicateOverNetworkChanged_Prefix(ref bool canCommunicateOverNetwork)
        {
            if (!Application.isBatchMode || _isQuitting) return true;
            MelonLogger.Msg($"[HeadlessMode] ChatManager.OnCanCommunicateOverNetworkChanged({canCommunicateOverNetwork}) → forcing true");
            canCommunicateOverNetwork = true;
            return true; // run original with forced-true argument
        }

        // ── FishNet crash guards ──────────────────────────────────────────────────────

        /// <summary>
        /// Harmony finalizer for <c>NetworkObject.InvokeStopCallbacks</c>. Suppresses any
        /// exception thrown by a network object's stop callbacks.
        ///
        /// Why needed: When a client disconnects, FishNet calls <c>InvokeStopCallbacks</c> on
        /// every <c>NetworkObject</c> owned by that client. If any callback throws (e.g. a
        /// <c>TrinketPack</c> whose owner sync-var is null after a crash), FishNet's despawn
        /// loop aborts mid-iteration. Objects remain stuck in a half-despawned state and
        /// subsequent clients that join get stuck on the loading screen because the server
        /// cannot complete the object-state sync handshake. Suppressing the exception lets
        /// teardown continue past any broken callback so the server stays in a valid state.
        /// </summary>
        private static Exception NetworkObject_InvokeStopCallbacks_Finalizer(Exception __exception)
        {
            if (_isQuitting) return __exception;
            if (__exception != null)
                MelonLogger.Warning($"[HeadlessMode] NetworkObject stop callback threw (suppressed): {__exception.GetType().Name}");
            return null;
        }

        /// <summary>
        /// Harmony prefix for <c>Sled.FollowOwnerWhileInactive</c>. Skips the method when the
        /// sled has no valid owner.
        ///
        /// Why needed: <c>FollowOwnerWhileInactive</c> runs every <c>FixedUpdate</c>. When a
        /// player disconnects mid-game, the sled's <c>sync_Owner</c> sync-var can be null or
        /// point to a disconnected player. The method already has an early-return for null
        /// owners, but the Unity error logging happens before that check fires — producing a
        /// continuous wall of "Owner is null!" errors in the server log. Short-circuiting here
        /// prevents the spam without changing the behaviour (the method would have returned
        /// immediately anyway).
        /// </summary>
        private static bool Sled_FollowOwnerWhileInactive_Prefix(Il2Cpp.Sled __instance)
        {
            if (_isQuitting) return false;
            try
            {
                var ownerSync = __instance.sync_Owner;
                if (ownerSync == null || ownerSync.Value == null) return false;
            }
            catch { return false; }
            return true;
        }

        // ── Harmony log suppression ───────────────────────────────────────────────────

        // Installs a prefix on AccessTools.GetTypesFromAssembly that silently handles
        // ReflectionTypeLoadException instead of logging it. Must be called before any
        // other harmony.Patch() to suppress the per-patch UnityEngine.CoreModule warning.
        private static void SuppressHarmonyAssemblyScanWarnings(HarmonyLib.Harmony harmony)
        {
            try
            {
                var m = typeof(HarmonyLib.AccessTools).GetMethod("GetTypesFromAssembly",
                    BindingFlags.Static | BindingFlags.Public,
                    null, new[] { typeof(Assembly) }, null);
                if (m == null) return;
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(HeadlessPatches), nameof(AccessTools_GetTypesFromAssembly_Prefix)));
            }
            catch { }
        }

        // Replaces AccessTools.GetTypesFromAssembly entirely: returns partial type list on
        // ReflectionTypeLoadException without emitting any log message.
        private static bool AccessTools_GetTypesFromAssembly_Prefix(Assembly assembly, ref IEnumerable<Type> __result)
        {
            try { __result = assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            { __result = System.Linq.Enumerable.Where(ex.Types, t => t != null); }
            return false;
        }

        // ── TryPatch helper ───────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a type by trying each name in <paramref name="typeNames"/> in order (handles
        /// namespace changes across game updates), finds the named method, and applies the
        /// requested Harmony prefix/postfix/finalizer. Logs a warning instead of throwing if
        /// the type or method is not found — the mod continues loading with reduced functionality
        /// rather than failing completely.
        /// </summary>
        private static void TryPatch(HarmonyLib.Harmony harmony, string[] typeNames, string methodName,
            string prefix = null, string postfix = null, string finalizer = null, string label = null)
        {
            Type targetType = null;
            foreach (var name in typeNames) { targetType = AccessTools.TypeByName(name); if (targetType != null) break; }
            if (targetType == null) { MelonLogger.Warning($"[HeadlessMode] Type not found for '{label ?? methodName}'."); return; }

            var method = AccessTools.Method(targetType, methodName);
            if (method == null) { MelonLogger.Warning($"[HeadlessMode] Method '{label ?? methodName}' not found on {targetType.FullName}."); return; }

            var prefixM = prefix != null ? new HarmonyMethod(typeof(HeadlessPatches), prefix) : null;
            var postfixM = postfix != null ? new HarmonyMethod(typeof(HeadlessPatches), postfix) : null;
            var finalizerM = finalizer != null ? new HarmonyMethod(typeof(HeadlessPatches), finalizer) : null;

            try
            {
                harmony.Patch(method, prefix: prefixM, postfix: postfixM, finalizer: finalizerM);
                MelonLogger.Msg($"[HeadlessMode] Patched '{label ?? $"{targetType.Name}.{methodName}"}'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Failed to patch '{label ?? methodName}': {ex.Message}"); }
        }

        /// <summary>
        /// Patches every method on the resolved type whose name starts with <paramref name="namePrefix"/>
        /// with the named finalizer. Used to attach one finalizer to all of a NetworkBehaviour's generated
        /// RpcReader___ methods at once — their hashed names (e.g. RpcReader___Cmd_CastLine___2166136261)
        /// can't be matched individually by AccessTools.Method.
        /// </summary>
        private static void TryPatchFinalizeByPrefix(HarmonyLib.Harmony harmony, string[] typeNames,
            string namePrefix, string finalizer, string prefix = null, string label = null)
        {
            Type targetType = null;
            foreach (var name in typeNames) { targetType = AccessTools.TypeByName(name); if (targetType != null) break; }
            if (targetType == null) { MelonLogger.Warning($"[HeadlessMode] Type not found for '{label ?? namePrefix}'."); return; }

            var finalizerM = finalizer != null ? new HarmonyMethod(typeof(HeadlessPatches), finalizer) : null;
            var prefixM = prefix != null ? new HarmonyMethod(typeof(HeadlessPatches), prefix) : null;
            int count = 0;
            foreach (var m in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name == null || !m.Name.StartsWith(namePrefix, StringComparison.Ordinal)) continue;
                try { harmony.Patch(m, prefix: prefixM, finalizer: finalizerM); count++; }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Failed to patch '{m.Name}': {ex.Message}"); }
            }
            MelonLogger.Msg($"[HeadlessMode] Patched {count} method(s) for '{label ?? $"{targetType.Name}.{namePrefix}*"}'.");
        }

        // Safety net for fishing server-side RPC readers. If a reader throws (e.g. some other null in the
        // fishing path beyond the SoundEffectManager.PlayClipAtPoint no-op), log the throwing method +
        // instance state and return null to swallow it — a throw inside a FishNet ServerRpc reader
        // otherwise makes FishNet kick the sending client.
        private static Exception Fishing_RpcReader_Finalizer(Exception __exception, MethodBase __originalMethod, Il2Cpp.FishingRod __instance)
        {
            if (!Application.isBatchMode) return __exception;
            if (__exception != null)
            {
                int nobId = -1; string scState = "?";
                try { if (__instance != null) { nobId = __instance.NetworkObject == null ? -1 : __instance.NetworkObject.ObjectId; var sc = __instance.sync_IsCasted; scState = sc == null ? "NULL" : "init=" + sc.IsInitialized; } } catch { }
                MelonLogger.Warning($"[HeadlessMode][FISH] {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name} threw (rod nob={nobId} sync_IsCasted={scState}): {__exception.GetType().Name}: {__exception.Message}");
                if (!string.IsNullOrEmpty(__exception.StackTrace))
                    MelonLogger.Warning($"[HeadlessMode][FISH] Stack: {__exception.StackTrace}");
                var inner = __exception.InnerException;
                if (inner != null)
                    MelonLogger.Warning($"[HeadlessMode][FISH] Inner: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
            }
            return null; // swallow so FishNet does not kick the client
        }
    }
}
