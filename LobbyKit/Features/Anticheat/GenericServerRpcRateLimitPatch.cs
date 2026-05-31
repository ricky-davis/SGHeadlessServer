using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib; // HarmonyMethod, HarmonyPatch
using Il2CppFishNet;
using Il2CppFishNet.Managing.Server;
using Il2CppFishNet.Object;
using MelonLoader;
using UnityEngine;

namespace LobbyKit.Features.Anticheat
{
    internal static class GenericServerRpcRateLimitPatch
    {
        // Set to true to log every ServerRpc call — useful for observing natural call
        // rates before committing to per-command limits.
        internal static bool LogAllRpcCalls = true;

        private const int HostConnectionId = 32767;

        // Loose limit intended to catch actual spam, not restrict normal gameplay. Any faster than this number we treat as a potential exploit attempt.
        private const float MinInterval = 0.05f; // 50ms — max 20 calls/sec per client

        // Per-command overrides — keyed by friendly name (e.g. "ThrowObject").
        // Commands listed here use their specific limit instead of MinInterval.
        // Any faster than this number we treat as a potential exploit attempt.
        private static readonly Dictionary<string, float> _commandOverrides = new()
        {
            { "ThrowObject", 0.5f },
        };

        // FishNet host-mode calls RpcLogic twice for the same command in the same frame.
        // Any gap shorter than this is treated as that duplicate, not a separate call.
        private const float SameCallTolerance = 0.015f;

        private const int ViolationsBeforeKick = 3;
        private const float ViolationWindow = 3f;

        // Key: (ownerId, method metadata token) — MetadataToken is an int unique per
        // method in the assembly, so this is two int comparisons with no string allocation.
        private static readonly Dictionary<(int, int), float> _lastCallTime = [];
        private static readonly Dictionary<(int, int), int> _violationCount = [];
        private static readonly Dictionary<(int, int), float> _violationWindowStart = [];

        // Friendly name cache: strips "RpcLogic___Cmd_" prefix and trailing "___<hash>"
        // so logs read "ThrowObject" instead of "RpcLogic___Cmd_ThrowObject___1971385254".
        private static readonly Dictionary<int, string> _friendlyNames = [];

        internal static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            var asm = Array.Find(
                AppDomain.CurrentDomain.GetAssemblies(),
                a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Warning("[LobbyKit] Anticheat: Assembly-CSharp not found — generic ServerRpc rate limit not applied.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(GenericServerRpcRateLimitPatch), nameof(Prefix));
            int count = 0;

            foreach (var type in asm.GetTypes())
            {
                foreach (var method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!method.Name.StartsWith("RpcLogic___Cmd_", StringComparison.Ordinal))
                        continue;

                    try
                    {
                        harmony.Patch(method, prefix: prefix);
                        _friendlyNames[method.MetadataToken] = FriendlyName(method.Name);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[LobbyKit] Anticheat: could not patch {type.Name}.{method.Name}: {ex.Message}");
                    }
                }
            }

            MelonLogger.Msg($"[LobbyKit] Anticheat: generic ServerRpc rate limit applied to {count} Cmd method(s).");
        }

        private static bool Prefix(NetworkBehaviour __instance, MethodBase __originalMethod)
        {
            int ownerId = __instance.OwnerId;

            if (ownerId == HostConnectionId || !LobbyKitCore.EnableAnticheat)
                return true;
            float now = Time.realtimeSinceStartup;
            int token = __originalMethod.MetadataToken;
            var key = (ownerId, token);
            _friendlyNames.TryGetValue(token, out string name);

            if (!_lastCallTime.TryGetValue(key, out float lastTime))
            {
                _lastCallTime[key] = now;
                if (LogAllRpcCalls)
                    MelonLogger.Msg($"[RPC] {name ?? __originalMethod.Name} | client={ownerId} | first call");
                return true;
            }

            float elapsed = now - lastTime;

            if (elapsed < SameCallTolerance)
                return true; // FishNet host-mode duplicate call — same command, same frame

            float limit = name != null && _commandOverrides.TryGetValue(name, out float ov) ? ov : MinInterval;

            if (LogAllRpcCalls)
            {
                string tag = elapsed < limit ? " BLOCKED" : string.Empty;
                MelonLogger.Msg($"[RPC] {name ?? __originalMethod.Name} | client={ownerId} | gap={elapsed * 1000f:F0}ms{tag}");
            }

            if (elapsed < limit)
            {
                if (!LogAllRpcCalls)
                    MelonLogger.Warning($"[LobbyKit] Anticheat: client {ownerId} spamming {name ?? __originalMethod.Name} ({elapsed * 1000f:F0}ms since last), blocking.");
                RecordViolation(ownerId, key, name ?? __originalMethod.Name, now);
                return false;
            }

            _lastCallTime[key] = now;

            // If the client had prior violations but their window has since expired,
            // clear the stale tracking so they start fresh next time they spam.
            if (_violationWindowStart.TryGetValue(key, out float windowStart) && now - windowStart > ViolationWindow)
            {
                _violationCount.Remove(key);
                _violationWindowStart.Remove(key);
            }

            return true;
        }

        private static string FriendlyName(string methodName)
        {
            const string prefix = "RpcLogic___Cmd_";
            if (methodName.StartsWith(prefix, StringComparison.Ordinal))
                methodName = methodName[prefix.Length..];
            int sep = methodName.IndexOf("___", StringComparison.Ordinal);
            return sep >= 0 ? methodName[..sep] : methodName;
        }

        private static void RecordViolation(int ownerId, (int, int) key, string methodName, float now)
        {
            _violationWindowStart.TryGetValue(key, out float windowStart);
            _violationCount.TryGetValue(key, out int count);

            if (now - windowStart > ViolationWindow)
            {
                count = 1;
                _violationWindowStart[key] = now;
            }
            else
            {
                count++;
            }

            _violationCount[key] = count;

            if (count >= ViolationsBeforeKick)
            {
                MelonLogger.Warning($"[LobbyKit] Anticheat: kicking client {ownerId} for spamming {methodName} ({count} violations in {ViolationWindow}s).");
                KickAnnouncer.Enqueue(ownerId, "was kicked for cheating (experimental)");
                try { InstanceFinder.ServerManager?.Kick(ownerId, KickReason.ExploitAttempt); }
                catch { MelonLogger.Warning($"[LobbyKit] Anticheat: failed to kick client {ownerId}."); }
                _violationCount.Remove(key);
                _violationWindowStart.Remove(key);
                _lastCallTime.Remove(key);
            }
        }
    }
}
