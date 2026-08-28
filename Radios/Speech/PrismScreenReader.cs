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

            // The decision itself lives in ReaderAvailabilityEdge, as pure
            // policy with its own tests (#291). It used to live here, as a
            // comment describing one rule sitting above a line implementing a
            // different one — "never displace a working reader" against code
            // that refused to NOTICE a new reader — and nothing could tell
            // them apart, because there was no seam between the policy and the
            // P/Invoke carrying it out. Read that class before changing this.
            var action = ReaderAvailabilityEdge.Decide(
                holdingControllerReader: Tier == SpeechTier.ScreenReader,
                heldReaderLost: _readerLost,
                isHeldReader: backendId == _activeReaderId,
                nowAvailable: available);

            switch (action)
            {
                case ReaderAvailabilityAction.Ignore:
                    return;

                case ReaderAvailabilityAction.HoldAndSweep:
                    // The handle stays: calls to a dead reader fail harmlessly,
                    // and tearing the channel down here would race every Speak
                    // in flight. The flag is what tells everything downstream
                    // that the binding is dead even though Tier still reads
                    // ScreenReader.
                    _readerLost = true;
                    Tracing.TraceLine(
                        $"Prism: {label} went away - holding the dead channel and "
                        + $"looking for another reader in {LostReaderSettleMs} ms, "
                        + "in case this one is only restarting.",
                        TraceLevel.Warning);

                    // THE #291 FIX. Waiting for a RISE was the bug: a reader
                    // that is already running cannot rise again, so this sweep
                    // is the only thing that can ever find it.
                    Task.Run(SweepAfterLoss);
                    return;

                case ReaderAvailabilityAction.Reacquire:
                    // Worker task, not the enumerator thread: creating and
                    // initialising a backend does RPC to the reader, and doing
                    // that inside the poll loop would stall availability
                    // detection itself.
                    Task.Run(() => TryReacquire(
                        new[] { (backendId, label) }, $"{label} became available"));
                    return;
            }
        }

        /// <summary>
        /// How long to wait, after the held reader disappears, before adopting
        /// a DIFFERENT reader that is already running.
        ///
        /// <b>The wait is the policy, not a delay for its own sake.</b> A
        /// reader that has gone away is very often a reader that is coming
        /// back — NVDA restarts are routine for the people who use it — and a
        /// restart must not cause a rebind. That is the same intent the
        /// watchdog encodes as its longer no-reader settle
        /// (<c>ScreenReaderWatch.NoReaderSettleTicks</c>), and it is preserved
        /// here rather than deleted along with the edge bug.
        ///
        /// If the lost reader returns inside this window it raises its own
        /// availability rise, that path re-acquires it, <c>_readerLost</c>
        /// clears, and the sweep below finds nothing to do and stands down —
        /// which is the correct outcome and costs the operator nothing.
        /// </summary>
        private const int LostReaderSettleMs = 3000;

        /// <summary>
        /// The reader we were bound to has gone. Look for one that is ALREADY
        /// running, in preference order.
        ///
        /// This is the whole of #291: the walk is the same one
        /// <see cref="SelectBackend"/> performs at startup, and that walk is
        /// known to work — launching with JAWS already up has always bound to
        /// JAWS correctly. The fault was never that we could not find a
        /// running reader; it was that after startup we only ever looked when
        /// one APPEARED.
        /// </summary>
        private void SweepAfterLoss()
        {
            try
            {
                System.Threading.Thread.Sleep(LostReaderSettleMs);

                // The lost reader came back on its own, or the app is exiting.
                if (!_readerLost || _ctx == IntPtr.Zero) return;

                TryReacquire(PrismNative.ControllerReaders,
                    "the reader we were bound to went away");
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"Prism: sweep after reader loss threw: {ex.Message}", TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Attempt to move the channel onto the first of
        /// <paramref name="candidates"/> that comes up. The retry loop covers
        /// the announce-to-accept gap: availability means the reader process
        /// exists, and its controller RPC endpoint can lag that by a moment —
        /// NVDA announces itself before its API is up. Five attempts a second
        /// apart is enough for any observed reader startup; a reader that
        /// still refuses gets caught by the NEXT availability cycle rather
        /// than polled forever.
        /// </summary>
        /// <param name="candidates">
        /// Readers to try, in preference order. One entry for the rising-edge
        /// path; the whole controller list for the after-loss sweep, where a
        /// candidate that is not running simply fails to create and the walk
        /// moves on — including the lost reader itself, which is adopted again
        /// if it turns out to be back.
        /// </param>
        /// <param name="reason">Why we are looking, for the trace.</param>
        private void TryReacquire(
            System.Collections.Generic.IReadOnlyList<(ulong Id, string Name)> candidates,
            string reason)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(1000);
                try
                {
                    string adopted;
                    lock (_reacquireLock)
                    {
                        // The context died (app exiting) or another recovery
                        // already won - both mean stop, not retry.
                        if (_ctx == IntPtr.Zero) return;
                        if (Tier == SpeechTier.ScreenReader && !_readerLost) return;

                        adopted = TryAdoptFirstLocked(candidates);
                        if (adopted.Length == 0) continue;

                        Tracing.TraceLine(
                            $"Prism: re-acquired {adopted} ({reason}) - speech now goes "
                            + "through the operator's own screen reader.",
                            TraceLevel.Info);
                    }
                    RaiseChannelChanged();
                    return;
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine(
                        $"Prism: re-acquire threw ({reason}): {ex.Message}", TraceLevel.Warning);
                    return;
                }
            }
            Tracing.TraceLine(
                $"Prism: no controller reader would initialise after 5 attempts ({reason}) - "
                + "staying on the current channel until the next availability change. "
                + "Speech is still going to a reader that may have gone; the delivery "
                + "check on every utterance (#277) will say so if it has.",
                TraceLevel.Warning);
        }

        /// <summary>
        /// Adopt the first candidate that initialises and swaps in cleanly.
        /// Returns its name, or an empty string when none would come up.
        /// Caller holds <see cref="_reacquireLock"/>.
        /// </summary>
        private string TryAdoptFirstLocked(
            System.Collections.Generic.IReadOnlyList<(ulong Id, string Name)> candidates)
        {
            foreach (var (id, name) in candidates)
            {
                var b = TryCreate(id, name);
                if (b == IntPtr.Zero) continue;
                if (!SwapBackend(b)) continue;

                Tier = SpeechTier.ScreenReader;
                _activeReaderId = id;
                _readerLost = false;
                return name;
            }
            return string.Empty;
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

        // ── Delivery (#277) ───────────────────────────────────────────────
        //
        // prism_backend_speak RETURNS A PrismError AND WE THREW IT AWAY. So
        // speaking into a reader that had left returned an error nobody read,
        // Speak returned normally, EmitCore set reachedBackend = true, and the
        // trace wrote "Spoke" for a sentence no human could have heard.
        //
        // Note the C header marks every one of these PRISM_NODISCARD. The
        // compiler was telling C callers not to do what our binding did.
        //
        // WHAT PRISM ACTUALLY RETURNS, established at the pinned source before
        // any of this was written rather than assumed — the task's own warning
        // is that a fix which reports failure on healthy utterances would be
        // worse than the silence it replaces. Read at
        // d2998e9 (v0.18.1, the shipped pin), source/prism.cpp:356:
        //
        //     const auto r = backend->impl->speak(text, interrupt);
        //     return r ? PRISM_OK : to_prism_error(r.error());
        //
        // So PRISM_OK (0) is the ONLY success value, from every backend, on
        // every successful call; there is no second healthy code to
        // accommodate. And to_prism_error is a raw static_cast from
        // BackendError, whose enum in source/backend.h matches
        // PrismError in include/prism.h index for index — checked, all 24
        // values — so PrismNative's enum names the right error and not a
        // neighbour.
        //
        // The two failures that matter here, both from the backends we
        // actually meet:
        //   - NVDA (source/backends/nvda.cpp): nvdaController_speakText
        //     returning anything but ERROR_SUCCESS becomes
        //     InternalBackendError. That is exactly the RPC to a reader that
        //     has gone, which is the fault this instrument was built for.
        //   - JAWS (source/backends/jaws.cpp): SayString returns an HRESULT
        //     AND a VARIANT_BOOL, and Prism fails the call unless BOTH say
        //     yes. A JAWS that accepts the call and refuses the utterance is
        //     therefore reportable — which is the discriminator #298 needs,
        //     and could not have while this value was discarded.

        /// <summary>
        /// Turn one native return code into a delivery result. Ok is the only
        /// success; everything else names the call, the reader and the error
        /// so the phrase can go straight into a trace line.
        /// </summary>
        private SpeechDelivery Report(PrismError rc, string call) =>
            Classify(rc, call, DetectedReader);

        /// <summary>
        /// The classification, separated from the P/Invoke so it can be tested
        /// without a screen reader on the desk.
        ///
        /// <b>Ok is the only success</b>, and that is a fact about Prism rather
        /// than an assumption about it — see the block comment above for the
        /// source that was read to establish it. Everything else is a refusal,
        /// including codes that look benign: a backend that reports
        /// NotImplemented for speak is a backend that did not speak.
        /// </summary>
        internal static SpeechDelivery Classify(PrismError rc, string call, string? reader)
        {
            if (rc == PrismError.Ok) return SpeechDelivery.Accepted;
            return SpeechDelivery.Failed(
                $"prism_backend_{call} to {reader ?? "an unnamed reader"} returned "
                + $"{rc} ({PrismNative.ErrorString(rc)})");
        }

        public SpeechDelivery Speak(string message, bool interrupt)
        {
            if (_backend == IntPtr.Zero || string.IsNullOrEmpty(message))
                return SpeechDelivery.NotAttempted;
            try
            {
                if (_supportsSpeak)
                    return Report(PrismNative.prism_backend_speak(_backend, message, interrupt), "speak");
                if (_supportsOutput)
                    return Report(PrismNative.prism_backend_output(_backend, message, interrupt), "output");
                return SpeechDelivery.NotAttempted;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Prism: Speak threw: {ex.Message}", TraceLevel.Error);
                return SpeechDelivery.Failed($"prism_backend_speak threw: {ex.Message}");
            }
        }

        /// <summary>Speech plus braille in one call.</summary>
        public SpeechDelivery Output(string message, bool interrupt)
        {
            if (_backend == IntPtr.Zero || string.IsNullOrEmpty(message))
                return SpeechDelivery.NotAttempted;
            try
            {
                if (_supportsOutput)
                {
                    var rc = PrismNative.prism_backend_output(_backend, message, interrupt);
                    // A backend may advertise output and still not implement
                    // it. That is a capability answer, not a delivery failure,
                    // so it falls through to speak rather than being reported.
                    if (rc == PrismError.NotImplemented && _supportsSpeak)
                        return Report(PrismNative.prism_backend_speak(_backend, message, interrupt), "speak");
                    return Report(rc, "output");
                }
                if (_supportsSpeak)
                    return Report(PrismNative.prism_backend_speak(_backend, message, interrupt), "speak");
                return SpeechDelivery.NotAttempted;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Prism: Output threw: {ex.Message}", TraceLevel.Error);
                return SpeechDelivery.Failed($"prism_backend_output threw: {ex.Message}");
            }
        }

        public SpeechDelivery Braille(string message)
        {
            if (_backend == IntPtr.Zero || !_supportsBraille || string.IsNullOrEmpty(message))
                return SpeechDelivery.NotAttempted;
            try { return Report(PrismNative.prism_backend_braille(_backend, message), "braille"); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Prism: Braille threw: {ex.Message}", TraceLevel.Error);
                return SpeechDelivery.Failed($"prism_backend_braille threw: {ex.Message}");
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
