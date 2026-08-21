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
            // A connected radio always has firmware, so an empty answer means the
            // version has not been learned rather than that the radio has none.
            // "Radio firmware version: empty" in an evidence block bound for Flex
            // support is worse than saying we could not read it.
            Probe(f, "radio-firmware", "Radio firmware version", () =>
            {
                string fw = rig.RadioFirmwareVersion ?? "";
                if (fw.Length == 0)
                {
                    return DiagnosticFact.Absent("radio-firmware", "Radio firmware version",
                        "the radio's firmware version was not learned when this connection was made",
                        "the radio");
                }
                return DiagnosticFact.Text("radio-firmware", "Radio firmware version", fw, "the radio");
            });
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
            // Consistent with mic-profile above, which already refuses to judge a
            // selection the radio has not listed the options for. A count of zero
            // published before the radio has answered is the same claim that fact
            // is careful not to make, one line apart.
            Probe(f, "mic-profile-count", "Mic profiles this radio offers", () =>
            {
                List<string> names = rig.MicProfileNames;
                if (names == null || names.Count == 0)
                {
                    return DiagnosticFact.Absent("mic-profile-count", "Mic profiles this radio offers",
                        "the radio has not yet listed its mic profiles, so there is nothing to count",
                        "the radio");
                }
                return DiagnosticFact.Measure("mic-profile-count", "Mic profiles this radio offers",
                                              names.Count, "", "the radio");
            });
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
            // EVERY ONE of these three scalars is gated on its meter really
            // existing, and that gate is load-bearing.
            //
            // FlexBase initialises all three to -150 and only ever moves them
            // from a DataReady handler it attaches IF hookTxMeters finds the
            // meter by name. "SC_MIC NOT FOUND" and a missing plain "ALC" are
            // both states FlexBase traces on purpose — a FLEX-8600 publishes
            // MIC, MICPEAK and HWALC and may carry no plain ALC at all, and
            // FlexBase's own comment says SwAlcDb then never moves. Without
            // this gate the accessor hands back an initialiser the radio has
            // never touched, and the analyzer would publish it as a measured
            // dBFS value: a fabricated floor that fires "your radio hears
            // nothing", inside an evidence block that says on one line that the
            // meter does not exist and on the next quotes a reading from it.
            //
            // The meter lookup is the right gate rather than a -150 test,
            // because -150 from a meter that IS reporting is real information.
            //
            // The gate below is on the meter having REPORTED, not merely on the
            // meter existing, and the difference is not academic. FlexBase moves
            // these scalars from a handler it attaches in hookTxMeters, which
            // runs only from the MIC meter's own callback and looks the meters up
            // through FlexLib's CASE-SENSITIVE FindMeterByName. The gate here asks
            // the inventory, which matches case-INSENSITIVELY. So "the meter
            // exists" and "the field behind this fact is being written" are two
            // different conditions, and gating on the first while publishing the
            // second is how an untouched initialiser reaches an operator wearing
            // the units of a real measurement.
            //
            // MeterInventory tracks readings for EVERY meter off the generic
            // MeterData feed, independent of that lazy hook, so HasReading is the
            // one signal that cannot disagree with itself.
            MeterInventory inv = SafeInventory(rig);
            MeterReading scMicMeter = inv?.Find("SC_MIC");
            MeterReading alcMeter = inv?.Find("ALC");
            bool haveScMic = scMicMeter != null;
            bool haveAlc = alcMeter != null;

            // NOT "this radio does not publish it". MEASURED 2026-08-20 on the
            // bench 8600: with no station client connected the radio publishes
            // ELEVEN meters — the power, supply and codec ones that are always
            // there — and the whole transmit signal chain, SC_MIC and ALC
            // included, appears only once a client brings the transmit chain
            // up. Thirty-five were present minutes earlier on the same radio.
            //
            // So an absent meter here is a statement about the MOMENT, not
            // about the model, and the two are not interchangeable: telling an
            // operator their radio lacks a meter it will publish two seconds
            // later is a false claim about their equipment, and one they may
            // well repeat to Flex support.
            const string noScMic = "the radio is not currently publishing an SC_MIC meter, so what it "
                                 + "hears on transmit cannot be read right now — this meter appears "
                                 + "with the transmit chain, so try again once the radio is fully up";

            Probe(f, "sc-mic-peak", "Loudest transmit audio the radio has heard", () =>
            {
                if (!haveScMic)
                    return DiagnosticFact.Absent("sc-mic-peak",
                        "Loudest transmit audio the radio has heard", noScMic, "the radio");
                if (!scMicMeter.HasReading)
                    return DiagnosticFact.Silent("sc-mic-peak", "Loudest transmit audio the radio has heard",
                        "the radio lists its transmit mic meter but has not reported a reading from it yet",
                        "the radio's SC_MIC meter");

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

            // This one stays a plain number at the -150 idle sentinel once the
            // meter is known to exist, and that is deliberate: -150 from a live
            // meter while transmitting is not an absence of information, it is
            // THE finding — the floor that stalled the honest-tx-audio
            // investigation for weeks. Turning a live meter's floor into a
            // "silent" state would make the rule that matters most unevaluable
            // in exactly the case it exists for.
            Probe(f, "sc-mic-recent", "Transmit audio the radio heard in the last second and a half", () =>
            {
                if (!haveScMic)
                    return DiagnosticFact.Absent("sc-mic-recent",
                        "Transmit audio the radio heard in the last second and a half",
                        noScMic, "the radio");
                // Has REPORTED, not merely exists. A meter that has reported -150
                // still passes here and still publishes -150, which is the
                // finding this fact exists for; what cannot pass is a field the
                // radio has never touched.
                if (!scMicMeter.HasReading)
                    return DiagnosticFact.Silent("sc-mic-recent",
                        "Transmit audio the radio heard in the last second and a half",
                        "the radio lists its transmit mic meter but has not reported a reading from it yet; "
                        + "transmit to measure",
                        "the radio's SC_MIC meter");
                return DiagnosticFact.Measure("sc-mic-recent",
                        "Transmit audio the radio heard in the last second and a half",
                        rig.ScMicRecentDb, "dBFS", "the radio's SC_MIC meter");
            });

            Probe(f, "sw-alc", "Transmit drive after the radio's own levelling", () =>
            {
                if (!haveAlc)
                    return DiagnosticFact.Absent("sw-alc", "Transmit drive after the radio's own levelling",
                        "the radio is not currently publishing a plain ALC meter, so transmit drive "
                        + "cannot be read right now — like SC_MIC, it appears with the transmit chain",
                        "the radio");
                if (!alcMeter.HasReading)
                    return DiagnosticFact.Silent("sw-alc", "Transmit drive after the radio's own levelling",
                        "the radio lists its ALC meter but has not reported a reading from it yet; "
                        + "transmit to measure",
                        "the radio's ALC meter");
                return DiagnosticFact.Measure("sw-alc", "Transmit drive after the radio's own levelling",
                        rig.SwAlcDb, "dBFS", "the radio's ALC meter");
            });

            // MicData's backing field initialises to ZERO, and zero dBFS is not a
            // floor — it is FULL SCALE, the loudest reading the meter has. An
            // ungated read here therefore does not merely invent a number, it
            // invents the most alarming one available, on a line that sits in the
            // evidence for "your radio hears nothing".
            MeterReading micMeter = inv?.Find("MIC");
            Probe(f, "codec-mic", "Analog microphone level at the radio's codec", () =>
            {
                if (micMeter == null)
                    return DiagnosticFact.Absent("codec-mic", "Analog microphone level at the radio's codec",
                        "the radio is not currently publishing a MIC meter", "the radio");
                if (!micMeter.HasReading)
                    return DiagnosticFact.Silent("codec-mic", "Analog microphone level at the radio's codec",
                        "the radio lists its MIC meter but has not reported a reading from it yet",
                        "the radio's MIC meter");
                return DiagnosticFact.Measure("codec-mic",
                        "Analog microphone level at the radio's codec (reads about -120 when transmit audio comes from the computer, which is normal)",
                        rig.MicData, "dBFS", "the radio's MIC meter");
            });

            // Named meters straight from the inventory, which is the only route
            // that carries a per-meter timestamp — so "this meter went quiet"
            // becomes a thing a rule can say.
            AddMeter(f, inv, "meter-sc-mic", "Radio transmit mic meter", "SC_MIC");
            AddMeter(f, inv, "meter-micpeak", "Radio mic peak meter", "MICPEAK");
            AddMeter(f, inv, "meter-comppeak", "Radio compression peak meter", "COMPPEAK");
            AddMeter(f, inv, "meter-fwdpwr", "Radio forward power meter", "FWDPWR");
            // REFPWR, not REVPWR. Every other place that names this meter — the
            // meters panel, MeterModel's note, FlexLib's own AddMeter wiring, and
            // the 2026-08-16 census of the bench 8600's 102 meters — spells it
            // REFPWR. This line was the only REVPWR in the repository, so the
            // fact was permanently Absent and told the operator, in the evidence
            // for a high-SWR verdict, that their radio publishes no reflected
            // power meter. It does.
            AddMeter(f, inv, "meter-revpwr", "Radio reflected power meter", "REFPWR");
            AddMeter(f, inv, "meter-swr", "Radio SWR meter", "SWR");
            AddMeter(f, inv, "meter-patemp", "Radio power amplifier temperature", "PATEMP");

            // ── Stage 12: did RF actually leave ───────────────────────────
            Probe(f, "transmitting", "The radio is transmitting right now",
                  () => DiagnosticFact.Flag("transmitting", "The radio is transmitting right now",
                                            rig.Transmit, "the radio"));
            // ── The two facts that were reading their own initialisers ────
            //
            // Both are gated the same way the SC_MIC facts above are, and for a
            // sharper reason: these two are the ONLY facts in this source that a
            // rule turns into a verdict about the radio's RF output.
            //
            // Forward power came from a dBm field initialised to -150. That
            // converts to about a millionth of a millionth of a watt and formats
            // as "0". The no-power-out rule fires below a tenth of a watt, so a
            // forward-power meter that had not reported during a real
            // transmission produced "your radio is transmitting but almost no
            // power is leaving it" — a confident wrong verdict, sending the
            // operator to their power setting and their band, and printed in the
            // same evidence block as the meter-fwdpwr line correctly saying the
            // meter has never reported.
            //
            // SWR came from a field with no initialiser at all, so it published
            // 0. An SWR of zero to one is not a low reading, it is an impossible
            // one — and because the high-swr rule tests "above 3", a silent
            // meter read as a perfect match and let stage 12 be declared healthy
            // while nobody had looked. The false alarm and the false all-clear
            // are the same defect from opposite ends.
            MeterReading fwdMeter = inv?.Find("FWDPWR");
            MeterReading swrMeter = inv?.Find("SWR");

            Probe(f, "forward-power", "Forward power", () =>
            {
                if (fwdMeter == null)
                    return DiagnosticFact.Absent("forward-power", "Forward power",
                        "the radio is not currently publishing a forward power meter, so what is leaving it "
                        + "cannot be read here", "the radio");
                if (!fwdMeter.HasReading)
                    return DiagnosticFact.Silent("forward-power", "Forward power",
                        "the radio's forward power meter has not reported yet; it reports while "
                        + "transmitting, so transmit to measure",
                        "the radio's FWDPWR meter");
                return DiagnosticFact.Measure("forward-power", "Forward power",
                        rig.ForwardPowerWatts, "watts", "the radio's FWDPWR meter");
            });

            Probe(f, "swr", "Standing wave ratio", () =>
            {
                if (swrMeter == null)
                    return DiagnosticFact.Absent("swr", "Standing wave ratio",
                        "the radio is not currently publishing a standing wave ratio meter", "the radio");
                if (!swrMeter.HasReading)
                    return DiagnosticFact.Silent("swr", "Standing wave ratio",
                        "the radio's standing wave ratio meter has not reported yet; it reports "
                        + "while transmitting, so transmit to measure",
                        "the radio's SWR meter");
                return DiagnosticFact.Measure("swr", "Standing wave ratio",
                        rig.SWRValue, "to 1", "the radio's SWR meter");
            });
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
            // Empty is NOT an observed mode. A slice always has one, so an empty
            // string here means the app has not yet seen a mode change for a
            // slice already flagged as the transmit slice — the app not knowing,
            // not the radio having nothing. This is the opposite case from
            // mic-profile, where an empty answer IS the pcap-confirmed fault and
            // is deliberately kept observed.
            Probe(f, "tx-mode", "Transmit mode", () =>
            {
                string mode = rig.TXMode ?? "";
                if (mode.Length == 0)
                {
                    return DiagnosticFact.Absent("tx-mode", "Transmit mode",
                        "the transmit slice has not reported its mode to this computer yet", "the radio");
                }
                return DiagnosticFact.Text("tx-mode", "Transmit mode", mode, "the radio");
            });

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
