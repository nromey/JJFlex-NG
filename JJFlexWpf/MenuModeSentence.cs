using System;
using System.Threading.Tasks;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>
    /// What a Slice menu mode row says, and when (#1117, repair rounds 1 and 2).
    /// The row asks the radio for the mode at the click; this says the row's
    /// sentence once the menu has closed, with the noise controller's answer to
    /// the mode change JOINED to it, as one utterance under
    /// <see cref="SpeechSubject.ModeChoice"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why one utterance, and why it interrupts.</b> Round 1 said the row's
    /// sentence queued, to avoid NVDA's cancel race, and released the noise
    /// answer queued behind it. The 8600 harness (round 2) then measured the
    /// noise sentence handed over 5.3 to 5.8 s after the mode word, against
    /// 0.6 to 0.8 s on the key routes. NVDA's own log shows why: a queued
    /// sentence waits behind everything the reader has queued itself, and as the
    /// menu closes it queues the window title ("JJ Flexible Radio Access, Slice
    /// A, 14.100, CW, window"), the focused field and the window name, about
    /// five seconds of speech. Our synchronous hand-over of "Mode CW" returns
    /// only once NVDA has said all of that and then "Mode CW", so the noise
    /// sentence followed it at once as heard, but the mode word itself came
    /// about five seconds after the click. Interrupting, as the menu always did
    /// before #1117, cuts that re-reading (the title already names the new
    /// mode) and says the mode at about half a second, as on the key routes.
    /// The interrupt's own cancel can land on it (the race in
    /// <c>project_nvda_cancel_lands_on_our_next_utterance</c>); NVDA still says
    /// a sentence the race "cuts" in full, and with the noise answer joined to
    /// it nothing of ours waits queued behind it to be withdrawn, which is what
    /// lost the noise sentence in round 1's check 7.
    /// </para>
    /// <para>
    /// <b>Why it waits.</b> Speech handed over while the menu is closing is
    /// flushed by the focus change (<c>project_speech_flushes_on_window_change</c>);
    /// Next and Previous Mode, which spoke at the click, were cut that way.
    /// </para>
    /// <para>
    /// <b>Why it may say nothing, and its lifetime.</b> If a newer mode choice,
    /// another radio or another slice came before the menu closed, it would name
    /// a mode that is no longer the answer, so it is dropped. Once said it still
    /// has owners: a newer mode choice by any road retires its subject
    /// (<see cref="ModeChoiceLedger"/>), and the noise controller retires it when
    /// what its noise part states changes, so the arbiter never rescues an older
    /// "Mode CW" after a newer "USB" (Codex, round 2).
    /// </para>
    /// </remarks>
    public static class MenuModeSentence
    {
        /// <summary>How long after the click the menu has closed and the reader's own close announcements are under way. The value the menu has always used.</summary>
        public const int AfterMenuCloseMs = 500;

        /// <param name="sentence">The row's existing words.</param>
        /// <param name="stillCurrent">False when a newer mode choice, another radio or another slice came first.</param>
        /// <param name="noiseAnswer">The noise controller's hold (<see cref="NoiseController.HoldModeAnswer"/>): taken and joined when the sentence is said, released (queued) otherwise.</param>
        /// <param name="delay">The wait; a test passes one that returns at once.</param>
        /// <param name="sayNow">Says the utterance now, interrupting, under <see cref="SpeechSubject.ModeChoice"/>, at the level given.</param>
        public static async Task SayWhenTheMenuHasClosed(string sentence, Func<bool> stillCurrent, IModeAnswerHold noiseAnswer,
            Func<int, Task> delay, Action<string, VerbosityLevel> sayNow)
        {
            try
            {
                await delay(AfterMenuCloseMs);
                if (!stillCurrent())
                {
                    System.Diagnostics.Trace.WriteLine("MenuModeSentence: dropped '" + sentence + "' (a newer mode choice, radio or slice came first)");
                    return;
                }
                // Any later operator action that has been answered (a key pressed
                // after the click, a noise action) outlives it: interrupting now
                // would cut that newer answer (repair round 3, the harness's R3-5:
                // a band announcement cut at its first word). Nothing is delayed
                // further; the reply is simply not said.
                if (noiseAnswer.OutlivedByANewerAnswer)
                {
                    System.Diagnostics.Trace.WriteLine("MenuModeSentence: dropped '" + sentence + "' (a later action was answered first)");
                    return;
                }
                var level = VerbosityLevel.Terse;
                string text = sentence;
                if (noiseAnswer.TakeJoined(SpeechSubject.ModeChoice) is { } joined)
                {
                    text = sentence.TrimEnd() + " " + joined.Text.Trim();
                    if ((int)joined.Level < (int)level) level = joined.Level;
                }
                sayNow(text, level);
            }
            finally
            {
                // Already taken: does nothing. Dropped: the answer goes out queued.
                noiseAnswer.Dispose();
            }
        }
    }

    /// <summary>
    /// The operator's mode choices this session, counted on every road that asks
    /// the radio for a mode (#1117, repair rounds 1 and 2). Each new choice
    /// retires the Slice menu's mode sentence still owed from an older one
    /// (<see cref="SpeechSubject.ModeChoice"/>), wherever it is: waiting for the
    /// menu to close (<see cref="Count"/> has moved on) or already handed to the
    /// reader and liable to be rescued.
    /// </summary>
    public sealed class ModeChoiceLedger
    {
        private readonly Action<string, string> _supersede;
        private int _count;

        /// <param name="supersede">Declares a subject covered (<c>ScreenReaderOutput.Supersede</c>); a test passes its own.</param>
        public ModeChoiceLedger(Action<string, string>? supersede = null) =>
            _supersede = supersede ?? ((subject, why) => ScreenReaderOutput.Supersede(subject, why));

        public int Count => _count;

        /// <summary>Count a mode choice and retire the older menu sentence. Returns the new count.</summary>
        public int Note()
        {
            _supersede(SpeechSubject.ModeChoice, "a newer mode choice");
            return ++_count;
        }
    }
}
