using System;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace SledHeadless
{
    internal static partial class HeadlessPatches
    {
        // ── Per-instance EOS identity (run multiple headless servers on one machine) ──────
        //
        // The headless server authenticates to EOS via a Connect DeviceId, which the SDK derives from the
        // machine's device fingerprint and persists. Every instance on the same machine therefore logs in
        // as the SAME persistent ProductUserId (PUID). EOS P2P/relay is keyed on the host PUID, so when two
        // same-PUID servers run at once a joining client's packets get routed/split across both — the server
        // only sees part of the client's stream, never advances that connection's FishNet PacketTick, and
        // the client is dropped after ~30-60s ("timed out"). (DeviceModel is only a display label; it does
        // NOT change the PUID.)
        //
        // Fix: give each instance its own EOS persistent storage so the SDK mints a SEPARATE, STABLE DeviceId
        // (hence PUID) per instance. The lever is the EOS platform's CacheDirectory (where the SDK persists
        // the DeviceId). We Harmony-prefix the managed Epic.OnlineServices.Platform.PlatformInterface.Create
        // (called once during the game's EOS init) and rewrite Options.CacheDirectory to a per-instance path
        // BEFORE the native platform is created. The instance is keyed by a stable id: the ServerInstanceId
        // preference if set, otherwise an auto-generated GUID stored in UserData/SledHeadless-instance.id and
        // reused on every restart (so a given install keeps the same PUID across restarts).
        //
        // Verification: we log the chosen id + cache dir here, and the resulting PUID is logged at host
        // registration ([HOSTPLR]) — two instances should show different PUIDs. If the PUID does NOT change,
        // this SDK persists the DeviceId outside CacheDirectory and we'd need a different lever.

        private static string _instanceId;

        // Resolves this instance's stable identity id from the ServerInstanceId MelonPreference. If that pref
        // is blank, generate a GUID and PERSIST it back into the pref (so it's visible/editable in
        // UserData/MelonPreferences.cfg and reused on every restart). Each install has its own MelonPreferences,
        // so cloning the server folder yields a fresh auto-id = a distinct EOS identity. Sanitized for a path.
        internal static string GetOrCreateInstanceId()
        {
            if (!string.IsNullOrEmpty(_instanceId)) return _instanceId;

            try
            {
                string pref = SledHeadlessCore.ServerInstanceId;
                if (!string.IsNullOrWhiteSpace(pref))
                {
                    _instanceId = SanitizeId(pref);
                    MelonLogger.Msg($"[HeadlessMode][EOSID] EOS instance id (from ServerInstanceId pref) → '{_instanceId}'.");
                    return _instanceId;
                }

                _instanceId = Guid.NewGuid().ToString("N");
                SledHeadlessCore.SetServerInstanceId(_instanceId);
                MelonLogger.Msg($"[HeadlessMode][EOSID] Generated EOS instance id '{_instanceId}' and saved it to MelonPreferences (SledHeadless → ServerInstanceId). " +
                                "Edit that value to rename/change this server's EOS identity.");
            }
            catch (Exception ex)
            {
                _instanceId = string.IsNullOrEmpty(_instanceId) ? "default" : _instanceId;
                MelonLogger.Warning($"[HeadlessMode][EOSID] Could not resolve/persist instance id ({ex.GetType().Name}: {ex.Message}); using '{_instanceId}'.");
            }
            return _instanceId;
        }

        private static string SanitizeId(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            string r = sb.ToString();
            return string.IsNullOrEmpty(r) ? "default" : (r.Length > 64 ? r.Substring(0, 64) : r);
        }

        // Absolute, per-instance EOS cache directory (created if missing).
        private static string GetInstanceEosCacheDir()
        {
            try
            {
                string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "EOSCache", GetOrCreateInstanceId());
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode][EOSID] Could not create EOS cache dir: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // Registers the PlatformInterface.Create prefix. Must run before the game creates the EOS platform
        // (we call this from ApplyPatches in OnInitializeMelon, which is well before the EOS boot step).
        // NOTE: the entire body is wrapped in try/catch. This runs FIRST in ApplyPatches, so a throw here
        // (e.g. AccessTools.Method's AmbiguousMatchException — PlatformInterface has multiple Create overloads)
        // would otherwise abort the WHOLE patch run and break the server. Never let that happen.
        private static void PatchEosPlatformCacheDirectory(HarmonyLib.Harmony harmony)
        {
            if (!Application.isBatchMode) return;
            try
            {
                var t = AccessTools.TypeByName("Il2CppEpic.OnlineServices.Platform.PlatformInterface")
                     ?? AccessTools.TypeByName("Epic.OnlineServices.Platform.PlatformInterface");
                if (t == null) { MelonLogger.Warning("[HeadlessMode][EOSID] PlatformInterface type not found — EOS cache isolation skipped (servers will share a PUID)."); return; }

                var optType = AccessTools.TypeByName("Il2CppEpic.OnlineServices.Platform.Options")
                           ?? AccessTools.TypeByName("Epic.OnlineServices.Platform.Options");
                var woptType = AccessTools.TypeByName("Il2CppEpic.OnlineServices.Platform.WindowsOptions")
                            ?? AccessTools.TypeByName("Epic.OnlineServices.Platform.WindowsOptions");

                // PlatformInterface.Create is overloaded AND its parameters are BY-REF: confirmed live there are
                // Create(ref Options) and Create(ref WindowsOptions) (the param type is "Options&"/"WindowsOptions&").
                // AccessTools.Method(t,"Create") throws AmbiguousMatchException, and a plain `pt == Options` match
                // misses because pt is the by-ref type. Unwrap by-ref (GetElementType) and patch BOTH overloads —
                // the game calls one of them on Windows (we don't know which, so cover both; the unused prefix
                // simply never fires). Both Options and WindowsOptions are CLASSES with a writable CacheDirectory.
                int patched = 0;
                var methods = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != "Create") continue;
                    var ps = methods[i].GetParameters();
                    if (ps.Length != 1) continue;
                    var pt = ps[0].ParameterType;
                    var elem = pt.IsByRef ? pt.GetElementType() : pt;
                    string prefixName = null;
                    if (optType != null && elem == optType) prefixName = nameof(PlatformInterface_Create_Options_Prefix);
                    else if (woptType != null && elem == woptType) prefixName = nameof(PlatformInterface_Create_WindowsOptions_Prefix);
                    if (prefixName == null) continue;
                    try
                    {
                        harmony.Patch(methods[i], prefix: new HarmonyMethod(typeof(HeadlessPatches), prefixName));
                        patched++;
                        MelonLogger.Msg($"[HeadlessMode][EOSID] Patched PlatformInterface.Create(ref {elem.Name}).");
                    }
                    catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][EOSID] patch Create(ref {elem.Name}) failed: {ex.GetType().Name}: {ex.Message}"); }
                }

                if (patched == 0) { MelonLogger.Warning("[HeadlessMode][EOSID] No Create(ref Options/WindowsOptions) overload patched — EOS cache isolation skipped."); return; }
                MelonLogger.Msg($"[HeadlessMode][EOSID] EOS cache isolation armed for instance '{GetOrCreateInstanceId()}' ({patched} overload(s)).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode][EOSID] EOS cache isolation setup failed (servers will share a PUID): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Prefixes: rewrite the EOS platform CacheDirectory to this instance's private path BEFORE the native
        // platform is created, so the SDK mints/loads a per-instance DeviceId. Both Create overloads take their
        // options BY-REF; both option types are classes with a CacheDirectory property.
        private static void PlatformInterface_Create_Options_Prefix(ref Il2CppEpic.OnlineServices.Platform.Options __0)
        {
            if (!Application.isBatchMode || _isQuitting || __0 == null) return;
            try
            {
                string dir = GetInstanceEosCacheDir();
                if (string.IsNullOrEmpty(dir)) return;
                string before = null; try { before = __0.CacheDirectory; } catch { }
                __0.CacheDirectory = dir;
                MelonLogger.Msg($"[HeadlessMode][EOSID] EOS CacheDirectory (Options) for instance '{_instanceId}': '{before ?? "<null>"}' → '{dir}'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][EOSID] Options CacheDirectory override failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void PlatformInterface_Create_WindowsOptions_Prefix(ref Il2CppEpic.OnlineServices.Platform.WindowsOptions __0)
        {
            if (!Application.isBatchMode || _isQuitting || __0 == null) return;
            try
            {
                string dir = GetInstanceEosCacheDir();
                if (string.IsNullOrEmpty(dir)) return;
                string before = null; try { before = __0.CacheDirectory; } catch { }
                __0.CacheDirectory = dir;
                MelonLogger.Msg($"[HeadlessMode][EOSID] EOS CacheDirectory (WindowsOptions) for instance '{_instanceId}': '{before ?? "<null>"}' → '{dir}'.");
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][EOSID] WindowsOptions CacheDirectory override failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
