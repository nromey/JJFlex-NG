#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace Radios.Tests.Fixtures
{
    /// <summary>
    /// The KEYTIME lines of the JAWS measurement Noel took on 2026-10-22 at
    /// 05:08:52, verbatim (#800, #571). Preserved copy of the trace:
    /// <c>JJFlex-private/planning/agile/junkbox-briefs/evidence/2026-10-22-jaws-shift-space-capture.txt</c>,
    /// ticks 840174 to 860687; the original rotates out of
    /// <c>%AppData%\JJFlexRadio</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was measured, and what was not.</b> One hold of Shift+Space on
    /// Home, about 1.5 s; four quick taps on Home; one plain Space (no
    /// modifier) on Home; one Shift+Space in a dialog, refused. JAWS, Prism
    /// backend, build 4.1.16.2435, a FLEX-8600 connected. NOT measured here:
    /// anything under NVDA, a hold in a dialog, or lock-then-unlock within
    /// half a second (the two closest taps were 880 ms apart). The brief's
    /// original protocol asked for three of each on Home and Settings under
    /// both readers; this is one of each, JAWS only, Settings only as a
    /// refusal. Say so wherever this fixture is cited.
    /// </para>
    /// <para>
    /// <b>What it showed.</b> The hold arrived as ONE non-repeat key-down,
    /// then <c>repeat=True</c> key-downs every ~31 ms starting 500 ms after
    /// it, then ONE key-up — the same shape as NVDA, no synthesised down/up
    /// pairs. On that build every repeat toggled the lock (the trace has
    /// <c>PTT: Locked</c> / <c>PTT: Idle</c> beside each one). Taps were 62 to
    /// 109 ms from down to up.
    /// </para>
    /// <para>
    /// Ticks are <c>Environment.TickCount64</c> as the trace recorded them.
    /// Trace line numbers of the preserved copy are in the comments.
    /// </para>
    /// </remarks>
    public static class JawsShiftSpaceCapture20261022
    {
        /// <summary>One Space key event as the KEYTIME line recorded it.</summary>
        public readonly record struct KeyEvent(long Tick, bool Down, string Mods, bool Repeat, string Door)
        {
            public bool IsShiftSpace => Mods == "Shift";
        }

        /// <summary>Shift+Space in the Settings dialog, refused. Lines 81 and 87.</summary>
        public static readonly KeyEvent[] DialogTap =
        {
            new(81818671, true,  "Shift", false, "dialog"),
            new(81818750, false, "Shift", false, "dialog"),
        };

        /// <summary>Four quick Shift+Space taps on Home; each toggled the lock once. Lines 184-604.</summary>
        public static readonly KeyEvent[][] HomeTaps =
        {
            new KeyEvent[] { new(81820750, true, "Shift", false, "home"), new(81820812, false, "Shift", false, "home") },   // 62 ms, Locked
            new KeyEvent[] { new(81822437, true, "Shift", false, "home"), new(81822531, false, "Shift", false, "home") },   // 94 ms, Idle
            new KeyEvent[] { new(81824953, true, "Shift", false, "home"), new(81825062, false, "Shift", false, "home") },   // 109 ms, Locked
            new KeyEvent[] { new(81825828, true, "Shift", false, "home"), new(81825906, false, "Shift", false, "home") },   // 78 ms, Idle
        };

        /// <summary>A plain Space on Home between the taps (no modifier). Lines 280 and 287.</summary>
        public static readonly KeyEvent[] PlainSpace =
        {
            new(81821515, true,  "None", false, "home"),
            new(81821593, false, "None", false, "home"),
        };

        /// <summary>
        /// The hold: one key-down, thirty repeat key-downs, one key-up, 1,484 ms
        /// from down to up. Lines 691 to 1906. On build 4.1.16.2435 every one of
        /// the 31 downs toggled the lock.
        /// </summary>
        public static readonly KeyEvent[] HomeHold =
        {
            new(81827687, true, "Shift", false, "home"),
            new(81828187, true, "Shift", true, "home"),
            new(81828218, true, "Shift", true, "home"),
            new(81828250, true, "Shift", true, "home"),
            new(81828281, true, "Shift", true, "home"),
            new(81828312, true, "Shift", true, "home"),
            new(81828343, true, "Shift", true, "home"),
            new(81828390, true, "Shift", true, "home"),
            new(81828421, true, "Shift", true, "home"),
            new(81828453, true, "Shift", true, "home"),
            new(81828484, true, "Shift", true, "home"),
            new(81828515, true, "Shift", true, "home"),
            new(81828546, true, "Shift", true, "home"),
            new(81828578, true, "Shift", true, "home"),
            new(81828609, true, "Shift", true, "home"),
            new(81828640, true, "Shift", true, "home"),
            new(81828671, true, "Shift", true, "home"),
            new(81828703, true, "Shift", true, "home"),
            new(81828750, true, "Shift", true, "home"),
            new(81828781, true, "Shift", true, "home"),
            new(81828812, true, "Shift", true, "home"),
            new(81828843, true, "Shift", true, "home"),
            new(81828875, true, "Shift", true, "home"),
            new(81828906, true, "Shift", true, "home"),
            new(81828937, true, "Shift", true, "home"),
            new(81828968, true, "Shift", true, "home"),
            new(81829000, true, "Shift", true, "home"),
            new(81829031, true, "Shift", true, "home"),
            new(81829078, true, "Shift", true, "home"),
            new(81829109, true, "Shift", true, "home"),
            new(81829140, true, "Shift", true, "home"),
            new(81829171, false, "Shift", false, "home"),
        };

        /// <summary>How many key-downs the hold delivered, repeats included: 31.</summary>
        public static int HoldDowns => HomeHold.Count(e => e.Down);

        /// <summary>The whole Home stream in trace order: taps, the plain Space, the hold.</summary>
        public static IEnumerable<KeyEvent> HomeStreamInOrder()
        {
            yield return HomeTaps[0][0]; yield return HomeTaps[0][1];
            yield return PlainSpace[0]; yield return PlainSpace[1];
            yield return HomeTaps[1][0]; yield return HomeTaps[1][1];
            yield return HomeTaps[2][0]; yield return HomeTaps[2][1];
            yield return HomeTaps[3][0]; yield return HomeTaps[3][1];
            foreach (var e in HomeHold) yield return e;
        }

        /// <summary>
        /// NOT measured: a synthetic down/up pair as JAWS was seen to deliver
        /// on the Up arrow on 2026-08-24 (the up a few ms after its down). Here
        /// only so the detector's positive control has something to count.
        /// </summary>
        public static readonly KeyEvent[] SyntheticPairNotMeasuredOnThisKey =
        {
            new(90000000, true,  "Shift", false, "home"),
            new(90000003, false, "Shift", false, "home"),
        };
    }
}
