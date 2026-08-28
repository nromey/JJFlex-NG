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

        // Accepted, deliberately — the whole point of this backend is that
        // every policy layer above it behaves exactly as in production, and
        // the ledger, the salvage rule and the anti-clip gap all key on
        // delivery. Reporting NotAttempted here would quietly disable the
        // protections a silent test run exists to exercise. Whether anything
        // SOUNDED is a different question, answered by the transcript's
        // rendered flag.
        public SpeechDelivery Speak(string message, bool interrupt) => SpeechDelivery.Accepted;
        public SpeechDelivery Output(string message, bool interrupt) => SpeechDelivery.Accepted;

        // Braille is the exception: HasBraille is false, so claiming delivery
        // would assert a display that is not there.
        public SpeechDelivery Braille(string message) => SpeechDelivery.NotAttempted;
        public void Silence() { }
        public void Dispose() { }
    }
}
