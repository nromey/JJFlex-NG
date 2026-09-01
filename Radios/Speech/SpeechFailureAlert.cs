namespace Radios.Speech
{
    /// <summary>
    /// Task #321: when <c>prism.dll</c> does not load, the application goes
    /// silent — and until now it said so only in places you have to READ.
    ///
    /// <para><b>The shape of the bug.</b> A failed load leaves
    /// <see cref="ScreenReaderFactory"/> returning a
    /// <see cref="NullScreenReader"/>, <c>ScreenReaderOutput</c> with
    /// <c>_available = false</c>, and a catch that says what it does in as many
    /// words — "stay silent rather than crash". The fact is then reported
    /// properly in three places: the trace, Help &gt; About, and every crash
    /// bundle. <b>All three are things you read.</b> The operator this fires for
    /// is a blind operator whose application has just stopped speaking, so
    /// passive reporting is the one channel guaranteed not to reach them.</para>
    ///
    /// <para><b>What actually reaches them, and why there are two channels.</b>
    /// An earcon does not depend on the speech stack at all, so it is the one
    /// signal that survives no matter what — it is the alarm, not the
    /// explanation. The explanation goes in a message box, and that is not
    /// merely "for a sighted helper": <b>the operator's screen reader is not
    /// broken, our bridge to it is.</b> NVDA and JAWS read an ordinary Windows
    /// dialog through the platform, with no help from us, so the dialog is
    /// genuinely reachable whenever a reader is running — which is nearly
    /// always, for this audience.</para>
    ///
    /// <para><b>Why the decision lives here rather than in the startup path.</b>
    /// It is the part with a right and a wrong answer, and it is the part worth
    /// pinning with tests. Raising an alarm on a harness run that deliberately
    /// has no speech would be noise, and noise is how a real warning gets
    /// ignored.</para>
    /// </summary>
    public static class SpeechFailureAlert
    {
        /// <summary>
        /// Whether the launch should raise the no-speech alarm.
        ///
        /// <para>Both conditions matter. <paramref name="renderEnabled"/> is
        /// false for the silent verification channel (<c>--render-off</c>), where
        /// having no speech is the POINT and an alarm would be a false one — that
        /// path never reaches the Prism backend at all. Otherwise the question is
        /// simply whether the application can talk.</para>
        /// </summary>
        /// <param name="renderEnabled">
        /// <c>OutputChannelRecorder.RenderEnabled</c> — false when this run is a
        /// deliberately silent harness.
        /// </param>
        /// <param name="speechAvailable">
        /// <c>ScreenReaderOutput.IsAvailable</c> — false when the backend could
        /// not be brought up.
        /// </param>
        public static bool ShouldAlert(bool renderEnabled, bool speechAvailable)
        {
            return renderEnabled && !speechAvailable;
        }

        // ------------------------------------------------------------------
        // USER-FACING PROSE — DRAFTED, NOT FINAL. Needs Noel's review.
        //
        // It is deliberately inline rather than in the lexicon: this fires when
        // startup has already failed at something fundamental, and a message
        // explaining that the app cannot talk should not itself depend on a
        // file load succeeding. If it moves to the lexicon later it needs a
        // hardcoded fallback, which is most of the value of leaving it here.
        // ------------------------------------------------------------------

        /// <summary>Title bar of the no-speech dialog.</summary>
        public static string AlertTitle => "JJ Flexible Radio cannot speak";

        /// <summary>
        /// Body of the no-speech dialog. Short paragraphs and no lists: this is
        /// read aloud, start to finish, by whatever reader is running.
        /// </summary>
        public static string AlertMessage =>
            "JJ Flexible Radio started, but it could not load the library it uses to speak, "
            + "so it will not announce anything by itself this session."
            + "\r\n\r\n"
            + "Your screen reader is fine. It is still running, and it will read this window "
            + "and the program's controls as usual — it is only the program's own "
            + "announcements that are missing."
            + "\r\n\r\n"
            + "The file is prism.dll, and it belongs in the runtimes folder inside the "
            + "program folder. Reinstalling puts it back. Help, then About, has the details, "
            + "and so does the diagnostic log.";

        /// <summary>
        /// The line written to the trace when the alarm is raised. Separate from
        /// the spoken-to-nobody backend line the factory already writes: this one
        /// records that the operator was TOLD, which is the fact #321 is about.
        /// </summary>
        public static string TraceLine =>
            "ScreenReaderOutput: no speech backend - alarm earcon sounded and the no-speech "
            + "dialog raised, because the failure that removes the operator's channel cannot "
            + "be reported through that channel (#321).";
    }
}
