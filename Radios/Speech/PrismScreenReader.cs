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

        public string BackendName => "Prism";
        public string? DetectedReader { get; private set; }
        public bool HasSpeech => _backend != IntPtr.Zero && (_supportsOutput || _supportsSpeak);
        public bool HasBraille => _backend != IntPtr.Zero && _supportsBraille;

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

                _backend = PrismNative.prism_registry_acquire_best(_ctx);
                if (_backend == IntPtr.Zero)
                {
                    Tracing.TraceLine("Prism: no screen reader or TTS backend available.", TraceLevel.Warning);
                    Cleanup();
                    return false;
                }

                var rc = PrismNative.prism_backend_initialize(_backend);
                if (rc != PrismError.Ok && rc != PrismError.AlreadyInitialized)
                {
                    Tracing.TraceLine(
                        $"Prism: backend initialize failed: {PrismNative.ErrorString(rc)}",
                        TraceLevel.Warning);
                    Cleanup();
                    return false;
                }

                var features = (PrismBackendFeature)PrismNative.prism_backend_get_features(_backend);
                _supportsOutput = features.HasFlag(PrismBackendFeature.SupportsOutput);
                _supportsSpeak = features.HasFlag(PrismBackendFeature.SupportsSpeak);
                _supportsBraille = features.HasFlag(PrismBackendFeature.SupportsBraille);
                _supportsStop = features.HasFlag(PrismBackendFeature.SupportsStop);
                DetectedReader = PrismNative.ReadUtf8(PrismNative.prism_backend_name(_backend));

                // A backend with neither entry point cannot speak, and pretending
                // otherwise would leave the operator in silence with the app
                // believing it is talking.
                if (!_supportsOutput && !_supportsSpeak)
                {
                    Tracing.TraceLine(
                        "Prism: backend supports neither output nor speak — unusable.",
                        TraceLevel.Warning);
                    Cleanup();
                    return false;
                }

                Tracing.TraceLine(
                    $"Prism backend up: '{DetectedReader ?? "unknown"}' "
                    + $"(output={_supportsOutput}, speak={_supportsSpeak}, "
                    + $"braille={_supportsBraille}, stop={_supportsStop}).",
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
