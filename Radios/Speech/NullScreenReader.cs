namespace Radios.Speech
{
    /// <summary>
    /// The backend of last resort: does nothing, safely.
    ///
    /// Returned by <see cref="ScreenReaderFactory"/> when Prism cannot come up
    /// at all — which, since Prism falls back to SAPI internally when no screen
    /// reader is running, means prism.dll is missing or broken. That is a
    /// DEPLOYMENT failure, not an environment one.
    ///
    /// This exists so the factory never returns null and no call site needs a
    /// guard. It is deliberately not a silent no-op: reaching it means a blind
    /// operator has an application that cannot talk to them, which is the most
    /// serious failure this app has. The factory traces it at Error, and
    /// ScreenReaderOutput reports it through TraceBackend on every launch, so a
    /// trace file always answers "why is it silent" on the first line anyone
    /// looks at.
    /// </summary>
    internal sealed class NullScreenReader : IScreenReader
    {
        public string BackendName => "none";
        public string? DetectedReader => null;
        public bool HasSpeech => false;
        public bool HasBraille => false;

        public bool Initialize() => false;

        // NotAttempted, not Failed: there is no backend here to refuse
        // anything, and the deployment fault is already reported once and
        // loudly by the factory. Reporting a delivery failure per utterance as
        // well would bury that one line under thousands of duplicates.
        public SpeechDelivery Speak(string message, bool interrupt) => SpeechDelivery.NotAttempted;
        public SpeechDelivery Output(string message, bool interrupt) => SpeechDelivery.NotAttempted;
        public SpeechDelivery Braille(string message) => SpeechDelivery.NotAttempted;
        public void Silence() { }
        public void Dispose() { }
    }
}
