using System;
using System.Collections.Generic;
using System.Globalization;
using JJTrace;
using System.Diagnostics;

namespace Radios.ChainChecks
{
    /// <summary>
    /// Reads the radio's side of the transmit chain and states it as facts the
    /// rule engine can reason about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only place that knows how to ask a Flex a question. The rules
    /// know fact NAMES; this knows properties and meters. Everything the rule
    /// file can test has to appear here, which is deliberate: a rule cannot
    /// invent an observable, so the set of things the analyzer can see stays
    /// visible in one readable list rather than being scattered through a
    /// decision tree.
    /// </para>
    /// <para>
    /// <b>Every probe is guarded on its own.</b> A property that throws becomes
    /// one unreadable fact with the exception's message as the reason, not a
    /// diagnostic that crashed while diagnosing. This is the discipline
    /// <see cref="DiagnosticSnapshot"/> already uses, for the same reason.
    /// </para>
    /// <para>
    /// <b>What it cannot see, it says.</b> Several stages of the transmit path
    /// have no observable at all in this build — there is no packet counter
    /// anywhere in the app, no accessor for the Opus encoder, and no public
    /// route to the transmit stream object the radio acknowledged. Those come
    /// back absent, with a reason, rather than being quietly skipped. The rule
    /// file declares the same thing at stage level so an operator reading the
    /// report is told which parts of their radio nobody looked at.
    /// </para>
    /// </remarks>
    public static class TxChainFacts
    {
        /// <summary>The default staleness window for a meter reading. See the
        /// invented-thresholds note in the rule file: this one is a guess and
        /// wants bench confirmation.</summary>
        public const double StaleSeconds = 10.0;

        /// <summary>
        /// dBFS at or below which a transmit mic meter is treated as having
        /// heard nothing at all. Not invented here: it is
        /// <c>MicAudioReport</c>'s existing "Nothing" band edge, reused so the
        /// analyzer and the mic verdict cannot disagree about silence.
        /// </summary>
        public const double HeardNothingDbfs = -100.0;

