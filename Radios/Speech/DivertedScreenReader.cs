namespace Radios.Speech
{
    /// <summary>
    /// The #171 render-off backend: reports a working speech channel and
    /// discards everything handed to it.
    ///
    /// Deliberately DISTINCT from <see cref="NullScreenReader"/>, which means
    /// "prism.dll is missing — the app cannot talk to a blind operator" and
    /// reports HasSpeech=false so callers can see the failure. This one means
    /// "the operator (or a harness) asked for silence on purpose": HasSpeech is
    /// TRUE, so every layer of policy above the backend — verbosity gating,
    /// intent arbitration, coalescing, suppression, the last-message and
    /// history rings — runs exactly as it does in production, and only the
    /// final hand-off goes nowhere. A silent test that passes against this
    /// backend has exercised the same decisions the operator's ears get.
    ///
    /// Prism is never loaded when this backend is selected, so no native DLL,
    /// no screen reader hookup, no SAPI voice — nothing that could make a
    /// sound or steal the screen reader from someone using the machine.
    /// </summary>
    internal sealed class DivertedScreenReader : IScreenReader
    {
        public string BackendName => "diverted";
        public string? DetectedReader => null;
        public bool HasSpeech => true;
        public bool HasBraille => false;

        public bool Initialize() => true;
        public void Speak(string message, bool interrupt) { }
        public void Output(string message, bool interrupt) { }
        public void Braille(string message) { }
        public void Silence() { }
        public void Dispose() { }
    }
}
