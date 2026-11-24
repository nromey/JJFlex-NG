using System.Windows.Forms;
using Radios.Speech;

namespace Radios
{
    /// <summary>
    /// Whether a SmartLink sign-in that ended without a token says "Login
    /// cancelled" (#1244, ruled by Noel 2026-11-23: the same two words as the
    /// Manage SmartLink Accounts sign-in window, from both of
    /// <c>PerformNewLogin</c>'s windows).
    /// </summary>
    /// <remarks>
    /// <para><b>Said only when the OPERATOR closed a window.</b> JJ Flexible's
    /// own "SmartLink Sign In" form: its Cancel button, Escape, or closing it,
    /// all of which come back as <see cref="DialogResult.Cancel"/>; a failure
    /// on its dialog thread comes back as <see cref="DialogResult.Abort"/> and
    /// says nothing. Choosing the browser instead (<see cref="DialogResult.Retry"/>)
    /// is not a cancel. The browser page: only when it records that the
    /// operator closed it (Escape, Alt+F4, the title bar). Its error boxes and
    /// a failed token exchange also end in Cancel, and say nothing.</para>
    /// <para>Pure, so it is tested without either window.</para>
    /// </remarks>
    public static class SmartLinkSignInCancel
    {
        /// <param name="nativeResult">How JJ Flexible's own sign-in form closed.</param>
        /// <param name="browserResult">How the browser page closed; null when it was never shown.</param>
        /// <param name="browserClosedByOperator">True when the browser page records that the operator closed it.</param>
        public static bool ShouldSay(DialogResult nativeResult, DialogResult? browserResult, bool browserClosedByOperator)
        {
            if (nativeResult == DialogResult.OK) return false;
            if (nativeResult != DialogResult.Retry) return nativeResult == DialogResult.Cancel;
            return browserResult.HasValue && browserResult.Value != DialogResult.OK && browserClosedByOperator;
        }

        /// <summary>Say it: the existing words, keyed by <see cref="SpeechSubject.SignInCancelled"/>.</summary>
        public static void Say() =>
            ScreenReaderOutput.Speak(Lexicon.Get("connect.smartlink.login_cancelled"), VerbosityLevel.Terse, true,
                subject: SpeechSubject.SignInCancelled);
    }
}