        /// <summary>
        /// Collect everything the radio can tell us about its transmit chain.
        /// </summary>
        /// <param name="rig">The radio. Null is a legitimate argument — it
        /// yields a fact set that says so, which is what lets the ruleset report
        /// "no radio is connected" as the first dead stage rather than as an
        /// error.</param>
        /// <param name="extra">Facts the layer above collected: anything about
        /// this computer's microphone, which the radio layer cannot see. Added
        /// after the radio facts so the evidence block reads in signal-path
        /// order only if the caller supplies them in that order — which is why
        /// the caller inserts them itself rather than passing them here when
        /// order matters.</param>
        public static DiagnosticFacts Collect(FlexBase rig, IEnumerable<DiagnosticFact> extra = null)
        {
            var f = new DiagnosticFacts();

            bool connected = false;
            try { connected = rig != null && rig.IsConnected; }
            catch { connected = false; }

            f.Add(DiagnosticFact.Flag("radio-connected", "A radio is connected", connected,
                                      "this computer"));

            if (extra != null) f.AddRange(extra);

            if (!connected)
            {
                // Everything below needs a live radio. Say that once, per fact,
                // rather than letting a hundred null-guards each invent their
                // own zero — a fabricated zero is the failure mode this whole
                // design exists to avoid.
                const string why = "no radio is connected, so the radio could not be asked";
                foreach (string name in RadioFactNames())
                    f.Add(DiagnosticFact.Absent(name, LabelFor(name), why, "the radio"));

                // The serial deliberately survives a disconnect, so an evidence
                // block from a dead connection still names the radio.
                Probe(f, "radio-serial", "Radio serial number",
                      () => DiagnosticFact.Text("radio-serial", "Radio serial number",
                                                rig?.SelectedRadioSerial ?? "", "the radio"));
                return f;
            }

            // ── Identity, for the evidence block ──────────────────────────
            Probe(f, "radio-model", "Radio model",
                  () => DiagnosticFact.Text("radio-model", "Radio model", rig.RadioModel, "the radio"));
            Probe(f, "radio-serial", "Radio serial number",
                  () => DiagnosticFact.Text("radio-serial", "Radio serial number", rig.SelectedRadioSerial, "the radio"));
            Probe(f, "radio-firmware", "Radio firmware version",
                  () => DiagnosticFact.Text("radio-firmware", "Radio firmware version", rig.RadioFirmwareVersion, "the radio"));
            Probe(f, "radio-nickname", "Radio nickname",
                  () => DiagnosticFact.Text("radio-nickname", "Radio nickname", rig.RadioNickname, "the radio"));
            Probe(f, "radio-callsign", "Callsign set on the radio",
                  () => DiagnosticFact.Text("radio-callsign", "Callsign set on the radio", rig.RadioCallsign, "the radio"));
            Probe(f, "connection", "How the radio is connected",
                  () => DiagnosticFact.Text("connection", "How the radio is connected",
                                            rig.RemoteRig ? "SmartLink" : "local network", "this computer"));
            Probe(f, "meter-count", "Meters this radio publishes",
                  () => DiagnosticFact.Measure("meter-count", "Meters this radio publishes",
                                               rig.MeterInventory?.Count ?? 0, "", "the radio"));

            // ── Stage 4: is this computer sending transmit audio ──────────
            //
            // PCAudio is INTENT, not liveness: it goes true the moment the
            // setter runs and stays true even if the audio thread gave up. The
            // thread itself, the Opus encoder and the transmit stream object
            // are all private to FlexBase, so true liveness is not observable
            // from here — see the stage notes in the rule file.
            Probe(f, "pc-audio", "Radio audio through this computer",
                  () => DiagnosticFact.Flag("pc-audio", "Radio audio through this computer",
                                            rig.PCAudio, "this computer"));
            Probe(f, "pc-tx-path-trouble", "What the app says is wrong with the computer transmit path",
                  () => DiagnosticFact.Text("pc-tx-path-trouble",
                                            "What the app says is wrong with the computer transmit path",
                                            rig.TxTonePathTrouble ?? "", "this computer"));

            // The nearest thing to "audio is reaching the encoder" that this
            // build can see. The loudness meter is fed one line before the Opus
            // encode call, so recent samples mean the encoder is being called —
            // which is why this stands in for a stage that is otherwise blind.
            Probe(f, "pc-tx-audio-flowing", "Sound from this computer is reaching the transmit stream",
                  () => DiagnosticFact.Flag("pc-tx-audio-flowing",
                                            "Sound from this computer is reaching the transmit stream",
                                            rig.TxLufsAvailable, "this computer"));
            Probe(f, "pc-tx-loudness", "Loudness of the transmit audio leaving this computer", () =>
            {
                if (!rig.TxLufsSampleAvailable)
                {
                    return DiagnosticFact.Silent("pc-tx-loudness",
                        "Loudness of the transmit audio leaving this computer",
                        "nothing has been measured yet; transmit with computer audio to measure",
                        "this computer");
                }
                return DiagnosticFact.Measure("pc-tx-loudness",
                                              "Loudness of the transmit audio leaving this computer",
                                              rig.TxLufsShortTerm, "LUFS", "this computer");
            });

            // ── Stage 8: which input the radio is listening to ────────────
            Probe(f, "mic-source", "Microphone input selected on the radio", () =>
            {
                string src = rig.MicSource ?? "";
                if (src.Length == 0)
                {
                    return DiagnosticFact.Absent("mic-source", "Microphone input selected on the radio",
                        "the radio has not yet said which microphone input it is using", "the radio");
                }
                return DiagnosticFact.Text("mic-source", "Microphone input selected on the radio", src, "the radio");
            });
            Probe(f, "mic-source-options", "Microphone inputs this radio offers", () =>
            {
                List<string> list = rig.MicSourceList;
                if (list == null || list.Count == 0)
                {
                    return DiagnosticFact.Absent("mic-source-options", "Microphone inputs this radio offers",
                        "the radio has not yet listed its microphone inputs", "the radio");
                }
                return DiagnosticFact.Text("mic-source-options", "Microphone inputs this radio offers",
                                           string.Join(", ", list), "the radio");
            });

            // ── Stage 9: the mic profile ──────────────────────────────────
            //
            // MicProfileSelectionEmpty is the pcap-confirmed silent-transmit
            // failure, and it is already careful: it is true only once the
            // radio has positively listed profiles, so a slow subscription can
            // never be mistaken for the fault. Reused rather than re-derived.
            Probe(f, "mic-profile", "Mic profile selected on the radio", () =>
            {
                List<string> names = rig.MicProfileNames;
                if (names == null || names.Count == 0)
                {
                    return DiagnosticFact.Absent("mic-profile", "Mic profile selected on the radio",
                        "the radio has not yet listed its mic profiles, so its selection cannot be judged",
                        "the radio");
                }
                return DiagnosticFact.Text("mic-profile", "Mic profile selected on the radio",
                                           rig.CurrentMicProfileName ?? "", "the radio");
            });
            Probe(f, "mic-profile-empty", "The radio has no mic profile selected",
                  () => DiagnosticFact.Flag("mic-profile-empty", "The radio has no mic profile selected",
                                            rig.MicProfileSelectionEmpty, "the radio"));
            Probe(f, "mic-profile-count", "Mic profiles this radio offers",
                  () => DiagnosticFact.Measure("mic-profile-count", "Mic profiles this radio offers",
                                               rig.MicProfileNames?.Count ?? 0, "", "the radio"));
            Probe(f, "mic-profile-suggested", "Mic profile the radio would load",
                  () => DiagnosticFact.Text("mic-profile-suggested", "Mic profile the radio would load",
                                            rig.SuggestedMicProfileName ?? "", "the radio"));

            // ── Stage 10: the radio's own transmit chain ──────────────────
            Probe(f, "mic-gain", "Mic gain on the radio",
                  () => DiagnosticFact.Measure("mic-gain", "Mic gain on the radio", rig.MicGain, "", "the radio"));
            Probe(f, "mic-boost", "Mic boost on the radio",
                  () => DiagnosticFact.Flag("mic-boost", "Mic boost on the radio",
                                            rig.MicBoost == FlexBase.OffOnValues.on, "the radio"));
            Probe(f, "mic-bias", "Mic bias on the radio",
                  () => DiagnosticFact.Flag("mic-bias", "Mic bias on the radio",
                                            rig.MicBias == FlexBase.OffOnValues.on, "the radio"));
            Probe(f, "speech-processor", "Speech processor",
                  () => DiagnosticFact.Flag("speech-processor", "Speech processor",
                                            rig.ProcessorOn == FlexBase.OffOnValues.on, "the radio"));
            Probe(f, "speech-processor-level", "Speech processor level",
                  () => DiagnosticFact.Text("speech-processor-level", "Speech processor level",
                                            rig.ProcessorSetting.ToString(), "the radio"));
            Probe(f, "compander", "Compander",
                  () => DiagnosticFact.Flag("compander", "Compander",
                                            rig.Compander == FlexBase.OffOnValues.on, "the radio"));
            Probe(f, "tx-filter-low", "Transmit filter low cut",
                  () => DiagnosticFact.Measure("tx-filter-low", "Transmit filter low cut",
                                               rig.TXFilterLow, "Hz", "the radio"));
            Probe(f, "tx-filter-high", "Transmit filter high cut",
                  () => DiagnosticFact.Measure("tx-filter-high", "Transmit filter high cut",
                                               rig.TXFilterHigh, "Hz", "the radio"));
            Probe(f, "tx-filter-width", "Transmit filter width",
                  () => DiagnosticFact.Measure("tx-filter-width", "Transmit filter width",
                                               rig.TXFilterHigh - rig.TXFilterLow, "Hz", "the radio"));
            Probe(f, "tx-eq", "Transmit equalizer", () =>
            {
                FlexBase.TxEqSettings eq = rig.GetTxEq();
                if (eq == null)
                {
                    return DiagnosticFact.Absent("tx-eq", "Transmit equalizer",
                        "the radio has not answered the transmit equalizer request yet", "the radio");
                }
                return DiagnosticFact.Flag("tx-eq", "Transmit equalizer", eq.Enabled, "the radio");
            });
            Probe(f, "tx-monitor", "Transmit monitor",
                  () => DiagnosticFact.Flag("tx-monitor", "Transmit monitor",
                                            rig.Monitor == FlexBase.OffOnValues.on, "the radio"));
            Probe(f, "tx-tone-armed", "Transmit test tone armed",
                  () => DiagnosticFact.Flag("tx-tone-armed", "Transmit test tone armed",
                                            rig.TxToneEngaged, "the radio"));

            // ── Stage 11: what the radio says it hears ────────────────────
            //
            // SC_MIC sits DOWNSTREAM of the mic selection, so it reads transmit
            // audio from either source. That is why it is the right meter here
            // and MicData is not: MicData is the analog codec path only and
            // reads about -120 whenever transmit audio is coming from the
            // computer. Reading MicData for a PC-audio operator is the
            // cry-wolf mistake this analyzer must not repeat, so it is
            // collected as context and no rule tests it.
            Probe(f, "sc-mic-peak", "Loudest transmit audio the radio has heard", () =>
            {
                float v = rig.ScMicMaxDb;
                if (v <= -149f)
                {
                    return DiagnosticFact.Silent("sc-mic-peak", "Loudest transmit audio the radio has heard",
                        "the radio's transmit mic meter has not seen any audio yet; transmit to measure",
                        "the radio's SC_MIC meter");
                }
                return DiagnosticFact.Measure("sc-mic-peak", "Loudest transmit audio the radio has heard",
                                              v, "dBFS", "the radio's SC_MIC meter");
            });
            // These two stay plain numbers even at the -150 idle sentinel, and
            // that is deliberate. -150 while transmitting is not an absence of
            // information — it is THE finding, the floor that stalled the
            // honest-tx-audio investigation for weeks. Turning it into a
            // "silent" state would make the one rule that matters most
            // unevaluable in exactly the case it exists for.
            Probe(f, "sc-mic-recent", "Transmit audio the radio heard in the last second and a half",
                  () => DiagnosticFact.Measure("sc-mic-recent",
                        "Transmit audio the radio heard in the last second and a half",
                        rig.ScMicRecentDb, "dBFS", "the radio's SC_MIC meter"));
            Probe(f, "sw-alc", "Transmit drive after the radio's own levelling",
                  () => DiagnosticFact.Measure("sw-alc", "Transmit drive after the radio's own levelling",
                        rig.SwAlcDb, "dBFS", "the radio's SW ALC meter"));
            Probe(f, "codec-mic", "Analog microphone level at the radio's codec",
                  () => DiagnosticFact.Measure("codec-mic",
                        "Analog microphone level at the radio's codec (reads about -120 when transmit audio comes from the computer, which is normal)",
                        rig.MicData, "dBFS", "the radio's MIC meter"));

            // Named meters straight from the inventory, which is the only route
            // that carries a per-meter timestamp — so "this meter went quiet"
            // becomes a thing a rule can say.
            MeterInventory inv = SafeInventory(rig);
            AddMeter(f, inv, "meter-sc-mic", "Radio transmit mic meter", "SC_MIC");
            AddMeter(f, inv, "meter-micpeak", "Radio mic peak meter", "MICPEAK");
            AddMeter(f, inv, "meter-comppeak", "Radio compression peak meter", "COMPPEAK");
            AddMeter(f, inv, "meter-fwdpwr", "Radio forward power meter", "FWDPWR");
            AddMeter(f, inv, "meter-revpwr", "Radio reflected power meter", "REVPWR");
            AddMeter(f, inv, "meter-swr", "Radio SWR meter", "SWR");
            AddMeter(f, inv, "meter-patemp", "Radio power amplifier temperature", "PATEMP");

            // ── Stage 12: did RF actually leave ───────────────────────────
            Probe(f, "transmitting", "The radio is transmitting right now",
                  () => DiagnosticFact.Flag("transmitting", "The radio is transmitting right now",
                                            rig.Transmit, "the radio"));
            Probe(f, "forward-power", "Forward power",
                  () => DiagnosticFact.Measure("forward-power", "Forward power",
                                               rig.ForwardPowerWatts, "watts", "the radio"));
            Probe(f, "swr", "Standing wave ratio",
                  () => DiagnosticFact.Measure("swr", "Standing wave ratio",
                                               rig.SWRValue, "to 1", "the radio"));
            Probe(f, "rf-power-setting", "Transmit power setting",
                  () => DiagnosticFact.Measure("rf-power-setting", "Transmit power setting",
                                               rig.XmitPower, "percent", "the radio"));
            Probe(f, "dummy-load", "Dummy load mode",
                  () => DiagnosticFact.Flag("dummy-load", "Dummy load mode", rig.DummyLoadMode, "the app"));
            Probe(f, "ptt-source", "What is keying the transmitter",
                  () => DiagnosticFact.Text("ptt-source", "What is keying the transmitter",
                                            rig.PttSourceName ?? "", "the radio"));
            Probe(f, "ptt-hardware", "The transmitter is keyed by a hardware line",
                  () => DiagnosticFact.Flag("ptt-hardware", "The transmitter is keyed by a hardware line",
                                            rig.PttSourceIsHardware, "the radio"));
            Probe(f, "tx-slice", "Transmit slice",
                  () => DiagnosticFact.Text("tx-slice", "Transmit slice", rig.TXSliceLetter ?? "", "the radio"));
            Probe(f, "tx-mode", "Transmit mode",
                  () => DiagnosticFact.Text("tx-mode", "Transmit mode", rig.TXMode ?? "", "the radio"));

            return f;
        }

