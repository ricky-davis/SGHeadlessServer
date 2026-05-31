using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppEpic.OnlineServices;
using Il2CppEpic.OnlineServices.Lobby;
using Il2CppPlayEveryWare.EpicOnlineServices;
using Il2CppPlayEveryWare.EpicOnlineServices.Samples;
using Il2CppTMPro;
using Il2Cpp_Scripts.Managers;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;
using EosLobby = Il2CppPlayEveryWare.EpicOnlineServices.Samples.Lobby;
using EosLobbyAttribute = Il2CppPlayEveryWare.EpicOnlineServices.Samples.LobbyAttribute;

namespace LobbyKit.Features.Headless
{
    internal static class HeadlessModePatches
    {
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            MelonLogger.Msg("[HeadlessMode] Applying headless suppression patches...");

            // Rewired enumerates input hardware on Awake — no devices in headless = crash.
            // The concrete InputManager doesn't override Awake, so target InputManager_Base.
            TryPatch(harmony,
                typeNames: new[] { "Il2CppRewired.InputManager_Base", "Il2CppRewired.InputManager", "Rewired.InputManager" },
                methodName: "Awake",
                prefix: nameof(SkipInHeadless),
                label: "Rewired.InputManager_Base.Awake");

            // Dissonance initialises microphone capture on Awake — no audio device in headless = crash
            TryPatch(harmony,
                typeNames: new[] { "Il2CppDissonance.DissonanceComms", "Dissonance.DissonanceComms" },
                methodName: "Awake",
                prefix: nameof(SkipInHeadless),
                label: "DissonanceComms.Awake");

            // EOS overlay tries to hook DXGI/D3D on init — no device exists in headless,
            // which stalls or crashes the boot sequence. Skip it entirely.
            TryPatch(harmony,
                typeNames: new[] {
                    "Il2CppPlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton",
                    "PlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton"
                },
                methodName: "InitializeOverlay",
                prefix: nameof(SkipInHeadless),
                label: "EOSManager.EOSSingleton.InitializeOverlay");

