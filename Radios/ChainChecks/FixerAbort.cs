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

            public Plan(Step[] steps, string announcement)
            { Steps = steps ?? Array.Empty<Step>(); Announcement = announcement ?? ""; }

            public bool UnkeysFirst => Steps.Length > 0 && Steps[0] == Step.UnkeyImmediately;
            public bool Asks => Array.IndexOf(Steps, Step.AskAbandonOrContinue) >= 0;
        }

        /// <summary>
        /// Decide what to do about a stop request.
        /// </summary>
        /// <param name="keyed">Is the transmitter keyed RIGHT NOW?</param>
        /// <param name="source">Where the request came from.</param>
        /// <param name="runInProgress">Is there a run to abandon at all?</param>
        public static Plan Decide(bool keyed, Source source, bool runInProgress)
        {
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
                    // there is nowhere to put the answer.
                    return new Plan(new[] { Step.UnkeyImmediately, Step.AbandonNow },
                        "Stopped transmitting, and the test was abandoned.");
                }

                return new Plan(new[] { Step.UnkeyImmediately, Step.AskAbandonOrContinue },
                    "Stopped transmitting. Do you want to abandon the test, or carry on?");
            }

            // Not keyed. Nothing urgent, and abandoning a run represents real
            // work, so a stray keypress must not throw it away.
            if (!runInProgress)
                return new Plan(new[] { Step.AbandonNow }, "");

            if (source == Source.WindowClosing)
                return new Plan(new[] { Step.AskAbandonOrContinue },
                    "The test has not finished. Abandon it?");

            return new Plan(new[] { Step.AskAbandonOrContinue },
                "Do you want to stop the test?");
        }

        /// <summary>
        /// True when the request must be acted on before anything else can be
        /// considered — used by the host to decide whether it may take its
        /// time. Any keyed stop is urgent regardless of where it came from.
        /// </summary>
        public static bool IsUrgent(bool keyed) => keyed;
    }
}