        /// <summary>
        /// The facts this source states about the radio. Used to say "no radio
        /// is connected" once per fact instead of letting each probe invent a
        /// zero — and it is the list to extend when a new radio-side observable
        /// is added.
        /// </summary>
        private static IEnumerable<string> RadioFactNames()
        {
            yield return "radio-model";
            yield return "radio-firmware";
            yield return "radio-nickname";
            yield return "radio-callsign";
            yield return "connection";
            yield return "meter-count";
            yield return "pc-audio";
            yield return "pc-tx-path-trouble";
            yield return "pc-tx-audio-flowing";
            yield return "pc-tx-loudness";
            yield return "mic-source";
            yield return "mic-source-options";
            yield return "mic-profile";
            yield return "mic-profile-empty";
            yield return "mic-profile-count";
            yield return "mic-profile-suggested";
            yield return "mic-gain";
            yield return "mic-boost";
            yield return "mic-bias";
            yield return "speech-processor";
            yield return "speech-processor-level";
            yield return "compander";
            yield return "tx-filter-low";
            yield return "tx-filter-high";
            yield return "tx-filter-width";
            yield return "tx-eq";
            yield return "tx-monitor";
            yield return "tx-tone-armed";
            yield return "sc-mic-peak";
            yield return "sc-mic-recent";
            yield return "sw-alc";
            yield return "codec-mic";
            yield return "meter-sc-mic";
            yield return "meter-micpeak";
            yield return "meter-comppeak";
            yield return "meter-fwdpwr";
            yield return "meter-revpwr";
            yield return "meter-swr";
            yield return "meter-patemp";
            yield return "transmitting";
            yield return "forward-power";
            yield return "swr";
            yield return "rf-power-setting";
            yield return "dummy-load";
            yield return "ptt-source";
            yield return "ptt-hardware";
            yield return "tx-slice";
            yield return "tx-mode";
        }

