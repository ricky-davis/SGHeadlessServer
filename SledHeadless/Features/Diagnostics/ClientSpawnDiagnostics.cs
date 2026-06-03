using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SledHeadless
{
    /// <summary>
    /// CLIENT-side diagnostics (runs only when NOT <c>Application.isBatchMode</c>, i.e. on a real game
    /// client — never on the headless server). Installs Harmony finalizers on the FishNet spawn-callback
    /// chokepoint and on the player spawn callbacks so that, when a client throws while spawning a remote
    /// player object during join, we log the EXACT throwing method + full IL2CPP stack trace.
    ///
    /// Why: the second client to join the headless server gets stuck on the loading screen because it
    /// NREs while processing an already-present player's spawn; FishNet's ParseReader catches that NRE
    /// and logs only the message ("error while parsing data for packetId N") with no game stack, so the
    /// real culprit is invisible. These finalizers sit INSIDE FishNet's try/catch and see the exception
    /// before it is swallowed — and they re-throw it unchanged, so behaviour is unaffected (the bug still
    /// reproduces; we just also get the stack). On a MelonLoader client the throw is swallowed by
    /// Il2CppInterop anyway, so this is purely additive logging.
    ///
    /// Usage: run a MINIMAL MelonLoader client (ideally just this mod — remove UnityExplorer/Toasty to
    /// avoid unrelated startup issues), join the headless server as the SECOND client (with another
    /// client already connected), and send the MelonLoader log. Look for the "[ClientSpawnDiag]" lines.
    ///
    /// Type resolution uses direct <c>typeof</c> references (no <c>AccessTools.TypeByName</c>) so it does
    /// not trigger Harmony's broad cross-assembly type scan.
    /// </summary>
    internal static class ClientSpawnDiagnostics
    {
        public static void Install(HarmonyLib.Harmony harmony)
        {
            if (Application.isBatchMode) return; // clients only
            MelonLogger.Msg("[ClientSpawnDiag] v6 Installing client spawn-failure capture finalizers...");

            // Snowball-scoop diagnostic. PlayerControl.HandlePickingUpActions is the per-frame input
            // handler that actually starts a scoop; its inline gate requires more than the server-visible
            // YoureAllowedTo_PickupSnow — also networkAnimator != null and UiReferenceController not in an
            // active menu. We log the LOCAL player's full condition set (throttled) so we can see which
            // client-only condition blocks a player that can't scoop snow.
            TryPostfix(harmony, typeof(Il2Cpp.PlayerControl), "HandlePickingUpActions",
                nameof(HandlePickingUpActions_Postfix), "PlayerControl.HandlePickingUpActions");

            // Primary capture point: every NetworkObject spawn runs its behaviours' OnStartClient /
            // SyncType start callbacks through this. A throw in any of them propagates here before
            // FishNet's ParseReader swallows it.
            TryFinalize(harmony, typeof(Il2CppFishNet.Object.NetworkObject), "InvokeStartCallbacks",
                nameof(NetworkObject_InvokeStartCallbacks_Finalizer), "NetworkObject.InvokeStartCallbacks");

            // Confirmed culprit (v2 capture): PlayerReferenceManager.OnPlayerReferenceAdded NREs while a
            // joining client batch-processes the synced sync_PlayerReferences list. This hook dumps the
            // entry at the throwing index + the whole list state so we know which PlayerReference is bad
            // and what is null about it.
            TryFinalize(harmony, typeof(Il2Cpp.PlayerReferenceManager), "OnPlayerReferenceAdded",
                nameof(PlayerReferenceManager_OnPlayerReferenceAdded_Finalizer), "PlayerReferenceManager.OnPlayerReferenceAdded");

            // Specific capture points for the most likely culprits (give the exact behaviour directly).
            TryFinalize(harmony, typeof(Il2Cpp.PlayerControl), "OnStartClient",
                nameof(Generic_OnStartClient_Finalizer), "PlayerControl.OnStartClient");
            TryFinalize(harmony, typeof(Il2Cpp.Sled), "OnStartClient",
                nameof(Generic_OnStartClient_Finalizer), "Sled.OnStartClient");
            TryFinalize(harmony, typeof(Il2Cpp.PlayerSledController), "OnStartClient",
                nameof(Generic_OnStartClient_Finalizer), "PlayerSledController.OnStartClient");
            TryFinalize(harmony, typeof(Il2Cpp.PlayerMovement), "OnStartClient",
                nameof(Generic_OnStartClient_Finalizer), "PlayerMovement.OnStartClient");
        }

        // Finalizer for FishNet's spawn-callback chokepoint. __instance is the NetworkObject being spawned.
        private static Exception NetworkObject_InvokeStartCallbacks_Finalizer(
            Exception __exception, Il2CppFishNet.Object.NetworkObject __instance)
        {
            if (__exception != null)
            {
                string id = "?", owner = "?", name = "?";
                try
                {
                    if (__instance != null)
                    {
                        id = __instance.ObjectId.ToString();
                        owner = __instance.OwnerId.ToString();
                        name = __instance.gameObject != null ? __instance.gameObject.name : "?";
                    }
                }
                catch { }
                MelonLogger.Error($"[ClientSpawnDiag] InvokeStartCallbacks THREW on NetworkObject " +
                                  $"objId={id} owner={owner} name='{name}': {__exception.GetType().Name}: {__exception.Message}");
                MelonLogger.Error($"[ClientSpawnDiag] FULL: {__exception}");
            }
            return __exception; // re-throw unchanged — do not alter behaviour
        }

        // Finalizer for individual player spawn callbacks. __instance kept as object to reuse across types.
        private static Exception Generic_OnStartClient_Finalizer(
            Exception __exception, MethodBase __originalMethod, object __instance)
        {
            if (__exception != null)
            {
                string where = $"{__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name}";
                string owner = "?";
                try
                {
                    var nb = __instance as Il2CppFishNet.Object.NetworkBehaviour;
                    if (nb != null) owner = nb.OwnerId.ToString();
                }
                catch { }
                MelonLogger.Error($"[ClientSpawnDiag] {where} THREW (owner={owner}): " +
                                  $"{__exception.GetType().Name}: {__exception.Message}");
                MelonLogger.Error($"[ClientSpawnDiag] FULL: {__exception}");
            }
            return __exception; // re-throw unchanged
        }

        // Targeted capture for the confirmed culprit. Dumps the entry being processed and the whole list.
        private static Exception PlayerReferenceManager_OnPlayerReferenceAdded_Finalizer(
            Exception __exception, int index, Il2Cpp.PlayerReferenceManager __instance)
        {
            if (__exception != null)
            {
                MelonLogger.Error($"[ClientSpawnDiag] OnPlayerReferenceAdded THREW at index={index}: " +
                                  $"{__exception.GetType().Name}: {__exception.Message}");
                try
                {
                    var listProp = typeof(Il2Cpp.PlayerReferenceManager).GetProperty("sync_PlayerReferences",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var list = listProp?.GetValue(__instance);
                    if (list != null)
                    {
                        var lt = list.GetType();
                        int count = (int)lt.GetProperty("Count").GetValue(list);
                        var itemP = lt.GetProperty("Item");
                        MelonLogger.Error($"[ClientSpawnDiag] sync_PlayerReferences count={count}:");
                        for (int i = 0; i < count; i++)
                        {
                            var pr = itemP.GetValue(list, new object[] { i });
                            string conn = "?", puid = "?", user = "?", voice = "?", pc = "?", plat = "?";
                            try
                            {
                                var prt = pr.GetType();
                                conn = prt.GetProperty("ConnectionID")?.GetValue(pr)?.ToString();
                                puid = prt.GetProperty("ProductUserId")?.GetValue(pr)?.ToString() ?? "NULL";
                                user = prt.GetProperty("Username")?.GetValue(pr)?.ToString() ?? "NULL";
                                voice = prt.GetProperty("VoiceId")?.GetValue(pr)?.ToString() ?? "NULL";
                                plat = prt.GetProperty("PlatformUserId")?.GetValue(pr)?.ToString();
                                var pcVal = prt.GetProperty("PlayerControl")?.GetValue(pr);
                                pc = pcVal == null ? "NULL" : "set";
                            }
                            catch (Exception ie) { conn = "<err:" + ie.Message + ">"; }
                            MelonLogger.Error($"[ClientSpawnDiag]   [{i}]{(i == index ? " <== THREW" : "")} conn={conn} pc={pc} " +
                                              $"puid={puid} plat={plat} voice='{voice}' user='{user}'");
                        }
                    }
                }
                catch (Exception ex) { MelonLogger.Error($"[ClientSpawnDiag] (dump failed: {ex.Message})"); }
                MelonLogger.Error($"[ClientSpawnDiag] FULL: {__exception}");
            }
            return __exception; // re-throw unchanged
        }

        // Logs the LOCAL player's full snow-scoop condition set (throttled to ~once/sec) so we can see
        // which client-only condition blocks a player that can't pick up snow. Mirrors the inline gate
        // inside PlayerControl.HandlePickingUpActions.
        private static int _lastPickupLogFrame = -1000;
        private static void HandlePickingUpActions_Postfix(Il2Cpp.PlayerControl __instance)
        {
            try
            {
                if (__instance == null || !__instance.IsOwner) return; // local player only
                int now = UnityEngine.Time.frameCount;
                if (now - _lastPickupLogFrame < 60) return; // ~1s throttle
                _lastPickupLogFrame = now;

                string gate = "?", snow = "?", foot = "?", footInit = "?", owner = "?", hands = "?", anim = "?", uiRef = "?", menu = "?", grounded = "?", hold = "?", roll = "?";
                try { var m = AccessTools.Method(typeof(Il2Cpp.PlayerControl), "YoureAllowedTo_PickupSnow"); gate = m == null ? "no-method" : m.Invoke(__instance, null)?.ToString(); } catch (Exception e) { gate = "err:" + (e.InnerException ?? e).GetType().Name; }
                try { var mv = __instance.movement; snow = mv == null ? "mv-NULL" : mv.GetIsStandingOnSnow().ToString(); } catch (Exception e) { snow = "err:" + e.GetType().Name; }
                try { var mv = __instance.movement; var sv = mv?.sync_CurrentFootstepCollection; foot = sv == null ? "sv-NULL" : sv.Value.ToString(); footInit = sv == null ? "?" : sv.IsInitialized.ToString(); } catch (Exception e) { foot = "err:" + e.GetType().Name; }
                try { var nob = __instance.NetworkObject; owner = nob == null ? "?" : nob.OwnerId.ToString(); } catch { owner = "err"; }
                try { var m = AccessTools.Method(typeof(Il2Cpp.PlayerControl), "YoureAllowedTo_UseYourHands"); hands = m == null ? "no-method" : m.Invoke(__instance, null)?.ToString(); } catch (Exception e) { hands = "err:" + (e.InnerException ?? e).GetType().Name; }
                try { anim = ReadProp(__instance, "networkAnimator") == null ? "NULL" : "set"; } catch { anim = "err"; }
                try { grounded = ReadProp(__instance, "local_CharacterIsGrounded")?.ToString() ?? "?"; } catch { grounded = "err"; }
                try { roll = ReadProp(__instance, "snowmanRollingController") == null ? "NULL(noPrompt)" : "set"; } catch { roll = "err"; }
                try { var hc = __instance.holdingController; hold = hc == null ? "hc-NULL" : hc.GetIsHoldingSomething().ToString(); } catch (Exception e) { hold = "err:" + e.GetType().Name; }
                try { var ui = Il2Cpp.UiReferenceController.Instance; uiRef = (ui != null).ToString(); menu = ui == null ? "?" : ui.isMenuActive.ToString(); } catch (Exception e) { uiRef = "err:" + e.GetType().Name; }

                MelonLogger.Msg($"[ClientPickupDiag] owner={owner} GATE={gate} | footstepType={foot} footInit={footInit} standingOnSnow={snow} | " +
                                $"allowedHands={hands} grounded={grounded} safezone={SafeStr(() => __instance.IsInSafeZone)} peaceful={SafeStr(() => __instance.IsPeaceful)} | " +
                                $"snowmanRollingCtrl={roll} networkAnimator={anim} uiRefInstance={uiRef} menuActive={menu} alreadyHolding={hold}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[ClientPickupDiag] postfix error: {ex.Message}"); }
        }

        private static object ReadProp(object inst, string name)
        {
            var p = inst.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return p?.GetValue(inst);
        }

        private static string SafeStr(Func<object> f)
        {
            try { return f()?.ToString() ?? "null"; } catch (Exception e) { return "err:" + e.GetType().Name; }
        }

        private static void TryPostfix(HarmonyLib.Harmony harmony, Type targetType, string methodName,
            string postfix, string label)
        {
            if (targetType == null) { MelonLogger.Warning($"[ClientSpawnDiag] Null type for '{label}'."); return; }
            var method = AccessTools.Method(targetType, methodName)
                         ?? targetType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) { MelonLogger.Warning($"[ClientSpawnDiag] Method '{label}' not found on {targetType.FullName}."); return; }
            try
            {
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(ClientSpawnDiagnostics), postfix));
                MelonLogger.Msg($"[ClientSpawnDiag] Hooked (postfix) '{label}'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[ClientSpawnDiag] Failed to hook '{label}': {ex.Message}"); }
        }

        private static void TryFinalize(HarmonyLib.Harmony harmony, Type targetType, string methodName,
            string finalizer, string label)
        {
            if (targetType == null) { MelonLogger.Warning($"[ClientSpawnDiag] Null type for '{label}'."); return; }

            // Targeted method lookup on a known type — does NOT scan other assemblies.
            var method = AccessTools.Method(targetType, methodName)
                         ?? targetType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) { MelonLogger.Warning($"[ClientSpawnDiag] Method '{label}' not found on {targetType.FullName}."); return; }

            try
            {
                harmony.Patch(method, finalizer: new HarmonyMethod(typeof(ClientSpawnDiagnostics), finalizer));
                MelonLogger.Msg($"[ClientSpawnDiag] Hooked '{label}'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[ClientSpawnDiag] Failed to hook '{label}': {ex.Message}"); }
        }
    }
}
