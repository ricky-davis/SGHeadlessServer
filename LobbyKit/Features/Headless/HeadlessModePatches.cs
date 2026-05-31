using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppFishNet;
using Il2CppPlayEveryWare.EpicOnlineServices;
using Il2CppPlayEveryWare.EpicOnlineServices.Samples;
using Il2CppTMPro;
using Il2Cpp_Scripts.Managers;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LobbyKit.Features.Headless
{
    internal static class HeadlessModePatches
    {
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            MelonLogger.Msg("[HeadlessMode] Applying headless suppression patches...");

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

            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp_Scripts.Managers.LobbyManager" },
                methodName: "CreateLobby",
                prefix: nameof(LobbyManager_CreateLobby_PreInit),
                finalizer: nameof(LobbyManager_CreateLobby_Finalizer),
                label: "LobbyManager.CreateLobby null-field init");

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

            // ── Steam-free EOS auth ───────────────────────────────────────────────────
            // We let SteamManager.Awake run — it sets Instance even if SteamAPI.Init fails
            // (Steam not running). Instance is needed so callers don't NullRef.

            // The EOSAuthenticator boot coroutine checks SteamManager.Initialized before
            // proceeding. Fake it as true so the coroutine doesn't hang waiting for Steam.
            PatchSteamManagerInitialized(harmony);

            // SteamManager.StartConnectLoginWithSteamSessionTicket (public, 1 param) is what
            // EOSAuthenticator calls to obtain a Steam session ticket and log into EOS Connect.
            // There are two overloads (public 1-param, private 2-param), so we must specify
            // the exact signature to avoid AmbiguousMatchException.
            PatchSteamConnectMethod(harmony);

            MelonCoroutines.Start(WaitForEosLoginAndAutoHost());
            MelonLogger.Msg("[HeadlessMode] Done.");
        }

        private static bool SkipInHeadless() => !Application.isBatchMode;

        private static Exception SuppressNullRefInHeadless(Exception __exception)
        {
            if (!Application.isBatchMode) return __exception;
            return null;
        }

        // ── SteamManager.Initialized spoof ───────────────────────────────────────────

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
                harmony.Patch(getter, prefix: new HarmonyMethod(typeof(HeadlessModePatches), nameof(SteamManager_Initialized_Prefix)));
                MelonLogger.Msg("[HeadlessMode] Patched 'SteamManager.Initialized → true'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] SteamManager.Initialized patch failed: {ex.Message}"); }
        }

        private static bool SteamManager_Initialized_Prefix(ref bool __result)
        {
            if (!Application.isBatchMode) return true;
            __result = true;
            return false;
        }

        // ── SteamManager.StartConnectLoginWithSteamSessionTicket → DeviceAuth ───────────

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

            // Select the public 1-param overload explicitly to avoid AmbiguousMatchException
            System.Reflection.MethodInfo method = null;
            try { method = AccessTools.Method(steamType, "StartConnectLoginWithSteamSessionTicket", new[] { callbackType }); }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Steam connect method lookup: {ex.GetType().Name}: {ex.Message}"); }

            if (method == null) { MelonLogger.Warning("[HeadlessMode] SteamManager.StartConnectLoginWithSteamSessionTicket(callback) not found."); return; }

            try
            {
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(HeadlessModePatches), nameof(SteamManager_StartConnectWithSteam_Prefix)));
                MelonLogger.Msg("[HeadlessMode] Patched 'SteamManager.StartConnectLoginWithSteamSessionTicket → DeviceAuth'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Steam connect patch failed: {ex.Message}"); }
        }

        // ── Device Auth — reuse game's ConnectLoginTokenCallback ──────────────────────

        // SteamManager.StartConnectLoginWithSteamSessionTicket(OnConnectLoginCallback callback)
        // is called by EOSAuthenticator (possibly multiple times via retry). We intercept the
        // FIRST call only, skip Steam, and pass the same callback to StartConnectLoginWithDeviceToken.
        private static volatile bool _deviceAuthStarted = false;

        private static bool SteamManager_StartConnectWithSteam_Prefix(object[] __args)
        {
            if (!Application.isBatchMode) return true;
            if (_deviceAuthStarted)
            {
                MelonLogger.Msg("[HeadlessMode] Device auth already in progress — suppressing retry.");
                return false;
            }
            _deviceAuthStarted = true;

            var callback = __args?[0] as EOSManager.OnConnectLoginCallback;
            MelonLogger.Msg($"[HeadlessMode] Steam connect intercepted → Device Auth. Callback: {(callback == null ? "NULL" : "captured")}");
            MelonCoroutines.Start(DeviceAuthCoroutine(callback));
            return false;
        }

        private static IEnumerator DeviceAuthCoroutine(EOSManager.OnConnectLoginCallback callback)
        {
            float waited = 0f;
            while (waited < 30f)
            {
                try { if (EOSManager.Instance != null) break; } catch { }
                yield return new WaitForSecondsRealtime(0.5f);
                waited += 0.5f;
            }

            if (EOSManager.Instance == null)
            {
                MelonLogger.Warning("[HeadlessMode] EOSManager null — device auth aborted.");
                yield break;
            }

            MelonLogger.Msg("[HeadlessMode] Calling StartConnectLoginWithDeviceToken...");
            try
            {
                EOSManager.Instance.StartConnectLoginWithDeviceToken("HeadlessServer", callback);
                MelonLogger.Msg("[HeadlessMode] StartConnectLoginWithDeviceToken returned (async).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode] StartConnectLoginWithDeviceToken threw: {ex.GetType().Name}: {ex.Message}");
                yield break;
            }

            // Poll to see if PEWS picked up the login via either check
            for (int i = 0; i < 20; i++)
            {
                yield return new WaitForSecondsRealtime(1f);
                bool hasLoggedIn = false;
                string productUserId = "null";
                try { hasLoggedIn = EOSManager.Instance?.HasLoggedInWithConnect() == true; } catch { }
                try
                {
                    var puid = EOSManager.Instance?.GetProductUserId();
                    productUserId = puid != null ? puid.ToString() : "null";
                    // If GetProductUserId() returns a valid ID, consider auth done
                    if (!hasLoggedIn && puid != null)
                    {
                        try { hasLoggedIn = puid.IsValid(); } catch { hasLoggedIn = true; }
                    }
                }
                catch { }
                MelonLogger.Msg($"[HeadlessMode] Poll {i + 1}/20: HasLoggedInWithConnect={hasLoggedIn}, ProductUserId={productUserId}");
                if (hasLoggedIn) break;
            }
        }

        // ── LobbyManager.CreateLobby null-field patches ───────────────────────────────

        private static bool _lobbyManagerDumped;

        private static void LobbyManager_CreateLobby_PreInit(LobbyManager __instance)
        {
            if (!Application.isBatchMode) return;

            if (!_lobbyManagerDumped)
            {
                _lobbyManagerDumped = true;
                foreach (var prop in typeof(LobbyManager).GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    try
                    {
                        if (!prop.CanRead) continue;
                        var val = prop.GetValue(__instance);
                        bool isNull = val == null || val is Object o && o == null;
                        var pt = prop.PropertyType;
                        bool isTMP = typeof(TMP_Text).IsAssignableFrom(pt) || typeof(TMP_InputField).IsAssignableFrom(pt);
                        if (isTMP) MelonLogger.Msg($"[HeadlessMode] LobbyManager.{prop.Name} ({pt.Name}) = {(isNull ? "NULL" : "set")}");
                    }
                    catch { }
                }
            }

            EnsureField<TextMeshProUGUI>(__instance, "lobbyNameText");
            EnsureField<TextMeshProUGUI>(__instance, "lobbyMaxPlayersText");
            EnsureField<TextMeshProUGUI>(__instance, "lobbyCodeText");
            EnsureField<TextMeshProUGUI>(__instance, "lobbyPasswordText");
            EnsureField<TextMeshProUGUI>(__instance, "lobbyNameTextLocalizationKey");
            EnsureField<TMP_InputField>(__instance, "_lobbyNameInputField");
            EnsureField<TMP_InputField>(__instance, "passwordInputField");
            EnsureField<TMP_InputField>(__instance, "passwordCheckInputField");
        }

        private static void EnsureField<T>(LobbyManager instance, string propName) where T : Component
        {
            try
            {
                var prop = typeof(LobbyManager).GetProperty(propName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null || !prop.CanRead || !prop.CanWrite) return;
                var val = prop.GetValue(instance);
                bool isNull = val == null || val is Object o && o == null;
                if (!isNull) return;

                var go = new GameObject($"HeadlessDummy_{propName}");
                Object.DontDestroyOnLoad(go);
                T comp;
                try { comp = go.AddComponent<T>(); }
                catch { Object.Destroy(go); return; }
                if (comp != null) { prop.SetValue(instance, comp); MelonLogger.Msg($"[HeadlessMode] Pre-initialized LobbyManager.{propName}"); }
                else Object.Destroy(go);
            }
            catch { }
        }

        private static Exception LobbyManager_CreateLobby_Finalizer(Exception __exception)
        {
            if (!Application.isBatchMode) return __exception;
            if (__exception != null)
                MelonLogger.Warning($"[HeadlessMode] LobbyManager.CreateLobby threw: {__exception.GetType().Name}: {__exception.Message}");
            return null;
        }

        // ── Auto-host coroutine ───────────────────────────────────────────────────────

        private static EOSLobbyManager _eosLobbyManager;

        private static IEnumerator WaitForEosLoginAndAutoHost()
        {
            if (!LobbyKitCore.HeadlessAutoHost) yield break;

            MelonCoroutines.Start(SilenceAudio());

            MelonLogger.Msg("[HeadlessMode] Waiting for EOS Connect login...");
            float elapsed = 0f;
            while (elapsed < 120f)
            {
                yield return new WaitForSecondsRealtime(0.1f);
                elapsed += 0.1f;
                try
                {
                    if (EOSManager.Instance?.HasLoggedInWithConnect() == true) break;
                    // Device auth sets ProductUserId without setting HasLoggedInWithConnect
                    var puid = EOSManager.Instance?.GetProductUserId();
                    if (puid != null && puid.IsValid()) break;
                }
                catch { }
            }

            if (elapsed >= 120f) { MelonLogger.Warning("[HeadlessMode] EOS login timed out."); yield break; }
            MelonLogger.Msg("[HeadlessMode] EOS Connect login confirmed.");

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
            yield return new WaitForSecondsRealtime(0.5f);

            string lobbyName = !string.IsNullOrWhiteSpace(LobbyKitCore.ServerName)
                ? LobbyKitCore.ServerName : "Headless Server";

            MelonLogger.Msg($"[HeadlessMode] Calling LobbyManager.CreateLobby('{lobbyName}', {LobbyKitCore.ServerCapacity})...");
            try
            {
                LobbyManager.Instance.CreateLobby(lobbyName, LobbyKitCore.ServerCapacity,
                    LobbyKitCore.IsPublicLobby, false, LobbyKitCore.IsPasswordProtected,
                    LobbyKitCore.LobbyPassword, LobbyKitCore.IsPeacefulMode,
                    "Steam", string.Empty, true);
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
                LobbyKitCore.isHost = true;
                LobbyKitCore.WasHosting = true;
                MelonLogger.Msg("[HeadlessMode] Lobby hosted successfully!");
            }
            else
                MelonLogger.Warning("[HeadlessMode] FishNet server never started after 20s.");
        }

        private static IEnumerator SilenceAudio()
        {
            yield return new WaitForSecondsRealtime(1f);
            try
            {
                AudioListener.volume = 0f;
                AudioListener.pause = true;
                MelonLogger.Msg("[HeadlessMode] Audio silenced.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] SilenceAudio: {ex.GetType().Name}"); }
        }

        // ── TryPatch helper ───────────────────────────────────────────────────────────

        private static void TryPatch(HarmonyLib.Harmony harmony, string[] typeNames, string methodName,
            string prefix = null, string postfix = null, string finalizer = null, string label = null)
        {
            Type targetType = null;
            foreach (var name in typeNames) { targetType = AccessTools.TypeByName(name); if (targetType != null) break; }
            if (targetType == null) { MelonLogger.Warning($"[HeadlessMode] Type not found for '{label ?? methodName}'."); return; }

            var method = AccessTools.Method(targetType, methodName);
            if (method == null) { MelonLogger.Warning($"[HeadlessMode] Method '{label ?? methodName}' not found on {targetType.FullName}."); return; }

            var prefixM = prefix != null ? new HarmonyMethod(typeof(HeadlessModePatches), prefix) : null;
            var postfixM = postfix != null ? new HarmonyMethod(typeof(HeadlessModePatches), postfix) : null;
            var finalizerM = finalizer != null ? new HarmonyMethod(typeof(HeadlessModePatches), finalizer) : null;

            try
            {
                harmony.Patch(method, prefix: prefixM, postfix: postfixM, finalizer: finalizerM);
                MelonLogger.Msg($"[HeadlessMode] Patched '{label ?? $"{targetType.Name}.{methodName}"}'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] Failed to patch '{label ?? methodName}': {ex.Message}"); }
        }
    }
}