        /// <summary>
        /// A readable label for a fact we could not collect. Falls back to the
        /// fact's own name, which is ugly but never wrong — better than a blank
        /// line in an evidence block.
        /// </summary>
        private static string LabelFor(string name)
        {
            switch (name)
            {
                case "mic-source": return "Microphone input selected on the radio";
                case "mic-profile": return "Mic profile selected on the radio";
                case "mic-profile-empty": return "The radio has no mic profile selected";
                case "pc-audio": return "Radio audio through this computer";
                case "sc-mic-peak": return "Loudest transmit audio the radio has heard";
                case "forward-power": return "Forward power";
                case "swr": return "Standing wave ratio";
                case "transmitting": return "The radio is transmitting right now";
                default: return name.Replace('-', ' ');
            }
        }

        private static MeterInventory SafeInventory(FlexBase rig)
        {
            try { return rig?.MeterInventory; }
            catch { return null; }
        }

        private static void AddMeter(DiagnosticFacts f, MeterInventory inv,
                                     string factName, string label, string meterName)
        {
            if (inv == null)
            {
                f.Add(DiagnosticFact.Absent(factName, label,
                    "the meter list for this radio could not be read", "the radio"));
                return;
            }
            Probe(f, factName, label,
                  () => DiagnosticFact.FromMeter(factName, label, inv.Find(meterName), meterName));
        }

