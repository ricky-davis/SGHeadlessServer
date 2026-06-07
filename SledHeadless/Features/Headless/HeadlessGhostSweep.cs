using System;
using System.IO;
using Il2CppEpic.OnlineServices;
using Il2CppEpic.OnlineServices.Lobby;
using Il2CppPlayEveryWare.EpicOnlineServices;
using Il2CppInterop.Runtime;
using MelonLoader;
using MelonLoader.Utils;

namespace SledHeadless
{
    // Crash-ghost cleanup. HeadlessShutdown destroys the lobby on a GRACEFUL exit, but a crash/hard-kill leaves
    // the EOS lobby alive as a ghost. Because the headless server logs in with the SAME persistent PUID every
    // restart, it still OWNS that leftover lobby and can destroy it. So: we record our lobby ID to a file when
    // we create it; on the next startup, before creating the new lobby, we destroy whatever ID that file holds.
    // A crash-ghost therefore vanishes the moment the server comes back instead of lingering on the EOS list.
    internal static class HeadlessGhostSweep
    {
        private static string FilePath => Path.Combine(MelonEnvironment.UserDataDirectory, "SledHeadless-lastlobby.txt");
        private static OnDestroyLobbyCallback _callbackRef;   // keep the converted delegate alive against GC

        // Record our current lobby ID so the next startup can sweep it if we don't exit cleanly.
        public static void RememberLobby(string lobbyId)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(lobbyId))
                    File.WriteAllText(FilePath, lobbyId.Trim());
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode][GhostSweep] Remember failed: {ex.Message}"); }
        }

        // Called after a confirmed graceful destroy so we don't pointlessly re-sweep a lobby that's already gone.
        public static void ClearRemembered()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }

        // Destroy any lobby left behind by a prior session. Pass the just-created lobby ID (or null) so we never
        // destroy our own live lobby. Fire-and-forget: the request is sent and the game's normal platform tick
        // completes it in the background.
        public static void Sweep(string currentLobbyId)
        {
            string stale;
            try { stale = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : null; }
            catch { return; }

            if (string.IsNullOrWhiteSpace(stale)) return;
            if (!string.IsNullOrWhiteSpace(currentLobbyId) &&
                string.Equals(stale, currentLobbyId, StringComparison.OrdinalIgnoreCase))
                return;   // that's our live lobby, not a ghost

            try
            {
                var em = EOSManager.Instance;
                ProductUserId puid = em?.GetProductUserId();
                LobbyInterface lobbyIf = em?.GetEOSPlatformInterface()?.GetLobbyInterface();
                if (puid == null || lobbyIf == null)
                {
                    MelonLogger.Warning("[HeadlessMode][GhostSweep] EOS not ready (no PUID/lobby interface); skipping sweep.");
                    return;
                }

                var opts = new DestroyLobbyOptions();
                opts.LocalUserId = puid;
                opts.LobbyId = (Utf8String)stale;

                _callbackRef = DelegateSupport.ConvertDelegate<OnDestroyLobbyCallback>((DestroyLobbyManagedCb)OnDestroyed);
                MelonLogger.Msg($"[HeadlessMode][GhostSweep] Destroying leftover lobby {stale} from a prior session...");
                lobbyIf.DestroyLobby(ref opts, null, _callbackRef);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode][GhostSweep] Sweep failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private delegate void DestroyLobbyManagedCb(ref DestroyLobbyCallbackInfo data);

        private static void OnDestroyed(ref DestroyLobbyCallbackInfo data)
        {
            // Success = the ghost is gone. NotFound = EOS already evicted it (also fine). Anything else is logged.
            try { MelonLogger.Msg($"[HeadlessMode][GhostSweep] Leftover lobby destroy result: {data.ResultCode}"); }
            catch { }
        }
    }
}
