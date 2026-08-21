using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// <see cref="IScreenReader"/> over Prism (ethindp/prism) through the
    /// <see cref="PrismNative"/> P/Invoke layer.
    ///
    /// Lifecycle: prism_config_init → prism_init → prism_registry_acquire_best
    /// → prism_backend_initialize, then output/speak/braille against the
    /// acquired backend.
    ///
    /// Prism speaks to NVDA, JAWS and SAPI itself and ships as a single
    /// prism.dll per architecture. Braille is a first-class backend call.
    /// </summary>
    public sealed class PrismScreenReader : IScreenReader
    {
        private IntPtr _ctx;
        private IntPtr _backend;
        private bool _supportsOutput;
        private bool _supportsSpeak;
        private bool _supportsBraille;
        private bool _supportsStop;
        private bool _supportsIsSpeaking;

        /// <summary>
        /// Serialises everything that swaps the backend or tears the context
        /// down: availability-driven re-acquires arrive on worker threads while
        /// Dispose can run on the UI thread at exit, and a re-acquire that uses
        /// _ctx after prism_shutdown is a native crash, not an exception.
        /// </summary>
        private readonly object _reacquireLock = new object();

        /// <summary>
        /// Backend id of the controller reader we are attached to, or 0 when
        /// the channel is not a controller reader. Lets an availability DROP be
        /// matched to the reader we actually hold — losing JAWS matters not at
        /// all while we are speaking through NVDA.
        /// </summary>
        private ulong _activeReaderId;

        /// <summary>
        /// True when the controller reader we hold has gone away (NVDA crashed
        /// or was closed). The Tier still reads ScreenReader — the handle
        /// exists, calls just go nowhere — so this flag is what tells the next
        /// availability RISE that re-acquiring is a rescue, not a displacement.
        /// </summary>
        private volatile bool _readerLost;

        /// <summary>
        /// Keeps the availability-callback delegate alive for the life of the
        /// process. Prism holds only the raw function pointer; if the GC
        /// collected the delegate behind it, the next poll tick would call
        /// freed memory. The classic P/Invoke callback trap — rooted here on
        /// purpose, not as a local.
        /// </summary>
        private static readonly PrismAvailabilityCallback _availabilityThunk =
            OnAvailabilityChangedThunk;

        /// <summary>GCHandle on this instance, passed to Prism as callback userdata.</summary>
        private GCHandle _selfHandle;

        /// <summary>
        /// Raised after the speech channel actually changed — a successful UIA
        /// upgrade or a controller-reader re-acquire. Carries the new tier and
        /// detected reader. May fire on a worker thread. ScreenReaderOutput
        /// subscribes to refresh its cached state, write the transcript event,
        /// and announce the recovery.
        /// </summary>
        public event Action<SpeechTier, string?>? ChannelChanged;

        public string BackendName => "Prism";
        public string? DetectedReader { get; private set; }
        public bool HasSpeech => _backend != IntPtr.Zero && (_supportsOutput || _supportsSpeak);
        public bool HasBraille => _backend != IntPtr.Zero && _supportsBraille;

        /// <summary>
        /// True when this backend can report whether speech is still in
        /// progress. Read for diagnostics, NOT as the basis of a queue:
        /// it is a per-backend feature bit, so a design that polls it would
        /// work under one screen reader and silently stall under another.
        /// The speech-flow work coalesces BEFORE emission for that reason.
        /// </summary>
        public bool CanReportSpeaking =>
            _backend != IntPtr.Zero && _supportsIsSpeaking;

        /// <summary>
        /// Never throws. Every failure path — no prism.dll, null context, no
        /// backend, init error — returns false so the caller can fall back.
        /// This runs before the app has drawn a window, on machines whose
        /// owners cannot see a crash dialog.
        /// </summary>
        public bool Initialize()
        {
            try
            {
                var cfg = PrismNative.prism_config_init();

                // #167: subscribe to Prism's own availability enumerator, so a
                // screen reader that starts (or restarts) AFTER us reaches us
                // as an event instead of never. Without this, an operator whose
                // NVDA came up a moment too late got a raw synthesiser for the
                // life of the process, with nothing anywhere saying why.
                //
                // Poll interval and debounce are left 0 = Prism's defaults
                // (1000 ms, 2 samples): a change is confirmed in about two
                // seconds, and the enumerator backs off on its own while
                // nothing changes. Auto power management pauses the polling on
                // suspend, matching upstream's own demo wiring.
                _selfHandle = GCHandle.Alloc(this);
                cfg.AvailabilityCallback =
                    Marshal.GetFunctionPointerForDelegate(_availabilityThunk);
                cfg.AvailabilityUserdata = GCHandle.ToIntPtr(_selfHandle);
                cfg.AvailabilityAutoPowerManage = true;

                _ctx = PrismNative.prism_init(ref cfg);
                if (_ctx == IntPtr.Zero)
                {
                    Tracing.TraceLine("Prism: prism_init returned a null context.", TraceLevel.Warning);
                    Cleanup();
                    return false;
                }

                _backend = SelectBackend(out var tier);
                if (_backend == IntPtr.Zero)
                {
                    Tracing.TraceLine("Prism: no screen reader or TTS backend available.", TraceLevel.Warning);
                    Cleanup();
                    return false;
                }
                Tier = tier;

                if (!AdoptBackend(_backend))
                {
                    Cleanup();
                    return false;
                }

                Tracing.TraceLine(
                    $"Prism backend up: '{DetectedReader ?? "unknown"}' tier={Tier} "
                    + $"(output={_supportsOutput}, speak={_supportsSpeak}, "
                    + $"braille={_supportsBraille}, stop={_supportsStop}, "
                    + $"isSpeaking={_supportsIsSpeaking}).",
                    TraceLevel.Info);
                return true;
            }
            catch (DllNotFoundException ex)
            {
                Tracing.TraceLine($"Prism: prism.dll not found ({ex.Message}).", TraceLevel.Warning);
                return false;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Prism: Initialize threw: {ex.Message}", TraceLevel.Error);
                Cleanup();
                return false;
            }
        }

        /// <summary>
        /// Which kind of channel we ended up on. Reported in diagnostics
        /// because "speech works" and "speech works WELL" are different states
        /// and the operator cannot tell them apart by listening.
        /// </summary>
        public SpeechTier Tier { get; private set; } = SpeechTier.None;

        /// <summary>
        /// Choose a backend by suitability, not by registration order.
        ///
        /// Prism's own acquire_best returns the first entry that initialises,
        /// which on a machine running Narrator lands on OneCore - a raw
        /// synthesiser that then talks over Narrator's screen reading with no
        /// shared queue. Two voices, neither aware of the other. Observed on a
        /// real machine 2026-08-18.
        /// </summary>
        private IntPtr SelectBackend(out SpeechTier tier)
        {
            // Tier 1 - a reader with a controller API. Our text joins ITS
            // queue, in ITS voice, and reaches ITS braille display.
            foreach (var (id, name) in PrismNative.ControllerReaders)
            {
                var b = TryCreate(id, name);
                if (b != IntPtr.Zero)
                {
                    tier = SpeechTier.ScreenReader;
                    _activeReaderId = id;
                    return b;
                }
            }

            // Tier 2 - UI Automation notifications, the only channel that
            // reaches Narrator (it has no controller API, so nothing else can).
            // Usually FAILS here and succeeds later via TryUpgradeToUia: the
            // backend requires a visible top-level window at initialise time,
            // and speech comes up before the app has drawn one.
            if (UiaAudiencePresent())
            {
                var b = TryCreate(PrismNative.BackendUia, "UIA");
                if (b != IntPtr.Zero)
                {
                    tier = SpeechTier.UiaNotifications;
                    return b;
                }
            }

            // Tier 3 - a raw synthesiser. Correct only when nothing is
            // listening: a magnifier user with no screen reader still wants the
            // important things spoken, and Ctrl+Shift+V turns speech off.
            tier = SpeechTier.Synthesiser;
            return PrismNative.prism_registry_acquire_best(_ctx);
        }

        /// <summary>
        /// Re-attempt the UIA channel once the application owns a visible
        /// window. Call after the main window is shown.
        ///
        /// Only upgrades AWAY from a raw synthesiser - a controller-based
        /// reader is already the better channel and must not be displaced.
        /// Returns true when the channel actually changed.
        /// </summary>
        public bool TryUpgradeToUia()
        {
            try
            {
                lock (_reacquireLock)
                {
                    if (_ctx == IntPtr.Zero || Tier != SpeechTier.Synthesiser) return false;
                    if (!UiaAudiencePresent())
                    {
                        Tracing.TraceLine(
                            "Prism: no UIA client listening - staying on the synthesiser.",
                            TraceLevel.Verbose);
                        return false;
                    }

                    var upgraded = TryCreate(PrismNative.BackendUia, "UIA");
                    if (upgraded == IntPtr.Zero) return false;
                    if (!SwapBackend(upgraded)) return false;

                    Tier = SpeechTier.UiaNotifications;
                    _activeReaderId = 0;
                    Tracing.TraceLine(
                        "Prism: upgraded from synthesiser to UIA notifications - "
                        + "a screen reader is listening, so it speaks our text itself.",
                        TraceLevel.Info);
                }
                RaiseChannelChanged();
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Prism: UIA upgrade threw: {ex.Message}", TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>
        /// Adopt an already-initialised candidate backend, freeing the old one
        /// ONLY on success and restoring it on failure. The shape matters:
        /// freeing first and adopting second leaves the operator in silence
        /// when the new channel turns out unusable — and silence is the one
        /// failure a blind operator cannot diagnose. Callers hold
        /// <see cref="_reacquireLock"/>.
        /// </summary>
        private bool SwapBackend(IntPtr candidate)
        {
            var previous = _backend;
            _backend = candidate;
            if (!AdoptBackend(candidate))
            {
                _backend = previous;
                PrismNative.prism_backend_free(candidate);
                if (previous != IntPtr.Zero) AdoptBackend(previous);
                return false;
            }
            if (previous != IntPtr.Zero) PrismNative.prism_backend_free(previous);
            return true;
        }

        private void RaiseChannelChanged()
        {
            try { ChannelChanged?.Invoke(Tier, DetectedReader); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Prism: ChannelChanged handler threw: {ex.Message}", TraceLevel.Warning);
            }
        }

        // ── Screen-reader recovery (#167) ─────────────────────────────────
        //
        // SelectBackend runs once, before the app has drawn a window. If NVDA
        // was not up yet at that instant — JJ Flexible auto-launched racing
        // NVDA's own startup, or NVDA crashed and restarted, which blind users
        // experience routinely — the operator got a raw synthesiser for the
        // life of the process: TryUpgradeToUia only ever climbed Synthesiser →
        // UIA, and nothing anywhere re-attempted the controller readers.
        //
        // Prism's availability enumerator is the trigger. It calls the thunk
        // below on ITS OWN thread whenever a backend's availability changes;
        // the real work is pushed to a worker task so the poll loop is never
        // blocked, and every attempt is serialised by _reacquireLock.
        //
        // This was not buildable before the v0.18.1 upgrade: until Prism
        // commit b1446f4 the NVDA backend never freed its RPC binding, so
        // initialize() FAILED on every attempt after the first — the retry
        // below would have created a backend that could never come up. Task 1
        // (the DLL swap) is the load-bearing half of this fix.

        /// <summary>
        /// Native entry point for availability changes. Runs on Prism's
        /// enumerator thread: catch everything (a managed exception escaping
        /// into native code kills the process) and return fast (this runs
        /// inside the poll loop).
        /// </summary>
        private static void OnAvailabilityChangedThunk(
            IntPtr userdata, ulong backendId, IntPtr name, bool available)
        {
            try
            {
                if (GCHandle.FromIntPtr(userdata).Target is PrismScreenReader self)
                    self.OnAvailabilityChanged(backendId, PrismNative.ReadUtf8(name), available);
            }
            catch { /* never let an exception cross into the native poll loop */ }
        }

        private void OnAvailabilityChanged(ulong backendId, string? name, bool available)
        {
            // Only controller readers can improve on where we are; a
            // synthesiser or UIA appearing changes nothing worth moving for.
            string? label = null;
            foreach (var (id, readerName) in PrismNative.ControllerReaders)
            {
                if (id == backendId) { label = readerName; break; }
            }
            if (label == null) return;

            if (!available)
            {
                // Only the loss of the reader WE hold matters. The handle
                // stays: calls to a dead reader fail harmlessly, and tearing
                // the channel down here would race every Speak in flight. The
                // flag is what lets the reader's RETURN displace what is now a
                // dead binding even though Tier still reads ScreenReader.
                if (Tier == SpeechTier.ScreenReader && backendId == _activeReaderId)
                {
                    _readerLost = true;
                    Tracing.TraceLine(
                        $"Prism: {label} went away - holding the dead channel and "
                        + "waiting for a reader to come back.",
                        TraceLevel.Warning);
                }
                return;
            }

            // A working controller reader is never displaced: if JAWS starts
            // while NVDA is speaking for us, the operator's channel stands.
            if (Tier == SpeechTier.ScreenReader && !_readerLost) return;

            // Worker task, not the enumerator thread: creating and
            // initialising a backend does RPC to the reader, and doing that
            // inside the poll loop would stall availability detection itself.
            Task.Run(() => TryReacquireReader(backendId, label));
        }

        /// <summary>
        /// Attempt to move the channel onto a controller reader that just
        /// became available. The retry loop covers the announce-to-accept gap:
        /// availability means the reader process exists, and its controller
        /// RPC endpoint can lag that by a moment — NVDA announces itself
        /// before its API is up. Five attempts a second apart is enough for
        /// any observed reader startup; a reader that still refuses gets
        /// caught by the NEXT availability cycle rather than polled forever.
        /// </summary>
        private void TryReacquireReader(ulong backendId, string label)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(1000);
                try
                {
                    lock (_reacquireLock)
                    {
                        // The context died (app exiting) or another recovery
                        // already won - both mean stop, not retry.
                        if (_ctx == IntPtr.Zero) return;
                        if (Tier == SpeechTier.ScreenReader && !_readerLost) return;

                        var b = TryCreate(backendId, label);
                        if (b == IntPtr.Zero) continue;
                        if (!SwapBackend(b)) continue;

                        Tier = SpeechTier.ScreenReader;
                        _activeReaderId = backendId;
                        _readerLost = false;
                        Tracing.TraceLine(
                            $"Prism: re-acquired {label} - speech now goes through "
                            + "the operator's own screen reader.",
                            TraceLevel.Info);
                    }
                    RaiseChannelChanged();
                    return;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine(
                        $"Prism: re-acquire of {label} threw: {ex.Message}", TraceLevel.Warning);
                    return;
                }
            }
            Tracing.TraceLine(
                $"Prism: {label} became available but would not initialise after "
                + "5 attempts - staying on the current channel until the next "
                + "availability change.",
                TraceLevel.Warning);
        }

        /// <summary>Create and initialise one specific backend, or IntPtr.Zero.</summary>
        private IntPtr TryCreate(ulong id, string label)
        {
            var b = PrismNative.prism_registry_create(_ctx, id);
            if (b == IntPtr.Zero) return IntPtr.Zero;

            var rc = PrismNative.prism_backend_initialize(b);
            if (rc == PrismError.Ok || rc == PrismError.AlreadyInitialized) return b;

            Tracing.TraceLine(
                $"Prism: {label} present but would not initialise "
                + $"({PrismNative.ErrorString(rc)}).",
                TraceLevel.Verbose);
            PrismNative.prism_backend_free(b);
            return IntPtr.Zero;
        }

        /// <summary>
        /// Read a backend's feature bits and identity. Returns false when the
        /// backend cannot speak at all - pretending otherwise leaves the
        /// operator in silence while the app believes it is talking.
        /// </summary>
        private bool AdoptBackend(IntPtr backend)
        {
            var features = (PrismBackendFeature)PrismNative.prism_backend_get_features(backend);
            _supportsOutput = features.HasFlag(PrismBackendFeature.SupportsOutput);
            _supportsSpeak = features.HasFlag(PrismBackendFeature.SupportsSpeak);
            _supportsBraille = features.HasFlag(PrismBackendFeature.SupportsBraille);
            _supportsStop = features.HasFlag(PrismBackendFeature.SupportsStop);
            _supportsIsSpeaking = features.HasFlag(PrismBackendFeature.SupportsIsSpeaking);
            DetectedReader = PrismNative.ReadUtf8(PrismNative.prism_backend_name(backend));

            if (_supportsOutput || _supportsSpeak) return true;

            Tracing.TraceLine(
                "Prism: backend supports neither output nor speak - unusable.",
                TraceLevel.Warning);
            return false;
        }

        private static bool UiaAudiencePresent()
        {
            try { return PrismNative.UiaClientsAreListening(); }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"Prism: UiaClientsAreListening unavailable ({ex.Message}).",
                    TraceLevel.Verbose);
                return false;
            }
        }

        public void Speak(string message, bool interrupt)
        {
            if (_backend == IntPtr.Zero || string.IsNullOrEmpty(message)) return;
            try
            {
                if (_supportsSpeak) PrismNative.prism_backend_speak(_backend, message, interrupt);
                else if (_supportsOutput) PrismNative.prism_backend_output(_backend, message, interrupt);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Prism: Speak threw: {ex.Message}", TraceLevel.Error);
            }
        }

        /// <summary>Speech plus braille in one call.</summary>
        public void Output(string message, bool interrupt)
        {
            if (_backend == IntPtr.Zero || string.IsNullOrEmpty(message)) return;
            try
            {
                if (_supportsOutput)
                {
                    var rc = PrismNative.prism_backend_output(_backend, message, interrupt);
                    // A backend may advertise output and still not implement it.
                    if (rc == PrismError.NotImplemented && _supportsSpeak)
                        PrismNative.prism_backend_speak(_backend, message, interrupt);
                    return;
                }
                if (_supportsSpeak) PrismNative.prism_backend_speak(_backend, message, interrupt);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Prism: Output threw: {ex.Message}", TraceLevel.Error);
            }
        }

        public void Braille(string message)
        {
            if (_backend == IntPtr.Zero || !_supportsBraille || string.IsNullOrEmpty(message)) return;
            try { PrismNative.prism_backend_braille(_backend, message); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Prism: Braille threw: {ex.Message}", TraceLevel.Error);
            }
        }

        public void Silence()
        {
            if (_backend == IntPtr.Zero || !_supportsStop) return;
            try { PrismNative.prism_backend_stop(_backend); } catch { /* best effort */ }
        }

        public void Dispose() => Cleanup();

        private void Cleanup()
        {
            try
            {
                // Same lock as the re-acquire path: a recovery worker that
                // reached for _ctx after prism_shutdown would be a native
                // crash. Holding the lock here makes the worker either finish
                // first or see the zeroed context and stand down.
                lock (_reacquireLock)
                {
                    if (_backend != IntPtr.Zero)
                    {
                        PrismNative.prism_backend_free(_backend);
                        _backend = IntPtr.Zero;
                    }
                    if (_ctx != IntPtr.Zero)
                    {
                        PrismNative.prism_shutdown(_ctx);
                        _ctx = IntPtr.Zero;
                    }
                }

                // Only after prism_shutdown, which joins the enumerator thread:
                // no further availability callback can arrive, so the handle
                // the callbacks resolve is now safe to release.
                if (_selfHandle.IsAllocated) _selfHandle.Free();
            }
            catch { /* best effort — we are usually on the way out */ }
        }
    }
}
