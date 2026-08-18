namespace Radios.Speech
{
    /// <summary>
    /// What KIND of speech channel the application ended up on.
    ///
    /// These are not interchangeable, and the difference is audible in ways an
    /// operator cannot diagnose by listening — which is why the tier is
    /// reported in Help &gt; About and in the trace. "Speech works" and "speech
    /// works well" sound similar right up until two voices collide.
    /// </summary>
    public enum SpeechTier
    {
        /// <summary>No usable channel. The application is silent.</summary>
        None = 0,

        /// <summary>
        /// A screen reader with a controller API — NVDA, JAWS and friends.
        /// The best case by a distance: our text enters the reader's own
        /// queue, is spoken in the operator's configured voice and rate, obeys
        /// their interrupt behaviour, and reaches their braille display.
        /// </summary>
        ScreenReader,

        /// <summary>
        /// UI Automation notifications. Whichever reader is attached speaks our
        /// text itself, so it cannot collide with that reader's own output.
        ///
        /// This is the ONLY channel that reaches Windows Narrator, which
        /// exposes no controller API. Requires the application to own a
        /// visible top-level window, so it can only be established after the
        /// main window is shown — never during early startup.
        /// </summary>
        UiaNotifications,

        /// <summary>
        /// A raw synthesiser (OneCore or SAPI). An independent voice that knows
        /// nothing about any screen reader on the machine.
        ///
        /// Correct ONLY when nothing else is listening — for instance a
        /// magnifier user who runs no screen reader but still wants the
        /// important things spoken. Ctrl+Shift+V turns it off.
        ///
        /// Landing here WITH a screen reader running is the failure this tier
        /// system exists to prevent: two speakers, no shared queue.
        /// </summary>
        Synthesiser,
    }
}
