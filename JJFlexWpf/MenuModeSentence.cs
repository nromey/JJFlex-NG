using System;
using System.Threading.Tasks;

namespace JJFlexWpf
{
    /// <summary>
    /// What a Slice menu mode row says, and when (#1117, repair round 1). The
    /// row asks the radio for the mode at the click; this says the row's
    /// sentence once the menu has closed, then lets the noise controller's held
    /// answer to the mode change follow it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the sentence is QUEUED and not an interrupt.</b> On the 8600 harness
    /// (2026-11-12, checks 7 and 8) the menu's "Mode CW" interrupt came right
    /// after the screen reader's own close announcements. With nothing of ours
    /// speaking and the reader having spoken since its last cancel, our cancel
    /// landed on "Mode CW" itself (the NVDA race in
    /// <c>project_nvda_cancel_lands_on_our_next_utterance</c>), the pump read
    /// that as a cut that was not ours and withdrew the noise sentence queued
    /// behind it, which then expired unheard or came back after the next key.
    /// A queued sentence sends no cancel, so there is no race to lose: the
    /// reader finishes its close announcements, says the mode, and the noise
    /// sentence goes out when the mode has been said, as on the key routes.
    /// </para>
    /// <para>
    /// <b>Why it waits.</b> Speech handed over while the menu is closing is
    /// flushed by the focus change (<c>project_speech_flushes_on_window_change</c>);
    /// Next and Previous Mode, which spoke at the click, were cut that way.
    /// </para>
    /// <para>
    /// <b>Why it may say nothing.</b> If a newer mode choice, another radio or
    /// another slice came before the menu closed, the sentence would name a mode
    /// that is no longer the answer, so it is dropped. The noise controller's
    /// held answer is released either way: the controller has already dropped it
    /// if a newer mode group replaced it.
    /// </para>
    /// </remarks>
    public static class MenuModeSentence
    {
        /// <summary>How long after the click the menu has closed and the reader's own close announcements are under way. The value the menu has always used.</summary>
        public const int AfterMenuCloseMs = 500;

        /// <param name="sentence">The row's existing words.</param>
        /// <param name="stillCurrent">False when a newer mode choice, another radio or another slice came first.</param>
        /// <param name="noiseAnswer">The noise controller's hold (<see cref="NoiseController.HoldModeAnswer"/>), released after the sentence, always.</param>
        /// <param name="delay">The wait; a test passes one that returns at once.</param>
        /// <param name="sayQueued">Says the sentence after whatever is being said, never over it.</param>
        public static async Task SayWhenTheMenuHasClosed(string sentence, Func<bool> stillCurrent, IDisposable noiseAnswer,
            Func<int, Task> delay, Action<string> sayQueued)
        {
            try
            {
                await delay(AfterMenuCloseMs);
                if (stillCurrent()) sayQueued(sentence);
                else System.Diagnostics.Trace.WriteLine("MenuModeSentence: dropped '" + sentence + "' (a newer mode choice, radio or slice came first)");
            }
            finally
            {
                noiseAnswer.Dispose();
            }
        }
    }
}
