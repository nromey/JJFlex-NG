#nullable enable
namespace Radios
{
    /// <summary>
    /// The rules JJ Flexible adds on top of the radio's own, judged together:
    /// the TX band lockout (#1310) and the 60 m rules (#1161). Pure, so
    /// <c>Radios.Tests</c> tests the very code <c>FreqOutHandlers.JudgeTransmitHere</c>
    /// runs; that method only gathers the inputs.
    /// </summary>
    public static class TransmitRules
    {
        /// <summary>
        /// The verdict on transmitting the judged signal under
        /// <paramref name="license"/>'s settings.
        /// </summary>
        /// <param name="transmit">The transmit slice's reading, or null when none of ours is designated.</param>
        /// <param name="selectedHz">The selected slice's frequency, judged when there is no transmit slice.</param>
        /// <param name="selectedLow">The selected slice's filter low edge.</param>
        /// <param name="selectedHigh">The selected slice's filter high edge.</param>
        /// <param name="sixtyMeterRule">Whether the 60 m rule is asked at all (false for the antenna
        /// tuner, which raises a tune carrier: the orchestrator's decision for junk box batch 31).</param>
        /// <remarks>
        /// <para><b>Two switches, independent.</b> The lockout
        /// (<see cref="LicenseConfig.TxLockout"/>, off by default) and the 60 m rules
        /// (<see cref="LicenseConfig.EnforceTxRules"/>, on by default): either can
        /// refuse with the other off, and both are reported when both refuse.</para>
        /// <para><b>The 60 m rules judge only a signal on 60 m</b>
        /// (<see cref="SixtyMeterChannels.JudgeTransmission"/>), so with the lockout
        /// off, which is every operator's default, nothing off 60 m is ever
        /// refused.</para>
        /// <para>With no transmit slice of ours, the selected slice is judged with
        /// no mode, on frequency alone, by both rules.</para>
        /// </remarks>
        public static TransmitVerdict Judge(LicenseConfig license, TransmitSliceReading? transmit,
            ulong selectedHz, int selectedLow, int selectedHigh, bool sixtyMeterRule,
            out bool judgedSelected, out bool modeUnrecognised, out bool rttyLetThrough)
        {
            judgedSelected = !(transmit is { } t0 && t0.Hz != 0UL);
            modeUnrecognised = false;
            rttyLetThrough = false;

            var lockout = LockoutReason.None;
            if (license.TxLockout)
                lockout = TransmitBandLockout.JudgeReason(transmit, selectedHz, selectedLow, selectedHigh,
                    LicenseConfig.JudgedClass(license.LicenseClass), out judgedSelected, out modeUnrecognised);

            var sixty = SixtyMeterReason.None;
            if (sixtyMeterRule && license.EnforceTxRules)
            {
                string country = license.Country ?? "US";
                if (transmit is { } t && t.Hz != 0UL)
                {
                    sixty = SixtyMeterChannels.JudgeTransmission(country, t.Hz, t.Mode, t.TxFilterLow, t.TxFilterHigh, out rttyLetThrough);
                    if (sixty == SixtyMeterReason.None && t.AlsoJudgeHz is { } also)
                        sixty = SixtyMeterChannels.JudgeTransmission(country, also, t.Mode, t.TxFilterLow, t.TxFilterHigh, out _);
                }
                else
                {
                    sixty = SixtyMeterChannels.JudgeTransmission(country, selectedHz, null, 0, 0, out _);
                }
            }
            return new TransmitVerdict(lockout, sixty);
        }
    }
}
