#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// The binding to NV Access's own controller client,
    /// <c>nvdaControllerClient.dll</c>, loaded by ABSOLUTE PATH and bound
    /// export by export. Nothing here goes through <c>DllImport</c> by name.
    ///
    /// <para><b>Why not <c>DllImport</c> (#541).</b> A name-bound import is
    /// resolved by the Windows search order, and the search order is what
    /// would find a stray pre-2024.1 copy of this DLL beside the exe — thirty
    /// of them sat in four build trees until 2026-09-05. That client exports
    /// <c>speakText</c> and NOT <c>speakSsml</c>, so the failure would be a
    /// missing entry point at RUNTIME, not at build, while whoever hit it was
    /// reasoning about RPC and marshalling. Nothing generates those copies
    /// now, but the hazard is a property of the technique, not of the files.
    /// So: <see cref="NativeLibrary.Load(string)"/> on the full path under
    /// <c>runtimes/win-{arch}/native/</c>, then every entry point from that
    /// handle. <c>NativeLoader.vb</c>'s name map is deliberately not taught
    /// this file.</para>
    ///
    /// <para><b>The positive control.</b> A genuine client of any age exports
    /// <c>nvdaController_speakText</c>. If that export cannot be found, the
    /// loader or the file is wrong and no conclusion about <c>speakSsml</c>
    /// is worth anything — the first attempt to check this DLL on
    /// 2026-09-05 reported neither name present, because <c>strings</c> was
    /// not installed, not because the DLL was.</para>
    ///
    /// <para><b>Marshalling, from the shipped <c>nvdaController.h</c>.</b>
    /// Every function is <c>__stdcall</c> (matters on x86, cosmetic on x64)
    /// and returns <c>error_status_t</c>, a 32-bit unsigned Windows error
    /// code. Strings are <c>const wchar_t*</c>. The MIDL <c>boolean</c> is one
    /// byte. The enums are <c>v1_enum</c>, so 32-bit ints. The mark callback
    /// is <c>error_status_t (__stdcall*)(const wchar_t*)</c> and must return
    /// 0 — the official C# binding returns <c>int</c>, not <c>void</c>, and
    /// on x86 a void-returning delegate would hand the RPC runtime whatever
    /// happened to be in EAX.</para>
    /// </summary>
    internal static class NvdaControllerClient
    {
        // ── Return codes (Windows error codes; the client returns them raw) ──

        /// <summary>Spoken to the end.</summary>
        internal const uint ErrorSuccess = 0;

        /// <summary>ERROR_CANCELLED: NVDA cancelled the sequence — ours, the operator's keystroke, a focus change, anyone's cancelSpeech.</summary>
        internal const uint ErrorCancelled = 1223;

        /// <summary>ERROR_ACCESS_DENIED: NVDA is in sleep mode for the focused application.</summary>
        internal const uint ErrorAccessDenied = 5;

        /// <summary>ERROR_INVALID_PARAMETER: the SSML did not parse.</summary>
        internal const uint ErrorInvalidParameter = 87;

        /// <summary>RPC_S_UNKNOWN_IF: the NVDA answering is older than 2024.1 and has no NvdaController2.</summary>
        internal const uint RpcUnknownInterface = 1717;

        /// <summary>RPC_S_SERVER_UNAVAILABLE: no NVDA at the endpoint.</summary>
        internal const uint RpcServerUnavailable = 1722;

        /// <summary>RPC_S_CALL_FAILED: the server went away mid-call.</summary>
        internal const uint RpcCallFailed = 1726;

        /// <summary>RPC_S_CALL_FAILED_DNE: the call never reached the server.</summary>
        internal const uint RpcCallFailedDidNotExecute = 1727;

        /// <summary>RPC_S_CALL_CANCELLED: OUR escape (RpcCancelThreadEx) cancelled the blocked call.</summary>
        internal const uint RpcCallCancelled = 1818;

        // ── SPEECH_PRIORITY and SYMBOL_LEVEL, from nvdaController.h ──

        internal const int SpeechPriorityNormal = 0;
        internal const int SpeechPriorityNext = 1;
        internal const int SpeechPriorityNow = 2;

        /// <summary>Use the operator's own punctuation setting, as speakText does.</summary>
        internal const int SymbolLevelUnchanged = -1;

        // ── Delegate shapes ──

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint TestIfRunningFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint CancelSpeechFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate uint SpeakSsmlFn(
            [MarshalAs(UnmanagedType.LPWStr)] string ssml,
            int symbolLevel,
            int priority,
            [MarshalAs(UnmanagedType.U1)] bool asynchronous);

        /// <summary>Mirrors onSsmlMarkReachedFuncType. Must return 0.</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        internal delegate uint OnSsmlMarkReachedFn([MarshalAs(UnmanagedType.LPWStr)] string mark);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint SetOnSsmlMarkReachedCallbackFn(OnSsmlMarkReachedFn? callback);

        // ── State ──

        private static readonly object _loadLock = new object();
        private static bool _loadAttempted;
        private static IntPtr _handle;
        private static string? _loadedFrom;
        private static string? _diagnosis;

        private static TestIfRunningFn? _testIfRunning;
        private static CancelSpeechFn? _cancelSpeech;
        private static SpeakSsmlFn? _speakSsml;
        private static SetOnSsmlMarkReachedCallbackFn? _setCallback;

        /// <summary>
        /// The registered mark callback, rooted for the life of the process.
        /// The DLL holds only the raw function pointer; if the GC collected
        /// the delegate, the next mark would call freed memory — the classic
        /// P/Invoke callback trap, and the same one PrismScreenReader roots
        /// its availability thunk against.
        /// </summary>
        private static OnSsmlMarkReachedFn? _markCallback;

        /// <summary>True when the DLL is loaded and every entry point this class needs was found.</summary>
        internal static bool IsLoaded
        {
            get { lock (_loadLock) { return _handle != IntPtr.Zero && _speakSsml != null; } }
        }

        /// <summary>Where the DLL came from, or null. For the trace.</summary>
        internal static string? LoadedFrom { get { lock (_loadLock) { return _loadedFrom; } } }

        /// <summary>Why the load failed, or null when it did not. For the trace.</summary>
        internal static string? Diagnosis { get { lock (_loadLock) { return _diagnosis; } } }

        /// <summary>
        /// The one place the shipped path is spelled: beside prism.dll, per
        /// architecture, exactly where the vbproj copies it.
        /// </summary>
        internal static string ShippedPath()
        {
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            return Path.Combine(AppContext.BaseDirectory, "runtimes", $"win-{arch}", "native",
                "nvdaControllerClient.dll");
        }

        /// <summary>
        /// Load the shipped DLL once. Never throws; false with
        /// <see cref="Diagnosis"/> set means the completion channel is simply
        /// absent, which is an ordinary outcome and not a fault in the app.
        /// </summary>
        internal static bool TryLoad() => TryLoadFrom(ShippedPath());

        /// <summary>
        /// Load from an explicit absolute path. Idempotent: the first call
        /// decides for the process, because the mark callback and the RPC
        /// binding inside the DLL are process-global and a second copy would
        /// fight the first.
        /// </summary>
        internal static bool TryLoadFrom(string absolutePath)
        {
            lock (_loadLock)
            {
                if (_loadAttempted) return _handle != IntPtr.Zero && _speakSsml != null;
                _loadAttempted = true;

                if (!Path.IsPathRooted(absolutePath))
                {
                    _diagnosis = $"refused to load '{absolutePath}': not an absolute path, and search order is the hazard this loader exists to avoid";
                    Tracing.TraceLine("NvdaControllerClient: " + _diagnosis, System.Diagnostics.TraceLevel.Warning);
                    return false;
                }

                if (!File.Exists(absolutePath))
                {
                    _diagnosis = $"no client DLL at '{absolutePath}' — completion reporting is off; speech goes through Prism as before";
                    Tracing.TraceLine("NvdaControllerClient: " + _diagnosis, System.Diagnostics.TraceLevel.Info);
                    return false;
                }

                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(absolutePath);
                }
                catch (Exception ex)
                {
                    _diagnosis = $"NativeLibrary.Load('{absolutePath}') failed: {ex.Message}";
                    Tracing.TraceLine("NvdaControllerClient: " + _diagnosis, System.Diagnostics.TraceLevel.Warning);
                    return false;
                }

                // POSITIVE CONTROL FIRST. Every genuine client since 2006
                // exports speakText. If this is missing, the file is not a
                // controller client at all (or the export table cannot be
                // read), and no conclusion about speakSsml below is worth
                // anything.
                if (!NativeLibrary.TryGetExport(h, "nvdaController_speakText", out _))
                {
                    _diagnosis = $"'{absolutePath}' does not export nvdaController_speakText — not a controller client, or the export table is unreadable";
                    Tracing.TraceLine("NvdaControllerClient: " + _diagnosis, System.Diagnostics.TraceLevel.Error);
                    NativeLibrary.Free(h);
                    return false;
                }

                if (!NativeLibrary.TryGetExport(h, "nvdaController_speakSsml", out var speakSsmlPtr)
                    || !NativeLibrary.TryGetExport(h, "nvdaController_setOnSsmlMarkReachedCallback", out var setCbPtr))
                {
                    // speakText present, speakSsml absent: the pre-2024.1
                    // client. It can speak but cannot answer, so it is useless
                    // here — and it is exactly the copy #541 warned would be
                    // found first if anything were ever bound by name.
                    _diagnosis = $"'{absolutePath}' exports speakText but not speakSsml — this is the pre-2024.1 client (#541); completion reporting is off";
                    Tracing.TraceLine("NvdaControllerClient: " + _diagnosis, System.Diagnostics.TraceLevel.Error);
                    NativeLibrary.Free(h);
                    return false;
                }

                if (!NativeLibrary.TryGetExport(h, "nvdaController_testIfRunning", out var testPtr)
                    || !NativeLibrary.TryGetExport(h, "nvdaController_cancelSpeech", out var cancelPtr))
                {
                    _diagnosis = $"'{absolutePath}' is missing testIfRunning or cancelSpeech — not a client this code understands";
                    Tracing.TraceLine("NvdaControllerClient: " + _diagnosis, System.Diagnostics.TraceLevel.Error);
                    NativeLibrary.Free(h);
                    return false;
                }

                _testIfRunning = Marshal.GetDelegateForFunctionPointer<TestIfRunningFn>(testPtr);
                _cancelSpeech = Marshal.GetDelegateForFunctionPointer<CancelSpeechFn>(cancelPtr);
                _speakSsml = Marshal.GetDelegateForFunctionPointer<SpeakSsmlFn>(speakSsmlPtr);
                _setCallback = Marshal.GetDelegateForFunctionPointer<SetOnSsmlMarkReachedCallbackFn>(setCbPtr);
                _handle = h;
                _loadedFrom = absolutePath;

                Tracing.TraceLine(
                    $"NvdaControllerClient: loaded by absolute path from '{absolutePath}' "
                    + "(speakText present as the positive control, speakSsml present).",
                    System.Diagnostics.TraceLevel.Info);
                return true;
            }
        }

        /// <summary>
        /// Register the process-wide mark callback. One utterance is ever in
        /// flight (the pump guarantees it), so one callback serves the
        /// process; the mark NAME carries the ticket, so the callback can
        /// still tell an abandoned call's late marks from the live one's.
        /// </summary>
        internal static bool TrySetMarkCallback(OnSsmlMarkReachedFn callback)
        {
            SetOnSsmlMarkReachedCallbackFn? set;
            lock (_loadLock) { set = _setCallback; }
            if (set == null) return false;
            try
            {
                _markCallback = callback;   // rooted BEFORE the pointer is handed over
                uint rc = set(callback);
                if (rc != ErrorSuccess)
                {
                    Tracing.TraceLine(
                        $"NvdaControllerClient: setOnSsmlMarkReachedCallback returned {rc}; marks will not be reported.",
                        System.Diagnostics.TraceLevel.Warning);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"NvdaControllerClient: setOnSsmlMarkReachedCallback threw: {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>0 when NVDA is running and answering RPC.</summary>
        internal static uint TestIfRunning()
        {
            TestIfRunningFn? fn;
            lock (_loadLock) { fn = _testIfRunning; }
            if (fn == null) return RpcServerUnavailable;
            try { return fn(); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"NvdaControllerClient: testIfRunning threw: {ex.Message}", System.Diagnostics.TraceLevel.Warning);
                return RpcCallFailed;
            }
        }

        /// <summary>Cut NVDA's speech. Safe from any thread, including while another is blocked in <see cref="SpeakSsml"/>.</summary>
        internal static uint CancelSpeech()
        {
            CancelSpeechFn? fn;
            lock (_loadLock) { fn = _cancelSpeech; }
            if (fn == null) return RpcServerUnavailable;
            try { return fn(); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"NvdaControllerClient: cancelSpeech threw: {ex.Message}", System.Diagnostics.TraceLevel.Warning);
                return RpcCallFailed;
            }
        }

        /// <summary>
        /// The synchronous call. BLOCKS until NVDA reports spoken (0),
        /// cancelled (1223), or refuses — or, on NVDA 2024.1 through 2026.1,
        /// possibly forever. Only <see cref="NvdaCompletionChannel"/> calls
        /// this, from the delivery thread, under a deadline.
        /// </summary>
        internal static uint SpeakSsml(string ssml, int priority)
        {
            SpeakSsmlFn? fn;
            lock (_loadLock) { fn = _speakSsml; }
            if (fn == null) return RpcServerUnavailable;
            try { return fn(ssml, SymbolLevelUnchanged, priority, asynchronous: false); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"NvdaControllerClient: speakSsml threw: {ex.Message}", System.Diagnostics.TraceLevel.Warning);
                return RpcCallFailed;
            }
        }

        // ── Classification ──

        /// <summary>
        /// Turn a return code and the mark tally into an outcome. Pure, so it
        /// is tested without NVDA on the desk. <paramref name="cancelledByUs"/>
        /// is the channel's knowledge of whether it issued a cancel for this
        /// ticket; the code alone cannot tell our cancel from the operator's.
        /// </summary>
        internal static SpeechOutcome Classify(uint rc, int marksReached, int markCount, bool cancelledByUs, int elapsedMs)
        {
            switch (rc)
            {
                case ErrorSuccess:
                    return SpeechOutcome.Completed(markCount, markCount, elapsedMs);
                case ErrorCancelled:
                    return SpeechOutcome.Cancelled(marksReached, markCount, cancelledByUs, elapsedMs);
                case RpcCallCancelled:
                    return SpeechOutcome.Unknown(SpeechUnknownReason.Escaped,
                        "RPC_S_CALL_CANCELLED (1818): our escape cancelled the blocked call", elapsedMs, marksReached, markCount);
                case ErrorAccessDenied:
                    return SpeechOutcome.Unknown(SpeechUnknownReason.Refused,
                        "ACCESS_DENIED (5): NVDA is in sleep mode for the focused application", elapsedMs, marksReached, markCount);
                case ErrorInvalidParameter:
                    return SpeechOutcome.Unknown(SpeechUnknownReason.InvalidSsml,
                        "INVALID_PARAMETER (87): NVDA refused the SSML as malformed", elapsedMs, marksReached, markCount);
                case RpcUnknownInterface:
                    return SpeechOutcome.Unknown(SpeechUnknownReason.ChannelAbsent,
                        "RPC_S_UNKNOWN_IF (1717): this NVDA is older than 2024.1 and has no NvdaController2", elapsedMs, marksReached, markCount);
                case RpcServerUnavailable:
                case RpcCallFailed:
                case RpcCallFailedDidNotExecute:
                    return SpeechOutcome.Unknown(SpeechUnknownReason.ChannelAbsent,
                        $"RPC error {rc}: NVDA is not answering", elapsedMs, marksReached, markCount);
                default:
                    return SpeechOutcome.Unknown(SpeechUnknownReason.Other,
                        $"speakSsml returned {rc}", elapsedMs, marksReached, markCount);
            }
        }

        // ── The RPC escape ──
        //
        // A synchronous speakSsml on NVDA 2024.1 through 2026.1 can block
        // forever when a cancel lands before NVDA registers its done-handler
        // (fixed in 2026.2, which this machine runs; Don's version is not on
        // record). The RPC runtime has a primitive for exactly this:
        // RpcCancelThreadEx makes a blocked call on another thread return
        // RPC_S_CALL_CANCELLED. It needs the blocked thread's HANDLE, and
        // that thread must have set its own cancel timeout beforehand or the
        // cancel waits for the server to acknowledge — which is the thing
        // that is not answering.
        //
        // UNVERIFIED against a real hang. The pinned design says so and this
        // code says so: it is the documented primitive, wired as documented,
        // and whether it frees a thread blocked inside NVDA 2026.1's speakSsml
        // is a measurement nobody has made. The layer above therefore treats
        // "the escape did not work" as an ordinary outcome and abandons the
        // thread.

        [DllImport("rpcrt4.dll")]
        private static extern int RpcCancelThreadEx(IntPtr thread, int timeoutSeconds);

        [DllImport("rpcrt4.dll")]
        private static extern int RpcMgmtSetCancelTimeout(int timeoutSeconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint ThreadAllAccess = 0x1FFFFF;

        /// <summary>
        /// Prepare the CALLING thread to have its RPC calls cancelled from
        /// outside: a zero cancel timeout so a cancel does not itself wait on
        /// the server, and a real handle to this thread for
        /// <see cref="TryCancelBlockedCall"/>. Call once per delivery thread.
        /// Returns IntPtr.Zero when the handle could not be opened; the
        /// caller then has no escape short of abandonment and should say so.
        /// </summary>
        internal static IntPtr PrepareCallingThreadForCancel()
        {
            try
            {
                int rc = RpcMgmtSetCancelTimeout(0);
                if (rc != 0)
                {
                    Tracing.TraceLine($"NvdaControllerClient: RpcMgmtSetCancelTimeout(0) returned {rc}; an escape may wait on NVDA.",
                        System.Diagnostics.TraceLevel.Warning);
                }
                IntPtr h = OpenThread(ThreadAllAccess, false, GetCurrentThreadId());
                if (h == IntPtr.Zero)
                {
                    Tracing.TraceLine($"NvdaControllerClient: OpenThread failed (win32 {Marshal.GetLastWin32Error()}); no RPC escape for this thread.",
                        System.Diagnostics.TraceLevel.Warning);
                }
                return h;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"NvdaControllerClient: preparing the thread for cancel threw: {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                return IntPtr.Zero;
            }
        }

        /// <summary>Ask the RPC runtime to cancel whatever call <paramref name="threadHandle"/> is blocked in. Returns the RPC_STATUS; 0 means the request was accepted, not that the call has returned.</summary>
        internal static int TryCancelBlockedCall(IntPtr threadHandle)
        {
            if (threadHandle == IntPtr.Zero) return -1;
            try { return RpcCancelThreadEx(threadHandle, 0); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"NvdaControllerClient: RpcCancelThreadEx threw: {ex.Message}", System.Diagnostics.TraceLevel.Warning);
                return -1;
            }
        }

        internal static void CloseThreadHandle(IntPtr threadHandle)
        {
            if (threadHandle == IntPtr.Zero) return;
            try { CloseHandle(threadHandle); } catch { /* best effort */ }
        }
    }
}
