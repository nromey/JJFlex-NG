#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// The mode cycle: which modes Alt+M and Alt+Shift+M (and the Home Mode
    /// field, and the menu's Next and Previous Mode) step through, in the
    /// operator's order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ruled by Noel 2026-08-26: <i>"we decided that we were going to have a
    /// setting to allow users to enable the modes they want to appear in the
    /// ring"</i>, and the same day: <i>"If a mode's enabled it should not affect
    /// the slice, it just means that when using the mode ring, it'd go to the
    /// next enabled one."</i> On DSB and SAM, 2026-10-19: <i>"defaulted to not
    /// enabled"</i>.
    /// </para>
    /// <para>
    /// <b>A filter on the cycle, never a capability gate.</b> Nothing here
    /// writes to the radio or changes a slice. A slice sitting in a mode that
    /// is not in the cycle is an ordinary state: the next press moves to the
    /// nearest mode that is. The other routes to a mode are the Mode menu,
    /// built from <see cref="KnownModes"/> too, and seven direct keys,
    /// which refuse a mode it does not hold; DSB has no key of its own.
    /// </para>
    /// <para>
    /// <b>One definition of the mode list</b> (<see cref="KnownModes"/>): the
    /// modes the radio itself reports for the slice, or, with no radio,
    /// FlexLib's documented set. The cycle used to walk a third hardcoded copy
    /// that left out SAM and DSB, and a slice already in SAM jumped to an end
    /// of it. The Mode menu and the direct mode keys read this list as well
    /// (2026-11-07); the classic Mode combo and the Memories window still
    /// read <see cref="RigCaps.ModeTable"/>.
    /// </para>
    /// </remarks>
    public static class ModeCycle
    {
        /// <summary>
        /// The cycle as shipped: the seven modes the keys visited before this
        /// was a setting, in the same order. DSB and SAM are offered and are
        /// not in it.
        /// </summary>
        public static IReadOnlyList<string> DefaultCycle { get; } =
            new[] { "USB", "LSB", "CW", "DIGU", "DIGL", "AM", "FM" };

        /// <summary>
        /// FlexLib's documented demodulation modes, from the comment on
        /// <c>Slice.DemodMode</c>. FlexLib exposes no constant for them, so
        /// this is the stand-in until a radio reports its own list.
        /// </summary>
        public static IReadOnlyList<string> FlexLibModes { get; } =
            new[] { "USB", "DIGU", "LSB", "DIGL", "CW", "DSB", "AM", "SAM", "FM" };

        /// <summary>
        /// Every mode there is to choose from: the radio's own list for the
        /// slice when it has reported one, otherwise <see cref="FlexLibModes"/>.
        /// Upper case, each once.
        /// </summary>
        public static IReadOnlyList<string> KnownModes(IEnumerable<string>? radioModes)
        {
            var reported = Clean(radioModes);
            return reported.Count != 0 ? reported : FlexLibModes;
        }

        /// <summary>
        /// The cycle as it can run on the connected radio: the saved cycle,
        /// in its order, without the modes the radio did not list, each named
        /// the way the radio names it. With no list reported (no radio, or
        /// not yet) it is the saved cycle unchanged.
        /// </summary>
        /// <remarks>
        /// The saved cycle is never changed by this: a mode left out here is
        /// still in the operator's file for a radio that has it. Found on the
        /// 8600 at the bench, 2026-11-07: it reports no DSB, so a DSB in the
        /// cycle was written, the radio answered DIGU, and the app said "DSB".
        /// A mode the radio did not list is never written now.
        /// </remarks>
        public static IReadOnlyList<string> OnRadio(IReadOnlyList<string> saved, IEnumerable<string>? radioModes)
        {
            var reported = Clean(radioModes);
            if (reported.Count == 0) return saved;
            var result = new List<string>();
            foreach (string m in saved)
            {
                int at = IndexOf(reported, m);
                if (at >= 0 && !result.Contains(reported[at])) result.Add(reported[at]);
            }
            return result;
        }

        /// <summary>
        /// Which sentence a press answers with when the cycle on this radio
        /// holds only the slice's own mode: "the only mode in the cycle", or,
        /// when the radio's list has left out some of the saved cycle, "the
        /// only one this radio has". Saying the first about a saved USB and
        /// DSB on a radio without DSB was untrue (Codex B2 on 4ffb2106).
        /// </summary>
        public static string OnlyOneSentenceKey(IReadOnlyList<string> saved, IReadOnlyList<string> onRadio) =>
            onRadio.Count < saved.Count
                ? "settings.mode_cycle.only_one_on_radio_press"
                : "settings.mode_cycle.only_one_press";

        /// <summary>
        /// The operator's saved cycle made safe to use: null (never set) is
        /// <see cref="DefaultCycle"/>; blanks and repeats are dropped and case
        /// is folded. An empty saved cycle stays empty: it is a choice.
        /// </summary>
        public static IReadOnlyList<string> FromSaved(IEnumerable<string>? saved) =>
            saved == null ? DefaultCycle : Clean(saved);

        /// <summary>
        /// The mode one press moves to, or null when the press has nowhere to
        /// go (the cycle is empty).
        /// </summary>
        /// <param name="cycle">The modes in the cycle, in the operator's order.</param>
        /// <param name="allModes">The full mode list (<see cref="KnownModes"/>),
        /// used only to find the nearest included mode when the slice is in a
        /// mode the cycle leaves out.</param>
        /// <param name="current">The slice's mode now.</param>
        /// <param name="direction">+1 for the next mode, -1 for the previous.</param>
        /// <remarks>
        /// <para>From a mode in the cycle: the next one in the operator's
        /// order, wrapping. With one mode in the cycle that is the same mode,
        /// which the caller reports rather than writing.</para>
        /// <para>From a mode outside the cycle: the nearest included mode
        /// after it in the full mode list (before it, for the previous),
        /// wrapping. From a mode in neither list: the first mode of the cycle
        /// (the last, for the previous).</para>
        /// </remarks>
        public static string? Next(IReadOnlyList<string> cycle, IReadOnlyList<string> allModes,
                                   string? current, int direction)
        {
            if (cycle == null || cycle.Count == 0) return null;
            int step = direction >= 0 ? 1 : -1;
            string now = (current ?? "").Trim();

            int at = IndexOf(cycle, now);
            if (at >= 0)
                return cycle[(at + step + cycle.Count) % cycle.Count];

            int inAll = IndexOf(allModes ?? Array.Empty<string>(), now);
            if (inAll >= 0)
            {
                for (int i = 1; i <= allModes!.Count; i++)
                {
                    string candidate = allModes[((inAll + step * i) % allModes.Count + allModes.Count) % allModes.Count];
                    if (IndexOf(cycle, candidate) >= 0) return cycle[IndexOf(cycle, candidate)];
                }
                // The cycle holds only modes the full list does not know:
                // fall through to its ends.
            }

            return step > 0 ? cycle[0] : cycle[cycle.Count - 1];
        }

        /// <summary>True when <paramref name="mode"/> is in <paramref name="modes"/>, case folded.</summary>
        public static bool Contains(IReadOnlyList<string> modes, string? mode) =>
            IndexOf(modes, mode ?? "") >= 0;

        private static int IndexOf(IReadOnlyList<string> modes, string mode)
        {
            for (int i = 0; i < modes.Count; i++)
                if (string.Equals(modes[i], mode, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static List<string> Clean(IEnumerable<string>? modes)
        {
            var result = new List<string>();
            if (modes == null) return result;
            foreach (string raw in modes)
            {
                string m = (raw ?? "").Trim().ToUpperInvariant();
                if (m.Length == 0 || result.Contains(m)) continue;
                result.Add(m);
            }
            return result;
        }
    }
}
