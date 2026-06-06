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
    }
}
