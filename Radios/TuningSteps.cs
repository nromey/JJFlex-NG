#nullable enable
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
        /// The STARTING coarse steps, smallest first: the list every operator
        /// begins with and can put back (#346 J3). Chosen as the four or five an
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
        public static IReadOnlyList<Choice> StartingCoarse { get; } = new[]
        {
            new Choice(500, "settings.tuning.coarse_step_500_hz"),
            new Choice(1000, "settings.tuning.coarse_step_1_khz"),
            new Choice(2000, "settings.tuning.coarse_step_2_khz"),
            new Choice(5000, "settings.tuning.coarse_step_5_khz"),
            new Choice(10000, "settings.tuning.coarse_step_10_khz"),
        };

        /// <summary>
        /// The STARTING fine steps, smallest first (#346 J3). 1 Hz for zero-beating, 5 and 10 Hz
        /// for CW, 50 and 100 Hz for phone.
        /// </summary>
        /// <remarks>
        /// The three values Settings offered before — 5, 10 and 100 Hz — are
        /// all still here.
        /// </remarks>
        public static IReadOnlyList<Choice> StartingFine { get; } = new[]
        {
            new Choice(1, "settings.tuning.fine_step_1_hz"),
            new Choice(5, "settings.tuning.fine_step_5_hz"),
            new Choice(10, "settings.tuning.fine_step_10_hz"),
            new Choice(50, "settings.tuning.fine_step_50_hz"),
            new Choice(100, "settings.tuning.fine_step_100_hz"),
        };

        /// <summary>
        /// The coarse steps in force: the operator's own list when they have
        /// one, else <see cref="StartingCoarse"/>. THE list the ladder keys
        /// walk, the S picker shows and Settings shows (#346 part two).
        /// </summary>
        /// <remarks>
        /// <para><b>The operator's lists FEED this; they never sit beside it.</b>
        /// Ruled by Noel 2026-11-09 20:39 and 2026-11-11 11:45 (night question
        /// J3): two lists, coarse and fine, edited in the list S opens, kept
        /// on the operator's record, with today's five as the starting list.
        /// The application hands this class a way to read the current
        /// operator's lists (<see cref="OperatorCoarse"/>), asked at every
        /// read, so an operator switch needs no reload and there is no second
        /// table to drift.</para>
        /// </remarks>
        public static IReadOnlyList<Choice> Coarse => InForce(OperatorCoarse, StartingCoarse, coarse: true);

        /// <summary>The fine steps in force; see <see cref="Coarse"/>.</summary>
        public static IReadOnlyList<Choice> Fine => InForce(OperatorFine, StartingFine, coarse: false);

        /// <summary>
        /// Reads the current operator's coarse list, in Hz; null or empty means
        /// the starting list. Set once by the application. Null (the default,
        /// and in tests) means the starting list.
        /// </summary>
        public static Func<IReadOnlyList<int>?>? OperatorCoarse { get; set; }

        /// <summary>Reads the current operator's fine list; see <see cref="OperatorCoarse"/>.</summary>
        public static Func<IReadOnlyList<int>?>? OperatorFine { get; set; }

        /// <summary>Fine steps run from 1 Hz to 1 kHz (J3, 2026-11-11).</summary>
        public const int FineMinHz = 1, FineMaxHz = 1000;

        /// <summary>Coarse steps run from 10 Hz to 1 MHz (J3, 2026-11-11). The
        /// two ranges overlap, and the same step may sit in both lists.</summary>
        public const int CoarseMinHz = 10, CoarseMaxHz = 1000000;

        private static IReadOnlyList<Choice> InForce(Func<IReadOnlyList<int>?>? read, IReadOnlyList<Choice> starting, bool coarse)
        {
            if (read == null) return starting;
            IReadOnlyList<int>? hz;
            try { hz = read(); }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("TuningSteps: the operator's step list could not be read, so the starting list is used: "
                    + ex.Message, System.Diagnostics.TraceLevel.Warning);
                return starting;
            }
            return ListOf(hz, coarse);
        }

        /// <summary>
        /// A list of steps as choices: the operator's own Hz values, kept to
        /// the list's range, without duplicates and sorted, each with the
        /// starting list's label when it has one. Null, empty or nothing left
        /// gives the starting list, so a list can never be empty.
        /// </summary>
        public static IReadOnlyList<Choice> ListOf(IReadOnlyList<int>? hz, bool coarse)
        {
            var starting = coarse ? StartingCoarse : StartingFine;
            if (hz == null || hz.Count == 0) return starting;
            var kept = Normalise(hz, coarse);
            if (kept.Count == 0) return starting;
            return kept.Select(v => new Choice(v, starting.FirstOrDefault(c => c.Hz == v).LabelKey ?? "")).ToList();
        }

        /// <summary>The Hz values of a list of choices.</summary>
        public static IReadOnlyList<int> HzOf(IReadOnlyList<Choice> list) => list.Select(c => c.Hz).ToList();

        /// <summary>Whether a list of Hz values is the starting list for its kind.</summary>
        public static bool IsStartingList(IReadOnlyList<int> hz, bool coarse) =>
            Normalise(hz, coarse).SequenceEqual(HzOf(coarse ? StartingCoarse : StartingFine));

        /// <summary>Whether <paramref name="hz"/> is a step the list's range allows.</summary>
        public static bool InRange(int hz, bool coarse) =>
            coarse ? hz >= CoarseMinHz && hz <= CoarseMaxHz : hz >= FineMinHz && hz <= FineMaxHz;

        private static List<int> Normalise(IReadOnlyList<int> hz, bool coarse) =>
            hz.Where(v => InRange(v, coarse)).Distinct().OrderBy(v => v).ToList();

        /// <summary>What an edit to a step list did, or why it did nothing.</summary>
        public enum ListEdit
        {
            /// <summary>The step was added, in its sorted place.</summary>
            Added,
            /// <summary>The step was removed.</summary>
            Removed,
            /// <summary>The text is not a number with Hz or kHz.</summary>
            NotAStep,
            /// <summary>Outside the list's range.</summary>
            OutOfRange,
            /// <summary>Already in THIS list (no check against the other list: J3).</summary>
            AlreadyThere,
            /// <summary>Removing it would leave the list empty, which is refused.</summary>
            LastStepStays,
            /// <summary>Removing a step the list does not hold.</summary>
            NotInList,
        }

        /// <summary>
        /// Add a step to one list (#346 J3): within the list's range, never a
        /// duplicate WITHIN the list, kept sorted. The other list is not
        /// consulted (Noel, 2026-11-11 11:45: the same step may sit in both).
        /// </summary>
        public static (ListEdit Outcome, IReadOnlyList<int> List) Add(IReadOnlyList<int> list, int hz, bool coarse)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            if (!InRange(hz, coarse)) return (ListEdit.OutOfRange, list);
            if (list.Contains(hz)) return (ListEdit.AlreadyThere, list);
            return (ListEdit.Added, list.Append(hz).OrderBy(v => v).ToList());
        }

        /// <summary>
        /// Remove a step from one list (#346 J3). Removing the last step is
        /// refused. Removing the step in force is allowed and does NOT change
        /// it: it stays the step until another is picked, which the pickers
        /// show through <see cref="ChoicesIncluding"/>.
        /// </summary>
        public static (ListEdit Outcome, IReadOnlyList<int> List) Remove(IReadOnlyList<int> list, int hz)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            if (!list.Contains(hz)) return (ListEdit.NotInList, list);
            if (list.Count <= 1) return (ListEdit.LastStepStays, list);
            return (ListEdit.Removed, list.Where(v => v != hz).ToList());
        }

        /// <summary>
        /// Read a typed step (#346 J3): a number with a unit, Hz or kHz, any
        /// case, with or without a space ("250 Hz", "2.5 kHz", "10khz"). A
        /// kilohertz value may have a decimal point when it comes to whole
        /// hertz. A bare number is refused: "500" could be either.
        /// </summary>
        public static bool TryParseStep(string? text, out int hz)
        {
            hz = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(text.Trim(),
                @"^(?<n>\d+(\.\d+)?)\s*(?<u>hz|khz)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
                TimeSpan.FromMilliseconds(100));
            if (!m.Success) return false;
            if (!decimal.TryParse(m.Groups["n"].Value, System.Globalization.NumberStyles.AllowDecimalPoint,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal n)) return false;
            // Refused before the multiply, not after: a pasted number near
            // decimal's own limit overflowed n * 1000 and threw out of the S
            // picker's Add (#346). Anything that large is no step anyway.
            if (n > int.MaxValue) return false;
            decimal value = m.Groups["u"].Value.Equals("khz", StringComparison.OrdinalIgnoreCase) ? n * 1000m : n;
            if (value != decimal.Truncate(value) || value > int.MaxValue) return false;
            hz = (int)value;
            return hz > 0;
        }

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
