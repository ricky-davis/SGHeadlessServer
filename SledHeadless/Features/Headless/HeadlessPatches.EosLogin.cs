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
        // ── Direct EOS C API for Device Auth ─────────────────────────────────────────
        // Il2CppInterop cannot bridge delegates whose parameters are non-blittable Il2Cpp
        // structs (the EOS callback info structs contain raw pointers), so we bypass the PEWS
        // managed wrappers entirely and call EOSSDK-Win64-Shipping.dll directly via P/Invoke.
        //
        // Struct layouts mirror *Internal variants from the Il2Cpp dump. Field offsets use
        // LayoutKind.Explicit because the EOS SDK packs structs with 8-byte alignment on
        // 64-bit Windows regardless of field size (e.g. int ApiVersion at 0x0, next ptr at 0x8).
        // Verified against dump.cs offsets.

        [StructLayout(LayoutKind.Explicit)]
        private struct EosCredentials
        {
            [FieldOffset(0x0)] public int ApiVersion;   // = 1
            [FieldOffset(0x8)] public IntPtr Token;     // null for Device Auth
            [FieldOffset(0x10)] public int Type;        // EOS_ECT_DEVICEID_ACCESS_TOKEN = 10
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct EosUserLoginInfo
        {
            [FieldOffset(0x0)] public int ApiVersion;   // = 2
            [FieldOffset(0x8)] public IntPtr DisplayName;
            [FieldOffset(0x10)] public IntPtr NsaIdToken; // null
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct EosConnectLoginOptions
        {
            [FieldOffset(0x0)] public int ApiVersion;   // = 2
            [FieldOffset(0x8)] public IntPtr Credentials;
            [FieldOffset(0x10)] public IntPtr UserLoginInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct EosCreateDeviceIdOptions
        {
            [FieldOffset(0x0)] public int ApiVersion;     // = 1
            [FieldOffset(0x8)] public IntPtr DeviceModel; // null = use default hardware fingerprint
        }

// LoginCallbackInfoInternal: ResultCode@0x0, ClientData@0x8, LocalUserId@0x10
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EosLoginRawCallback(IntPtr info);

        // CreateDeviceIdCallbackInfoInternal: ResultCode@0x0, ClientData@0x8
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EosCreateDeviceIdRawCallback(IntPtr info);

        // CreateUser options: ApiVersion@0x0, ContinuanceToken@0x8
        [StructLayout(LayoutKind.Explicit)]
        private struct EosCreateUserOptions
        {
            [FieldOffset(0x0)] public int ApiVersion;
            [FieldOffset(0x8)] public IntPtr ContinuanceToken;
        }

        // Static fields pin the delegates so the GC never collects them while the native EOS
        // callback table still holds a raw function pointer to them.
        private static readonly EosLoginRawCallback _pinnedLoginCb = OnEosLoginCallback;
        private static readonly EosCreateDeviceIdRawCallback _pinnedCreateDeviceCb = OnEosCreateDeviceIdCallback;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EosCreateUserRawCallback(IntPtr info);
        private static readonly EosCreateUserRawCallback _pinnedCreateUserCb = OnEosCreateUserCallback;

        // Shared state flags written by the P/Invoke callbacks and polled by the coroutine loop.
        // These are not thread-safe but EOS callbacks fire on the same Unity main thread (via
        // EOS SDK's tick mechanism inside EOSManager.Update), so no synchronisation is needed.
        private static bool _deviceAuthDone;
        private static bool _deviceAuthSuccess;
        private static bool _createDeviceIdDone;
        private static bool _createDeviceIdSuccess;
        private static bool _createUserDone;
        private static bool _createUserSuccess;
        // ContinuanceToken from a failed EOS_Connect_Login (EOS_InvalidUser) — EOS issues this
        // token so the caller can pass it straight to EOS_Connect_CreateUser without re-authenticating.
        private static IntPtr _loginContinuanceToken;

        // Raw ProductUserId handle from OnEosLoginCallback, used by the P/Invoke fallback path
        // to inject the PUID into PEWS when StartConnectLoginWithDeviceToken is unavailable.
        private static IntPtr _rawProductUserId = IntPtr.Zero;

        /// <summary>
        /// Native callback for <c>EOS_Connect_Login</c>.
        /// On success, captures the raw <c>ProductUserId</c> handle and signals the waiting
        /// coroutine. On first-time failure (<c>EOS_InvalidUser</c>), EOS sets <c>ContinuanceToken</c>
        /// instead of <c>LocalUserId</c> — we capture that too for the CreateUser retry path.
        /// Struct layout: ResultCode@0x0, ClientData@0x8, LocalUserId@0x10, ContinuanceToken@0x18.
        /// </summary>
        private static void OnEosLoginCallback(IntPtr info)
        {
            if (info == IntPtr.Zero) { _deviceAuthDone = true; return; }
            int raw = Marshal.ReadInt32(info, 0x0); // ResultCode
            var result = (Result)raw;
            _deviceAuthSuccess = result == Result.Success;
            if (_deviceAuthSuccess)
                _rawProductUserId = Marshal.ReadIntPtr(info, 0x10); // ProductUserId handle
            _loginContinuanceToken = Marshal.ReadIntPtr(info, 0x18); // ContinuanceToken (set on EOS_InvalidUser)
            MelonLogger.Msg($"[HeadlessMode] EOS_Connect_Login callback: {result}");
            _deviceAuthDone = true;
        }

        /// <summary>
        /// Native callback for <c>EOS_Connect_CreateDeviceId</c>.
        /// <c>DuplicateNotAllowed</c> (24) means a DeviceId credential already exists on this
        /// machine — this is not an error; treat it the same as Success and proceed to login.
        /// Raw value 1004 is an undocumented variant seen in some SDK versions for the same condition.
        /// </summary>
        private static void OnEosCreateDeviceIdCallback(IntPtr info)
        {
            if (info == IntPtr.Zero) { _createDeviceIdDone = true; return; }
            int raw = Marshal.ReadInt32(info, 0x0);
            var result = (Result)raw;
            // DuplicateNotAllowed (24) = device ID already exists on this machine — also a success path
            _createDeviceIdSuccess = result == Result.Success || result == Result.DuplicateNotAllowed || raw == 1004;
            MelonLogger.Msg($"[HeadlessMode] EOS_Connect_CreateDeviceId callback: {result}");
            _createDeviceIdDone = true;
        }

        // Native callback for EOS_Connect_CreateUser — called when we create a new EOS Connect
        // account for this machine's DeviceId on first ever login.
        private static void OnEosCreateUserCallback(IntPtr info)
        {
            if (info == IntPtr.Zero) { _createUserDone = true; return; }
            int raw = Marshal.ReadInt32(info, 0x0);
            var result = (Result)raw;
            _createUserSuccess = result == Result.Success;
            MelonLogger.Msg($"[HeadlessMode] EOS_Connect_CreateUser callback: {result}");
            _createUserDone = true;
        }

        /// <summary>
        /// Calls <c>EOS_Connect_Login</c> with <c>EOS_ECT_DEVICEID_ACCESS_TOKEN</c> credentials.
        ///
        /// Structs are allocated in unmanaged memory rather than declared as local value types
        /// because C# iterators (yield-based coroutines) cannot take the address of locals —
        /// they live on the heap as fields of the compiler-generated state machine class, which
        /// moves them unpredictably. Unmanaged allocations have a fixed address for the duration
        /// of the call. They are freed in the finally block before returning.
        /// </summary>
        private static void CallEosDeviceLogin(IntPtr handle, IntPtr displayNamePtr, IntPtr loginCbPtr)
        {
            IntPtr credsPtr  = Marshal.AllocHGlobal(24);  // EosCredentials: ApiVersion@0, Token@8, Type@16
            IntPtr userPtr   = Marshal.AllocHGlobal(24);  // EosUserLoginInfo: ApiVersion@0, DisplayName@8, NsaIdToken@16
            IntPtr optsPtr   = Marshal.AllocHGlobal(24);  // EosConnectLoginOptions: ApiVersion@0, Creds@8, User@16
            try
            {
                Marshal.WriteInt32(credsPtr, 0x0, 1);          // ApiVersion
                Marshal.WriteIntPtr(credsPtr, 0x8, IntPtr.Zero); // Token = null
                Marshal.WriteInt32(credsPtr, 0x10, 10);         // Type = DeviceId

                Marshal.WriteInt32(userPtr, 0x0, 2);           // ApiVersion
                Marshal.WriteIntPtr(userPtr, 0x8, displayNamePtr);
                Marshal.WriteIntPtr(userPtr, 0x10, IntPtr.Zero);

                Marshal.WriteInt32(optsPtr, 0x0, 2);           // ApiVersion
                Marshal.WriteIntPtr(optsPtr, 0x8, credsPtr);
                Marshal.WriteIntPtr(optsPtr, 0x10, userPtr);

                var opts = new EosConnectLoginOptions
                {
                    ApiVersion = 2,
                    Credentials = credsPtr,
                    UserLoginInfo = userPtr
                };
                EosSdkLogin(handle, ref opts, IntPtr.Zero, loginCbPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(credsPtr);
                Marshal.FreeHGlobal(userPtr);
                Marshal.FreeHGlobal(optsPtr);
            }
        }

        // Calls EOS_Connect_CreateDeviceId to register this machine's device credential.
        // DeviceModel=null tells the SDK to use its built-in device fingerprint (hardware ID).
        private static void CallEosCreateDeviceId(IntPtr handle, IntPtr createCbPtr)
        {
            var opts = new EosCreateDeviceIdOptions { ApiVersion = 1, DeviceModel = IntPtr.Zero };
            EosSdkCreateDeviceId(handle, ref opts, IntPtr.Zero, createCbPtr);
        }

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Connect_Login(IntPtr handle, ref EosConnectLoginOptions options, IntPtr clientData, IntPtr completionDelegate);

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Connect_CreateDeviceId(IntPtr handle, ref EosCreateDeviceIdOptions options, IntPtr clientData, IntPtr completionDelegate);

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Connect_CreateUser(IntPtr handle, ref EosCreateUserOptions options, IntPtr clientData, IntPtr completionDelegate);

        // ── EOS Auth service P/Invoke ─────────────────────────────────────────────────
        // EOS_Auth_Login obtains an Epic account-level session (EpicAccountId).
        // Auth.CredentialsInternal layout (from dump): ApiVersion@0x0, Id@0x8, Token@0x10,
        //   Type@0x18 (LoginCredentialType), SystemAuthCredentialsOptions@0x20, ExternalType@0x28
        // Auth.LoginOptionsInternal layout: ApiVersion@0x0, Credentials@0x8, ScopeFlags@0x10, LoginFlags@0x18
        // Auth.CopyIdTokenOptionsInternal: ApiVersion@0x0, AccountId@0x8
        // Auth.IdTokenInternal: ApiVersion@0x0, AccountId@0x8, JsonWebToken@0x10

        [StructLayout(LayoutKind.Explicit, Size = 0x30)]
        private struct EosAuthCredentials
        {
            [FieldOffset(0x0)]  public int ApiVersion;    // = 1
            [FieldOffset(0x8)]  public IntPtr Id;         // null for PersistentAuth / ExternalAuth+Epic
            [FieldOffset(0x10)] public IntPtr Token;      // null for PersistentAuth; OAuth token for ExternalAuth
            [FieldOffset(0x18)] public int Type;          // LoginCredentialType: 2=PersistentAuth, 7=ExternalAuth
            [FieldOffset(0x20)] public IntPtr SystemAuthCredentialsOptions; // null
            [FieldOffset(0x28)] public int ExternalType;  // ExternalCredentialType: 0=Epic (only for ExternalAuth)
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x20)]
        private struct EosAuthLoginOptions
        {
            [FieldOffset(0x0)]  public int ApiVersion;   // = 2
            [FieldOffset(0x8)]  public IntPtr Credentials;
            [FieldOffset(0x10)] public int ScopeFlags;   // AuthScopeFlags = 0
            [FieldOffset(0x18)] public int LoginFlags;   // = 0
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x10)]
        private struct EosAuthCopyIdTokenOptions
        {
            [FieldOffset(0x0)] public int ApiVersion;   // = 1
            [FieldOffset(0x8)] public IntPtr AccountId; // EpicAccountId raw handle
        }

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Auth_Login(IntPtr handle, ref EosAuthLoginOptions options, IntPtr clientData, IntPtr completionDelegate);

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern int EOS_Auth_CopyIdToken(IntPtr handle, ref EosAuthCopyIdTokenOptions options, out IntPtr outIdToken);

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern void EOS_Auth_IdToken_Release(IntPtr idToken);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EosAuthLoginRawCallback(IntPtr info);
        private static readonly EosAuthLoginRawCallback _pinnedAuthLoginCb = OnEosAuthLoginCallback;

        private static bool _authLoginDone;
        private static bool _authLoginSuccess;
        private static IntPtr _authLocalUserId; // EpicAccountId raw handle from auth callback

        // Auth LoginCallbackInfoInternal: ResultCode@0x0, ClientData@0x8, LocalUserId@0x10, ...
        private static void OnEosAuthLoginCallback(IntPtr info)
        {
            if (info == IntPtr.Zero) { _authLoginDone = true; return; }
            int raw = Marshal.ReadInt32(info, 0x0);
            var result = (Result)raw;
            _authLoginSuccess = result == Result.Success;
            if (_authLoginSuccess)
                _authLocalUserId = Marshal.ReadIntPtr(info, 0x10); // LocalUserId (EpicAccountId)
            MelonLogger.Msg($"[HeadlessMode] EOS_Auth_Login callback: {result}");
            _authLoginDone = true;
        }

        [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
        private static extern int EOS_ProductUserId_ToString(IntPtr accountId, IntPtr outBuffer, ref int inOutBufferLength);

        /// <summary>
        /// Bridges the gap between a raw native EOS <c>ProductUserId</c> pointer (obtained from
        /// our P/Invoke callback) and the PEWS managed layer that <c>CreateLobby</c> relies on.
        ///
        /// Why this two-step conversion is required:
        ///   - P/Invoke callbacks give us a raw <c>IntPtr</c> — an opaque EOS handle.
        ///   - PEWS's <c>EOSManager.GetProductUserId()</c> returns a managed <c>ProductUserId</c>
        ///     object that was created by PEWS's own callback path, which we bypassed.
        ///   - <c>ProductUserId.FromString</c> is the only public way to construct a managed
        ///     instance without going through PEWS's normal login flow.
        ///   - After construction we push the value into PEWS via <c>SetLocalProductUserId</c>
        ///     (method) and <c>s_localProductUserId</c> (field) as belt-and-suspenders, because
        ///     the exact storage location varies across PEWS versions.
        /// </summary>
        private static bool InjectPuidIntoEosManager(IntPtr rawPuid)
        {
            if (rawPuid == IntPtr.Zero) return false;

            // Step 1: native handle → PUID string
            int bufLen = 128;
            IntPtr buf = Marshal.AllocHGlobal(bufLen);
            int toRes = EOS_ProductUserId_ToString(rawPuid, buf, ref bufLen);
            string puidStr = toRes == 0 ? Marshal.PtrToStringAnsi(buf) : null;
            Marshal.FreeHGlobal(buf);

            if (string.IsNullOrEmpty(puidStr)) { MelonLogger.Warning("[HeadlessMode] EOS_ProductUserId_ToString failed."); return false; }
            MelonLogger.Msg($"[HeadlessMode] PUID string: {puidStr}");

            // Step 2: string → managed ProductUserId
            ProductUserId puid = null;
            try { puid = ProductUserId.FromString(puidStr); }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] ProductUserId.FromString: {ex.Message}"); return false; }
            if (puid == null || !puid.IsValid()) { MelonLogger.Warning("[HeadlessMode] ProductUserId invalid after FromString."); return false; }

            // Step 3: inject into EOSManager via SetLocalProductUserId (protected method on EOSSingleton)
            Type singletonType = null;
            object singletonObj = null;
            try
            {
                singletonType = AccessTools.TypeByName("Il2CppPlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton")
                             ?? AccessTools.TypeByName("Il2CppPlayEveryWare.EpicOnlineServices.EOSManager");
                // EOSManager.Instance is the EOSSingleton itself in this version of PEWS
                singletonObj = EOSManager.Instance;
            }
            catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] InjectPuid: get singleton: {ex.Message}"); }

            if (singletonType == null || singletonObj == null)
            {
                MelonLogger.Warning("[HeadlessMode] InjectPuid: could not find EOSManager singleton.");
                return false;
            }

            var setMethod = AccessTools.Method(singletonType, "SetLocalProductUserId")
                         ?? AccessTools.Method(typeof(EOSManager), "SetLocalProductUserId");
            if (setMethod != null)
            {
                try { setMethod.Invoke(singletonObj, new object[] { puid }); }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] SetLocalProductUserId invoke: {ex.Message}"); }
            }

            // Also try direct field set as fallback
            var field = singletonType.GetField("s_localProductUserId",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null)
            {
                try { field.SetValue(null, puid); }
                catch (Exception ex) { MelonLogger.Warning($"[HeadlessMode] s_localProductUserId field set: {ex.Message}"); }
            }

            MelonLogger.Msg($"[HeadlessMode] PUID injected into EOSManager (method={setMethod != null}, field={field != null}).");
            return true;
        }

        // Thin wrappers so callers don't have to pass ref on every site and the iterator
        // restriction on unsafe/ref locals doesn't block coroutine code from calling these.
        private static void EosSdkLogin(IntPtr handle, ref EosConnectLoginOptions options, IntPtr clientData, IntPtr cb)
            => EOS_Connect_Login(handle, ref options, clientData, cb);

        private static void EosSdkCreateDeviceId(IntPtr handle, ref EosCreateDeviceIdOptions options, IntPtr clientData, IntPtr cb)
            => EOS_Connect_CreateDeviceId(handle, ref options, clientData, cb);

        private static void EosSdkCreateUser(IntPtr handle, IntPtr continuanceToken, IntPtr cb)
        {
            var opts = new EosCreateUserOptions { ApiVersion = 1, ContinuanceToken = continuanceToken };
            EOS_Connect_CreateUser(handle, ref opts, IntPtr.Zero, cb);
        }

        // ── EOS Auth + EpicIdToken Connect flow ──────────────────────────────────────

        /// <summary>
        /// Fallback auth path: obtain an <c>EpicAccountId</c> via the EOS Auth service, then
        /// exchange it for an EOS Connect <c>ProductUserId</c> using the <c>EpicIdToken</c>
        /// credential type. Used when DeviceId auth is unavailable (product configuration).
        ///
        /// <paramref name="authType"/>:
        ///   2 = <c>PersistentAuth</c> — uses a locally-cached refresh token from a previous
        ///       Epic account login; no credentials needed. Silent on servers that logged in once.
        ///   7 = <c>ExternalAuth+Epic</c> — pass an OAuth access_token as <paramref name="tokenPtr"/>.
        ///
        /// The two-step flow (Auth → Connect) is required because the Connect service only accepts
        /// <c>EpicIdToken</c> (a JWT signed by Epic's Auth service), not raw credentials.
        /// </summary>
        private static IEnumerator TryEosAuthAndConnect(IntPtr authHandle, ConnectInterface connectIface, int authType, IntPtr tokenPtr, float authTimeoutSecs = 30f)
        {
            IntPtr credsPtr = Marshal.AllocHGlobal(0x30);
            for (int i = 0; i < 0x30; i++) Marshal.WriteByte(credsPtr, i, 0);
            Marshal.WriteInt32(credsPtr, 0x0, 1);           // ApiVersion
            Marshal.WriteIntPtr(credsPtr, 0x10, tokenPtr);  // Token (null for PersistentAuth)
            Marshal.WriteInt32(credsPtr, 0x18, authType);   // Type (LoginCredentialType)
            // ExternalType stays 0 = Epic

            var opts = new EosAuthLoginOptions { ApiVersion = 2, Credentials = credsPtr, ScopeFlags = 0, LoginFlags = 0 };
            IntPtr authCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedAuthLoginCb);
            _authLoginDone = false;
            _authLoginSuccess = false;
            _authLocalUserId = IntPtr.Zero;
            EOS_Auth_Login(authHandle, ref opts, IntPtr.Zero, authCbPtr);
            Marshal.FreeHGlobal(credsPtr);
            MelonLogger.Msg($"[HeadlessMode] EOS_Auth_Login (type={authType}) sent...");

            float t = Time.realtimeSinceStartup;
            while (!_authLoginDone && Time.realtimeSinceStartup - t < authTimeoutSecs)
                yield return new WaitForSecondsRealtime(0.25f);

            if (!_authLoginDone) { MelonLogger.Warning("[HeadlessMode] EOS_Auth_Login callback never fired."); yield break; }
            if (!_authLoginSuccess) yield break;

            // Auth succeeded — copy ID token (JWT) for use with EOS_Connect_Login
            var copyOpts = new EosAuthCopyIdTokenOptions { ApiVersion = 1, AccountId = _authLocalUserId };
            int copyRes = EOS_Auth_CopyIdToken(authHandle, ref copyOpts, out IntPtr idTokenPtr);
            MelonLogger.Msg($"[HeadlessMode] EOS_Auth_CopyIdToken: result={copyRes} ptr={idTokenPtr}");
            if (copyRes != 0 || idTokenPtr == IntPtr.Zero) yield break;

            IntPtr jwtStrPtr = Marshal.ReadIntPtr(idTokenPtr, 0x10); // JsonWebToken@0x10
            string jwt = jwtStrPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(jwtStrPtr) : null;
            EOS_Auth_IdToken_Release(idTokenPtr);

            if (string.IsNullOrEmpty(jwt)) { MelonLogger.Warning("[HeadlessMode] JWT empty after CopyIdToken."); yield break; }
            MelonLogger.Msg($"[HeadlessMode] EpicIdToken obtained (len={jwt.Length}), connecting...");

            yield return TryConnectWithEpicIdToken(connectIface, jwt);
        }

        // Second half of TryEosAuthAndConnect: sends EOS_Connect_Login with the JWT obtained
        // from EOS_Auth_CopyIdToken. Handles the first-time case (EOS_InvalidUser) by calling
        // EOS_Connect_CreateUser with the ContinuanceToken before retrying the login.
        private static IEnumerator TryConnectWithEpicIdToken(ConnectInterface connectIface, string jwt)
        {
            IntPtr handle = connectIface.InnerHandle;
            IntPtr loginCbPtr = Marshal.GetFunctionPointerForDelegate(_pinnedLoginCb);
            IntPtr jwtPtr = Marshal.StringToHGlobalAnsi(jwt);

            _deviceAuthDone = false;
            _deviceAuthSuccess = false;
            _loginContinuanceToken = IntPtr.Zero;

            IntPtr credsPtr = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(credsPtr, 0x0, 1);          // ApiVersion
                Marshal.WriteIntPtr(credsPtr, 0x8, jwtPtr);    // Token = JWT
                Marshal.WriteInt32(credsPtr, 0x10, 16);        // Type = EpicIdToken
                var connectOpts = new EosConnectLoginOptions { ApiVersion = 2, Credentials = credsPtr, UserLoginInfo = IntPtr.Zero };
                EosSdkLogin(handle, ref connectOpts, IntPtr.Zero, loginCbPtr);
                MelonLogger.Msg("[HeadlessMode] EOS_Connect_Login (EpicIdToken) sent...");
            }
            finally { Marshal.FreeHGlobal(credsPtr); Marshal.FreeHGlobal(jwtPtr); }

            float t = Time.realtimeSinceStartup;
            while (!_deviceAuthDone && Time.realtimeSinceStartup - t < 30f)
                yield return new WaitForSecondsRealtime(0.25f);

            if (!_deviceAuthDone) { MelonLogger.Warning("[HeadlessMode] Connect_Login (EpicIdToken) callback never fired."); yield break; }
            if (_deviceAuthSuccess) yield break;

            if (_loginContinuanceToken == IntPtr.Zero) { MelonLogger.Warning("[HeadlessMode] EpicIdToken login failed, no continuance token."); yield break; }

            // First-time login — create a Connect user account, then retry
            MelonLogger.Msg("[HeadlessMode] EOS_Connect_CreateUser (EpicIdToken first-time)...");
            IntPtr createUserCb = Marshal.GetFunctionPointerForDelegate(_pinnedCreateUserCb);
            _createUserDone = false;
            _createUserSuccess = false;
            EosSdkCreateUser(handle, _loginContinuanceToken, createUserCb);

            t = Time.realtimeSinceStartup;
            while (!_createUserDone && Time.realtimeSinceStartup - t < 15f)
                yield return new WaitForSecondsRealtime(0.25f);

            if (!_createUserSuccess) yield break;

            jwtPtr = Marshal.StringToHGlobalAnsi(jwt);
            _deviceAuthDone = false; _deviceAuthSuccess = false; _loginContinuanceToken = IntPtr.Zero;
            credsPtr = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(credsPtr, 0x0, 1);
                Marshal.WriteIntPtr(credsPtr, 0x8, jwtPtr);
                Marshal.WriteInt32(credsPtr, 0x10, 16);
                var retryOpts = new EosConnectLoginOptions { ApiVersion = 2, Credentials = credsPtr, UserLoginInfo = IntPtr.Zero };
                EosSdkLogin(handle, ref retryOpts, IntPtr.Zero, loginCbPtr);
                MelonLogger.Msg("[HeadlessMode] EOS_Connect_Login (EpicIdToken) retry after CreateUser...");
            }
            finally { Marshal.FreeHGlobal(credsPtr); Marshal.FreeHGlobal(jwtPtr); }

            t = Time.realtimeSinceStartup;
            while (!_deviceAuthDone && Time.realtimeSinceStartup - t < 30f)
                yield return new WaitForSecondsRealtime(0.25f);
        }
    }
}
