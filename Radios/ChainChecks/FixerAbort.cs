using System;

namespace Radios.ChainChecks
{
    /// <summary>
    /// What happens when the operator asks to stop, and in what order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure, and separate from the window for the usual reason: this is the
    /// decision that stands between an operator and their own transmitter, and
    /// it must be testable without a WebView, a screen reader or a radio.
    /// </para>
    /// <para>
    /// <b>The rule inverts depending on whether RF is going out</b>, and the
    /// inversion is deliberate. Normally a destructive action confirms first.
    /// Here, while keyed, THE DANGEROUS THING IS THE DELAY, NOT THE ACTION —
    /// putting a prompt between the operator and stopping their transmission
    /// means the carrier stays up while something waits for an answer.
    /// </para>
    /// </remarks>
    public static class FixerAbort
    {
        /// <summary>Where the request came from. All of them are equal in
        /// authority; the field exists so the trace can say which route
        /// worked, which is the only way to find out whether Escape actually
        /// reaches us through browse mode.</summary>
        public enum Source
        {
            /// <summary>Escape, wherever it was caught.</summary>
            EscapeKey,
            /// <summary>The always-visible Stop control on the page.</summary>
            StopButton,
            /// <summary>A host-level chord, outside the web content.</summary>
            HostChord,
            /// <summary>The window is closing.</summary>
            WindowClosing,
        }

        /// <summary>One step the caller must perform, in order.</summary>
        public enum Step
        {
            /// <summary>Drop the carrier now. No prompt, no delay.</summary>
            UnkeyImmediately,
            /// <summary>Ask whether to abandon the run or carry on.</summary>
            AskAbandonOrContinue,
            /// <summary>Abandon without asking.</summary>
            AbandonNow,
        }

        /// <summary>The ordered steps, and what to say.</summary>
        public readonly struct Plan
        {
            public readonly Step[] Steps;

            /// <summary>Spoken and shown. Empty when nothing needs saying.</summary>
            public readonly string Announcement;

            /// <summary>
            /// True when the ask should offer a third choice — stop now and
            /// resume the run later from the saved test runs list (#376).
            /// Only ever true when the caller asserted the run is genuinely
            /// persisting AND it holds at least one result: an option that
            /// promises "pick it up later" over a journal that never opened,
            /// or over a run with nothing in it worth returning to, would be
            /// a lie with a button on it.
            /// </summary>
            public readonly bool OffersResumeLater;

            public Plan(Step[] steps, string announcement, bool offersResumeLater = false)
            {
                Steps = steps ?? Array.Empty<Step>();
                Announcement = announcement ?? "";
                OffersResumeLater = offersResumeLater;
            }

            public bool UnkeysFirst => Steps.Length > 0 && Steps[0] == Step.UnkeyImmediately;
            public bool Asks => Array.IndexOf(Steps, Step.AskAbandonOrContinue) >= 0;
        }

