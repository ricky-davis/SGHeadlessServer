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
    internal static class HeadlessPatches
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
            MelonLogger.Msg("[HeadlessMode] v72 Applying headless suppression patches...");

            // Silence Harmony's per-patch "WARNING AccessTools.GetTypesFromAssembly" spam.
            // Every harmony.Patch() call internally scans assemblies; UnityEngine.CoreModule
            // has a broken IdentityAttributes type that always throws ReflectionTypeLoadException,
            // and Harmony logs the full exception. We patch the scanner itself to swallow it silently.
            SuppressHarmonyAssemblyScanWarnings(harmony);

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

        // ── Direct EOS C API for Device Auth ─────────────────────────────────────────
        // Il2CppInterop cannot bridge delegates whose parameters are non-blittable Il2Cpp
        // structs (the EOS callback info structs contain raw pointers), so we bypass the PEWS
        // managed wrappers entirely and call EOSSDK-Win64-Shipping.dll directly via P/Invoke.
        //
        // Struct layouts mirror *Internal variants from the Il2Cpp dump. Field offsets use
        // LayoutKind.Explicit because the EOS SDK packs structs with 8-byte alignment on
        // 64-bit Windows regardless of field size (e.g. int ApiVersion at 0x0, next ptr at 0x8).
        // Verified against dump.cs offsets.

        [StructLayout(LayoutKind.Explicit)]
        private struct EosCredentials
        {
            [FieldOffset(0x0)] public int ApiVersion;   // = 1
            [FieldOffset(0x8)] public IntPtr Token;     // null for Device Auth
            [FieldOffset(0x10)] public int Type;        // EOS_ECT_DEVICEID_ACCESS_TOKEN = 10
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct EosUserLoginInfo
        {
            [FieldOffset(0x0)] public int ApiVersion;   // = 2
            [FieldOffset(0x8)] public IntPtr DisplayName;
            [FieldOffset(0x10)] public IntPtr NsaIdToken; // null
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct EosConnectLoginOptions
        {
            [FieldOffset(0x0)] public int ApiVersion;   // = 2
            [FieldOffset(0x8)] public IntPtr Credentials;
            [FieldOffset(0x10)] public IntPtr UserLoginInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct EosCreateDeviceIdOptions
        {
            [FieldOffset(0x0)] public int ApiVersion;     // = 1
            [FieldOffset(0x8)] public IntPtr DeviceModel; // null = use default hardware fingerprint
        }

// LoginCallbackInfoInternal: ResultCode@0x0, ClientData@0x8, LocalUserId@0x10
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EosLoginRawCallback(IntPtr info);

        // CreateDeviceIdCallbackInfoInternal: ResultCode@0x0, ClientData@0x8
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EosCreateDeviceIdRawCallback(IntPtr info);

        // CreateUser options: ApiVersion@0x0, ContinuanceToken@0x8
        [StructLayout(LayoutKind.Explicit)]
        private struct EosCreateUserOptions
        {
            [FieldOffset(0x0)] public int ApiVersion;
            [FieldOffset(0x8)] public IntPtr ContinuanceToken;
        }

        // Static fields pin the delegates so the GC never collects them while the native EOS
        // callback table still holds a raw function pointer to them.
        private static readonly EosLoginRawCallback _pinnedLoginCb = OnEosLoginCallback;
        private static readonly EosCreateDeviceIdRawCallback _pinnedCreateDeviceCb = OnEosCreateDeviceIdCallback;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EosCreateUserRawCallback(IntPtr info);
        private static readonly EosCreateUserRawCallback _pinnedCreateUserCb = OnEosCreateUserCallback;

        // Shared state flags written by the P/Invoke callbacks and polled by the coroutine loop.
        // These are not thread-safe but EOS callbacks fire on the same Unity main thread (via
        // EOS SDK's tick mechanism inside EOSManager.Update), so no synchronisation is needed.
        private static bool _deviceAuthDone;
        private static bool _deviceAuthSuccess;
        private static bool _createDeviceIdDone;
        private static bool _createDeviceIdSuccess;
        private static bool _createUserDone;
        private static bool _createUserSuccess;
        // ContinuanceToken from a failed EOS_Connect_Login (EOS_InvalidUser) — EOS issues this
        // token so the caller can pass it straight to EOS_Connect_CreateUser without re-authenticating.
        private static IntPtr _loginContinuanceToken;

        // Raw ProductUserId handle from OnEosLoginCallback, used by the P/Invoke fallback path
        // to inject the PUID into PEWS when StartConnectLoginWithDeviceToken is unavailable.
        private static IntPtr _rawProductUserId = IntPtr.Zero;

        /// <summary>
        /// Native callback for <c>EOS_Connect_Login</c>.
        /// On success, captures the raw <c>ProductUserId</c> handle and signals the waiting
        /// coroutine. On first-time failure (<c>EOS_InvalidUser</c>), EOS sets <c>ContinuanceToken</c>
        /// instead of <c>LocalUserId</c> — we capture that too for the CreateUser retry path.
        /// Struct layout: ResultCode@0x0, ClientData@0x8, LocalUserId@0x10, ContinuanceToken@0x18.
        /// </summary>
        private static void OnEosLoginCallback(IntPtr info)
        {
            if (info == IntPtr.Zero) { _deviceAuthDone = true; return; }
            int raw = Marshal.ReadInt32(info, 0x0); // ResultCode
            var result = (Result)raw;
            _deviceAuthSuccess = result == Result.Success;
            if (_deviceAuthSuccess)
                _rawProductUserId = Marshal.ReadIntPtr(info, 0x10); // ProductUserId handle
            _loginContinuanceToken = Marshal.ReadIntPtr(info, 0x18); // ContinuanceToken (set on EOS_InvalidUser)
            MelonLogger.Msg($"[HeadlessMode] EOS_Connect_Login callback: {result}");
            _deviceAuthDone = true;
        }

        /// <summary>
        /// Native callback for <c>EOS_Connect_CreateDeviceId</c>.
        /// <c>DuplicateNotAllowed</c> (24) means a DeviceId credential already exists on this
        /// machine — this is not an error; treat it the same as Success and proceed to login.
        /// Raw value 1004 is an undocumented variant seen in some SDK versions for the same condition.
        /// </summary>
        private static void OnEosCreateDeviceIdCallback(IntPtr info)
        {
            if (info == IntPtr.Zero) { _createDeviceIdDone = true; return; }
            int raw = Marshal.ReadInt32(info, 0x0);
            var result = (Result)raw;
            // DuplicateNotAllowed (24) = device ID already exists on this machine — also a success path
            _createDeviceIdSuccess = result == Result.Success || result == Result.DuplicateNotAllowed || raw == 1004;
            MelonLogger.Msg($"[HeadlessMode] EOS_Connect_CreateDeviceId callback: {result}");
            _createDeviceIdDone = true;
        }

        // Native callback for EOS_Connect_CreateUser — called when we create a new EOS Connect
        // account for this machine's DeviceId on first ever login.
        private static void OnEosCreateUserCallback(IntPtr info)
        {
            if (info == IntPtr.Zero) { _createUserDone = true; return; }
            int raw = Marshal.ReadInt32(info, 0x0);
            var result = (Result)raw;
            _createUserSuccess = result == Result.Success;
            MelonLogger.Msg($"[HeadlessMode] EOS_Connect_CreateUser callback: {result}");
            _createUserDone = true;
        }

        /// <summary>
        /// Calls <c>EOS_Connect_Login</c> with <c>EOS_ECT_DEVICEID_ACCESS_TOKEN</c> credentials.
        ///
        /// Structs are allocated in unmanaged memory rather than declared as local value types
        /// because C# iterators (yield-based coroutines) cannot take the address of locals —
        /// they live on the heap as fields of the compiler-generated state machine class, which
        /// moves them unpredictably. Unmanaged allocations have a fixed address for the duration
        /// of the call. They are freed in the finally block before returning.
        /// </summary>
        private static void CallEosDeviceLogin(IntPtr handle, IntPtr displayNamePtr, IntPtr loginCbPtr)
        {
            IntPtr credsPtr  = Marshal.AllocHGlobal(24);  // EosCredentials: ApiVersion@0, Token@8, Type@16
            IntPtr userPtr   = Marshal.AllocHGlobal(24);  // EosUserLoginInfo: ApiVersion@0, DisplayName@8, NsaIdToken@16
            IntPtr optsPtr   = Marshal.AllocHGlobal(24);  // EosConnectLoginOptions: ApiVersion@0, Creds@8, User@16
            try
            {
                Marshal.WriteInt32(credsPtr, 0x0, 1);          // ApiVersion
                Marshal.WriteIntPtr(credsPtr, 0x8, IntPtr.Zero); // Token = null
                Marshal.WriteInt32(credsPtr, 0x10, 10);         // Type = DeviceId

                Marshal.WriteInt32(userPtr, 0x0, 2);           // ApiVersion
                Marshal.WriteIntPtr(userPtr, 0x8, displayNamePtr);
                Marshal.WriteIntPtr(userPtr, 0x10, IntPtr.Zero);

                Marshal.WriteInt32(optsPtr, 0x0, 2);           // ApiVersion
                Marshal.WriteIntPtr(optsPtr, 0x8, credsPtr);
                Marshal.WriteIntPtr(optsPtr, 0x10, userPtr);

                var opts = new EosConnectLoginOptions
                {
                    ApiVersion = 2,
                    Credentials = credsPtr,
                    UserLoginInfo = userPtr
                };
                EosSdkLogin(handle, ref opts, IntPtr.Zero, loginCbPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(credsPtr);
                Marshal.FreeHGlobal(userPtr);
                Marshal.FreeHGlobal(optsPtr);
            }
        }

        // Calls EOS_Connect_CreateDeviceId to register this machine's device credential.
        // DeviceModel=null tells the SDK to use its built-in device fingerprint (hardware ID).
        private static void CallEosCreateDeviceId(IntPtr handle, IntPtr createCbPtr)
        {
            var opts = new EosCreateDeviceIdOptions { ApiVersion = 1, DeviceModel = IntPtr.Zero };
            EosSdkCreateDeviceId(handle, ref opts, IntPtr.Zero, createCbPtr);
        }

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Connect_Login(IntPtr handle, ref EosConnectLoginOptions options, IntPtr clientData, IntPtr completionDelegate);

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Connect_CreateDeviceId(IntPtr handle, ref EosCreateDeviceIdOptions options, IntPtr clientData, IntPtr completionDelegate);

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Connect_CreateUser(IntPtr handle, ref EosCreateUserOptions options, IntPtr clientData, IntPtr completionDelegate);

        // ── EOS Auth service P/Invoke ─────────────────────────────────────────────────
        // EOS_Auth_Login obtains an Epic account-level session (EpicAccountId).
        // Auth.CredentialsInternal layout (from dump): ApiVersion@0x0, Id@0x8, Token@0x10,
        //   Type@0x18 (LoginCredentialType), SystemAuthCredentialsOptions@0x20, ExternalType@0x28
        // Auth.LoginOptionsInternal layout: ApiVersion@0x0, Credentials@0x8, ScopeFlags@0x10, LoginFlags@0x18
        // Auth.CopyIdTokenOptionsInternal: ApiVersion@0x0, AccountId@0x8
        // Auth.IdTokenInternal: ApiVersion@0x0, AccountId@0x8, JsonWebToken@0x10

        [StructLayout(LayoutKind.Explicit, Size = 0x30)]
        private struct EosAuthCredentials
        {
            [FieldOffset(0x0)]  public int ApiVersion;    // = 1
            [FieldOffset(0x8)]  public IntPtr Id;         // null for PersistentAuth / ExternalAuth+Epic
            [FieldOffset(0x10)] public IntPtr Token;      // null for PersistentAuth; OAuth token for ExternalAuth
            [FieldOffset(0x18)] public int Type;          // LoginCredentialType: 2=PersistentAuth, 7=ExternalAuth
            [FieldOffset(0x20)] public IntPtr SystemAuthCredentialsOptions; // null
            [FieldOffset(0x28)] public int ExternalType;  // ExternalCredentialType: 0=Epic (only for ExternalAuth)
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x20)]
        private struct EosAuthLoginOptions
        {
            [FieldOffset(0x0)]  public int ApiVersion;   // = 2
            [FieldOffset(0x8)]  public IntPtr Credentials;
            [FieldOffset(0x10)] public int ScopeFlags;   // AuthScopeFlags = 0
            [FieldOffset(0x18)] public int LoginFlags;   // = 0
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x10)]
        private struct EosAuthCopyIdTokenOptions
        {
            [FieldOffset(0x0)] public int ApiVersion;   // = 1
            [FieldOffset(0x8)] public IntPtr AccountId; // EpicAccountId raw handle
        }

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Auth_Login(IntPtr handle, ref EosAuthLoginOptions options, IntPtr clientData, IntPtr completionDelegate);

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern int EOS_Auth_CopyIdToken(IntPtr handle, ref EosAuthCopyIdTokenOptions options, out IntPtr outIdToken);

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Auth_IdToken_Release(IntPtr idToken);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EosAuthLoginRawCallback(IntPtr info);
        private static readonly EosAuthLoginRawCallback _pinnedAuthLoginCb = OnEosAuthLoginCallback;

        private static bool _authLoginDone;
        private static bool _authLoginSuccess;
        private static IntPtr _authLocalUserId; // EpicAccountId raw handle from auth callback

        // Auth LoginCallbackInfoInternal: ResultCode@0x0, ClientData@0x8, LocalUserId@0x10, ...
        private static void OnEosAuthLoginCallback(IntPtr info)
        {
            if (info == IntPtr.Zero) { _authLoginDone = true; return; }
            int raw = Marshal.ReadInt32(info, 0x0);
            var result = (Result)raw;
            _authLoginSuccess = result == Result.Success;
            if (_authLoginSuccess)
                _authLocalUserId = Marshal.ReadIntPtr(info, 0x10); // LocalUserId (EpicAccountId)
            MelonLogger.Msg($"[HeadlessMode] EOS_Auth_Login callback: {result}");
            _authLoginDone = true;
        }

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern int EOS_ProductUserId_ToString(IntPtr accountId, IntPtr outBuffer, ref int inOutBufferLength);

        /// <summary>
        /// Bridges the gap between a raw native EOS <c>ProductUserId</c> pointer (obtained from
        /// our P/Invoke callback) and the PEWS managed layer that <c>CreateLobby</c> relies on.
        ///
        /// Why this two-step conversion is required:
        ///   - P/Invoke callbacks give us a raw <c>IntPtr</c> — an opaque EOS handle.
        ///   - PEWS's <c>EOSManager.GetProductUserId()</c> returns a managed <c>ProductUserId</c>
        ///     object that was created by PEWS's own callback path, which we bypassed.
        ///   - <c>ProductUserId.FromString</c> is the only public way to construct a managed
        ///     instance without going through PEWS's normal login flow.
        ///   - After construction we push the value into PEWS via <c>SetLocalProductUserId</c>
        ///     (method) and <c>s_localProductUserId</c> (field) as belt-and-suspenders, because
        ///     the exact storage location varies across PEWS versions.
        /// </summary>
        private static bool InjectPuidIntoEosManager(IntPtr rawPuid)
        {
            if (rawPuid == IntPtr.Zero) return false;

            // Step 1: native handle → PUID string
            int bufLen = 128;
            IntPtr buf = Marshal.AllocHGlobal(bufLen);
            int toRes = EOS_ProductUserId_ToString(rawPuid, buf, ref bufLen);
            string puidStr = toRes == 0 ? Marshal.PtrToStringAnsi(buf) : null;
            Marshal.FreeHGlobal(buf);

            if (string.IsNullOrEmpty(puidStr)) { MelonLogger.Warning("[HeadlessMode] EOS_ProductUserId_ToString failed."); return false; }
            MelonLogger.Msg($"[HeadlessMode] PUID string: {puidStr}");

            // Step 2: string → managed ProductUserId
            ProductUserId puid = null;
            try { puid = ProductUserId.FromString(puidStr); }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] ProductUserId.FromString: {ex.Message}"); return false; }
            if (puid == null || !puid.IsValid()) { MelonLogger.Warning("[HeadlessMode] ProductUserId invalid after FromString."); return false; }

            // Step 3: inject into EOSManager via SetLocalProductUserId (protected method on EOSSingleton)
            Type singletonType = null;
            object singletonObj = null;
            try
            {
                singletonType = AccessTools.TypeByName("Il2CppPlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton")
                             ?? AccessTools.TypeByName("Il2CppPlayEveryWare.EpicOnlineServices.EOSManager");
                // EOSManager.Instance is the EOSSingleton itself in this version of PEWS
                singletonObj = EOSManager.Instance;
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] InjectPuid: get singleton: {ex.Message}"); }

            if (singletonType == null || singletonObj == null)
            {
                MelonLogger.Warning("[HeadlessMode] InjectPuid: could not find EOSManager singleton.");
                return false;
            }

            var setMethod = AccessTools.Method(singletonType, "SetLocalProductUserId")
                         ?? AccessTools.Method(typeof(EOSManager), "SetLocalProductUserId");
            if (setMethod != null)
            {
                try { setMethod.Invoke(singletonObj, new object[] { puid }); }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] SetLocalProductUserId invoke: {ex.Message}"); }
            }

            // Also try direct field set as fallback
            var field = singletonType.GetField("s_localProductUserId",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null)
            {
                try { field.SetValue(null, puid); }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] s_localProductUserId field set: {ex.Message}"); }
            }

            MelonLogger.Msg($"[HeadlessMode] PUID injected into EOSManager (method={setMethod != null}, field={field != null}).");
            return true;
        }

        // Thin wrappers so callers don't have to pass ref on every site and the iterator
        // restriction on unsafe/ref locals doesn't block coroutine code from calling these.
        private static void EosSdkLogin(IntPtr handle, ref EosConnectLoginOptions options, IntPtr clientData, IntPtr cb)
            => EOS_Connect_Login(handle, ref options, clientData, cb);

        private static void EosSdkCreateDeviceId(IntPtr handle, ref EosCreateDeviceIdOptions options, IntPtr clientData, IntPtr cb)
            => EOS_Connect_CreateDeviceId(handle, ref options, clientData, cb);

        private static void EosSdkCreateUser(IntPtr handle, IntPtr continuanceToken, IntPtr cb)
        {
            var opts = new EosCreateUserOptions { ApiVersion = 1, ContinuanceToken = continuanceToken };
            EOS_Connect_CreateUser(handle, ref opts, IntPtr.Zero, cb);
        }

        // ── EOS Auth + EpicIdToken Connect flow ──────────────────────────────────────

        /// <summary>
        /// Fallback auth path: obtain an <c>EpicAccountId</c> via the EOS Auth service, then
        /// exchange it for an EOS Connect <c>ProductUserId</c> using the <c>EpicIdToken</c>
        /// credential type. Used when DeviceId auth is unavailable (product configuration).
        ///
        /// <paramref name="authType"/>:
        ///   2 = <c>PersistentAuth</c> — uses a locally-cached refresh token from a previous
        ///       Epic account login; no credentials needed. Silent on servers that logged in once.
        ///   7 = <c>ExternalAuth+Epic</c> — pass an OAuth access_token as <paramref name="tokenPtr"/>.
        ///
        /// The two-step flow (Auth → Connect) is required because the Connect service only accepts
        /// <c>EpicIdToken</c> (a JWT signed by Epic's Auth service), not raw credentials.
        /// </summary>
        private static IEnumerator TryEosAuthAndConnect(IntPtr authHandle, ConnectInterface connectIface, int authType, IntPtr tokenPtr, float authTimeoutSecs = 30f)
        {
            IntPtr credsPtr = Marshal.AllocHGlobal(0x30);
            for (int i = 0; i < 0x30; i++) Marshal.WriteByte(credsPtr, i, 0);
            Marshal.WriteInt32(credsPtr, 0x0, 1);           // ApiVersion
            Marshal.WriteIntPtr(credsPtr, 0x10, tokenPtr);  // Token (null for PersistentAuth)
            Marshal.WriteInt32(credsPtr, 0x18, authType);   // Type (LoginCredentialType)
            // ExternalType stays 0 = Epic

            var opts = new EosAuthLoginOptions { ApiVersion = 2, Credentials = credsPtr, ScopeFlags = 0, LoginFlags = 0 };
            IntPtr authCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedAuthLoginCb);
            _authLoginDone = false;
            _authLoginSuccess = false;
            _authLocalUserId = IntPtr.Zero;
            EOS_Auth_Login(authHandle, ref opts, IntPtr.Zero, authCbPtr);
            Marshal.FreeHGlobal(credsPtr);
            MelonLogger.Msg($"[HeadlessMode] EOS_Auth_Login (type={authType}) sent...");

            float t = Time.realtimeSinceStartup;
            while (!_authLoginDone && Time.realtimeSinceStartup - t < authTimeoutSecs)
                yield return new WaitForSecondsRealtime(0.25f);

            if (!_authLoginDone) { MelonLogger.Warning("[HeadlessMode] EOS_Auth_Login callback never fired."); yield break; }
            if (!_authLoginSuccess) yield break;

            // Auth succeeded — copy ID token (JWT) for use with EOS_Connect_Login
            var copyOpts = new EosAuthCopyIdTokenOptions { ApiVersion = 1, AccountId = _authLocalUserId };
            int copyRes = EOS_Auth_CopyIdToken(authHandle, ref copyOpts, out IntPtr idTokenPtr);
            MelonLogger.Msg($"[HeadlessMode] EOS_Auth_CopyIdToken: result={copyRes} ptr={idTokenPtr}");
            if (copyRes != 0 || idTokenPtr == IntPtr.Zero) yield break;

            IntPtr jwtStrPtr = Marshal.ReadIntPtr(idTokenPtr, 0x10); // JsonWebToken@0x10
            string jwt = jwtStrPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(jwtStrPtr) : null;
            EOS_Auth_IdToken_Release(idTokenPtr);

            if (string.IsNullOrEmpty(jwt)) { MelonLogger.Warning("[HeadlessMode] JWT empty after CopyIdToken."); yield break; }
            MelonLogger.Msg($"[HeadlessMode] EpicIdToken obtained (len={jwt.Length}), connecting...");

            yield return TryConnectWithEpicIdToken(connectIface, jwt);
        }

        // Second half of TryEosAuthAndConnect: sends EOS_Connect_Login with the JWT obtained
        // from EOS_Auth_CopyIdToken. Handles the first-time case (EOS_InvalidUser) by calling
        // EOS_Connect_CreateUser with the ContinuanceToken before retrying the login.
        private static IEnumerator TryConnectWithEpicIdToken(ConnectInterface connectIface, string jwt)
        {
            IntPtr handle = connectIface.InnerHandle;
            IntPtr loginCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedLoginCb);
            IntPtr jwtPtr = Marshal.StringToHGlobalAnsi(jwt);

            _deviceAuthDone = false;
            _deviceAuthSuccess = false;
            _loginContinuanceToken = IntPtr.Zero;

            IntPtr credsPtr = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(credsPtr, 0x0, 1);          // ApiVersion
                Marshal.WriteIntPtr(credsPtr, 0x8, jwtPtr);    // Token = JWT
                Marshal.WriteInt32(credsPtr, 0x10, 16);        // Type = EpicIdToken
                var connectOpts = new EosConnectLoginOptions { ApiVersion = 2, Credentials = credsPtr, UserLoginInfo = IntPtr.Zero };
                EosSdkLogin(handle, ref connectOpts, IntPtr.Zero, loginCbPtr);
                MelonLogger.Msg("[HeadlessMode] EOS_Connect_Login (EpicIdToken) sent...");
            }
            finally { Marshal.FreeHGlobal(credsPtr); Marshal.FreeHGlobal(jwtPtr); }

            float t = Time.realtimeSinceStartup;
            while (!_deviceAuthDone && Time.realtimeSinceStartup - t < 30f)
                yield return new WaitForSecondsRealtime(0.25f);

            if (!_deviceAuthDone) { MelonLogger.Warning("[HeadlessMode] Connect_Login (EpicIdToken) callback never fired."); yield break; }
            if (_deviceAuthSuccess) yield break;

            if (_loginContinuanceToken == IntPtr.Zero) { MelonLogger.Warning("[HeadlessMode] EpicIdToken login failed, no continuance token."); yield break; }

            // First-time login — create a Connect user account, then retry
            MelonLogger.Msg("[HeadlessMode] EOS_Connect_CreateUser (EpicIdToken first-time)...");
            IntPtr createUserCb = Marshal.GetFunctionPointerForDelegate(_pinnedCreateUserCb);
            _createUserDone = false;
            _createUserSuccess = false;
            EosSdkCreateUser(handle, _loginContinuanceToken, createUserCb);

            t = Time.realtimeSinceStartup;
            while (!_createUserDone && Time.realtimeSinceStartup - t < 15f)
                yield return new WaitForSecondsRealtime(0.25f);

            if (!_createUserSuccess) yield break;

            jwtPtr = Marshal.StringToHGlobalAnsi(jwt);
            _deviceAuthDone = false; _deviceAuthSuccess = false; _loginContinuanceToken = IntPtr.Zero;
            credsPtr = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(credsPtr, 0x0, 1);
                Marshal.WriteIntPtr(credsPtr, 0x8, jwtPtr);
                Marshal.WriteInt32(credsPtr, 0x10, 16);
                var retryOpts = new EosConnectLoginOptions { ApiVersion = 2, Credentials = credsPtr, UserLoginInfo = IntPtr.Zero };
                EosSdkLogin(handle, ref retryOpts, IntPtr.Zero, loginCbPtr);
                MelonLogger.Msg("[HeadlessMode] EOS_Connect_Login (EpicIdToken) retry after CreateUser...");
            }
            finally { Marshal.FreeHGlobal(credsPtr); Marshal.FreeHGlobal(jwtPtr); }

            t = Time.realtimeSinceStartup;
            while (!_deviceAuthDone && Time.realtimeSinceStartup - t < 30f)
                yield return new WaitForSecondsRealtime(0.25f);
        }

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
        }

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

        // Player NetworkObjects whose footstep value we have already force-re-sent to clients.
        private static readonly HashSet<int> _footstepResentNobs = new();

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
                                if (_footstepResentNobs.Contains(nobId)) continue;
                                var sv = mv.sync_CurrentFootstepCollection;
                                if (sv == null || sv.Value == Il2Cpp.FootstepCollectionType.None) continue; // wait for a real surface
                                resendQueue.Add(mv);
                            }
                            catch { }
                        }
                    }

                    // Forget players who have LEFT: FishNet pools NetworkObjects, so a rejoining player can
                    // REUSE the same object (same GetInstanceID). If we kept its id, the re-send would be
                    // skipped and the rejoiner's footstep would never replicate (until they restart their
                    // client). Keep only ids still spawned, so a reused-on-rejoin object is treated as fresh
                    // and re-sent again.
                    _footstepResentNobs.IntersectWith(spawnedNobIds);
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
                        if (nob != null) _footstepResentNobs.Add(nob.GetInstanceID());
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

            // Also drive through the official UI codepath that normally fires when the host
            // clicks "Yes" in the setup dialog — UiReferenceController.YesNo_TextChat/VoiceChat.
            try
            {
                var uiRef = Il2Cpp.UiReferenceController.Instance;
                if (uiRef != null)
                {
                    uiRef.YesNo_TextChat(true);
                    uiRef.YesNo_VoiceChat(true);
                    MelonLogger.Msg("[HeadlessMode] Called YesNo_TextChat(true) + YesNo_VoiceChat(true).");
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] EnableHeadlessChat (YesNo): {ex.Message}"); }
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

        /// <summary>
        /// Coroutine: continuously enforces audio silence every frame for the lifetime of the process.
        ///
        /// Why continuous enforcement is necessary: the game's settings system re-applies the saved
        /// <c>MasterVolume</c> value when the main scene finishes loading (~40s after boot, because
        /// the boot bypass lets the main scene load). This overwrites a one-time mute set at startup.
        /// Reasserting <c>AudioListener.volume=0</c> and <c>AudioListener.pause=true</c> every frame
        /// ensures the game can never un-mute the headless. <c>AudioListener</c> is a global Unity
        /// static — setting it once affects all audio in the process (no FMOD/Wwise on this game).
        /// The cost is negligible: two property writes per frame.
        /// </summary>
        private static IEnumerator SilenceAudio()
        {
            // The game's settings system applies the saved MasterVolume and un-pauses audio when the
            // main scene loads (~40s in, now that the boot bypass lets it load). A one-time mute is
            // overridden, so re-assert AudioListener.volume=0 + pause every frame. It's a global
            // Unity static (no FMOD/Wwise here), so this guarantees silence. Cheap on a headless server.
            bool logged = false;
            while (!_isQuitting)
            {
                try
                {
                    if (AudioListener.volume != 0f) AudioListener.volume = 0f;
                    if (!AudioListener.pause) AudioListener.pause = true;
                    if (!logged) { MelonLogger.Msg("[HeadlessMode] Audio muted (continuous enforcement)."); logged = true; }
                }
                catch { }
                yield return null;
            }
        }

        /// <summary>
        /// Coroutine: ensure <c>SoundEffectManager.Instance</c> is non-null on the headless server.
        ///
        /// On a normal (client-host) game the SoundEffectManager is part of a persistent-managers prefab
        /// instantiated in the main-menu flow; the headless boot skips that flow, so the manager is never
        /// created and its static <c>Instance</c> stays null (confirmed live: zero SoundEffectManager
        /// components in the scene). That null is the root cause of two client-facing bugs:
        ///   • Fishing: FishingRod.CheckCastLineOnAllPlayers → SoundEffectManager.PlayClipAtPoint derefs
        ///     the null Instance inside a ServerRpc reader → FishNet kicks the caster.
        ///   • Statues: StatueUnlockSystem.OnTargetsHitChanged (the _targetsHit SyncVar OnChange) reads
        ///     <c>SoundEffectManager.Instance.statueTargetPracticeHitSound</c> to feed PlayClipAtPoint.
        ///     The deref of the null Instance throws, aborting the OnChange — so the server never runs the
        ///     statue's completion logic and the statue's interactable never activates (the snowball targets
        ///     still react, because that visual is a separate server→client TargetRpc).
        ///
        /// PlayClipAtPoint is also no-op'd in headless (see ApplyPatches), so the stub's null SoundEffectSO
        /// fields are never dereferenced — every read of them feeds the skipped PlayClipAtPoint. Combined,
        /// the manager exists (Instance non-null, OnChange handlers complete) but produces no audio.
        /// Verified live via /eval: with the stub present, completing all targets sets HasCompletedAllTargets
        /// and IsInteractableEnabled true with no NRE.
        /// </summary>
        private static IEnumerator EnsureSoundEffectManagerInstance()
        {
            yield return null;
            if (!Application.isBatchMode) yield break;

            for (int attempt = 0; attempt < 20 && !_isQuitting; attempt++)
            {
                bool done = false;
                try
                {
                    // If the real manager ever shows up, leave it alone.
                    if (SoundEffectManager.Instance != null)
                        yield break;

                    var go = new GameObject("HeadlessSoundEffectManager");
                    Object.DontDestroyOnLoad(go);
                    var comp = go.AddComponent<SoundEffectManager>();

                    // SoundEffectManager.Awake (which normally does `Instance = this`) is suppressed in
                    // headless, so assign the singleton ourselves. The stub's serialized clip/audio-source
                    // fields stay null — harmless, because PlayClipAtPoint is no-op'd in headless.
                    if (SoundEffectManager.Instance == null)
                        SetSoundEffectManagerInstance(comp);

                    if (SoundEffectManager.Instance != null)
                    {
                        MelonLogger.Msg("[HeadlessMode] Created silent SoundEffectManager stub " +
                                        "(Instance was null; needed by statue/fishing positional-sound OnChange handlers).");
                        done = true;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[HeadlessMode] SoundEffectManager stub attempt {attempt} failed: {ex.GetType().Name}: {ex.Message}");
                }

                if (done) yield break;
                yield return new WaitForSeconds(1f);
            }
        }

        /// <summary>
        /// Assigns the <c>SoundEffectManager.Instance</c> singleton via reflection. The Il2CppInterop
        /// property setter writes the native static backing field; a direct managed assignment may not be
        /// accessible depending on the generated accessibility, so we go through reflection (with a backing
        /// field fallback) — both proven to write native memory correctly.
        /// </summary>
        private static void SetSoundEffectManagerInstance(SoundEffectManager comp)
        {
            var t = typeof(SoundEffectManager);
            var prop = t.GetProperty("Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(null, comp);
                return;
            }
            var field = t.GetField("_003CInstance_003Ek__BackingField",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                field.SetValue(null, comp);
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
