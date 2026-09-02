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
        /// The most power a transverter can legally be sent, in watts: +15.00
        /// dBm, which is FlexLib's own upper clamp on <c>Xvtr.MaxPower</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>#163, and this figure is not chosen — it is read off FlexLib.</b>
        /// <c>Xvtr.MaxPower</c> is a double in dBm clamped -10.00 to +15.00
        /// (lower still on some models: +10.00 on 6400 and 6600, +8.00 when the
        /// IF is above 80 MHz). So the whole legal drive band is 0.0001 W to
        /// 0.0316 W, and this is its ceiling. Taking the absolute maximum
        /// across models rather than the current radio's is the conservative
        /// direction: it can under-report on a 6400, never over-report.
        /// </para>
        /// <para>
        /// <b>It does two jobs, and they are the same statement from opposite
        /// sides.</b> At or below it, a forward-power reading cannot tell legal
        /// transverter drive from a dead key, so there is no reading — and a
        /// non-answer must never score as the best possible value, which is
        /// exactly what happened while stage 12's rules simply never applied.
        /// Above it, more power is leaving than any transverter is rated to
        /// accept, which is what <c>transverter-overdrive</c> tests. The first
        /// readable value on this path is therefore already out of spec, and
        /// that is a property of the instrument rather than a coincidence of
        /// two numbers.
        /// </para>
        /// <para>
        /// <b>MEASURED support for the gate.</b> The lowest FWDPWR reading ever
        /// recorded from the bench 8600 is 17.4 dBm — 0.055 W, across a normal
        /// transmission on 2026-08-20, at the radio's minimum power setting.
        /// That is 2.4 dB ABOVE this ceiling. So the entire legal transverter
        /// band sits below anything this meter has been seen to report, and
        /// expecting it to resolve a hundredth of a watt is expecting something
        /// no capture supports.
        /// </para>
        /// <para>
        /// <b>The gap this leaves, stated rather than papered over.</b> A path
        /// running at, say, three times rated drive is caught — it is above the
        /// ceiling, so it is readable and the rule fires. What is NOT
        /// distinguished is HOW far over, because what a given transverter
        /// tolerates varies by transverter and nothing in the radio knows it.
        /// The verdict therefore says stop rather than quantifying the danger.
        /// Sharpening this needs task #27, transverter bench Session One, which
        /// also settles whether the forward-power coupler reads the XVTR port
        /// at all.
        /// </para>
        /// </remarks>
        public const double TransverterDriveCeilingWatts = 0.0316;

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
            // STAGE 7 — the radio's own answer when it opened our transmit
            // stream. This was marked not-observable in the rules file with the
            // words "held privately inside the app and is not published
            // anywhere a check can read it". True when written; untrue from the
            // moment Sprint 33 Track G recorded it. The observation existed and
            // went only to a trace file, so the analyzer kept saying it could
            // not look at the single most likely cause of silent transmit.
            //
            // Three-valued on purpose. No stream open is NOT the same answer as
            // a stream opened uncompressed: the first is stage 6, the second is
            // stage 7, and sending an operator after the wrong one costs them
            // the afternoon.
            Probe(f, "tx-stream-open", "A transmit audio stream is open",
                  () => DiagnosticFact.Flag("tx-stream-open",
                                            "A transmit audio stream is open",
                                            rig.TxStreamIsOpus.HasValue, "the radio"));
            Probe(f, "tx-stream-compression", "How the radio opened our transmit stream",
                  () => rig.TxStreamIsOpus.HasValue
                        // The radio's own word, UNPARSED, or empty when it sent
                        // no compression key at all. Deliberately not a
                        // sentence: a rule that matched on prose would be
                        // coupled to this file's phrasing, and the evidence
                        // block already renders an empty text fact as "not
                        // reported".
                        ? DiagnosticFact.Text("tx-stream-compression",
                                              "How the radio opened our transmit stream",
                                              rig.TxStreamCompression ?? "",
                                              "the radio")
                        : DiagnosticFact.Silent("tx-stream-compression",
                                                "How the radio opened our transmit stream",
                                                "no transmit audio stream is open, so the radio has "
                                                + "not answered one way or the other",
                                                "the radio"));
            Probe(f, "tx-stream-is-opus", "The radio opened our transmit stream as Opus",
                  () => rig.TxStreamIsOpus.HasValue
                        ? DiagnosticFact.Flag("tx-stream-is-opus",
                                              "The radio opened our transmit stream as Opus",
                                              rig.TxStreamIsOpus.Value, "the radio")
                        : DiagnosticFact.Silent("tx-stream-is-opus",
                                                "The radio opened our transmit stream as Opus",
                                                "no transmit audio stream is open",
                                                "the radio"));
            // The unparsed line, for the evidence block: a reader at Flex who
            // distrusts our interpretation can read what their own radio said.
            Probe(f, "tx-stream-status-line", "The radio's transmit stream status line",
                  () => DiagnosticFact.Text("tx-stream-status-line",
                                            "The radio's transmit stream status line",
                                            rig.TxStreamStatusLine ?? "", "the radio"));

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
            // FlexBase reads all three at -150 until a copy of the meter has
            // reported and been elected (TransmitMeterElection). "SC_MIC NOT
            // FOUND" and a missing plain "ALC" are both states FlexBase traces
            // on purpose — a FLEX-8600 publishes
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
            // meter existing, and the difference is not academic. "The meter
            // exists" and "the field behind this fact is being written" are two
            // different conditions, and gating on the first while publishing the
            // second is how an untouched initialiser reaches an operator wearing
            // the units of a real measurement.
            //
            // Until 2026-09-02 the gate was the inventory's HasReading for the
            // FIRST meter of that name, while the value came from FlexBase — and
            // on a radio that publishes several copies (#502: Don's 6300 has
            // three SC_MIC, the first of which never reports) those are two
            // different meters. The gate said "not reported yet" while the
            // elected copy was streaming his voice: the same wrong "the radio
            // hears nothing" this comment was written to prevent, from the
            // other direction. So the gate now asks FlexBase whether the copy
            // BEHIND THE VALUE has reported; the inventory still answers whether
            // the radio publishes the meter at all.
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
                if (!rig.ScMicHasReported)
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
                if (!rig.ScMicHasReported)
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
                if (!rig.SwAlcHasReported)
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
            // ── What is in the path, stated before anything judges it ──────
            //
            // #188 and #163, and they are the same omission seen from two
            // sides. Until now stage 12 judged power and standing wave ratio
            // without ever saying which connector the RF left by, and without
            // knowing whether it left through a transverter at all.
            //
            // The port comes first because every number below it is meaningless
            // without it. A capture that says "17.5 watts forward, 13.4 back"
            // and does not say ANT1 cannot be read by anyone, including the
            // person who took it.
            //
            // Both are read off the ACTIVE SLICE, and with no active slice both
            // properties answer "ANT1" — a default, not an observation. That is
            // the same shape as the forward-power fact that used to publish its
            // own initialiser and produce a confident wrong verdict, so it gets
            // the same treatment: no slice means no reading.
            bool haveSlice = false;
            try { haveSlice = rig.HasActiveSlice; } catch { haveSlice = false; }

            const string NoSliceReason =
                "the antenna port is a property of the active slice, and this computer does not "
                + "currently have one, so the port would be a default rather than a reading";

            Probe(f, "tx-antenna", "Transmit antenna port",
                  () => haveSlice
                      ? DiagnosticFact.Text("tx-antenna", "Transmit antenna port",
                                            rig.TXAntennaName ?? "", "the radio")
                      : DiagnosticFact.Absent("tx-antenna", "Transmit antenna port",
                                              NoSliceReason, "the radio"));
            Probe(f, "rx-antenna", "Receive antenna port",
                  () => haveSlice
                      ? DiagnosticFact.Text("rx-antenna", "Receive antenna port",
                                            rig.RXAntennaName ?? "", "the radio")
                      : DiagnosticFact.Absent("rx-antenna", "Receive antenna port",
                                              NoSliceReason, "the radio"));

            bool xvtrPath = false;
            try { xvtrPath = haveSlice && rig.TXAntennaIsTransverter; } catch { xvtrPath = false; }

            Probe(f, "transverter-path", "Transmitting through a transverter",
                  () => haveSlice
                      ? DiagnosticFact.Flag("transverter-path", "Transmitting through a transverter",
                                            rig.TXAntennaIsTransverter, "the radio")
                      : DiagnosticFact.Absent("transverter-path",
                                              "Transmitting through a transverter",
                                              NoSliceReason, "the radio"));

            // Empty is a real and separate answer: the transmit antenna is the
            // XVTR port but no transverter definition covers the slice
            // frequency, so the radio will send drive somewhere we cannot
            // describe. That is not the same as not being on a transverter.
            Probe(f, "transverter-name", "Transverter in use", () =>
            {
                if (!xvtrPath)
                    return DiagnosticFact.Absent("transverter-name", "Transverter in use",
                        "your transmit antenna is not the transverter port", "the radio");
                return DiagnosticFact.Text("transverter-name", "Transverter in use",
                        rig.ActiveXvtrName ?? "", "the radio");
            });

            // dBm, because that is the unit the radio itself uses for this and
            // the unit a transverter's drive spec is written in. Converting it
            // to watts here would hand the operator 0.003 and help nobody.
            Probe(f, "transverter-drive", "Transverter drive", () =>
            {
                if (!xvtrPath)
                    return DiagnosticFact.Absent("transverter-drive", "Transverter drive",
                        "your transmit antenna is not the transverter port", "the radio");
                if (string.IsNullOrEmpty(rig.ActiveXvtrName))
                    return DiagnosticFact.Absent("transverter-drive", "Transverter drive",
                        "the transmit antenna is the transverter port, but no transverter definition "
                        + "on this radio covers the frequency the transmit slice is on, so what drive "
                        + "it is set to cannot be read", "the radio");
                return DiagnosticFact.Measure("transverter-drive", "Transverter drive",
                        rig.XvtrDrivePowerCentiDbm / 100.0, "dBm", "the radio");
            });

            // The ceiling FlexLib will clamp a drive setting to. Worth stating
            // because it is model-dependent and the model list behind it is
            // incomplete: Xvtr.MaxPower names only FLEX-6400/6400M/6600/6600M,
            // so the 8000 series and Aurora reach the 15.0 dBm else-branch by
            // OMISSION rather than by being recognised. Whether 15.0 is right
            // for an 8400, 8600, AU-510 or AU-520 is unknown (task #163).
            Probe(f, "transverter-drive-ceiling", "Highest drive this radio will accept", () =>
            {
                if (!xvtrPath || string.IsNullOrEmpty(rig.ActiveXvtrName))
                    return DiagnosticFact.Absent("transverter-drive-ceiling",
                        "Highest drive this radio will accept",
                        "there is no transverter in the transmit path to have a drive limit",
                        "the radio");
                return DiagnosticFact.Measure("transverter-drive-ceiling",
                        "Highest drive this radio will accept",
                        rig.XvtrDriveMaxCentiDbm / 100.0, "dBm", "this app, from the radio's model");
            });

            MeterReading fwdMeter = inv?.Find("FWDPWR");
            MeterReading swrMeter = inv?.Find("SWR");
            MeterReading refMeter = inv?.Find("REFPWR");

            // ── The transverter blind spot, closed at the FACT rather than in
            // the rules ───────────────────────────────────────────────────────
            //
            // #163. Stage 12's power and standing-wave rules were written for
            // an antenna and are wrong for a transverter, and the way they were
            // wrong was the worst available: they simply never applied. The
            // no-power-out rule is guarded on "rf-power-setting above 0", and
            // the transverter operator lives at setting 0 permanently — Noel's
            // 8600 reads rfpower=0 today — so that rule is switched off for
            // exactly the operator it would matter most to. high-swr and
            // power-coming-back are guarded on "forward-power at least 1",
            // which that operator never reaches. Three checks, silently off,
            // and the stage reporting healthy.
            //
            // This is fixed HERE and not in the rule file on the codebase's own
            // instruction, written at the top of DiagnosticFact: observability
            // is a property of the FACT, not of the rule that reads it. Put it
            // here and "could not check" propagates on its own; put it in the
            // rules and every rule has to carry its own honesty, which means one
            // day one of them will not. Gating the rules with "needs:
            // transverter-path is no" would have produced NOT APPLICABLE, which
            // costs nothing and is counted nowhere — the same silence in a
            // different coat.
            //
            // So on a transverter path, below the meters' useful floor, these
            // four facts are ABSENT with the reason. Every rule that touches
            // them then reports as a check that COULD NOT BE MADE, and stage 12
            // comes back "not observable from here" instead of healthy. That is
            // the third tier the finding asked for: below the meter's declared
            // floor means "I have no reading", and it must not score as the best
            // possible value.
            //
            // NOT lowering the threshold, deliberately. Any single absolute watt
            // figure is wrong for both paths at once: 0.1 W and even 0.01 W sit
            // INSIDE the legal transverter drive band. Today's guard fails
            // silent; a lowered number would fail WRONG and hand the transverter
            // case a false all-clear. (Decision, 2026-08-20.)
            double fwdWatts = double.NaN;
            try { fwdWatts = rig.ForwardPowerWatts; } catch { fwdWatts = double.NaN; }
            bool xvtrBelowMeterFloor =
                xvtrPath && (double.IsNaN(fwdWatts) || fwdWatts <= TransverterDriveCeilingWatts);

            const string XvtrFloorReason =
                "your transmit antenna is the transverter port, where the most drive the radio will "
                + "let you set is about three hundredths of a watt. The lowest reading its forward "
                + "power meter has ever produced is nearly twice that, so a reading this low cannot "
                + "tell legal transverter drive from a dead key";

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
                if (xvtrBelowMeterFloor)
                    return DiagnosticFact.Absent("forward-power", "Forward power",
                        XvtrFloorReason, "the radio's FWDPWR meter");
                return DiagnosticFact.Measure("forward-power", "Forward power",
                        rig.ForwardPowerWatts, "watts", "the radio's FWDPWR meter");
            });

            Probe(f, "reflected-power", "Reflected power", () =>
            {
                if (refMeter == null)
                    return DiagnosticFact.Absent("reflected-power", "Reflected power",
                        "the radio is not currently publishing a reflected power meter, so what is "
                        + "coming back from the antenna cannot be read here", "the radio");
                if (!refMeter.HasReading)
                    return DiagnosticFact.Silent("reflected-power", "Reflected power",
                        "the radio's reflected power meter has not reported yet; it reports while "
                        + "transmitting, so transmit to measure",
                        "the radio's REFPWR meter");
                if (xvtrBelowMeterFloor)
                    return DiagnosticFact.Absent("reflected-power", "Reflected power",
                        XvtrFloorReason, "the radio's REFPWR meter");
                return DiagnosticFact.Measure("reflected-power", "Reflected power",
                        rig.ReflectedPowerWatts, "watts", "the radio's REFPWR meter");
            });

            // The fact that actually caught the open antenna port on 2026-08-22,
            // and the reason it is here rather than leaving SWR to do the job
            // alone: a fraction cannot blow up. SWR runs to infinity as the match
            // worsens and gets numerically unstable near the end of its range,
            // while "how much of it came back" stays a plain percentage from 0 to
            // 100 whatever happens. Into the dummy load it was 0.05 percent; into
            // an empty connector on the same radio it was 76.
            Probe(f, "reflected-percent", "Power coming back", () =>
            {
                if (fwdMeter == null || refMeter == null)
                    return DiagnosticFact.Absent("reflected-percent", "Power coming back",
                        "this needs both the forward and the reflected power meter, and the radio is "
                        + "not publishing both", "the radio");
                if (!fwdMeter.HasReading || !refMeter.HasReading)
                    return DiagnosticFact.Silent("reflected-percent", "Power coming back",
                        "the forward and reflected power meters report while transmitting, so "
                        + "transmit to measure",
                        "the radio's FWDPWR and REFPWR meters");
                if (xvtrBelowMeterFloor)
                    return DiagnosticFact.Absent("reflected-percent", "Power coming back",
                        XvtrFloorReason, "the radio's FWDPWR and REFPWR meters");
                float fraction = rig.ReflectedFraction;
                if (float.IsNaN(fraction))
                    return DiagnosticFact.Silent("reflected-percent", "Power coming back",
                        "there is too little forward power to work out what fraction of it is "
                        + "coming back",
                        "the radio's FWDPWR and REFPWR meters");
                return DiagnosticFact.Measure("reflected-percent", "Power coming back",
                        fraction * 100f, "percent", "the radio's FWDPWR and REFPWR meters");
            });

            // -- Why this fact no longer reports what the SWR meter says --
            //
            // On 2026-08-22 the bench 8600 transmitted into an EMPTY ANT1
            // connector with the dummy load sitting on ANT2. 76 percent of the
            // power came straight back -- 13.4 W reflected of 17.5 W forward, the
            // radio folding itself back hard to survive it -- and the radio's own
            // SWR meter reported 1.008. Two full sessions of measurements were
            // taken through that reassuring number before anyone noticed the load
            // was never getting warm.
            //
            // A safety reading that is correct when things are fine and wrong
            // when they are not is worse than no reading, because it is only ever
            // consulted in the second case. So compute it from the two numbers
            // the radio does report honestly, and fall back to the meter only
            // when the arithmetic cannot be done.
            //
            // The sentinel matters as much as the arithmetic. The same radio, in
            // the same session, published -25 mid-transmission to mean "no
            // reading". HasReading is TRUE for that value -- the meter did
            // report, it just reported a non-answer -- and -25 is not "above 3",
            // so the high-swr rule read a screaming mismatch as a healthy stage
            // and said nothing. Treat the sentinel as silence, which is what it
            // means. Anything at or below 1 is physically impossible, so the test
            // catches the sentinel without hard-coding its exact value.
            Probe(f, "swr", "Standing wave ratio", () =>
            {
                // The transverter gate goes ABOVE the computed value on
                // purpose. ComputedSWR is worked out from the same two meters,
                // so on a transverter path it would produce a confident ratio
                // out of two non-readings — which is the 1.008-into-an-open-
                // connector failure again, arrived at by arithmetic instead of
                // by a bad meter.
                if (xvtrBelowMeterFloor)
                    return DiagnosticFact.Absent("swr", "Standing wave ratio",
                        XvtrFloorReason, "the radio's FWDPWR and REFPWR meters");

                float computed = rig.ComputedSWR;
                if (!float.IsNaN(computed))
                    return DiagnosticFact.Measure("swr", "Standing wave ratio",
                            computed, "to 1",
                            "worked out from the radio's forward and reflected power meters");

                if (swrMeter == null)
                    return DiagnosticFact.Absent("swr", "Standing wave ratio",
                        "the radio is not currently publishing a standing wave ratio meter, and "
                        + "there is not enough power reported to work one out", "the radio");
                if (!swrMeter.HasReading)
                    return DiagnosticFact.Silent("swr", "Standing wave ratio",
                        "the radio's standing wave ratio meter has not reported yet; it reports "
                        + "while transmitting, so transmit to measure",
                        "the radio's SWR meter");
                if (rig.SWRValue <= 1f)
                    return DiagnosticFact.Silent("swr", "Standing wave ratio",
                        "the radio's standing wave ratio meter is reporting its no-reading value "
                        + "rather than a measurement, and there is not enough power reported to "
                        + "work one out instead",
                        "the radio's SWR meter");
                return DiagnosticFact.Measure("swr", "Standing wave ratio",
                        rig.SWRValue, "to 1", "the radio's SWR meter");
            });
            // The unit comes from TxPowerPhrasing and nowhere else (#444). This
            // line said "percent" while the stage sentence beside it in the same
            // report said "at 10 watts into ANT1", off ONE reading of ONE
            // property. Whether watts or percent is the true unit is a bench
            // question and is still open; what is settled is that this document
            // may not answer it two ways.
            Probe(f, "rf-power-setting", "Transmit power setting",
                  () => DiagnosticFact.Measure("rf-power-setting", "Transmit power setting",
                                               rig.XmitPower, TxPowerPhrasing.SettingUnits,
                                               "the radio"));
            // Stands the power and standing-wave rules down while the tuner is
            // working. A tune cycle transmits into a deliberately bad match and
            // walks toward a good one, so high reflected power during one is the
            // tuner doing its job, not a fault. Without this fact, every tune-up
            // would report a broken antenna.
            Probe(f, "atu-tuning", "The antenna tuner is running a tune cycle",
                  () => DiagnosticFact.Flag("atu-tuning", "The antenna tuner is running a tune cycle",
                                            rig.ATUTuneInProgress, "the radio"));
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
            // WHETHER THIS MODE MAKES ITS POWER OUT OF AUDIO (#437).
            //
            // In a voice mode a radio with nothing arriving at its microphone
            // has nothing to modulate, so zero forward power is the CORRECT
            // behaviour and reporting it as a second, independent fault sends an
            // operator to check a transmitter that is working exactly as
            // designed. In CW there is no transmit audio path at all and the
            // same reasoning is simply false.
            //
            // The list is NOT restated here. TransmitStageSet.TransmitAudioModes
            // is the one place this project says which modes have a real
            // transmit-audio path — ruled by Noel 2026-08-30 — and a second copy
            // of it would be exactly the duplication that produces two
            // vocabularies for one idea. An unread mode stays ABSENT rather than
            // guessing "no": the rule that reads this then reports as a check
            // that could not be made, which is the honest answer.
            Probe(f, "tx-audio-mode", "This transmit mode carries audio", () =>
            {
                string mode = rig.TXMode ?? "";
                if (mode.Length == 0)
                {
                    return DiagnosticFact.Absent("tx-audio-mode", "This transmit mode carries audio",
                        "the transmit slice has not reported its mode to this computer yet, so there "
                        + "is no way to tell whether power here comes from audio", "the radio");
                }
                return DiagnosticFact.Flag("tx-audio-mode", "This transmit mode carries audio",
                    Fixer.TransmitStageSet.IsTransmitAudioMode(mode), "the radio");
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
            yield return "tx-stream-open";
            yield return "tx-stream-compression";
            yield return "tx-stream-is-opus";
            yield return "tx-stream-status-line";
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
            yield return "tx-antenna";
            yield return "rx-antenna";
            yield return "transverter-path";
            yield return "transverter-name";
            yield return "transverter-drive";
            yield return "transverter-drive-ceiling";
            yield return "forward-power";
            yield return "reflected-power";
            yield return "reflected-percent";
            yield return "swr";
            yield return "rf-power-setting";
            yield return "atu-tuning";
            yield return "dummy-load";
            yield return "ptt-source";
            yield return "ptt-hardware";
            yield return "tx-slice";
            yield return "tx-mode";
            yield return "tx-audio-mode";
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
                case "tx-stream-open": return "A transmit audio stream is open";
                case "tx-stream-compression": return "How the radio opened our transmit stream";
                case "tx-stream-is-opus": return "The radio opened our transmit stream as Opus";
                case "tx-stream-status-line": return "The radio's transmit stream status line";
                case "sc-mic-peak": return "Loudest transmit audio the radio has heard";
                case "tx-antenna": return "Transmit antenna port";
                case "rx-antenna": return "Receive antenna port";
                case "transverter-path": return "Transmitting through a transverter";
                case "transverter-name": return "Transverter in use";
                case "transverter-drive": return "Transverter drive";
                case "transverter-drive-ceiling": return "Highest drive this radio will accept";
                case "forward-power": return "Forward power";
                case "reflected-power": return "Reflected power";
                case "reflected-percent": return "Power coming back";
                case "swr": return "Standing wave ratio";
                case "atu-tuning": return "The antenna tuner is running a tune cycle";
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
            // #188. The port belongs with the radio's identity, not buried in
            // the readings, because a power or standing-wave figure quoted
            // without it cannot be interpreted by anybody — including a
            // FlexRadio engineer reading the pasted block. Both directions:
            // switching the receive port and hearing no change is a common
            // first test, and it is uninterpretable unless the port is recorded.
            // Empty reads as "not reported", which is what an operator with no
            // active slice should see: both properties answer "ANT1" in that
            // state, and printing a default beside real measurements is how a
            // capture acquires a port it never had.
            Line("Transmit antenna", () => rig.HasActiveSlice ? rig.TXAntennaName : "");
            Line("Receive antenna", () => rig.HasActiveSlice ? rig.RXAntennaName : "");
            Line("Connection", () => rig.RemoteRig ? "SmartLink (over the internet)" : "local network");
            Line("Meters published", () => (rig.MeterInventory?.Count ?? 0)
                                           .ToString(CultureInfo.CurrentCulture));
            // Which copy of each transmit meter the readings above came from,
            // and what every other copy did. On a radio with several copies
            // this is the line that says whether "the radio hears nothing" was
            // measured or merely never connected (#502).
            Line("Transmit mic meter", () => rig.ScMicElectionText);
            Line("Transmit drive meter", () => rig.SwAlcElectionText);
            return lines;
        }
    }
}
