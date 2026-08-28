using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// The step sizes Modern tuning offers, and the walk between them (#302).
    ///
    /// <para><b>Why this exists.</b> Modern tuning's whole identity is a coarse
    /// step and a fine step, and until 2026-08-27 those two numbers could only
    /// be changed by opening Settings. Classic has "Plus then digits" on the
    /// surface; the Logging radio pane has Left / Right on the surface; the one
    /// mode built around step sizes had nothing. Worse from the chair, the
    /// context help NAMES both values and offers Shift+S to hear them again —
    /// it names a number, lets you re-hear it, and gives you no way to act on
    /// it.</para>
    ///
    /// <para><b>Why the values live HERE and not in the Settings dialog.</b>
    /// They used to live there, as two private arrays. The moment a key could
    /// walk past the ends of those arrays, a second vocabulary appeared: the
    /// key could reach 10 kHz, the Settings combo could not show it, and
    /// pressing OK in Settings would silently reset the operator's step to
    /// 1 kHz — a combo with no matching item falls back to index 0. So the
    /// ladder the keys walk and the list the pickers show are the SAME list,
    /// stated once.</para>
    ///
    /// <para><b>Deliberately NOT the adaptive step (#199).</b> The flywheel
    /// plan scales the step with how fast the knob is turning, because the
    /// radio will not accept an unbounded command rate. That is the MACHINE
    /// choosing a step. Everything in this file is the OPERATOR choosing one.
    /// They will meet, and whichever lands second must decide precedence
    /// deliberately — an operator whose deliberate choice is silently
    /// overridden mid-spin will report it as a bug and be right. Nothing here
    /// scales, rate-limits, or infers.</para>
    ///
    /// <para>Lives in Radios rather than JJFlexWpf so Radios.Tests can walk the
    /// ladder and read the spoken values without constructing a window.</para>
    /// </summary>
    public static class TuningSteps
    {
        /// <summary>
        /// One offered step: the value in Hz, and the lexicon key for its
        /// short written label ("5 kHz"). The written label and the spoken
        /// form are different jobs — a list shows "5 kHz", a screen reader
        /// says "5 kilohertz" — so the label is a lexicon key and the speech
        /// comes from <see cref="FormatForSpeech"/>.
        /// </summary>
        public readonly record struct Choice(int Hz, string LabelKey);

        /// <summary>
        /// The result of one press: where the step landed, and whether the
        /// ladder refused to go further.
        /// </summary>
        /// <remarks>
        /// <see cref="AtLimit"/> is not an error. It is the thing that has to
        /// be SPOKEN, because a key that lands on the same value it started on
        /// is indistinguishable from a key that did nothing at all — and this
        /// ladder deliberately does not wrap. Wrapping would take an operator
        /// from 10 kHz to 500 Hz on one keystroke while they were listening to
        /// the band rather than to the app.
        /// </remarks>
        public readonly record struct Move(int Hz, bool AtLimit);

        /// <summary>
        /// The coarse steps, smallest first. Chosen as the four or five an
        /// operator actually reaches for rather than every value that could be
        /// expressed: 500 Hz for picking through a crowded CW segment, 1 and
        /// 2 kHz for working a band, 5 kHz for phone channel spacing, 10 kHz
        /// for crossing a band quickly.
        /// </summary>
        /// <remarks>
        /// The three values Settings offered before this list existed — 1, 2
        /// and 5 kHz — are all still here, in the same order, so nobody's
        /// saved step becomes unreachable.
        /// </remarks>
        public static IReadOnlyList<Choice> Coarse { get; } = new[]
        {
            new Choice(500, "settings.tuning.coarse_step_500_hz"),
            new Choice(1000, "settings.tuning.coarse_step_1_khz"),
            new Choice(2000, "settings.tuning.coarse_step_2_khz"),
            new Choice(5000, "settings.tuning.coarse_step_5_khz"),
            new Choice(10000, "settings.tuning.coarse_step_10_khz"),
        };

        /// <summary>
        /// The fine steps, smallest first. 1 Hz for zero-beating, 5 and 10 Hz
        /// for CW, 50 and 100 Hz for phone.
        /// </summary>
        /// <remarks>
        /// The three values Settings offered before — 5, 10 and 100 Hz — are
        /// all still here.
        /// </remarks>
        public static IReadOnlyList<Choice> Fine { get; } = new[]
        {
            new Choice(1, "settings.tuning.fine_step_1_hz"),
            new Choice(5, "settings.tuning.fine_step_5_hz"),
            new Choice(10, "settings.tuning.fine_step_10_hz"),
            new Choice(50, "settings.tuning.fine_step_50_hz"),
            new Choice(100, "settings.tuning.fine_step_100_hz"),
        };

        /// <summary>
        /// Walk one rung. <paramref name="direction"/> above zero goes to the
        /// next larger step, below zero to the next smaller one.
        /// </summary>
        /// <remarks>
        /// <para>Off-ladder values snap INTO the ladder rather than being
        /// rejected: a current step of 3 kHz answers 5 kHz going up and 2 kHz
        /// going down. Nothing produces an off-ladder value today, but the
        /// adaptive step (#199) and any future imported profile could, and a
        /// ladder that refuses to move from a value it does not recognise
        /// would strand the operator on it.</para>
        /// <para>A value past either end reports <see cref="Move.AtLimit"/>
        /// and does not move, in either direction of travel — the top of the
        /// ladder is a wall, never a wrap.</para>
        /// </remarks>
        public static Move Step(IReadOnlyList<Choice> ladder, int current, int direction)
        {
            if (ladder == null) throw new ArgumentNullException(nameof(ladder));
            if (ladder.Count == 0 || direction == 0) return new Move(current, true);

            if (direction > 0)
            {
                var larger = ladder.Where(c => c.Hz > current).ToList();
                return larger.Count == 0
                    ? new Move(current, true)
                    : new Move(larger.Min(c => c.Hz), false);
            }

            var smaller = ladder.Where(c => c.Hz < current).ToList();
            return smaller.Count == 0
                ? new Move(current, true)
                : new Move(smaller.Max(c => c.Hz), false);
        }

        /// <summary>
        /// The list a picker should show for a ladder, with the operator's
        /// current value included even when it is not one of ours.
        /// </summary>
        /// <remarks>
        /// A picker that quietly omits the value currently in force is a
        /// picker that lies about the present and changes it the moment the
        /// operator presses OK. Cannot happen with today's values; costs three
        /// lines to make it unable to happen at all.
        /// </remarks>
        public static IReadOnlyList<Choice> ChoicesIncluding(IReadOnlyList<Choice> ladder, int current)
        {
            if (ladder == null) throw new ArgumentNullException(nameof(ladder));
            if (ladder.Any(c => c.Hz == current)) return ladder;

            var widened = ladder.ToList();
            widened.Add(new Choice(current, ""));
            widened.Sort((a, b) => a.Hz.CompareTo(b.Hz));
            return widened;
        }

        /// <summary>
        /// A step size as a screen reader should say it — "5 kilohertz",
        /// "500 hertz".
        /// </summary>
        /// <remarks>
        /// Only an EXACT multiple is spoken in the larger unit. The earlier
        /// version divided by 1000 and truncated, so a 2500 Hz step announced
        /// itself as "2 kilohertz" — a wrong number said confidently, which is
        /// worse than a long one. Nothing produces such a value today; #199's
        /// adaptive step is exactly the thing that would.
        /// </remarks>
        public static string FormatForSpeech(int hz)
        {
            if (hz >= 1000000 && hz % 1000000 == 0)
                return Lexicon.Get("settings.tuning.step_megahertz", ("value", hz / 1000000));
            if (hz >= 1000 && hz % 1000 == 0)
                return Lexicon.Get("settings.tuning.step_kilohertz", ("value", hz / 1000));
            return Lexicon.Get("settings.tuning.step_hertz", ("value", hz));
        }

        /// <summary>
        /// The short written label for a step — what a list shows. Falls back
        /// to the spoken form for a value that has no label of its own, so a
        /// widened picker never shows a blank row.
        /// </summary>
        public static string LabelFor(Choice choice) =>
            string.IsNullOrEmpty(choice.LabelKey)
                ? FormatForSpeech(choice.Hz)
                : Lexicon.Get(choice.LabelKey);
    }
}
