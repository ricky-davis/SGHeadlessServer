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

            // Rewired enumerates input hardware on Awake — no devices in headless = crash.
            TryPatch(harmony,
                typeNames: new[] { "Il2CppRewired.InputManager_Base", "Il2CppRewired.InputManager" },
                methodName: "Awake",
                prefix: nameof(SkipInHeadless),
                label: "Rewired.InputManager_Base.Awake");

            // Dissonance initialises microphone capture on Awake — no audio device in headless = crash.
            TryPatch(harmony,
                typeNames: new[] { "Il2CppDissonance.DissonanceComms" },
                methodName: "Awake",
                prefix: nameof(SkipInHeadless),
                label: "DissonanceComms.Awake");

            // EOS overlay hooks DXGI/D3D — no device in headless, stalls boot.
            TryPatch(harmony,
                typeNames: new[] { "Il2CppPlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton" },
                methodName: "InitializeOverlay",
                prefix: nameof(SkipInHeadless),
                label: "EOSManager.EOSSingleton.InitializeOverlay");

            // UiReferenceController.Update throws NullRef every frame when Rewired is suppressed.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.UiReferenceController" },
                methodName: "Update",
                finalizer: nameof(SuppressNullRefInHeadless),
                label: "UiReferenceController.Update NullRef suppressor");

            // ReturnToMainMenu tries to open UI menus that don't exist in headless.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.UiReferenceController" },
                methodName: "ReturnToMainMenu",
                prefix: nameof(SkipInHeadless),
                label: "UiReferenceController.ReturnToMainMenu headless skip");

            // Pre-initialize null TMP fields on LobbyManager before CreateLobby runs.
            // Finalizer suppresses any remaining exception so the EOS call still fires.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp_Scripts.Managers.LobbyManager" },
                methodName: "CreateLobby",
                prefix: nameof(LobbyManager_CreateLobby_PreInit),
                finalizer: nameof(LobbyManager_CreateLobby_Finalizer),
                label: "LobbyManager.CreateLobby null-field init");

            MelonCoroutines.Start(WaitForEosLoginAndAutoHost());
            MelonLogger.Msg("[HeadlessMode] Done.");
        }

        // Returns false to skip the original method in headless.
        private static bool SkipInHeadless() => !Application.isBatchMode;

        // Swallows exceptions from the patched method in headless.
        private static Exception SuppressNullRefInHeadless(Exception __exception)
        {
            if (!Application.isBatchMode) return __exception;
            return null;
        }

        private static bool _lobbyManagerDumped;

        // Pre-init null TMP_Text / TMP_InputField fields so LobbyManager.CreateLobby body can run.
        // MUST use typeof(LobbyManager) — __instance.GetType() returns the native Il2Cpp type which
        // has different PropertyInfo objects and GetProperty returns null for field-backed props.
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
                        if (isTMP)
                            MelonLogger.Msg($"[HeadlessMode] LobbyManager.{prop.Name} ({pt.Name}) = {(isNull ? "NULL" : "set")}");
                    }
                    catch { }
                }
            }

            // Use typeof(LobbyManager) so we get the C# wrapper PropertyInfo (not native Il2Cpp type).
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
                if (comp != null)
                {
                    prop.SetValue(instance, comp);
                    MelonLogger.Msg($"[HeadlessMode] Pre-initialized LobbyManager.{propName}");
                }
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

        private static EOSLobbyManager _eosLobbyManager;

        private static IEnumerator WaitForEosLoginAndAutoHost()
        {
            if (!LobbyKitCore.HeadlessAutoHost) yield break;

            MelonLogger.Msg("[HeadlessMode] Waiting for EOS Connect login...");

            float elapsed = 0f;
            while (elapsed < 120f)
            {
                yield return new WaitForSecondsRealtime(0.1f);
                elapsed += 0.1f;
                try { if (EOSManager.Instance?.HasLoggedInWithConnect() == true) break; } catch { }
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

            // _lobbyManager is null right after EOS login — set when scene finishes initializing.
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

            if (_eosLobbyManager == null)
            {
                MelonLogger.Warning("[HeadlessMode] _lobbyManager stayed null — auto-host aborted.");
                yield break;
            }

            MelonLogger.Msg($"[HeadlessMode] EOSLobbyManager ready after {Time.realtimeSinceStartup - startCapture:F1}s.");
            yield return new WaitForSecondsRealtime(0.5f);

            string lobbyName = !string.IsNullOrWhiteSpace(LobbyKitCore.ServerName)
                ? LobbyKitCore.ServerName : "Headless Server";

            MelonLogger.Msg($"[HeadlessMode] Calling LobbyManager.CreateLobby('{lobbyName}', {LobbyKitCore.ServerCapacity})...");
            try
            {
                LobbyManager.Instance.CreateLobby(
                    lobbyName,
                    LobbyKitCore.ServerCapacity,
                    LobbyKitCore.IsPublicLobby,
                    false,                           // proximityChatEnabled
                    LobbyKitCore.IsPasswordProtected,
                    LobbyKitCore.LobbyPassword,
                    LobbyKitCore.IsPeacefulMode,
                    "Steam",
                    string.Empty,                    // region
                    true                             // crossplayEnabled
                );
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode] CreateLobby outer exception: {ex.GetType().Name}: {ex.Message}");
            }

            // In headless mode there is no local player with ConnectionID==32767, so isHost is never
            // set by the normal PlayerJoinPatch path. Poll FishNet's server state directly instead.
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
                MelonLogger.Warning("[HeadlessMode] FishNet server never started after 20s — check Player.log.");
        }

        private static void TryPatch(
            HarmonyLib.Harmony harmony,
            string[] typeNames,
            string methodName,
            string prefix = null,
            string postfix = null,
            string finalizer = null,
            string label = null)
        {
            Type targetType = null;
            foreach (var name in typeNames)
            {
                targetType = AccessTools.TypeByName(name);
                if (targetType != null) break;
            }

            if (targetType == null)
            {
                MelonLogger.Warning($"[HeadlessMode] Type not found for '{label ?? methodName}' — patch skipped.");
                return;
            }

            var method = AccessTools.Method(targetType, methodName);
            if (method == null)
            {
                MelonLogger.Warning($"[HeadlessMode] Method '{label ?? methodName}' not found on {targetType.FullName} — patch skipped.");
                return;
            }

            var prefixMethod = prefix != null ? new HarmonyMethod(typeof(HeadlessModePatches), prefix) : null;
            var postfixMethod = postfix != null ? new HarmonyMethod(typeof(HeadlessModePatches), postfix) : null;
            var finalizerMethod = finalizer != null ? new HarmonyMethod(typeof(HeadlessModePatches), finalizer) : null;

            try
            {
                harmony.Patch(method, prefix: prefixMethod, postfix: postfixMethod, finalizer: finalizerMethod);
                MelonLogger.Msg($"[HeadlessMode] Patched '{label ?? $"{targetType.Name}.{methodName}"}'.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode] Failed to patch '{label ?? methodName}': {ex.Message}");
            }
        }
    }
}
