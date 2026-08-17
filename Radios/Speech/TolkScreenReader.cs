using System;
using System.Diagnostics;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// <see cref="IScreenReader"/> over Tolk — the backend JJ Flexible used
    /// from Jim's era until 2026-08-17.
    ///
    /// **Retained deliberately, and only as a fallback until Prism is proven
    /// on real hardware with both NVDA and JAWS.** Once that is confirmed this
    /// class goes, and with it four native DLLs per architecture: Tolk is a
    /// shim that loads a separate client library per reader
    /// (nvdaControllerClient, SAAPI, dolapi32), so removing it shrinks the
    /// shipped payload rather than merely swapping one file for another.
    ///
    /// Do not add features here. Anything new belongs in
    /// <see cref="PrismScreenReader"/>; this exists to be deleted.
    /// </summary>
    public sealed class TolkScreenReader : IScreenReader
    {
        private bool _loaded;

        public string BackendName => "Tolk";
        public string? DetectedReader { get; private set; }

        public bool Initialize()
        {
            try
            {
                // SAPI as a last resort so a machine with no reader running
                // still gets speech — the historical JJ Flexible behaviour.
                Tolk.TrySAPI(true);
                Tolk.Load();
                _loaded = Tolk.IsLoaded();
                if (_loaded)
                {
                    DetectedReader = Tolk.DetectScreenReader();
                    Tracing.TraceLine(
                        $"Tolk backend up: '{DetectedReader ?? "none detected"}', speech={Tolk.HasSpeech()}.",
                        TraceLevel.Info);
                }
                else
                {
                    Tracing.TraceLine("Tolk: Load() did not produce a loaded library.", TraceLevel.Warning);
                }
                return _loaded;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Tolk: Initialize threw: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        public bool HasSpeech
        {
            get { try { return _loaded && Tolk.HasSpeech(); } catch { return false; } }
        }

        public bool HasBraille
        {
            get { try { return _loaded && Tolk.HasBraille(); } catch { return false; } }
        }

        public void Speak(string message, bool interrupt)
        {
            if (!_loaded || string.IsNullOrEmpty(message)) return;
            try { Tolk.Speak(message, interrupt); }
            catch (Exception ex) { Tracing.TraceLine($"Tolk: Speak threw: {ex.Message}", TraceLevel.Error); }
        }

        public void Output(string message, bool interrupt)
        {
            if (!_loaded || string.IsNullOrEmpty(message)) return;
            try { Tolk.Output(message, interrupt); }
            catch (Exception ex) { Tracing.TraceLine($"Tolk: Output threw: {ex.Message}", TraceLevel.Error); }
        }

        public void Braille(string message)
        {
            if (!_loaded || string.IsNullOrEmpty(message)) return;
            try { Tolk.Braille(message); }
            catch (Exception ex) { Tracing.TraceLine($"Tolk: Braille threw: {ex.Message}", TraceLevel.Error); }
        }

        public void Silence()
        {
            if (!_loaded) return;
            try { Tolk.Silence(); } catch { /* best effort */ }
        }

        public void Dispose()
        {
            if (!_loaded) return;
            try { Tolk.Unload(); } catch { /* best effort */ }
            _loaded = false;
        }
    }
}
