using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppEpic.OnlineServices;
using Il2CppPlayEveryWare.EpicOnlineServices;
using Il2CppPlayEveryWare.EpicOnlineServices.Samples;
using Il2Cpp_Scripts.Managers;
using MelonLoader;

namespace SledHeadless
{
    // Destroys the EOS lobby before the headless process exits, so it doesn't linger as a ghost on the EOS
    // lobby list. The catch: shutdown signals (Ctrl+C, console close, logoff/shutdown) fire on a Windows-
    // injected thread where calling Il2Cpp/Unity is unsafe. So a signal just REQUESTS shutdown and BLOCKS;
    // the Unity main thread (SledHeadlessCore.OnUpdate -> Tick) runs the actual destroy + EOS pump and signals
    // completion, then the signal handler returns and lets the process terminate. OnApplicationQuit (already
    // on the main thread) runs the same path for the graceful Application.Quit case. A hard kill (taskkill /f)
    // can't be intercepted by anything.
    internal static class HeadlessShutdown
    {
        private const int PumpTimeoutMs = 3000;   // max time to pump EOS waiting for the destroy callback
        private const int WaitTimeoutMs = 4000;   // max time a signal handler blocks for the main thread

        private static volatile bool _requested;
        private static volatile bool _started;
        private static volatile bool _complete;
        private static readonly object _lock = new object();
        private static ConsoleCtrlDelegate _consoleHandler;   // kept alive against GC

        public static void Install()
        {
            try
            {
                _consoleHandler = OnConsoleCtrl;
                SetConsoleCtrlHandler(_consoleHandler, true);
                MelonLogger.Msg("[HeadlessMode] Graceful-shutdown hook installed (lobby is destroyed on exit).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode] Shutdown hook install failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Main-thread tick (from OnUpdate): run the destroy if a signal requested it.
        public static void Tick()
        {
            if (_requested && !_started)
                DestroyLobbyMainThread("signal");
        }

        // Main-thread quit (from OnApplicationQuit).
        public static void OnQuit() => DestroyLobbyMainThread("OnApplicationQuit");

        // Off-thread signal handlers: request shutdown, then block until the main thread finishes (or timeout).
        private static void RequestAndWait()
        {
            _requested = true;
            var sw = Stopwatch.StartNew();
            while (!_complete && sw.ElapsedMilliseconds < WaitTimeoutMs)
                Thread.Sleep(20);
        }

        private static void DestroyLobbyMainThread(string source)
        {
            lock (_lock) { if (_started) return; _started = true; }   // run exactly once
            try
            {
                LobbyManager lm = LobbyManager.Instance;
                string lobbyId = null;
                try { lobbyId = lm?.GetLobbyId(); } catch { }
                if (lm == null || string.IsNullOrEmpty(lobbyId))
                {
                    MelonLogger.Msg($"[HeadlessMode] Shutdown ({source}): no active lobby to destroy.");
                    return;
                }

                EOSLobbyManager eos = ResolveEosLobbyManager(lm);
                if (eos == null)
                {
                    MelonLogger.Warning("[HeadlessMode] Shutdown: EOSLobbyManager unavailable — cannot destroy lobby cleanly.");
                    return;
                }

                MelonLogger.Msg($"[HeadlessMode] Shutdown ({source}): destroying lobby {lobbyId}...");
                bool destroyed = false;
                Action<Result> onDone = (Result r) => { destroyed = true; MelonLogger.Msg($"[HeadlessMode] Lobby destroy result: {r}"); };
                EOSLobbyManager.OnLobbyCallback cb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EOSLobbyManager.OnLobbyCallback>(onDone);
                eos.DestroyCurrentLobby(ref cb);

                var platform = EOSManager.Instance?.GetEOSPlatformInterface();
                var sw = Stopwatch.StartNew();
                while (!destroyed && sw.ElapsedMilliseconds < PumpTimeoutMs)
                {
                    try { platform?.Tick(); } catch { }
                    Thread.Sleep(20);
                }

                MelonLogger.Msg(destroyed
                    ? $"[HeadlessMode] Lobby destroyed cleanly in {sw.ElapsedMilliseconds}ms."
                    : "[HeadlessMode] Lobby destroy did not confirm within timeout — it may linger briefly on EOS.");
                if (destroyed) HeadlessGhostSweep.ClearRemembered();   // nothing left for the next-boot sweep
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HeadlessMode] Shutdown destroy failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _complete = true;   // unblock any waiting signal handler
            }
        }

        private static EOSLobbyManager ResolveEosLobbyManager(LobbyManager lm)
        {
            try
            {
                PropertyInfo prop = typeof(LobbyManager).GetProperty("_lobbyManager",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object raw = prop?.GetValue(lm);
                return (raw as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<EOSLobbyManager>() ?? raw as EOSLobbyManager;
            }
            catch { return null; }
        }

        // Win32 console control handler: catches Ctrl+C(0), Ctrl+Break(1), console close(2), logoff(5), shutdown(6).
        private delegate bool ConsoleCtrlDelegate(int ctrlType);
        [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

        private static bool OnConsoleCtrl(int ctrlType)
        {
            RequestAndWait();   // block until the main thread has destroyed the lobby
            return false;       // not handled -> let Windows terminate the process now that we've cleaned up
        }
    }
}