        /// <summary>
        /// Run one probe. Anything it throws becomes an unreadable fact carrying
        /// the reason, never an exception out of a diagnostic. The trace line is
        /// for us; the fact is for the operator.
        /// </summary>
        private static void Probe(DiagnosticFacts f, string name, string label, Func<DiagnosticFact> probe)
        {
            try
            {
                DiagnosticFact fact = probe();
                if (fact != null) f.Add(fact);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxChainFacts: probe " + name + " failed — " + ex.Message,
                                  TraceLevel.Warning);
                f.Add(DiagnosticFact.Absent(name, label,
                    "reading it from the radio failed: " + ex.Message, "the radio"));
            }
        }

        /// <summary>
        /// The radio half of the evidence block's identity lines. Kept beside
        /// the facts because it reads the same properties — and separate from
        /// <see cref="DiagnosticSnapshot"/>, which owns the SOFTWARE identity
        /// and must stay the only assembler of version strings.
        /// </summary>
        public static IReadOnlyList<string> StationLines(FlexBase rig)
        {
            var lines = new List<string>();
            if (rig == null)
            {
                lines.Add("No radio object was available.");
                return lines;
            }

            void Line(string label, Func<string> read)
            {
                try
                {
                    string v = read() ?? "";
                    lines.Add(label + ": " + (v.Length == 0 ? "not reported" : v));
                }
                catch (Exception ex)
                {
                    lines.Add(label + ": could not be read (" + ex.Message + ")");
                }
            }

            Line("Model", () => rig.RadioModel);
            Line("Serial number", () => rig.SelectedRadioSerial);
            Line("Firmware version", () => rig.RadioFirmwareVersion);
            Line("Firmware this app expects", () => FlexBase.LibraryExpectedFirmwareVersion);
            Line("Nickname", () => rig.RadioNickname);
            Line("Callsign", () => rig.RadioCallsign);
            Line("Connected", () => rig.IsConnected ? "yes" : "no");
            Line("Connection", () => rig.RemoteRig ? "SmartLink (over the internet)" : "local network");
            Line("Meters published", () => (rig.MeterInventory?.Count ?? 0)
                                           .ToString(CultureInfo.CurrentCulture));
            return lines;
        }
    }
}