            // Diagnostic: log when EOS auth completes and when the game calls SafeQuit
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp_Scripts.Boot.EOSAuthenticator", "Il2Cpp_Scripts.Boot.BootSceneManager" },
                methodName: "EOSAuthenticationComplete",
                postfix: nameof(EOSAuthenticationComplete_Postfix),
                label: "EOSAuthenticator.EOSAuthenticationComplete");

            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp_Scripts.Binary.Quit" },
                methodName: "SafeQuit",
                prefix: nameof(SafeQuit_Prefix),
                label: "_Scripts.Binary.Quit.SafeQuit");

            // Save EOSLobbyManager instance — EOSLobbyManager has no Awake override; OnEnable is earliest.
            TryPatch(harmony,
                typeNames: new[] { "Il2CppPlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager" },
                methodName: "OnEnable",
                postfix: nameof(EOSLobbyManager_Awake_Postfix),
                label: "EOSLobbyManager.OnEnable (save instance)");

            // UiReferenceController.Update calls JoystickOnlyUpdate and HandleInput_RegardlessOfNetwork,
            // both of which throw NullRef every frame when Rewired is suppressed.
            // Suppress the whole Update() — no input handling needed in headless.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.UiReferenceController" },
                methodName: "Update",
                finalizer: nameof(SuppressNullRefInHeadless),
                label: "UiReferenceController.Update NullRef suppressor");

            // UiReferenceController.ReturnToMainMenu is called when lobby creation fails — it tries to
            // open UI menus (null in headless). Skip it entirely.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp.UiReferenceController" },
                methodName: "ReturnToMainMenu",
                prefix: nameof(SkipInHeadless),
                label: "UiReferenceController.ReturnToMainMenu headless skip");

            // LobbyManager.CreateLobby references UI text fields (lobbyNameText etc.) that
            // are null before the in-game scene loads. Pre-initialize them with dummy GameObjects
            // so the original method can run and properly invoke EOSLobbyManager.CreateLobby.
            TryPatch(harmony,
                typeNames: new[] { "Il2Cpp_Scripts.Managers.LobbyManager" },
                methodName: "CreateLobby",
                prefix: nameof(LobbyManager_CreateLobby_PreInit),
                finalizer: nameof(LobbyManager_CreateLobby_Finalizer),
                label: "LobbyManager.CreateLobby null-field init");

            // Auto-host: poll EOSSingleton.HasLoggedInWithConnect() instead of hooking a
            // callback — the Steam callback lambdas live on a different type in the steam
            // assembly and aren't accessible from here.
            MelonCoroutines.Start(WaitForEosLoginAndAutoHost());

            MelonLogger.Msg("[HeadlessMode] Done.");
        }

        // Prefix: returns false to skip the original method when running headless
        private static bool SkipInHeadless() => !Application.isBatchMode;

        private static void EOSAuthenticationComplete_Postfix()
        {
            MelonLogger.Msg("[HeadlessMode] EOSAuthenticationComplete fired.");
        }

        private static void SafeQuit_Prefix()
        {
            MelonLogger.Warning("[HeadlessMode] SafeQuit called — game is about to exit.");
            MelonLogger.Warning(new System.Diagnostics.StackTrace(true).ToString());
        }

        private static EOSLobbyManager _eosLobbyManager;

        private static void EOSLobbyManager_Awake_Postfix(EOSLobbyManager __instance)
        {
            _eosLobbyManager = __instance;
            MelonLogger.Msg("[HeadlessMode] EOSLobbyManager instance saved.");
        }

        // Silently swallow any exception from the patched method in headless.
        private static Exception SuppressNullRefInHeadless(Exception __exception)
        {
            if (!Application.isBatchMode) return __exception;
            return null;
        }

        // Prefix: pre-initialize the known null TMP_Text / TMP_InputField fields on LobbyManager
        // so the game's CreateLobby code can safely call .text on them in headless.
        private static bool _lobbyManagerDumped;
        private static void LobbyManager_CreateLobby_PreInit(LobbyManager __instance)
        {
            if (!Application.isBatchMode) return;

            // One-time diagnostic: check the known UI-holding properties that cause NullRef in headless.
            if (!_lobbyManagerDumped)
            {
                _lobbyManagerDumped = true;
                string[] knownUIFields = { "lobbyNameText", "lobbyMaxPlayersText", "lobbyCodeText",
                    "lobbyPasswordText", "lobbyNameTextLocalizationKey", "lobbyNameInputField" };
                foreach (var fieldName in knownUIFields)
                {
                    try
                    {
                        var prop = AccessTools.Property(__instance.GetType(), fieldName);
                        if (prop == null) { MelonLogger.Msg($"[HeadlessMode] LobbyManager.{fieldName} — prop not found"); continue; }
                        var val = prop.GetValue(__instance);
                        bool isNull = val == null || val is Object o && o == null;
                        MelonLogger.Msg($"[HeadlessMode] LobbyManager.{fieldName} = {(isNull ? "NULL" : "set")}");
                    }
                    catch { }
                }
            }

            EnsureField<TextMeshProUGUI>(__instance, "lobbyNameText");
            EnsureField<TextMeshProUGUI>(__instance, "lobbyMaxPlayersText");
            EnsureField<TextMeshProUGUI>(__instance, "lobbyCodeText");
            EnsureField<TextMeshProUGUI>(__instance, "lobbyPasswordText");
            EnsureField<TextMeshProUGUI>(__instance, "lobbyNameTextLocalizationKey");
            EnsureField<TMP_InputField>(__instance, "lobbyNameInputField");
        }

        private static void EnsureField<T>(object instance, string fieldName) where T : UnityEngine.Component
        {
            try
            {
                // Il2CppInterop exposes native fields as C# properties, not fields
                var prop = AccessTools.Property(instance.GetType(), fieldName);
                if (prop == null || !prop.CanRead || !prop.CanWrite) return;
                var value = prop.GetValue(instance);
                if (value == null || value is Object obj && obj == null)
                {
                    var go = new GameObject($"HeadlessDummy_{fieldName}");
                    Object.DontDestroyOnLoad(go);
                    T comp = null;
                    try { comp = go.AddComponent<T>(); }
                    catch { Object.Destroy(go); return; }
                    if (comp != null)
                    {
                        prop.SetValue(instance, comp);
                        MelonLogger.Msg($"[HeadlessMode] Pre-initialized LobbyManager.{fieldName}");
                    }
                }
            }
            catch { }
        }

        // Suppress NullReferenceExceptions from LobbyManager.CreateLobby in headless — the
        // UI text fields it tries to update are null before the in-game scene loads.
        private static Exception LobbyManager_CreateLobby_Finalizer(Exception __exception)
        {
            if (!Application.isBatchMode) return __exception;
            if (__exception != null)
                MelonLogger.Warning("[HeadlessMode] LobbyManager.CreateLobby threw (suppressed) — checking if EOS lobby was created.");
            return null;
        }

        private static IEnumerator WaitForEosLoginAndAutoHost()
        {
            if (!LobbyKitCore.HeadlessAutoHost) yield break;

            MelonLogger.Msg("[HeadlessMode] Waiting for EOS Connect login...");

            // Poll EOSSingleton.HasLoggedInWithConnect() every 100ms (real time).
            // Frame-based polling is useless in headless — Unity runs at ~50k fps with no renderer.
            const float pollInterval = 0.1f;
            const float timeoutSeconds = 120f;
            float elapsed = 0f;
            while (elapsed < timeoutSeconds)
            {
                yield return new WaitForSecondsRealtime(pollInterval);
                elapsed += pollInterval;
                try
                {
                    var singleton = EOSManager.Instance;
                    if (singleton != null && singleton.HasLoggedInWithConnect())
                        break;
                }
                catch { /* EOSManager not ready yet */ }
            }

            if (elapsed >= timeoutSeconds)
            {
                MelonLogger.Warning("[HeadlessMode] EOS Connect login timed out after 120s — auto-host aborted.");
                yield break;
            }

            MelonLogger.Msg("[HeadlessMode] EOS Connect login confirmed — queuing auto-host.");

            // Wait for LobbyManager to become available after scene load
            elapsed = 0f;
            while (LobbyManager.Instance == null && elapsed < 30f)
            {
                yield return new WaitForSecondsRealtime(pollInterval);
                elapsed += pollInterval;
            }

            if (LobbyManager.Instance == null)
            {
                MelonLogger.Warning("[HeadlessMode] LobbyManager unavailable after 30 seconds — auto-host aborted.");
                yield break;
            }

            // Poll LobbyManager._lobbyManager until it is initialized (set in LobbyManager's Start/Awake).
            // It is null right after EOS login — the manager gets wired up when the scene finishes loading.
            // Use Time.realtimeSinceStartup so the timeout is immune to frame-rate variation in headless.
            var lobbyMgrProp = AccessTools.Property(typeof(LobbyManager), "_lobbyManager");
            float startCapture = Time.realtimeSinceStartup;
            MelonLogger.Msg("[HeadlessMode] Polling for LobbyManager._lobbyManager...");
            while (_eosLobbyManager == null)
            {
                if (Time.realtimeSinceStartup - startCapture > 30f) break;
                try
                {
                    var raw = lobbyMgrProp?.GetValue(LobbyManager.Instance);
                    if (raw != null)
                    {
                        _eosLobbyManager = (raw as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                            ?.TryCast<EOSLobbyManager>() ?? raw as EOSLobbyManager;
                    }
                }
                catch { }
                if (_eosLobbyManager != null) break;
                yield return new WaitForSecondsRealtime(1f);
            }

            if (_eosLobbyManager == null)
            {
                MelonLogger.Warning("[HeadlessMode] LobbyManager._lobbyManager stayed null for 30s — auto-host aborted.");
                yield break;
            }

            MelonLogger.Msg($"[HeadlessMode] EOSLobbyManager ready after {Time.realtimeSinceStartup - startCapture:F1}s.");

            string lobbyName = !string.IsNullOrWhiteSpace(LobbyKitCore.ServerName)
                ? LobbyKitCore.ServerName
                : "Headless Server";

            // Call EOSLobbyManager.CreateLobby directly and route the callback to LobbyManager.OnCreateLobbyComplete.
            MelonLogger.Msg($"[HeadlessMode] Creating lobby '{lobbyName}' (capacity: {LobbyKitCore.ServerCapacity})...");
            MelonCoroutines.Start(TryDirectLobbyCreation(
                lobbyName, LobbyKitCore.ServerCapacity, LobbyKitCore.IsPublicLobby,
                proximityChatEnabled: false, LobbyKitCore.IsPasswordProtected,
                LobbyKitCore.IsPeacefulMode, platform: "Steam",
                region: string.Empty, crossplayEnabled: true));
        }

        private static IEnumerator TryDirectLobbyCreation(string lobbyName, int maxPlayers,
            bool isPublic, bool proximityChatEnabled, bool passwordProtected,
            bool peacefulMode, string platform, string region, bool crossplayEnabled)
        {
            var eosLobbyManager = _eosLobbyManager;

            // EOSLobbyManager is stored in LobbyManager._lobbyManager (plain Il2CppSystem.Object, not MonoBehaviour).
            if (eosLobbyManager == null && LobbyManager.Instance != null)
            {
                try
                {
                    var prop = AccessTools.Property(typeof(LobbyManager), "_lobbyManager");
                    var raw = prop?.GetValue(LobbyManager.Instance);
                    if (raw != null)
                    {
                        eosLobbyManager = (raw as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                            ?.TryCast<EOSLobbyManager>() ?? raw as EOSLobbyManager;
                    }
                    if (eosLobbyManager != null)
                        MelonLogger.Msg("[HeadlessMode] EOSLobbyManager captured from LobbyManager._lobbyManager (in TryDirect).");
                    else
                        MelonLogger.Warning($"[HeadlessMode] LobbyManager._lobbyManager = {raw?.GetType().FullName ?? "null"}");
                }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] _lobbyManager fallback: {ex.GetType().Name}: {ex.Message}"); }
            }

            if (eosLobbyManager == null)
            {
                MelonLogger.Warning("[HeadlessMode] EOSLobbyManager not found — cannot create lobby.");
                yield break;
            }

            try
            {
                string bucketId = string.Empty;
                try
                {
                    bucketId = AccessTools.Property(typeof(EOSLobbyManager), "BUCKET_ID")
                        ?.GetValue(null) as string ?? string.Empty;
                }
                catch { }

                var lobby = new EosLobby();
                lobby.MaxNumLobbyMembers = (uint)Mathf.Clamp(maxPlayers, 1, 64);
                lobby.LobbyPermissionLevel = isPublic
                    ? LobbyPermissionLevel.Publicadvertised
                    : LobbyPermissionLevel.Inviteonly;
                lobby.RTCRoomEnabled = false;
                lobby.PresenceEnabled = true;
                lobby.AllowInvites = true;
                if (!string.IsNullOrEmpty(bucketId))
                    lobby.BucketId = bucketId;

                // LobbyAttribute has flat Key/AsString/AsBool/AsInt64 fields and a List-based Attributes
                AddLobbyAttrString(lobby, "PLATFORM", platform);
                AddLobbyAttrString(lobby, "REGION", region);
                AddLobbyAttrBool(lobby, "CROSSPLAY", crossplayEnabled);
                AddLobbyAttrBool(lobby, "MODDED", true);
                AddLobbyAttrBool(lobby, "PEACEFUL", peacefulMode);
                AddLobbyAttrBool(lobby, "PROXIMITY_VOICE_CHAT", proximityChatEnabled);
                AddLobbyAttrBool(lobby, "REQUIRE_PASSWORD", passwordProtected);
                AddLobbyAttrInt64(lobby, "MAXPLAYERS", maxPlayers);

                // Route the EOS callback back to LobbyManager.OnCreateLobbyComplete so FishNet starts.
                // Il2Cpp delegate constructors take IntPtr; use DelegateSupport to wrap a static method.
                EOSLobbyManager.OnLobbyCallback callback = null;
                try
                {
                    callback = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EOSLobbyManager.OnLobbyCallback>(
                        (System.Action<Result>)DirectCreateLobbyCallback);
                }
                catch (Exception ex3) { MelonLogger.Warning($"[HeadlessMode] DelegateSupport: {ex3.GetType().Name}"); }

                MelonLogger.Msg($"[HeadlessMode] Calling EOSLobbyManager.CreateLobby directly (bucket:'{bucketId}')...");
                eosLobbyManager.CreateLobby(lobby, callback);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[HeadlessMode] Direct EOSLobbyManager.CreateLobby failed: {ex}");
                yield break;
            }

            float elapsed = 0f;
            while (!LobbyKitCore.isHost && elapsed < 15f)
            {
                yield return new WaitForSecondsRealtime(0.1f);
                elapsed += 0.1f;
            }

            if (LobbyKitCore.isHost)
                MelonLogger.Msg("[HeadlessMode] Lobby hosted successfully via direct EOS call.");
            else
                MelonLogger.Warning("[HeadlessMode] Still not hosting after direct call — check Player.log.");
        }

        private static void DirectCreateLobbyCallback(Result result)
        {
            MelonLogger.Msg($"[HeadlessMode] Direct CreateLobby EOS callback: {result}");
            try
            {
                AccessTools.Method(typeof(LobbyManager), "OnCreateLobbyComplete")
                    ?.Invoke(LobbyManager.Instance, new object[] { (int)result });
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] OnCreateLobbyComplete invoke: {ex.GetType().Name}"); }
        }

        private static void AddLobbyAttrString(EosLobby lobby, string key, string value)
        {
            try
            {
                var attr = new EosLobbyAttribute();
                attr.Key = key;
                attr.AsString = value ?? string.Empty;
                attr.Visibility = LobbyAttributeVisibility.Public;
                lobby.Attributes.Add(attr);
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] AddLobbyAttrString({key}): {ex.GetType().Name}"); }
        }

        private static void AddLobbyAttrBool(EosLobby lobby, string key, bool value)
        {
            try
            {
                var attr = new EosLobbyAttribute();
                attr.Key = key;
                attr.AsBool = new Il2CppSystem.Nullable<bool>(value);
                attr.Visibility = LobbyAttributeVisibility.Public;
                lobby.Attributes.Add(attr);
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] AddLobbyAttrBool({key}): {ex.GetType().Name}"); }
        }

        private static void AddLobbyAttrInt64(EosLobby lobby, string key, long value)
        {
            try
            {
                var attr = new EosLobbyAttribute();
                attr.Key = key;
                attr.AsInt64 = new Il2CppSystem.Nullable<long>(value);
                attr.Visibility = LobbyAttributeVisibility.Public;
                lobby.Attributes.Add(attr);
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] AddLobbyAttrInt64({key}): {ex.GetType().Name}"); }
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
