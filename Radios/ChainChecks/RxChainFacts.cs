using System;
using System.Collections.Generic;
using System.Text;
using JJTrace;
using System.Diagnostics;

namespace Radios.ChainChecks
{
    /// <summary>
    /// What the radio says about its own outputs, and what has actually been
    /// arriving from it, for the receive walk.
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
    /// <para>
    /// <b>Settings are not evidence, and until 2026-08-28 that was all this
    /// collected (#350).</b> Nine facts, every one of them something WE had set:
    /// three mutes, two levels, a routing switch, the model and the serial. The
    /// report could be entirely, verifiably correct while no audio had ever
    /// reached the computer — it said the plumbing was configured and never said
    /// water came out. Don asked for the missing half and he was right: "radio
    /// audio through this computer: on" is a switch of ours, while "audio is
    /// arriving from the radio at 42 kilobits per second" is a fact about the
    /// RADIO, measured from bytes that crossed the network, and only the second
    /// survives a reader who distrusts our software (#217).
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

                const string noTraffic = "no radio is connected, so nothing could be arriving to measure";
                foreach (string name in TrafficFactNames())
                    f.Add(DiagnosticFact.Absent(name, LabelFor(name), noTraffic, "the radio"));

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

            // LAST, because the evidence block reads as a walk and this is the
            // far end of it: everything above is what the radio was told, and
            // this is what came back across the network.
            AddTrafficFacts(f, rig);

