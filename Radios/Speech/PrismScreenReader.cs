using System;
using System.Diagnostics;
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
                _ctx = PrismNative.prism_init(ref cfg);
                if (_ctx == IntPtr.Zero)
                {
                    Tracing.TraceLine("Prism: prism_init returned a null context.", TraceLevel.Warning);
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

                var previous = _backend;
                _backend = upgraded;
                if (!AdoptBackend(upgraded))
                {
                    // Unusable after all - put the synthesiser back rather than
                    // leaving the operator in silence.
                    _backend = previous;
                    PrismNative.prism_backend_free(upgraded);
                    AdoptBackend(previous);
                    return false;
                }

                Tier = SpeechTier.UiaNotifications;
                if (previous != IntPtr.Zero) PrismNative.prism_backend_free(previous);
                Tracing.TraceLine(
                    "Prism: upgraded from synthesiser to UIA notifications - "
                    + "a screen reader is listening, so it speaks our text itself.",
                    TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Prism: UIA upgrade threw: {ex.Message}", TraceLevel.Warning);
                return false;
            }
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
            catch { /* best effort — we are usually on the way out */ }
        }
    }
}
