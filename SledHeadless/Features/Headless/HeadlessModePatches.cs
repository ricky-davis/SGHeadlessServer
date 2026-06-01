using System;
using System.Collections;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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
            MelonLogger.Msg("[HeadlessMode] v43 Applying headless suppression patches...");

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

            // ── Audio suppression ─────────────────────────────────────────────────────
            // -nographics does NOT suppress audio. Silence the game's audio managers.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp_Scripts.SoundEffectManager", "Il2Cpp.SoundEffectManager" },
                methodName: "Awake",
                prefix: nameof(SkipInHeadless),
                label: "SoundEffectManager.Awake");

            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp_Scripts.MusicController", "Il2Cpp.MusicController" },
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

            // steamclient64.dll loads later (after Unity's Steam integration wakes up).
            // Poll for it in a coroutine and hook SteamInternal_SetMinidumpSteamID the
            // moment it's available — before it can register this process with Steam.
            MelonCoroutines.Start(HookSteamClientWhenLoaded());

            // Enable the non-Steam NetworkManager immediately so FishNet registers with
            // InstanceFinder before CreateLobby is called. In headless without Steam we
            // want the KCP/Tugboat or EOS P2P NetworkManager, not the Steam one.
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
        private static bool SkipInHeadless() => !Application.isBatchMode;

        /// <summary>
        /// Harmony finalizer that silently swallows any exception thrown by the patched method,
        /// but only in headless. Used on <c>UiReferenceController.Update</c> which continuously
        /// polls UI state that is never initialized in batch mode, producing a wall of NullRef spam.
        /// Returning null from a finalizer tells Harmony "no exception to propagate."
        /// </summary>
        private static Exception SuppressNullRefInHeadless(Exception __exception)
        {
            if (!Application.isBatchMode) return __exception;
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

        // SteamInternal_SetMinidumpSteamID tells Steam which account owns this process.
        // It's called from within the Breakpad crash handler setup. Hooking the entry point
        // (SteamAPI_UseBreakpadCrashHandler) as a no-op prevents it ever being called.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetMinidumpSteamIdDelegate(ulong steamId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void UseBreakpadCrashHandlerDelegate(
            IntPtr pchVersion, IntPtr pchDate, IntPtr pchTime, int bFullMemoryDumps, IntPtr pvContext, IntPtr m_pfnPreMinidumpCallback);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetBreakpadAppIdDelegate(uint appId);

        // Breakpad_SteamSetSteamID is the direct setter that registers the process SteamID
        // with the Breakpad crash reporter — the upstream caller of SteamInternal_SetMinidumpSteamID.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void BreakpadSetSteamIdDelegate(ulong steamId);

        // Holds every NativeHook object and its associated delegate so the GC cannot collect
        // them. A collected delegate whose function pointer is still installed in the native
        // hook table causes a crash on the next native call through that hook.
        private static readonly System.Collections.Generic.List<object> _steamIdHooks = new();

        // Handle to steam_api64.dll — loaded explicitly so we can call GetProcAddress on it
        // for hooking and (optionally) for calling SteamAPI_Shutdown without a static import.
        private static IntPtr _steamApiModule = IntPtr.Zero;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void VoidDelegate();

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
            [FieldOffset(0x0)] public int ApiVersion;   // = 1
            [FieldOffset(0x8)] public IntPtr DeviceModel; // null = use default
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

        /// <summary>
        /// Coroutine: attempt EOS DeviceId login, creating the device credential first if needed.
        /// This is the self-contained fallback path used when PEWS's
        /// <c>StartConnectLoginWithDeviceToken</c> is not available via reflection.
        ///
        /// Flow:
        ///   1. Send <c>EOS_Connect_Login</c> with DeviceId credentials.
        ///   2. If login fails with <c>EOS_InvalidUser</c>: create device ID, then retry login.
        ///   3. Leave <c>_deviceAuthSuccess</c> and <c>_rawProductUserId</c> for the caller.
        /// </summary>
        private static IEnumerator TryDeviceAuth(Il2CppEpic.OnlineServices.Connect.ConnectInterface connectInterface)
        {
            IntPtr handle     = connectInterface.InnerHandle;
            IntPtr loginCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedLoginCb);
            IntPtr createCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedCreateDeviceCb);
            IntPtr displayNamePtr = Marshal.StringToHGlobalAnsi("HeadlessServer");

            _deviceAuthDone = false;
            _deviceAuthSuccess = false;
            CallEosDeviceLogin(handle, displayNamePtr, loginCbPtr);
            MelonLogger.Msg("[HeadlessMode] EOS_Connect_Login (Device Auth) sent — waiting...");

            float t = Time.realtimeSinceStartup;
            while (!_deviceAuthDone && Time.realtimeSinceStartup - t < 15f)
                yield return new WaitForSecondsRealtime(0.25f);

            if (!_deviceAuthDone) { MelonLogger.Warning("[HeadlessMode] Login callback never fired."); Marshal.FreeHGlobal(displayNamePtr); yield break; }
            if (_deviceAuthSuccess) { Marshal.FreeHGlobal(displayNamePtr); yield break; }

            // EOS_InvalidUser means no device ID — create it then retry.
            MelonLogger.Msg("[HeadlessMode] No device ID — calling EOS_Connect_CreateDeviceId...");
            _createDeviceIdDone = false;
            _createDeviceIdSuccess = false;
            CallEosCreateDeviceId(handle, createCbPtr);

            t = Time.realtimeSinceStartup;
            while (!_createDeviceIdDone && Time.realtimeSinceStartup - t < 15f)
                yield return new WaitForSecondsRealtime(0.25f);

            if (!_createDeviceIdDone || !_createDeviceIdSuccess)
            {
                MelonLogger.Warning("[HeadlessMode] CreateDeviceId failed.");
                Marshal.FreeHGlobal(displayNamePtr);
                yield break;
            }

            // Retry login after device ID created.
            _deviceAuthDone = false;
            _deviceAuthSuccess = false;
            CallEosDeviceLogin(handle, displayNamePtr, loginCbPtr);
            MelonLogger.Msg("[HeadlessMode] EOS_Connect_Login retry after CreateDeviceId...");

            t = Time.realtimeSinceStartup;
            while (!_deviceAuthDone && Time.realtimeSinceStartup - t < 15f)
                yield return new WaitForSecondsRealtime(0.25f);

            Marshal.FreeHGlobal(displayNamePtr);
        }

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Connect_Login(IntPtr handle, ref EosConnectLoginOptions options, IntPtr clientData, IntPtr completionDelegate);

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Connect_CreateDeviceId(IntPtr handle, ref EosCreateDeviceIdOptions options, IntPtr clientData, IntPtr completionDelegate);

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Connect_CreateUser(IntPtr handle, ref EosCreateUserOptions options, IntPtr clientData, IntPtr completionDelegate);

        // ── EOSLobbyManager OnConnectLogin trigger ────────────────────────────────────

        /// <summary>
        /// Manually fires <c>EOSLobbyManager.OnConnectLogin</c> with our PUID so that PEWS's
        /// internal lobby manager initializes its <c>LocalProductUserId</c> and subscribes to
        /// EOS lobby notification handles.
        ///
        /// Why: In the normal game flow, <c>EOSManager.EOSSingleton</c> calls
        /// <c>OnConnectLogin</c> automatically after a successful <c>EOS_Connect_Login</c> via
        /// its own callback chain. When we bypass that chain (P/Invoke direct login or
        /// StartConnectLoginWithDeviceToken without the standard callback wiring), the
        /// <c>EOSLobbyManager</c> never receives the login event and its internal state stays
        /// uninitialized — causing NullRefs inside CreateLobby. This method synthesizes that
        /// event with the correct PUID so the manager bootstraps itself properly.
        /// </summary>
        private static void FireEosLobbyManagerOnConnectLogin(EOSLobbyManager lobbyMgr)
        {
            if (lobbyMgr == null) return;

            // Get ProductUserId from PEWS (set by StartConnectLoginWithDeviceToken or InjectPuid)
            ProductUserId puid = null;
            try { puid = EOSManager.Instance?.GetProductUserId(); } catch { }

            if (puid == null || !puid.IsValid())
            {
                MelonLogger.Warning("[HeadlessMode] FireOnConnectLogin: no valid PUID from EOSManager.");
                return;
            }

            // Build a Connect.LoginCallbackInfo with ResultCode=Success and our LocalUserId
            try
            {
                var callbackType = typeof(Il2CppEpic.OnlineServices.Connect.LoginCallbackInfo);
                var callbackInfo = (Il2CppEpic.OnlineServices.Connect.LoginCallbackInfo)
                    Activator.CreateInstance(callbackType);

                // Set ResultCode = Success (0) and LocalUserId = our puid
                callbackType.GetProperty("ResultCode",
                    BindingFlags.Instance | BindingFlags.Public)
                    ?.SetValue(callbackInfo, Result.Success);
                callbackType.GetProperty("LocalUserId",
                    BindingFlags.Instance | BindingFlags.Public)
                    ?.SetValue(callbackInfo, puid);

                var onConnectLogin = AccessTools.Method(typeof(EOSLobbyManager), "OnConnectLogin");
                if (onConnectLogin != null)
                {
                    onConnectLogin.Invoke(lobbyMgr, new object[] { callbackInfo });
                    MelonLogger.Msg("[HeadlessMode] EOSLobbyManager.OnConnectLogin fired with PUID.");
                }
                else
                    MelonLogger.Warning("[HeadlessMode] EOSLobbyManager.OnConnectLogin method not found.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] FireOnConnectLogin: {ex.GetType().Name}: {ex.Message}"); }
        }

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

        // ── EOS OAuth client-credentials flow ────────────────────────────────────────

        /// <summary>
        /// Fetches a service-level OAuth bearer token using the game's bundled ClientId and
        /// ClientSecret against the EOS OAuth endpoint. The resulting <c>access_token</c> can
        /// be used as an <c>EOS_ECT_OPENID_ACCESS_TOKEN</c> or similar credential type in
        /// <c>EOS_Connect_Login</c>.
        ///
        /// Note: This is a client-credentials grant (machine-to-machine), not a user login.
        /// It authenticates the application, not a player. Some EOS product configurations
        /// do not allow client_credentials for Connect login — DeviceId is preferred.
        /// </summary>
        private static async Task<string> FetchEosClientToken()
        {
            const string clientId     = "xyza7891WyWUCOssWbPLjEm5PeZ2JcTC";
            const string clientSecret = "58F6NQ5uGIMsa6dxQiYmzggu9yn8thzlI6hutGIP2Qk";
            const string deploymentId = "2e613563d52a48e59968157fe00ae3d2";

            using var http = new HttpClient();
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);

            var body = new StringContent(
                $"grant_type=client_credentials&deployment_id={deploymentId}",
                Encoding.UTF8, "application/x-www-form-urlencoded");

            var resp = await http.PostAsync("https://api.epicgames.dev/auth/v1/oauth/token", body);
            var json = await resp.Content.ReadAsStringAsync();
            MelonLogger.Msg($"[HeadlessMode] EOS OAuth: HTTP {(int)resp.StatusCode}");

            if (!resp.IsSuccessStatusCode)
            {
                MelonLogger.Warning($"[HeadlessMode] EOS OAuth error: {json[..Math.Min(300, json.Length)]}");
                return null;
            }

            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("access_token", out var tok))
                return tok.GetString();

            MelonLogger.Warning("[HeadlessMode] No access_token in EOS response.");
            return null;
        }

        // Sends EOS_Connect_Login with a generic token credential type (default 9 = OpenId).
        // credType maps to EOS_EExternalCredentialType; caller picks the right value for the token.
        private static void CallEosOpenIdLogin(IntPtr handle, IntPtr tokenPtr, IntPtr loginCbPtr, int credType = 9)
        {
            IntPtr credsPtr = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(credsPtr, 0x0, 1);            // ApiVersion
                Marshal.WriteIntPtr(credsPtr, 0x8, tokenPtr);    // Token = OAuth access_token
                Marshal.WriteInt32(credsPtr, 0x10, credType);    // ExternalCredentialType

                var opts = new EosConnectLoginOptions
                {
                    ApiVersion = 2,
                    Credentials = credsPtr,
                    UserLoginInfo = IntPtr.Zero
                };
                EosSdkLogin(handle, ref opts, IntPtr.Zero, loginCbPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(credsPtr);
            }
        }

        // Coroutine wrapper around CallEosOpenIdLogin. Handles the first-time EOS_InvalidUser
        // case by creating a Connect user with the ContinuanceToken and retrying, identical
        // pattern to TryConnectWithEpicIdToken. Currently not invoked in the main boot path
        // but kept as a ready fallback for products where DeviceId and PersistentAuth both fail.
        private static IEnumerator TryOpenIdLogin(ConnectInterface connectInterface, string accessToken, int credType = 9)
        {
            IntPtr handle       = connectInterface.InnerHandle;
            IntPtr loginCbPtr   = Marshal.GetFunctionPointerForDelegate(_pinnedLoginCb);
            IntPtr createUserCb = Marshal.GetFunctionPointerForDelegate(_pinnedCreateUserCb);
            IntPtr tokenPtr     = Marshal.StringToHGlobalAnsi(accessToken);

            _deviceAuthDone = false;
            _deviceAuthSuccess = false;
            _loginContinuanceToken = IntPtr.Zero;
            CallEosOpenIdLogin(handle, tokenPtr, loginCbPtr, credType);
            MelonLogger.Msg($"[HeadlessMode] EOS_Connect_Login (credType={credType}) sent...");

            float t = Time.realtimeSinceStartup;
            while (!_deviceAuthDone && Time.realtimeSinceStartup - t < 30f)
                yield return new WaitForSecondsRealtime(0.25f);

            Marshal.FreeHGlobal(tokenPtr);

            if (!_deviceAuthDone) { MelonLogger.Warning("[HeadlessMode] Login callback never fired."); yield break; }
            if (_deviceAuthSuccess) yield break;

            // EOS_InvalidUser: first login with this credential — create a Connect user account.
            if (_loginContinuanceToken == IntPtr.Zero)
            {
                MelonLogger.Warning("[HeadlessMode] Login failed with no continuance token — can't create user.");
                yield break;
            }

            MelonLogger.Msg("[HeadlessMode] EOS_Connect_CreateUser (first-time setup)...");
            _createUserDone = false;
            _createUserSuccess = false;
            EosSdkCreateUser(handle, _loginContinuanceToken, createUserCb);

            t = Time.realtimeSinceStartup;
            while (!_createUserDone && Time.realtimeSinceStartup - t < 15f)
                yield return new WaitForSecondsRealtime(0.25f);

            if (!_createUserDone || !_createUserSuccess)
            {
                MelonLogger.Warning("[HeadlessMode] CreateUser failed.");
                yield break;
            }

            // Retry login after account creation.
            tokenPtr = Marshal.StringToHGlobalAnsi(accessToken);
            _deviceAuthDone = false;
            _deviceAuthSuccess = false;
            _loginContinuanceToken = IntPtr.Zero;
            CallEosOpenIdLogin(handle, tokenPtr, loginCbPtr);
            MelonLogger.Msg("[HeadlessMode] EOS_Connect_Login retry after CreateUser...");

            t = Time.realtimeSinceStartup;
            while (!_deviceAuthDone && Time.realtimeSinceStartup - t < 30f)
                yield return new WaitForSecondsRealtime(0.25f);

            Marshal.FreeHGlobal(tokenPtr);
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

        /// <summary>
        /// Calls <c>SteamAPI_Shutdown</c> to sever the live Steam IPC connection and clear the
        /// "in game" status on the user's account.
        ///
        /// WARNING: This method is intentionally NOT called anywhere in the active boot flow.
        /// Calling SteamAPI_Shutdown while Steam is running on the same machine causes a
        /// deterministic hard crash ~1 second later because Unity's Steam integration continues
        /// pumping callbacks into a now-dead Steam client. The fake SteamID spoof in
        /// <see cref="PatchNativeSteamId"/> already prevents the headless from occupying the
        /// real account slot, making shutdown unnecessary. This method is kept as a reference
        /// in case a future build reverts to a Steam-free launch mode.
        /// </summary>
        private static void CallSteamApiShutdown()
        {
            if (_steamApiModule == IntPtr.Zero) { MelonLogger.Warning("[HeadlessMode] SteamAPI_Shutdown: module not loaded."); return; }
            IntPtr addr = GetProcAddress(_steamApiModule, "SteamAPI_Shutdown");
            if (addr == IntPtr.Zero) { MelonLogger.Warning("[HeadlessMode] SteamAPI_Shutdown not found in steam_api64."); return; }
            var fn = Marshal.GetDelegateForFunctionPointer<VoidDelegate>(addr);
            fn();
            MelonLogger.Msg("[HeadlessMode] SteamAPI_Shutdown called — Steam 'in game' status should clear.");

            // Hook RunCallbacks as a no-op so the game's update loop doesn't crash trying to
            // pump a disconnected Steam client.
            IntPtr rcAddr = GetProcAddress(_steamApiModule, "SteamAPI_RunCallbacks");
            if (rcAddr != IntPtr.Zero)
            {
                try
                {
                    VoidDelegate noop = () => { };
                    IntPtr noopPtr = Marshal.GetFunctionPointerForDelegate(noop);
                    var hook = new MelonLoader.NativeUtils.NativeHook<VoidDelegate>(rcAddr, noopPtr);
                    hook.Attach();
                    _steamIdHooks.Add(hook); _steamIdHooks.Add(noop);
                    MelonLogger.Msg("[HeadlessMode] Native hook: SteamAPI_RunCallbacks → no-op");
                }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Hook SteamAPI_RunCallbacks failed: {ex.Message}"); }
            }
        }

        // ── SteamManager.StartConnectLoginWithSteamSessionTicket ─────────────────────
        // Stub kept for reference. Steam auth proceeds naturally on machines where Steam is
        // open; when it isn't, the boot bypass + DeviceId path handles login instead.
        private static void PatchSteamConnectMethod(HarmonyLib.Harmony harmony)
        {
            string[] typeNames = {
                "Il2CppPlayEveryWare.EpicOnlineServices.Samples.Steam.SteamManager",
                "Il2CppPlayEveryWare.EpicOnlineServices.SteamManager"
            };
            Type steamType = null;
            foreach (var name in typeNames) { steamType = AccessTools.TypeByName(name); if (steamType != null) break; }
            if (steamType == null) { MelonLogger.Warning("[HeadlessMode] SteamManager type not found for steam connect patch."); return; }

            // OnConnectLoginCallback is a nested type of EOSManager.EOSSingleton
            var callbackType = AccessTools.TypeByName("Il2CppPlayEveryWare.EpicOnlineServices.EOSManager+OnConnectLoginCallback");
            if (callbackType == null) { MelonLogger.Warning("[HeadlessMode] OnConnectLoginCallback type not found."); return; }

            // No further patching — Steam auth proceeds naturally.
        }

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

        /// <summary>
        /// Coroutine: waits for <c>steamclient64.dll</c> to be mapped into the process, then
        /// hooks <c>SteamInternal_SetMinidumpSteamID</c> as a no-op.
        ///
        /// Why a coroutine: <c>steamclient64.dll</c> is loaded lazily by Steam's own IPC layer
        /// after Unity's SteamManager.Awake, which is well after MelonLoader initialization.
        /// It is not present in the module list at the time <see cref="PatchNativeSteamId"/> runs,
        /// so we cannot hook it there. Polling with <c>GetModuleHandle</c> every 50ms catches it
        /// the moment it appears and installs the hook before any code in the game calls through it.
        /// </summary>
        private static IEnumerator HookSteamClientWhenLoaded()
        {
            if (!Application.isBatchMode) yield break;

            // Poll until steamclient64.dll is mapped into this process.
            // It loads during Unity's native Steam integration startup, well after MelonLoader.
            IntPtr clientModule = IntPtr.Zero;
            float elapsed = 0f;
            while (elapsed < 30f)
            {
                clientModule = GetModuleHandle("steamclient64");
                if (clientModule == IntPtr.Zero) clientModule = GetModuleHandle("steamclient");
                if (clientModule != IntPtr.Zero) break;
                yield return new WaitForSecondsRealtime(0.05f);
                elapsed += 0.05f;
            }

            if (clientModule == IntPtr.Zero)
            {
                MelonLogger.Warning("[HeadlessMode] steamclient64.dll never loaded — SteamInternal_SetMinidumpSteamID not hooked.");
                yield break;
            }

            MelonLogger.Msg("[HeadlessMode] steamclient64 loaded — hooking SteamInternal_SetMinidumpSteamID.");

            IntPtr addr = GetProcAddress(clientModule, "SteamInternal_SetMinidumpSteamID");
            if (addr == IntPtr.Zero)
            {
                MelonLogger.Warning("[HeadlessMode] SteamInternal_SetMinidumpSteamID not found in steamclient64.");
                yield break;
            }

            try
            {
                SetMinidumpSteamIdDelegate detour = id =>
                    MelonLogger.Msg($"[HeadlessMode] Suppressed SteamInternal_SetMinidumpSteamID({id})");
                IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(detour);
                var hook = new MelonLoader.NativeUtils.NativeHook<SetMinidumpSteamIdDelegate>(addr, detourPtr);
                hook.Attach();
                _steamIdHooks.Add(hook); _steamIdHooks.Add(detour);
                MelonLogger.Msg("[HeadlessMode] Native hook: SteamInternal_SetMinidumpSteamID → no-op");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Hook SteamInternal_SetMinidumpSteamID failed: {ex.Message}"); }
        }

        // ── Headless NetworkManager activation ───────────────────────────────────────

        /// <summary>
        /// Coroutine: activates the correct FishNet <c>NetworkManager</c> GameObject so
        /// FishNet registers with <c>InstanceFinder</c> before <c>CreateLobby</c> is called.
        ///
        /// Why needed: The game ships two NetworkManagers — one wired to FishySteamworks (Steam P2P)
        /// and one wired to FishyEOS (EOS P2P). Only the active one registers with InstanceFinder.
        /// Normally a human clicking "Host" calls <c>EnableCorrectNetworkManager</c> which picks
        /// the right one based on platform. In headless there is no UI interaction, so both start
        /// inactive. We wait 3 seconds for the scene to finish loading, then activate the first
        /// non-Steam, currently-inactive NetworkManager. The preference for inactive ones is
        /// intentional: the Steam one is typically the scene's default active object; the EOS one
        /// starts disabled.
        /// </summary>
        private static IEnumerator EnableHeadlessNetworkManager()
        {
            if (!SledHeadlessCore.HeadlessAutoHost) yield break;

            // Poll for a NetworkManager — FishNet registers it via Awake when the GO is active.
            // In batchmode the full menu scene doesn't load, so NetworkManagers start inactive.
            // Use Resources.FindObjectsOfTypeAll which includes inactive objects.
            yield return new WaitForSecondsRealtime(3f);

            var allNMs = Resources.FindObjectsOfTypeAll<Il2CppFishNet.Managing.NetworkManager>();
            MelonLogger.Msg($"[HeadlessMode] Found {(allNMs?.Count ?? 0)} NetworkManager(s) total.");

            if (allNMs == null || allNMs.Count == 0) { MelonLogger.Warning("[HeadlessMode] No NetworkManager found."); yield break; }

            // Log all found NetworkManagers to understand the scene structure
            foreach (var nm in allNMs)
            {
                try { MelonLogger.Msg($"[HeadlessMode] NetworkManager: {nm.gameObject.name} active={nm.gameObject.activeSelf}"); }
                catch { }
            }

            // Enable the first non-Steam NetworkManager (prefer inactive ones — they're the non-default)
            Il2CppFishNet.Managing.NetworkManager target = null;
            foreach (var nm in allNMs)
            {
                try
                {
                    string name = nm.gameObject.name?.ToLower() ?? "";
                    bool isSteam = name.Contains("steam");
                    if (!isSteam && !nm.gameObject.activeSelf) { target = nm; break; }
                }
                catch { }
            }

            // Fallback: use first inactive one
            if (target == null)
                foreach (var nm in allNMs)
                    try { if (!nm.gameObject.activeSelf) { target = nm; break; } } catch { }

            // Fallback: use first one period
            if (target == null && allNMs.Count > 0) target = allNMs[0];

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
        ///   Phase 1 — EOS Login (up to 20s):
        ///     Wait for the game's normal Steam-based EOS Connect login to complete.
        ///     This succeeds if Steam is open on the machine; skipped if it times out.
        ///
        ///   Phase 2a — DeviceId auth (no Steam required):
        ///     Call <c>EOS_Connect_CreateDeviceId</c> then invoke PEWS's
        ///     <c>StartConnectLoginWithDeviceToken</c> via reflection so PEWS fires its own
        ///     <c>OnConnectLogin</c> event chain and fully initializes <c>EOSLobbyManager</c>.
        ///     Falls back to a direct P/Invoke login + <see cref="InjectPuidIntoEosManager"/>
        ///     if the reflection path is unavailable.
        ///
        ///   Phase 2b — PersistentAuth fallback:
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

            MelonCoroutines.Start(SilenceAudio());

            // Phase 1: wait for Steam-based EOS login (succeeds when Steam is running).
            // Timeout is short — if Steam is closed it will never arrive.
            MelonLogger.Msg("[HeadlessMode] Waiting for EOS Connect login (Steam)...");
            float elapsed = 0f;
            bool loggedIn = false;
            while (elapsed < 20f)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                elapsed += 0.5f;
                try
                {
                    if (EOSManager.Instance?.HasLoggedInWithConnect() == true) { loggedIn = true; break; }
                    var puid = EOSManager.Instance?.GetProductUserId();
                    if (puid != null && puid.IsValid()) { loggedIn = true; break; }
                }
                catch { }
            }

            // Phase 2: multiple auth paths, no Steam required.
            if (!loggedIn)
            {
                IntPtr authHandle = IntPtr.Zero;
                ConnectInterface connectIface = null;
                try
                {
                    var platform = EOSManager.Instance?.GetEOSPlatformInterface();
                    authHandle = platform?.GetAuthInterface()?.InnerHandle ?? IntPtr.Zero;
                    connectIface = platform?.GetConnectInterface();
                }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] GetPlatformInterfaces: {ex.Message}"); }

                if (connectIface == null)
                {
                    MelonLogger.Warning("[HeadlessMode] Could not get EOS Connect interface.");
                }
                else
                {
                    // 2a: DeviceId with explicit DeviceModel (retry — earlier attempt used null)
                    IntPtr handle = connectIface.InnerHandle;
                    IntPtr loginCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedLoginCb);
                    IntPtr createCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedCreateDeviceCb);
                    IntPtr modelPtr = Marshal.StringToHGlobalAnsi("HeadlessServer");

                    MelonLogger.Msg("[HeadlessMode] Trying EOS_Connect_CreateDeviceId (explicit DeviceModel)...");
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
                        MelonLogger.Msg("[HeadlessMode] DeviceId ready. Triggering PEWS connect login...");

                        // Use PEWS's own StartConnectLoginWithDeviceToken so it fires all internal
                        // OnConnectLogin events and properly initializes EOSLobbyManager.
                        // Passing null callback is safe — PEWS checks null before invoking it.
                        bool pewsLoginStarted = false;
                        try
                        {
                            // EOSManager.Instance returns EOSManager.EOSSingleton — the method is on that type
                            var singleton = EOSManager.Instance;
                            var singletonType = singleton?.GetType()
                                ?? AccessTools.TypeByName("Il2CppPlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton");
                            var startMethod = singletonType != null ? AccessTools.Method(singletonType, "StartConnectLoginWithDeviceToken") : null;
                            if (startMethod != null && singleton != null)
                            {
                                startMethod.Invoke(singleton, new object[] { "HeadlessServer", null });
                                MelonLogger.Msg("[HeadlessMode] StartConnectLoginWithDeviceToken called via reflection.");
                                pewsLoginStarted = true;
                            }
                            else
                                MelonLogger.Warning($"[HeadlessMode] StartConnectLoginWithDeviceToken not found (type={singletonType?.Name}, method={startMethod != null}).");
                        }
                        catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] StartConnectLoginWithDeviceToken: {ex.Message}"); }

                        if (pewsLoginStarted)
                        {
                            // Wait for PEWS to complete the login internally
                            float tpews = Time.realtimeSinceStartup;
                            while (Time.realtimeSinceStartup - tpews < 15f)
                            {
                                yield return new WaitForSecondsRealtime(0.5f);
                                try
                                {
                                    if (EOSManager.Instance?.HasLoggedInWithConnect() == true) { loggedIn = true; break; }
                                    var puid = EOSManager.Instance?.GetProductUserId();
                                    if (puid != null && puid.IsValid()) { loggedIn = true; break; }
                                }
                                catch { }
                            }
                            if (!loggedIn) MelonLogger.Warning("[HeadlessMode] PEWS connect login did not complete.");
                        }

                        if (!loggedIn)
                        {
                            // Fallback: P/Invoke direct login + inject PUID
                            MelonLogger.Msg("[HeadlessMode] Falling back to P/Invoke DeviceId login...");
                            IntPtr displayNamePtr = Marshal.StringToHGlobalAnsi("HeadlessServer");
                            _deviceAuthDone = false; _deviceAuthSuccess = false;
                            _rawProductUserId = IntPtr.Zero;
                            CallEosDeviceLogin(handle, displayNamePtr, loginCbPtr);
                            float tl = Time.realtimeSinceStartup;
                            while (!_deviceAuthDone && Time.realtimeSinceStartup - tl < 15f)
                                yield return new WaitForSecondsRealtime(0.25f);
                            Marshal.FreeHGlobal(displayNamePtr);
                            loggedIn = _deviceAuthSuccess;
                            if (loggedIn)
                                InjectPuidIntoEosManager(_rawProductUserId);
                        }
                    }
                    else
                        MelonLogger.Msg("[HeadlessMode] DeviceId unavailable (disabled for this product).");

                    // 2b: PersistentAuth — uses cached refresh token from any prior login
                    if (!loggedIn && authHandle != IntPtr.Zero)
                    {
                        MelonLogger.Msg("[HeadlessMode] Trying EOS_Auth_Login (PersistentAuth)...");
                        yield return TryEosAuthAndConnect(authHandle, connectIface, 2, IntPtr.Zero);
                        loggedIn = _deviceAuthSuccess;
                    }
                }
            }

            if (!loggedIn) { MelonLogger.Warning("[HeadlessMode] EOS login failed via all methods (Steam, PersistentAuth, app token, AccountPortal)."); yield break; }
            MelonLogger.Msg("[HeadlessMode] EOS Connect login confirmed.");
            // NOTE: We deliberately do NOT call SteamAPI_Shutdown here. We authenticate via EOS
            // DeviceId (Steam-independent), so Steam is never needed for our auth. When Steam IS
            // running on the machine, calling SteamAPI_Shutdown tears down the live Steam session
            // mid-boot and hard-crashes the process ~1s later (deterministic). The native fake-
            // SteamID spoof already prevents this headless from occupying the real account slot,
            // so shutting Steam down serves no purpose. Leave Steam alone.

            elapsed = 0f;
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

            string lobbyName = !string.IsNullOrWhiteSpace(SledHeadlessCore.ServerName)
                ? SledHeadlessCore.ServerName : "Headless Server";

            MelonLogger.Msg($"[HeadlessMode] Calling LobbyManager.CreateLobby('{lobbyName}', {SledHeadlessCore.ServerCapacity})...");
            try
            {
                LobbyManager.Instance.CreateLobby(lobbyName, SledHeadlessCore.ServerCapacity,
                    SledHeadlessCore.IsPublicLobby, true, SledHeadlessCore.IsPasswordProtected,
                    SledHeadlessCore.LobbyPassword, SledHeadlessCore.IsPeacefulMode,
                    string.Empty, string.Empty, false);
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
            while (true)
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
            try
            {
                var ownerSync = __instance.sync_Owner;
                if (ownerSync == null || ownerSync.Value == null) return false;
            }
            catch { return false; }
            return true;
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
    }
}
