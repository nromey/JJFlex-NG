using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    // PROBE BRANCH ONLY (probe/746-replies). Never merges. #746: does the radio
    // refuse NRL, ANFL and ANFT without the noise_reduction licence? FlexLib's
    // setters are optimistic and ignore the reply, so this sends each command
    // raw, logs the radio's reply code, then logs what the radio's own status
    // made of it a second later, and puts the old value back. NRS, NRF and RNN
    // are the positive control: an unlicensed 8000-series radio should refuse
    // them, which proves a refusal is visible here at all.
    public partial class FlexBase
    {
        public void ProbeNoiseCommandReplies()
        {
            Slice slice = theRadio?.ActiveSlice;
            if (slice == null)
            {
                Tracing.TraceLine("LicenceProbe: no active slice, nothing sent", TraceLevel.Info);
                return;
            }

            var items = new (string Key, Func<bool> Read)[]
            {
                ("anft", () => slice.ANFTOn),
                ("anfl", () => slice.ANFLOn),
                ("nrl", () => slice.NRLOn),
                ("nrs", () => slice.NRSOn),
                ("nrf", () => slice.NRFOn),
                ("rnn", () => slice.RNNOn),
            };

            Task.Run(async () =>
            {
                Tracing.TraceLine(
                    $"LicenceProbe: start, slice {slice.Index}, licence noise_reduction "
                    + $"reported={NoiseReductionLicenseReported} enabled={NoiseReductionLicensed}",
                    TraceLevel.Info);
                foreach (var (key, read) in items)
                {
                    bool before = read();
                    if (before)
                    {
                        Tracing.TraceLine($"LicenceProbe: {key} already on per the radio's status; not touched", TraceLevel.Info);
                        continue;
                    }
                    ProbeSend(slice.Index, key, 1);
                    await Task.Delay(1500).ConfigureAwait(false);
                    Tracing.TraceLine($"LicenceProbe: {key} status after =1 is {(read() ? "ON" : "off")}", TraceLevel.Info);
                    ProbeSend(slice.Index, key, 0);
                    await Task.Delay(1000).ConfigureAwait(false);
                    Tracing.TraceLine($"LicenceProbe: {key} status after =0 is {(read() ? "ON" : "off")}", TraceLevel.Info);
                }
                Tracing.TraceLine("LicenceProbe: done", TraceLevel.Info);
            });
        }

        private void ProbeSend(int index, string key, int value)
        {
            string cmd = $"slice set {index} {key}={value}";
            theRadio?.SendReplyCommand((seq, resp, s) =>
                Tracing.TraceLine(
                    $"LicenceProbe: reply to '{cmd}': 0x{resp:X8}{(resp == 0 ? " (OK)" : " (ERROR)")} '{s}'",
                    TraceLevel.Info),
                cmd);
        }
    }
}
