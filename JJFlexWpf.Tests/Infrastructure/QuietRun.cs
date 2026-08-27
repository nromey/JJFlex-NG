using System.Runtime.CompilerServices;
using Radios;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// Tier 1 makes no sound.
/// </summary>
/// <remarks>
/// <para>
/// <b>Task #233, found 2026-08-25 by Noel from the operator's chair while a
/// Tier 1 run was going:</b> "it doesn't bring up menu windows, it does ding
/// whenever it does the tests, you just can't see them."
/// </para>
/// <para>
/// <b>The isolation was built to a sighted person's definition of safe.</b>
/// <see cref="PrivateDesktop"/> moves the UI thread to a desktop object nobody
/// looks at, so the windows genuinely cannot be seen. It does nothing whatever
/// about AUDIO: earcons, system sounds and speech go to the session's audio
/// device regardless of which desktop the window belongs to. For this project's
/// users that is close to worthless and arguably worse than visible windows —
/// sounds arrive from something that cannot be found, focused or dismissed.
/// "You cannot see it" and "you are not disturbed by it" are the same sentence
/// only if the person is sighted.
/// </para>
/// <para>
/// <b>No second mechanism was built.</b> The silent verification channel
/// (#171) already does exactly this:
/// <see cref="OutputChannelRecorder.RenderEnabled"/> is consulted by
/// <c>ScreenReaderOutput</c> before every utterance and by
/// <c>EarconPlayer</c>, <c>EarconCwOutput</c> and <c>MorseNotifier</c> before
/// any audio device is opened — including the <c>Console.Beep</c> fallback,
/// which the earcon player already treats as an audio device "as far as a blind
/// operator's ears are concerned". Turning render off is therefore a
/// suppression the app itself already understands rather than a new switch.
/// </para>
/// <para>
/// <b>"Something might forget to check it" is not hypothetical, and that
/// sentence used to say it was.</b> <c>ClusterDialog</c> called
/// <c>Console.Beep()</c> raw, two files from the <c>EarconPlayer</c> fallback
/// that states the rule; <c>LOTWMerge</c> and <c>LogEntry</c> did the same in
/// the VB tree. All four are gated now, and
/// <c>Radios.Tests.AudioGateCoverageTests</c> is the standing rule that catches
/// the fifth. Two projects that make sound — <c>JJArclusterLib</c> and
/// <c>JJLogLib</c> — do not reference <c>Radios</c> and so cannot consult the
/// gate at all; neither is reached by this tier, and both are named in that
/// rule rather than quietly excluded.
/// </para>
/// <para>
/// <b>What is NOT covered: see <see cref="UnsuppressedSounds"/>.</b> It is
/// stated in every run report, including the ones where everything worked.
/// </para>
/// <para>
/// <b>A module initializer, not a fixture.</b> This has to be true before the
/// first dialog is constructed, and dialogs are constructed from test bodies
/// whose types load at unpredictable moments; <c>ScreenReaderOutput</c> and
/// <c>EarconPlayer</c> read <c>RenderEnabled</c> when they initialise their
/// devices, so being late is the same as not doing it.
/// </para>
/// <para>
/// <b>Recording is off by default and available.</b> Setting
/// <c>JJFLEX_TIER1_RECORD=1</c> (or a path) opens a transcript of everything
/// the run WOULD have said. That is a free improvement to what the tier can
/// assert — the recorded stream comes from the same code path that renders — but
/// writing a file on every run is a side effect, and this type exists to remove
/// side effects rather than add them.
/// </para>
/// </remarks>
internal static class QuietRun
{
    /// <summary>Environment variable that also opens a transcript of the run.</summary>
    public const string RecordVariable = "JJFLEX_TIER1_RECORD";

    /// <summary>
    /// The sound this cannot switch off, stated in every run report because a
    /// suppression that overstates its reach is the instrument this whole track
    /// exists to remove.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MessageBox.Show</c> with any <c>MessageBoxImage</c> plays a Windows
    /// system sound through <c>user32</c>'s own <c>MessageBeep</c>, before a
    /// single line of our code runs. <c>RenderEnabled</c> cannot reach it: it is
    /// not our audio stack, and there is no per-process way to mute it that does
    /// not change a machine-wide setting belonging to the operator.
    /// </para>
    /// <para>
    /// It is not hypothetical here. <see cref="ModalWatchdog"/> exists precisely
    /// because at least one dialog raises a message box while it is being
    /// CONSTRUCTED, which is the one thing this tier does to every dialog in the
    /// app — and the watchdog closes that box after it has appeared, which is
    /// after it has already sounded. So it is named rather than counted as
    /// suppressed. An operator who heard a ding during an otherwise silent run
    /// deserves a report that accounts for it instead of one that contradicts
    /// him.
    /// </para>
    /// </remarks>
    public const string UnsuppressedSounds =
        "Windows' own message-box sounds are NOT suppressed — a dialog that raises a "
        + "MessageBox while it is constructed makes that sound through user32 before any "
        + "of our code runs, and nothing in this process can gate it.";

    /// <summary>True once render has been turned off for this process.</summary>
    public static bool Silenced { get; private set; }

    /// <summary>What went wrong, if anything. Null when the run is silent.</summary>
    public static string? Failure { get; private set; }

    [ModuleInitializer]
    internal static void Silence()
    {
        try
        {
            string? record = Environment.GetEnvironmentVariable(RecordVariable);
            bool wantRecord = !string.IsNullOrWhiteSpace(record);
            string? path = (wantRecord && record != "1") ? record : null;

            OutputChannelRecorder.Configure(render: false, record: wantRecord, explicitPath: path);

            // Read it back rather than assume the call took. A silencing that
            // reports itself done without being done is the exact shape of the
            // defect this closes.
            Silenced = !OutputChannelRecorder.RenderEnabled;
            if (!Silenced)
            {
                Failure = "OutputChannelRecorder.Configure(render: false) returned with "
                          + "RenderEnabled still true.";
            }
        }
        catch (Exception ex)
        {
            Failure = ex.GetType().Name + ": " + ex.Message;
        }

        if (Failure != null)
        {
            Console.Error.WriteLine(
                "QuietRun: THIS RUN MAY MAKE SOUND. " + Failure);
        }
    }

    /// <summary>
    /// One line for the run report. An operator who heard something during a run
    /// must be able to find out afterwards whether the run believed it was
    /// silent.
    /// </summary>
    public static string Describe()
    {
        if (!Silenced) return "audio NOT suppressed — this run could be heard" +
                              (Failure == null ? "" : " (" + Failure + ")");

        string what = OutputChannelRecorder.RecordEnabled
            ? "audio suppressed — speech and earcons rendered nothing; what would have been said "
              + "was recorded to " + (OutputChannelRecorder.TranscriptPath ?? "a transcript")
            : "audio suppressed — speech and earcons rendered nothing";

        // The residual is stated on the SUCCESS path deliberately. A caveat
        // that only appears when something already went wrong is a caveat the
        // reader has never seen, and this one explains a sound heard during a
        // run that was otherwise entirely correct.
        return what + ". " + UnsuppressedSounds;
    }
}