            return f;
        }

        // ── What actually arrived ────────────────────────────────────────────

        /// <summary>
        /// The measured half of the report: how much traffic the radio has been
        /// sending us, taken from the rolling sampler on <see cref="FlexBase"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Absent, never zero, when there is nothing to report.</b> A window
        /// with no readings in it means we have not looked yet — the connect is
        /// seconds old, or the sampler could not start — and that is a completely
        /// different answer from "no audio arrived". Defaulting it to zero would
        /// fire a rule accusing a station that is working perfectly, which is a
        /// worse outcome than the gap this whole fact set exists to close.
        /// </para>
        /// <para>
        /// <b>Zero IS the right answer in the commonest setup</b>, and nothing
        /// here treats it as a fault on its own. The Opus receive stream carries
        /// sound to this computer only when radio audio through this computer is
        /// switched on; an operator listening on the radio's own speaker has no
        /// such stream, no such traffic, and nothing wrong. Every rule that reads
        /// these facts is gated on <c>pc-audio</c> for that reason, and the
        /// evidence block carries <c>pc-audio</c> and <c>remote-radio</c>
        /// immediately above them so the scope travels with the number.
        /// </para>
        /// </remarks>
        private static void AddTrafficFacts(DiagnosticFacts f, FlexBase rig)
        {
            RxTrafficReading rx;
            try
            {
                rx = rig.RxTraffic;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("RxChainFacts: receive traffic could not be read — " + ex.Message,
                                  TraceLevel.Warning);
                rx = null;
            }

            AddTrafficFactsFrom(f, rx);
        }

        /// <summary>
        /// The reading-to-facts conversion on its own, so a test can drive the
        /// real thing with a real window instead of hand-building facts that
        /// mirror this code and drift from it. This is the seam the warm-up
        /// counting (#368) lives in, which is exactly the part a mirrored copy
        /// would have got wrong.
        /// </summary>
        internal static void AddTrafficFactsFrom(DiagnosticFacts f, RxTrafficReading rx)
        {
            if (rx == null)
            {
                // ABSENT: nothing in this build is watching, so this is not
                // observable from here at all.
                foreach (string name in TrafficFactNames())
                    f.Add(DiagnosticFact.Absent(name, LabelFor(name),
                        "the receive traffic sampler could not be read", "the radio"));
                return;
            }

            if (!rx.HasSamples)
            {
                // SILENT, not absent, and the distinction is the one this whole
                // fact type exists to keep: we ARE watching and have simply not
                // taken a reading yet, which is the ordinary state for the first
                // second or two after a connect. Calling it absent would say the
                // measurement cannot be made here, and send whoever reads the
                // report looking for a hole in the application.
                foreach (string name in TrafficFactNames())
                    f.Add(DiagnosticFact.Silent(name, LabelFor(name),
                        "no traffic readings have been taken yet — this needs a few seconds "
                        + "after connecting, so run the check again shortly", "the radio"));
                return;
            }

            string window = rx.DescribeWindow();
            DateTime? at = rx.NewestAtUtc;

            f.Add(DiagnosticFact.Measure("rx-audio-kbps", LabelFor("rx-audio-kbps"),
                                         rx.AudioPeakKbps, "kilobits per second",
                                         "the radio's network audio stream, highest of " + window,
                                         at));

            // The consistency count begins at the first reading that carried
            // audio (#368). The sampler starts on connect and the stream a few
            // seconds later, so a first run after ANY connect holds warm-up
            // zeros at the front — and counted raw they turn "audio arrived in
            // every reading" into "14 of 18" on a radio that is working
            // perfectly, for every operator, on exactly the run people take.
            // Holes AFTER audio began are the opposite of noise — they are what
            // a weak or congested network looks like — so they get their own
            // fact rather than being collapsed into one count with the warm-up.
            if (rx.AudioReadingsWithTraffic > 0)
            {
                int since = rx.ReadingsSinceAudioBegan;
                string basis = "the radio's network audio stream, " + window;
                if (rx.LeadingZeroReadings > 0)
                {
                    basis += "; the count starts at the first reading that carried audio, leaving out "
                           + (rx.LeadingZeroReadings == 1
                                  ? "the one earlier reading"
                                  : "the " + rx.LeadingZeroReadings + " earlier readings")
                           + " taken before the stream had begun";
                }

                f.Add(DiagnosticFact.Measure("rx-audio-readings", LabelFor("rx-audio-readings"),
                                             rx.AudioReadingsWithTraffic, "of " + since,
                                             basis, at));

                f.Add(DiagnosticFact.Measure("rx-audio-gaps", LabelFor("rx-audio-gaps"),
                                             rx.AudioGapReadings, "of " + since,
                                             basis, at));
            }
            else
            {
                // No reading carried audio, so there is no "first reading that
                // carried audio" to count from: the raw window is the story,
                // and warm-up cannot be told apart from a hole. Saying either
                // would be a guess, so the gap fact says exactly that instead
                // of a number.
                f.Add(DiagnosticFact.Measure("rx-audio-readings", LabelFor("rx-audio-readings"),
                                             rx.AudioReadingsWithTraffic, "of " + rx.SampleCount,
                                             "the radio's network audio stream, " + window,
                                             at));

                f.Add(DiagnosticFact.Silent("rx-audio-gaps", LabelFor("rx-audio-gaps"),
                    "no reading carried audio, so a hole in the stream cannot be told apart "
                    + "from the stream never having started", "the radio"));
            }

            f.Add(DiagnosticFact.Measure("rx-total-kbps", LabelFor("rx-total-kbps"),
                                         rx.TotalPeakKbps, "kilobits per second",
                                         "the radio, highest of " + window,
                                         at));

            f.Add(DiagnosticFact.Measure("rx-meter-kbps", LabelFor("rx-meter-kbps"),
                                         rx.MeterPeakKbps, "kilobits per second",
                                         "the radio's meter stream, highest of " + window,
                                         at));
        }

        /// <summary>
        /// The measurement said plainly, for the report an operator reads and
        /// hears — not for the evidence block, which quotes the facts themselves.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A measurement, never a verdict.</b> The sentence is about what WE
        /// observed, not about whether the radio is faulty: "we measured no
        /// receive audio in ten readings" is something we can stand behind, and
        /// "your audio is broken" is not. Where something genuinely is wrong, a
        /// rule in <c>rx-chain-rules.txt</c> says so and this still reports only
        /// the numbers underneath it.
        /// </para>
        /// <para>
        /// It exists because the receive report is the ONLY place these readings
        /// reach a person. The stage-by-stage walk is shown only when a rule
        /// fires, and the receive check has never rendered an evidence block at
        /// all — so without this sentence the measurement would be joined to the
        /// report and still invisible in the case that matters most, which is the
        /// report that looks fine and says nothing about audio.
        /// </para>
        /// <para>
        /// <b>The consistency count starts where audio did, and holes after that
        /// are reported on their own (#368).</b> Its first field reading said
        /// "in 11 of 16 readings" on a perfectly healthy radio, because the
        /// sampler starts on connect and the stream a few seconds later — and
        /// the sentence never said WHERE the missing five sat, which is the
        /// whole difference between a warm-up and a dropout. So: warm-up zeros
        /// are left out and the leaving-out is stated; holes AFTER audio began
        /// survive, named and interpreted, because audio going missing mid-run
        /// is what a weak or congested network looks like and is the single
        /// most useful thing this measurement can say to a remote operator.
        /// </para>
        /// <para>
        /// <b>Each figure carries its reason (#368 again).</b> A number with no
        /// interpretation is the first thing a sceptical reader discards, so
        /// the total and the meter figures say why they were measured: data or
        /// meters still arriving while audio is not is a specific, nameable
        /// fault — the radio talking to this computer without sending sound.
        /// </para>
        /// </remarks>
        public static string ArrivalSentence(DiagnosticFacts facts)
        {
            if (facts == null) return "";

            DiagnosticFact audio = facts.Find("rx-audio-kbps");
            if (audio == null) return "";

            if (audio.State != FactState.Observed)
            {
                return "Audio arriving from the radio: not measured — "
                     + (audio.Why.Length != 0 ? audio.Why : "no reading was available") + ".";
            }

            DiagnosticFact readings = facts.Find("rx-audio-readings");
            DiagnosticFact gaps = facts.Find("rx-audio-gaps");
            DiagnosticFact total = facts.Find("rx-total-kbps");
            DiagnosticFact meter = facts.Find("rx-meter-kbps");
            DiagnosticFact pcAudio = facts.Find("pc-audio");

            // Whether radio audio through this computer is switched OFF right
            // now. It scopes two things below: a correct zero must not read as
            // an accusation, and a hole left by the operator turning the stream
            // off must not be blamed on their network.
            bool pcAudioOff = pcAudio != null && pcAudio.State == FactState.Observed
                              && !(pcAudio.Number > 0);

            var sb = new StringBuilder();

            double peak = audio.Number ?? 0;
            sb.Append("Audio arriving from the radio: ");

            if (peak > 0)
            {
                sb.Append("up to ").Append(audio.TextValue).Append(' ').Append(audio.Units);

                int? withAudio = (readings != null && readings.State == FactState.Observed)
                    ? (int?)Math.Round(readings.Number ?? 0) : null;
                int? holes = (gaps != null && gaps.State == FactState.Observed)
                    ? (int?)Math.Round(gaps.Number ?? 0) : null;

                if (withAudio.HasValue && holes.HasValue)
                {
                    // The denominator is readings since audio began — the two
                    // facts are complements within it, so the sentence and the
                    // evidence block can never disagree about the count.
                    int since = withAudio.Value + holes.Value;

                    if (holes.Value == 0)
                    {
                        sb.Append(since == 1
                            ? ", in the one reading taken since audio began."
                            : ", in every one of " + since + " readings taken about a second "
                              + "apart, counted from the first reading that carried audio.");
                    }
                    else
                    {
                        sb.Append(", but it was missing in ").Append(holes.Value)
                          .Append(" of ").Append(since)
                          .Append(" readings taken about a second apart, counted from the ")
                          .Append("first reading that carried audio.");
                        sb.Append(pcAudioOff
                            ? " Radio audio through this computer is now switched off, so the "
                              + "stream stopping is expected — the sound stays at the radio."
                            : " Audio missing from readings scattered through the run can mean "
                              + "drop-outs — often a weak or congested network connection.");
                    }
                }
                else if (readings != null && readings.State == FactState.Observed)
                {
                    sb.Append(", in ").Append(readings.TextValue).Append(' ').Append(readings.Units)
                      .Append(" readings taken about a second apart.");
                }
                else
                {
                    sb.Append('.');
                }
            }
            else
            {
                sb.Append("none measured");
                if (readings != null && readings.State == FactState.Observed)
                {
                    sb.Append(", in ").Append(readings.TextValue).Append(' ').Append(readings.Units)
                      .Append(" readings taken about a second apart");
                }
                sb.Append('.');
            }

            if (total != null && total.State == FactState.Observed)
            {
                if ((total.Number ?? 0) > 0)
                {
                    sb.Append(" All data arriving from the radio over the same readings: up to ")
                      .Append(total.TextValue).Append(' ').Append(total.Units);
                    if (meter != null && meter.State == FactState.Observed)
                        sb.Append(", of which meter readings — the radio reporting its own gauges — ")
                          .Append("were up to ").Append(meter.TextValue);
                    sb.Append('.');

                    // Why those two numbers are here at all — skipped when the
                    // stream is switched off, because then meters without audio
                    // are the expected state and the comparison would quietly
                    // accuse a working station.
                    if (!pcAudioOff)
                    {
                        sb.Append(" Those figures are measured for comparison: data or meters ")
                          .Append("still arriving while audio is not would mean the radio is ")
                          .Append("talking to this computer but not sending sound — a different ")
                          .Append("problem from a dead link.");
                    }
                }
                else
                {
                    sb.Append(" All data arriving from the radio over the same readings: none measured.");
                }
            }

            // The scope, said out loud rather than left for the reader to infer
            // from a switch three lines further up. Without it a correct zero
            // reads as an accusation.
            if (pcAudio != null && pcAudio.State == FactState.Observed && peak <= 0)
            {
                sb.Append(pcAudio.Number > 0
                    ? " Radio audio through this computer is on, so audio should be arriving here."
                    : " Radio audio through this computer is off, so none is expected here — "
                      + "the sound stays at the radio.");
            }

            return sb.ToString();
        }

        // ── Names and labels ─────────────────────────────────────────────────

        /// <summary>The facts read off the radio's own settings.</summary>
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

        /// <summary>The facts measured from what arrived. Kept separate from
        /// <see cref="RadioFactNames"/> because their reason for being missing is
        /// a different sentence: nothing was asked versus nothing was seen.</summary>
        private static IEnumerable<string> TrafficFactNames()
        {
            yield return "rx-audio-kbps";
            yield return "rx-audio-readings";
            yield return "rx-audio-gaps";
            yield return "rx-total-kbps";
            yield return "rx-meter-kbps";
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
                case "rx-audio-kbps": return "Audio arriving over the network from the radio";
                case "rx-audio-readings": return "Readings in which audio was arriving";
                case "rx-audio-gaps": return "Readings audio went missing from after it began";
                case "rx-total-kbps": return "All data arriving from the radio";
                case "rx-meter-kbps": return "Meter readings arriving from the radio";
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