        /// <summary>
        /// Decide what to do about a stop request.
        /// </summary>
        /// <param name="keyed">Is the transmitter keyed RIGHT NOW?</param>
        /// <param name="source">Where the request came from.</param>
        /// <param name="runInProgress">Is a stage executing RIGHT NOW? This is
        /// about the moment, not the run as a whole — a run with recorded
        /// results and no stage running answers false here and says what it
        /// holds through <paramref name="resultsCollected"/>.</param>
        /// <param name="resultsCollected">
        /// How many stage results the run holds. Zero closes without ceremony;
        /// anything more means the question must name the count (#250) — until
        /// now a close between stages ended the run silently, and on the
        /// transmitting stages a measurement is paid for with RF.
        /// </param>
        /// <param name="resultsAreKept">
        /// Is something actually persisting this run's results as it goes?
        /// The FATE CLAIM in the question rides this fact: false says the
        /// results are discarded, true says they are saved under the test ID.
        /// A parameter rather than an assumption, because the evidence layer
        /// can fail to set up on a given machine — and a question that
        /// promises "saved" over a journal that never opened is silent data
        /// loss with a reassuring voice. The caller passes the evidence
        /// layer's own signal, never a constant true.
        /// </param>
        public static Plan Decide(bool keyed, Source source, bool runInProgress,
                                  int resultsCollected = 0, bool resultsAreKept = false)
        {
            // The resume-later offer (#376): only over a run that is really
            // being persisted AND really holds something worth returning to.
            bool resumeLater = resultsAreKept && resultsCollected > 0;

            // The kept-case question. It states the situation — saved, and
            // where a stopped test can be found again — and asks an open
            // question, because the ask is presented as three labelled
            // choices rather than yes-or-no. Naming the door matters: an
            // option the operator cannot find afterwards is not an option.
            string KeptQuestion(string lead)
                => lead + " has recorded " + ResultsPhrase(resultsCollected)
                 + ", and " + (resultsCollected == 1 ? "it is" : "they are")
                 + " saved under its test ID. A stopped test can be picked up "
                 + "later from View or resume saved test runs, on the Fix menu. What "
                 + "would "
                 + "you like to do?";

            // Keyed: stop the RF first, ALWAYS, whatever asked and whatever
            // else is true. Only then is there time to ask anything.
            if (keyed)
            {
                if (!runInProgress)
                {
                    return new Plan(new[] { Step.UnkeyImmediately },
                        "Stopped transmitting.");
                }

                if (source == Source.WindowClosing)
                {
                    // The window is going away; asking is pointless because
                    // there is nowhere to put the answer. With a live journal
                    // the record survives anyway and the saved-runs list will
                    // offer it, so nothing is lost by not asking.
                    return new Plan(new[] { Step.UnkeyImmediately, Step.AbandonNow },
                        "Stopped transmitting, and the test was abandoned.");
                }

                return resumeLater
                    ? new Plan(new[] { Step.UnkeyImmediately, Step.AskAbandonOrContinue },
                        "Stopped transmitting. This test" + KeptQuestion(""),
                        offersResumeLater: true)
                    : new Plan(new[] { Step.UnkeyImmediately, Step.AskAbandonOrContinue },
                        "Stopped transmitting. Do you want to abandon the test, or carry on?");
            }

            // Not keyed. Nothing urgent, and abandoning a run represents real
            // work, so a stray keypress must not throw it away — and a run
            // holding results must never end silently, because the results
            // die with it and the operator was not told (#250).
            if (!runInProgress && resultsCollected <= 0)
                return new Plan(new[] { Step.AbandonNow }, "");

            if (!runInProgress)
            {
                // Results and no stage running: the quiet moment between
                // stages, which is exactly when closing used to end the run
                // without a word.
                if (resumeLater)
                    return new Plan(new[] { Step.AskAbandonOrContinue },
                        KeptQuestion("This test"), offersResumeLater: true);

                string fate = " Ending it now discards "
                            + (resultsCollected == 1 ? "it" : "them") + ".";

                return source == Source.WindowClosing
                    ? new Plan(new[] { Step.AskAbandonOrContinue },
                        "This test has recorded " + ResultsPhrase(resultsCollected)
                        + "." + fate + " Abandon the test?")
                    : new Plan(new[] { Step.AskAbandonOrContinue },
                        "This test has recorded " + ResultsPhrase(resultsCollected)
                        + "." + fate + " Do you want to stop the test?");
            }

            if (resumeLater)
                return new Plan(new[] { Step.AskAbandonOrContinue },
                    "The test has not finished. It" + KeptQuestion(""),
                    offersResumeLater: true);

            if (source == Source.WindowClosing)
                return new Plan(new[] { Step.AskAbandonOrContinue },
                    resultsCollected > 0
                        ? "The test has not finished, and it has recorded "
                          + ResultsPhrase(resultsCollected) + "."
                          + " Ending it now discards "
                          + (resultsCollected == 1 ? "it" : "them") + "."
                          + " Abandon it?"
                        : "The test has not finished. Abandon it?");

            return new Plan(new[] { Step.AskAbandonOrContinue },
                resultsCollected > 0
                    ? "This test has recorded " + ResultsPhrase(resultsCollected) + "."
                      + " Ending it now discards "
                      + (resultsCollected == 1 ? "it" : "them") + "."
                      + " Do you want to stop the test?"
                    : "Do you want to stop the test?");
        }

        /// <summary>"one result", "three results" — words for the counts a
        /// person says as words, numerals past twelve.</summary>
        private static string ResultsPhrase(int n)
        {
            string[] small = { "zero", "one", "two", "three", "four", "five", "six",
                               "seven", "eight", "nine", "ten", "eleven", "twelve" };
            string count = n >= 0 && n < small.Length
                ? small[n]
                : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return count + (n == 1 ? " result" : " results");
        }

        /// <summary>
        /// Turn the page's <c>source</c> string into a <see cref="Source"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Anything unrecognised becomes <see cref="Source.StopButton"/>,
        /// never a refusal and never a no-op.</b> The source is recorded for
        /// the trace and gates nothing, so the only thing an unknown value
        /// could reasonably do is stop the test — and the alternative, ignoring
        /// a stop request because its label was not on a list, is a stop that
        /// fails at precisely the moment somebody wanted out.
        /// </para>
        /// <para>
        /// <c>StopButton</c> rather than <c>EscapeKey</c> as the fallback
        /// because the button is the surface's primary way out: Escape can be
        /// swallowed by a screen reader in browse mode, so attributing an
        /// unknown stop to Escape would make the trace suggest Escape works
        /// when the evidence for that is exactly what we are trying to gather.
        /// </para>
        /// </remarks>
        public static Source SourceFrom(string source)
        {
            string s = (source ?? "").Trim();

            if (s.Equals("escape", StringComparison.OrdinalIgnoreCase)) return Source.EscapeKey;
            if (s.Equals("button", StringComparison.OrdinalIgnoreCase)) return Source.StopButton;
            if (s.Equals("host", StringComparison.OrdinalIgnoreCase)) return Source.HostChord;
            if (s.Equals("closing", StringComparison.OrdinalIgnoreCase)) return Source.WindowClosing;

            return Source.StopButton;
        }

        /// <summary>
        /// True when the request must be acted on before anything else can be
        /// considered — used by the host to decide whether it may take its
        /// time. Any keyed stop is urgent regardless of where it came from.
        /// </summary>
        public static bool IsUrgent(bool keyed) => keyed;
    }
}
