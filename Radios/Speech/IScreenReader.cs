namespace Radios.Speech
{
    /// <summary>
    /// Backend-neutral screen-reader output surface. Every direct-speech call
    /// in JJ Flexible ultimately arrives here, so the backend (Tolk today,
    /// Prism from 2026-08-17) can change without the ~665 call sites above
    /// knowing which one is in use.
    ///
    /// The verbosity gate, the suppression flag and the last-message history
    /// live in <see cref="ScreenReaderOutput"/>, ABOVE this interface, so that
    /// policy applies uniformly to whichever backend is loaded.
    ///
    /// **This interface is not speculation.** Before it existed, Tolk calls
    /// were welded directly into ScreenReaderOutput, which is precisely why
    /// changing backend was a project rather than a configuration change. One
    /// implementation is still worth an interface when the whole point is that
    /// the implementation is replaceable.
    /// </summary>
    public interface IScreenReader : System.IDisposable
    {
        /// <summary>
        /// Bring the backend up. Returns true when a usable output channel
        /// exists; false means the caller should try another backend.
        ///
        /// **Must never throw.** A missing native DLL, an absent screen reader
        /// or an initialisation error are all ordinary outcomes here and must
        /// come back as false — an exception would take the app down before it
        /// ever drew a window, on the machines least able to recover from it.
        /// </summary>
        bool Initialize();

        /// <summary>Speak text. interrupt=true cuts off speech in progress.</summary>
        void Speak(string message, bool interrupt);

        /// <summary>
        /// Speak AND push to a braille display in one call, where the backend
        /// supports it. Falls back to <see cref="Speak"/> when it does not, so
        /// callers never need to ask.
        /// </summary>
        void Output(string message, bool interrupt);

        /// <summary>
        /// Push text to a connected braille display WITHOUT speaking it. Used
        /// by the status line, which updates far too often to be spoken.
        /// No-op when the backend or the machine has no braille.
        /// </summary>
        void Braille(string message);

        /// <summary>Stop speech immediately. Best-effort.</summary>
        void Silence();

        /// <summary>True when this backend can currently produce speech.</summary>
        bool HasSpeech { get; }

        /// <summary>True when a braille display is present and reachable.</summary>
        bool HasBraille { get; }

        /// <summary>Backend identity for diagnostics — "Tolk" or "Prism".</summary>
        string BackendName { get; }

        /// <summary>
        /// The detected screen reader or TTS target ("NVDA", "JAWS", "SAPI"),
        /// or null when none was detected. Diagnostics only — never branch
        /// behaviour on it. Reader-specific special-casing is how an app comes
        /// to work well for one reader and badly for the rest.
        /// </summary>
        string? DetectedReader { get; }
    }
}
