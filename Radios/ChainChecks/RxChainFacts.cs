using System;
using System.Collections.Generic;
using JJTrace;
using System.Diagnostics;

namespace Radios.ChainChecks
{
    /// <summary>
    /// What the radio says about its own outputs, for the receive walk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Small on purpose. The receive side has far fewer places to go wrong than
    /// the transmit side, and pretending otherwise by collecting facts no rule
    /// reads would make the evidence block longer without making it truer.
    /// </para>
    /// <para>
    /// <b>Same honesty rule as the transmit facts.</b> A reading that could not
    /// be taken is recorded as ABSENT with the reason, never defaulted to zero
    /// — a fabricated zero here would fire "your level is at zero" and send an
    /// operator to a control that was already correct.
    /// </para>
    /// </remarks>
    public static class RxChainFacts
    {
        public static DiagnosticFacts Collect(FlexBase rig)
        {
            var f = new DiagnosticFacts();

            bool connected = false;
            try { connected = rig != null && rig.IsConnected; }
            catch { connected = false; }

            f.Add(DiagnosticFact.Flag("radio-connected", "A radio is connected", connected,
                                      "this computer"));

            if (!connected)
            {
                const string why = "no radio is connected, so the radio could not be asked";
                foreach (string name in RadioFactNames())
                    f.Add(DiagnosticFact.Absent(name, LabelFor(name), why, "the radio"));
                return f;
            }

            Probe(f, "headphone-muted", LabelFor("headphone-muted"),
                  () => DiagnosticFact.Flag("headphone-muted", LabelFor("headphone-muted"),
                                            rig.HeadphoneMute, "the radio"));
            Probe(f, "lineout-muted", LabelFor("lineout-muted"),
                  () => DiagnosticFact.Flag("lineout-muted", LabelFor("lineout-muted"),
                                            rig.LineoutMute, "the radio"));
            Probe(f, "front-speaker-muted", LabelFor("front-speaker-muted"),
                  () => DiagnosticFact.Flag("front-speaker-muted", LabelFor("front-speaker-muted"),
                                            rig.FrontSpeakerMute, "the radio"));

            Probe(f, "headphone-level", LabelFor("headphone-level"),
                  () => DiagnosticFact.Measure("headphone-level", LabelFor("headphone-level"),
                                               rig.HeadphoneGain, "", "the radio"));
            Probe(f, "lineout-level", LabelFor("lineout-level"),
                  () => DiagnosticFact.Measure("lineout-level", LabelFor("lineout-level"),
                                               rig.LineoutGain, "", "the radio"));

            Probe(f, "pc-audio", LabelFor("pc-audio"),
                  () => DiagnosticFact.Flag("pc-audio", LabelFor("pc-audio"),
                                            rig.PCAudio, "this computer"));
            Probe(f, "remote-radio", LabelFor("remote-radio"),
                  () => DiagnosticFact.Flag("remote-radio", LabelFor("remote-radio"),
                                            rig.RemoteRig, "this computer"));

            // Named so the evidence block says WHICH radio, the same way the
            // transmit block does. A report that does not identify the radio is
            // hard to act on when it reaches Flex.
            Probe(f, "radio-model", LabelFor("radio-model"),
                  () => DiagnosticFact.Text("radio-model", LabelFor("radio-model"),
                                            rig.RadioModel ?? "", "the radio"));
            Probe(f, "radio-serial", LabelFor("radio-serial"),
                  () => DiagnosticFact.Text("radio-serial", LabelFor("radio-serial"),
                                            rig.SelectedRadioSerial ?? "", "the radio"));

            return f;
        }

        private static IEnumerable<string> RadioFactNames()
        {
            yield return "headphone-muted";
            yield return "lineout-muted";
            yield return "front-speaker-muted";
            yield return "headphone-level";
            yield return "lineout-level";
            yield return "pc-audio";
            yield return "remote-radio";
            yield return "radio-model";
            yield return "radio-serial";
        }

        private static string LabelFor(string name)
        {
            switch (name)
            {
                case "headphone-muted": return "The headphone output is muted";
                case "lineout-muted": return "The line out output is muted";
                case "front-speaker-muted": return "The front speaker is muted";
                case "headphone-level": return "Headphone level";
                case "lineout-level": return "Line out level";
                case "pc-audio": return "Radio audio through this computer";
                case "remote-radio": return "Connected remotely";
                case "radio-model": return "Radio model";
                case "radio-serial": return "Radio serial number";
                default: return name.Replace('-', ' ');
            }
        }

        private static void Probe(DiagnosticFacts f, string name, string label, Func<DiagnosticFact> probe)
        {
            try
            {
                DiagnosticFact fact = probe();
                if (fact != null) f.Add(fact);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("RxChainFacts: probe " + name + " failed — " + ex.Message,
                                  TraceLevel.Warning);
                f.Add(DiagnosticFact.Absent(name, label,
                    "reading it from the radio failed: " + ex.Message, "the radio"));
            }
        }
    }
}
