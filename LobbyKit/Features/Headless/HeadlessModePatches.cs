using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppFishNet;
using Il2CppPlayEveryWare.EpicOnlineServices;
using Il2CppPlayEveryWare.EpicOnlineServices.Samples;
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
            MelonLogger.Msg("[HeadlessMode] v8 Applying headless suppression patches...");

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
            // Steam auth proceeds naturally — no interception needed when Steam is open.

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

            // No further patching — Steam auth proceeds naturally.
        }

        // ── LobbyManager.CreateLobby null-field patches ───────────────────────────────

        private static void LobbyManager_CreateLobby_PreInit(LobbyManager __instance)
        {
            if (!Application.isBatchMode) return;

            EnsureUnityEvent(__instance, "LobbyJoiningEvent");
        }

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

            MelonLogger.Msg("[HeadlessMode] Waiting for EOS Connect login (Steam)...");
            float elapsed = 0f;
            while (elapsed < 120f)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                elapsed += 0.5f;
                try
                {
                    if (EOSManager.Instance?.HasLoggedInWithConnect() == true) break;
                    var puid = EOSManager.Instance?.GetProductUserId();
                    if (puid != null && puid.IsValid()) break;
                }
                catch { }
            }

            if (elapsed >= 120f) { MelonLogger.Warning("[HeadlessMode] EOS login timed out — is Steam running?"); yield break; }
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
