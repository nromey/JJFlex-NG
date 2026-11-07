using System;

namespace Radios
{
    /// <summary>
    /// Whether a filter write's sentence is still true when a delayed speaker
    /// gets to it (#747, round nine of the 2026-11-06 junk box). The Filter
    /// and Radio menus wait 500 ms after the menu closes before speaking, and
    /// in that time the filter can change again.
    /// </summary>
    /// <remarks>
    /// The check is against the rig, Radio and command connection the write
    /// was made on, captured now; never against whichever rig the window holds
    /// later (Codex's fifth re-verify, F3). A written result stays true while
    /// that connection holds those edges. A refusal ("not changed") stays true
    /// until a later write to the same filter runs on that rig.
    /// </remarks>
    public static class FilterWriteFreshness
    {
        public static Func<bool> StillTrue(FlexBase rig, FilterApplied applied, bool transmit)
        {
            var radio = rig.theRadio;
            object? connection = radio?.CurrentCommandConnection;
            long writesThen = transmit ? rig.TxFilterWritesRun : rig.RxFilterWritesRun;
            bool SameConnection() => ReferenceEquals(rig.theRadio, radio)
                && ReferenceEquals(radio?.CurrentCommandConnection, connection);

            if (applied.NotWritten)
                return () => SameConnection() && (transmit ? rig.TxFilterWritesRun : rig.RxFilterWritesRun) == writesThen;
            return transmit
                ? () => SameConnection() && rig.TXFilterLow == applied.Low && rig.TXFilterHigh == applied.High
                : () => SameConnection() && rig.FilterLow == applied.Low && rig.FilterHigh == applied.High;
        }
    }
}
