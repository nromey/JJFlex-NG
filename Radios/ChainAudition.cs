using System;
using System.Collections.Generic;
using System.Globalization;
using JJPortaudio;

namespace Radios
{
    /// <summary>The three ways the one audition take is played (#262).</summary>
    public enum AuditionRender
    {
        /// <summary>The take as the transmit chain would send it, level-matched to the raw take on speech loudness.</summary>
        Cleaned,
        /// <summary>The take as the microphone heard it.</summary>
        Raw,
        /// <summary>What the chain took out: the raw take less the cleaned one, before level matching.</summary>
        Removed,
    }

    /// <summary>What the analysis found in the take before it looked at the chain.</summary>
    public enum AuditionSpeech
    {
        /// <summary>Speech stands clear of the room's noise, so the figures mean something.</summary>
        Heard,
        /// <summary>Too few blocks for the loudness meter's noise floor (<see cref="LufsMeter.MinProfileBlocks"/>), decided by the block count alone.</summary>
        TooShort,
        /// <summary>
        /// Long enough, but nothing in it stands clear of the noise: no figure is
        /// reported. This includes a take with no usable signal at all (silence,
        /// or everything under the meter's absolute gate), whose profile is
        /// invalid for a reason recording longer would not fix.
        /// </summary>
        NoSpeech,
    }

    /// <summary>
    /// Whether the raw and cleaned takes, once lined up, could be compared. Kept
    /// apart from <see cref="AuditionSpeech"/>, which is about the original take:
    /// speech can be heard in the take and the comparison still be impossible.
    /// </summary>
    public enum AuditionComparison
    {
        /// <summary>Not attempted: no speech in the take, or no output from the chain.</summary>
        NotAttempted,
        /// <summary>Both lined-up takes measured: the speech-to-noise figures and the change are reported.</summary>
        Valid,
        /// <summary>Lining the output up left too few blocks for a noise floor on one side or both.</summary>
        TooShortOnceAligned,
        /// <summary>The lined-up stretch of the raw take holds no speech clear of the noise.</summary>
        NoSpeechOnceAligned,
        /// <summary>The cleaned take held nothing the meter could measure: the chain silenced it.</summary>
        NothingCameOut,
    }

    /// <summary>The gate failures #262 says the verdict must name. Several can be found in one take.</summary>
    [Flags]
    public enum AuditionGateFault
    {
        None = 0,
        /// <summary>The speech itself comes out cut by about the gate's depth: the threshold is too high or the margin too large.</summary>
        NeverOpens = 1,
        /// <summary>The noise between words is not reduced: the floor was mis-measured, or the threshold sits under it.</summary>
        NeverCloses = 2,
        /// <summary>The gate shuts and reopens inside words: the hold is too short for this speaker.</summary>
        Chatters = 4,
        /// <summary>The start of each word comes out cut: the gate is opening late.</summary>
        ClipsFirstSyllable = 8,
    }

    /// <summary>
    /// What a chain made of the take, as a processor hands it back: either the
    /// processed samples with what produced them, or why there are none.
    /// </summary>
    public sealed class ChainProcessing
    {
        private ChainProcessing() { }

        /// <summary>True when <see cref="Processed"/> holds the chain's output.</summary>
        public bool Available { get; private init; }

        /// <summary>The chain's output, mono, at the take's rate, the same length as what it was given.</summary>
        public float[] Processed { get; private init; } = Array.Empty<float>();

        /// <summary>How many samples late the output runs behind its input (the chain's applied latency), so the two can be lined up.</summary>
        public int LatencySamples { get; private init; }

        /// <summary>Whether the chain's gate was on. The gate's failures are named only when it was.</summary>
        public bool GateEnabled { get; private init; }

        /// <summary>The gate threshold that produced the output, in dB, when the chain knows it.</summary>
        public float? ThresholdDb { get; private init; }

        /// <summary>The lexicon key that says why there is no output; empty when there is.</summary>
        public string UnavailableKey { get; private init; } = "";

        /// <summary>The chain ran.</summary>
        public static ChainProcessing Rendered(float[] processed, int latencySamples, bool gateEnabled, float? thresholdDb) => new()
        {
            Available = true,
            Processed = processed ?? throw new ArgumentNullException(nameof(processed)),
            LatencySamples = Math.Max(0, latencySamples),
            GateEnabled = gateEnabled,
            ThresholdDb = thresholdDb,
        };

        /// <summary>The chain could not run; <paramref name="reasonKey"/> says why, in the lexicon.</summary>
        public static ChainProcessing Unavailable(string reasonKey) => new()
        {
            Available = false,
            UnavailableKey = reasonKey ?? "",
        };
    }

    /// <summary>
    /// Runs the take through a transmit chain, offline, and hands back what came
    /// out. Phase B's offline twin of the configured chain is one of these; in
    /// phase A the window is wired to <see cref="ChainNotConnectedYet"/>, and
    /// tests use fakes.
    /// </summary>
    /// <remarks>
    /// A processor is given its own copy of the take and must never touch the
    /// live chain: the audition reports and lets the operator listen, and it
    /// changes no transmit setting (#262, "NEVER AUTO-APPLY").
    /// </remarks>
    public interface IChainAuditionProcessor
    {
        /// <summary>Process <paramref name="rawMono"/>, a copy the processor may keep or change.</summary>
        ChainProcessing Process(float[] rawMono, int sampleRate);
    }

    /// <summary>
    /// Phase A's wiring: the offline chain is not built yet, so every take comes
    /// back with only its raw render and a sentence saying so. Phase B replaces
    /// this one wiring with the offline twin of the configured chain.
    /// </summary>
    public sealed class ChainNotConnectedYet : IChainAuditionProcessor
    {
        /// <summary>The placeholder sentence the verdict carries.</summary>
        public const string ReasonKey = "audio.audition.verdict.chain_not_connected";

        public static readonly ChainNotConnectedYet Instance = new();

        private ChainNotConnectedYet() { }

        public ChainProcessing Process(float[] rawMono, int sampleRate) => ChainProcessing.Unavailable(ReasonKey);
    }

    /// <summary>What the audition found, and the three renders to play.</summary>
    public sealed class ChainAuditionResult
    {
        internal ChainAuditionResult() { }

        public int SampleRate { get; internal init; }

        /// <summary>Length of the take as recorded, in seconds.</summary>
        public double Seconds { get; internal init; }

        public AuditionSpeech Speech { get; internal init; }

        public bool ChainAvailable { get; internal init; }

        /// <summary>Why the chain did not run, as a lexicon key; empty when it did.</summary>
        public string ChainUnavailableKey { get; internal init; } = "";

        /// <summary>
        /// The raw take's figures: from the lined-up stretch when the comparison
        /// is <see cref="AuditionComparison.Valid"/> or
        /// <see cref="AuditionComparison.NothingCameOut"/>, otherwise from the
        /// whole take. Null when the profile they would come from is invalid,
        /// never a sentinel.
        /// </summary>
        public float? RawSpeechLufs { get; internal init; }
        public float? RawFloorLufs { get; internal init; }
        public float? RawSpeechToNoiseLu { get; internal init; }

        /// <summary>Whether the lined-up raw and cleaned takes could be compared.</summary>
        public AuditionComparison Comparison { get; internal init; }

        /// <summary>Length of the lined-up stretch that was compared, in seconds; null when the chain did not run on heard speech.</summary>
        public double? ComparedSeconds { get; internal init; }

        /// <summary>The cleaned take's figures, measured after lining it up with the raw one and before level matching. Null unless the comparison is valid.</summary>
        public float? CleanedSpeechLufs { get; internal init; }
        public float? CleanedFloorLufs { get; internal init; }
        public float? CleanedSpeechToNoiseLu { get; internal init; }

        /// <summary>Cleaned speech-to-noise less raw speech-to-noise, in LU: #262's figure of merit. Null unless the comparison is valid.</summary>
        public float? SpeechToNoiseChangeLu { get; internal init; }

        /// <summary>The gate threshold that produced the cleaned take, when the chain said.</summary>
        public float? ThresholdDb { get; internal init; }

        public bool GateEnabled { get; internal init; }

        public AuditionGateFault Faults { get; internal init; }

        /// <summary>The gain the cleaned render was given so its speech is as loud as the raw take's.</summary>
        public float MatchGainDb { get; internal init; }

        /// <summary>False when the cleaned take's speech was too far from the raw take's to match honestly (more than <see cref="ChainAudition.MaxMatchGainDb"/>).</summary>
        public bool LevelMatched { get; internal init; }

        /// <summary>The one scale all three renders share, so none clips and the match survives.</summary>
        public float PlaybackScale { get; internal init; } = 1f;

        internal float[]? RawRender { get; init; }
        internal float[]? CleanedRender { get; init; }
        internal float[]? RemovedRender { get; init; }

        /// <summary>The renders there are, in the order they are offered: cleaned first, since it is what goes out (Noel, 2026-11-19 07:03).</summary>
        public IReadOnlyList<AuditionRender> Playable
        {
            get
            {
                var list = new List<AuditionRender>(3);
                foreach (var r in ChainAudition.PlayOrder)
                    if (Render(r) != null) list.Add(r);
                return list;
            }
        }

        /// <summary>One render's samples, mono at <see cref="SampleRate"/>, or null when there is none.</summary>
        public float[]? Render(AuditionRender render) => render switch
        {
            AuditionRender.Cleaned => CleanedRender,
            AuditionRender.Raw => RawRender,
            AuditionRender.Removed => RemovedRender,
            _ => null,
        };
    }

    /// <summary>
    /// One take, processed twice (#262): the pure half of the audition. Given the
    /// raw take and what a chain made of it, line them up, level-match them on
    /// speech loudness, render raw, cleaned and removed, report the change in
    /// speech-to-noise and name the gate's failures. No window, no device, no
    /// radio, so every case is testable with synthetic samples.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One take, never two.</b> Speaking twice compares two performances, and
    /// the speaker's own variation swamps the effect. The processor is handed a
    /// copy of the same samples the raw render plays.
    /// </para>
    /// <para>
    /// <b>Level-matched on <see cref="LufsMeter.LoudnessProfile.SpeechLufs"/>, or
    /// the tool flatters itself.</b> A gate drops the floor, so a cleaned take is
    /// quieter between words, and in a comparison the quieter one wins on
    /// impression whether or not the speech survived. The cleaned render is
    /// raised or lowered until its speech is as loud as the raw take's, so the
    /// noise is the only audible difference. The removed render is the literal
    /// difference, the way the conditioner's own Residual monitor computes it
    /// (<c>TxAudioConditioner</c>, input less output), so it carries anything
    /// the chain took from the speech too.
    /// </para>
    /// <para>
    /// <b>Where the take holds no speech, it says so instead of reporting a
    /// figure.</b> Matching on speech loudness when there is no speech would
    /// match the noise, undoing exactly what is being auditioned.
    /// </para>
    /// <para>
    /// The thresholds below that decide "speech", "cut" and "closed" are
    /// Claude's judgement (2026-11-19), chosen against the gate's own defaults
    /// (25 dB depth, 150 ms hold, 3 ms attack) and shown to separate the failure
    /// modes in <c>ChainAuditionAnalysisTests</c>. They are not measured on a
    /// real voice yet; that is the bench's question after phase B.
    /// </para>
    /// </remarks>
    public static class ChainAudition
    {
        /// <summary>The order the renders are offered in: cleaned first (ruled 2026-11-19 07:03), then raw beside it, then what was removed.</summary>
        public static readonly IReadOnlyList<AuditionRender> PlayOrder = new[]
        {
            AuditionRender.Cleaned, AuditionRender.Raw, AuditionRender.Removed,
        };

        /// <summary>Speech must stand this far above the noise floor, in LU, to count as speech.</summary>
        public const float MinSpeechToNoiseLu = 6f;

        /// <summary>A level match larger than this either way is refused rather than applied: the cleaned take is played as it came out.</summary>
        public const float MaxMatchGainDb = 30f;

        /// <summary>Speech cut by this much or more, in dB, reads as a gate that never opened.</summary>
        public const float NeverOpensDb = -12f;

        /// <summary>The noise between words reduced by less than this, relative to the speech, reads as a gate that never closed.</summary>
        public const float NeverClosesDb = -3f;

        /// <summary>A frame this far below the speech, relative, is closed.</summary>
        public const float ClosedDb = -10f;

        /// <summary>A closed frame reopens once it is back within this of the speech.</summary>
        public const float ReopenDb = -3f;

        /// <summary>A word's first 40 ms this much below the rest of it reads as a clipped first syllable.</summary>
        public const float ClippedOnsetDb = -6f;

        /// <summary>Closures inside words, per second of speech, at or above which the gate chatters.</summary>
        public const double ChatterPerSecond = 2.0;

        /// <summary>The frame the gate analysis works in, in milliseconds.</summary>
        public const int FrameMs = 10;

        // Speech runs: frames above the midpoint between floor and loud, with
        // dips of up to 50 ms bridged, and 80 ms the shortest run kept.
        private const int BridgeFrames = 5;
        private const int MinRunFrames = 8;
        // The onset window and where the body begins.
        private const int OnsetFrames = 4;
        private const int BodyFromFrame = 6;
        private const int MinOnsetRunFrames = 15;
        // A gap frame this far from any speech run is past any hold or release.
        private const int DeepGapFrames = 15;
        private const int MinDeepGapFrames = 20;
        // Headroom the shared playback scale leaves: -1 dBFS.
        private const float PeakCeiling = 0.891f;

        /// <summary>
        /// Analyse one take. <paramref name="raw"/> is mono at
        /// <paramref name="sampleRate"/>; it is not changed. The processor gets a
        /// copy.
        /// </summary>
        public static ChainAuditionResult Analyse(float[] raw, int sampleRate, IChainAuditionProcessor processor)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (processor == null) throw new ArgumentNullException(nameof(processor));
            if (sampleRate <= 0) sampleRate = 48000;
            double seconds = (double)raw.Length / sampleRate;

            var rawProfile = Measure(raw, raw.Length, sampleRate);
            var speech = ClassifySpeech(rawProfile);

            var processing = processor.Process((float[])raw.Clone(), sampleRate)
                ?? ChainProcessing.Unavailable(ChainNotConnectedYet.ReasonKey);
            bool chain = processing.Available && processing.Processed.Length > 0;

            // Without speech, or without a chain, there is nothing to match:
            // the raw take alone is offered, as recorded.
            if (speech != AuditionSpeech.Heard || !chain)
            {
                float scaleRaw = ScaleFor(Peak(raw, raw.Length));
                return new ChainAuditionResult
                {
                    SampleRate = sampleRate,
                    Seconds = seconds,
                    Speech = speech,
                    ChainAvailable = chain,
                    ChainUnavailableKey = chain ? "" : processing.UnavailableKey,
                    Comparison = AuditionComparison.NotAttempted,
                    RawSpeechLufs = Figure(rawProfile, rawProfile.SpeechLufs),
                    RawFloorLufs = Figure(rawProfile, rawProfile.NoiseFloorLufs),
                    RawSpeechToNoiseLu = Figure(rawProfile, rawProfile.SpeechToNoiseLu),
                    ThresholdDb = chain ? processing.ThresholdDb : null,
                    GateEnabled = chain && processing.GateEnabled,
                    PlaybackScale = scaleRaw,
                    RawRender = Scaled(raw, raw.Length, 1f, scaleRaw),
                };
            }

            // Line the output up with its input: output sample i + L is input i.
            int latency = Math.Min(processing.LatencySamples, raw.Length);
            int length = Math.Max(0, Math.Min(raw.Length, processing.Processed.Length - latency));
            var aligned = new float[length];
            Array.Copy(processing.Processed, latency, aligned, 0, length);

            var rawPart = Measure(raw, length, sampleRate);
            var cleanedProfile = Measure(aligned, length, sampleRate);
            var comparison = Compare(rawPart, cleanedProfile);
            bool valid = comparison == AuditionComparison.Valid;

            float match = rawPart.SpeechLufs - cleanedProfile.SpeechLufs;
            bool matched = valid && Math.Abs(match) <= MaxMatchGainDb;
            float gainDb = matched ? match : 0f;
            float gain = (float)Math.Pow(10, gainDb / 20.0);

            var removed = new float[length];
            for (int i = 0; i < length; i++) removed[i] = raw[i] - aligned[i];

            float peak = Math.Max(Peak(raw, length), Math.Max(Peak(aligned, length) * gain, Peak(removed, length)));
            float scale = ScaleFor(peak);

            // The gate's failures are named only from evidence that measured.
            // A valid comparison judges every frame relative to the speech's own
            // level change (how much the chain did to the voice itself, before
            // any matching). A chain that silenced heard speech entirely has cut
            // it by more than any gate's depth, and that is all a silent output
            // can show: it carries no evidence of chatter or a late opening.
            // A comparison too short, or with no speech in its stretch, rests
            // on no measurement at all, so it names nothing.
            AuditionGateFault faults = AuditionGateFault.None;
            if (processing.GateEnabled && valid)
                faults = FindGateFaults(raw, aligned, length, sampleRate, cleanedProfile.SpeechLufs - rawPart.SpeechLufs);
            else if (processing.GateEnabled && comparison == AuditionComparison.NothingCameOut)
                faults = AuditionGateFault.NeverOpens;

            // The raw figures come from the stretch that was compared when that
            // stretch measured, and from the whole take, which heard speech,
            // when it did not. The cleaned figures and the change exist only
            // for a valid comparison: no slot is filled with a sentinel or a zero.
            bool rawPartHeard = ClassifySpeech(rawPart) == AuditionSpeech.Heard;
            var rawSource = rawPartHeard ? rawPart : rawProfile;

            return new ChainAuditionResult
            {
                SampleRate = sampleRate,
                Seconds = seconds,
                Speech = speech,
                ChainAvailable = true,
                Comparison = comparison,
                ComparedSeconds = (double)length / sampleRate,
                RawSpeechLufs = Figure(rawSource, rawSource.SpeechLufs),
                RawFloorLufs = Figure(rawSource, rawSource.NoiseFloorLufs),
                RawSpeechToNoiseLu = Figure(rawSource, rawSource.SpeechToNoiseLu),
                CleanedSpeechLufs = valid ? cleanedProfile.SpeechLufs : null,
                CleanedFloorLufs = valid ? cleanedProfile.NoiseFloorLufs : null,
                CleanedSpeechToNoiseLu = valid ? cleanedProfile.SpeechToNoiseLu : null,
                SpeechToNoiseChangeLu = valid ? cleanedProfile.SpeechToNoiseLu - rawPart.SpeechToNoiseLu : null,
                ThresholdDb = processing.ThresholdDb,
                GateEnabled = processing.GateEnabled,
                Faults = faults,
                MatchGainDb = gainDb,
                LevelMatched = matched,
                PlaybackScale = scale,
                RawRender = Scaled(raw, length, 1f, scale),
                CleanedRender = Scaled(aligned, length, gain, scale),
                RemovedRender = Scaled(removed, length, 1f, scale),
            };
        }

        /// <summary>
        /// The loudness meter's profile of the first <paramref name="count"/>
        /// mono samples: the same meter, and the same figures, the microphone
        /// check and the gate's derived threshold use. Fed as the transmit stream
        /// carries the microphone, duplicated onto both channels, 10 ms at a time.
        /// </summary>
        public static LufsMeter.LoudnessProfile Measure(float[] mono, int count, int sampleRate)
        {
            var meter = new LufsMeter();
            int frame = Math.Max(1, sampleRate * FrameMs / 1000);
            var stereo = new float[frame * 2];
            for (int at = 0; at < count; at += frame)
            {
                int n = Math.Min(frame, count - at);
                for (int i = 0; i < n; i++)
                {
                    float s = mono[at + i];
                    stereo[2 * i] = s;
                    stereo[2 * i + 1] = s;
                }
                meter.Process(stereo, n * 2, (uint)sampleRate);
            }
            return meter.Profile;
        }

        /// <summary>
        /// What a profile says about speech. <see cref="LufsMeter.LoudnessProfile.IsValid"/>
        /// is false for two different reasons, and only one is shortness: too
        /// few blocks, or enough blocks with no signal over the meter's absolute
        /// gate (silence, a muted microphone, a level far too low). The second
        /// is no speech, since a longer take of it would not help.
        /// </summary>
        public static AuditionSpeech ClassifySpeech(LufsMeter.LoudnessProfile profile) =>
            profile.BlockCount < LufsMeter.MinProfileBlocks ? AuditionSpeech.TooShort
            : !profile.IsValid ? AuditionSpeech.NoSpeech
            : profile.SpeechToNoiseLu >= MinSpeechToNoiseLu ? AuditionSpeech.Heard
            : AuditionSpeech.NoSpeech;

        /// <summary>
        /// Whether the lined-up raw stretch and the cleaned take can be compared,
        /// re-checked after trimming for latency, since the trim can take a take
        /// that measured below the meter's minimum.
        /// </summary>
        public static AuditionComparison Compare(LufsMeter.LoudnessProfile rawPart, LufsMeter.LoudnessProfile cleaned)
        {
            if (rawPart.BlockCount < LufsMeter.MinProfileBlocks || cleaned.BlockCount < LufsMeter.MinProfileBlocks)
                return AuditionComparison.TooShortOnceAligned;
            if (ClassifySpeech(rawPart) != AuditionSpeech.Heard) return AuditionComparison.NoSpeechOnceAligned;
            if (!cleaned.IsValid) return AuditionComparison.NothingCameOut;
            return AuditionComparison.Valid;
        }

        private static float? Figure(LufsMeter.LoudnessProfile profile, float value) => profile.IsValid ? value : null;

        /// <summary>
        /// Name the gate's failures (#262): never opens, never closes, chatters,
        /// clips the first syllable. Works in 10 ms frames on the raw take and the
        /// lined-up cleaned take. <paramref name="speechChangeDb"/> is the cleaned
        /// speech's loudness less the raw speech's; every frame is judged relative
        /// to it, so a chain that lowers the whole voice a little is not mistaken
        /// for a gate closing.
        /// </summary>
        public static AuditionGateFault FindGateFaults(float[] raw, float[] cleaned, int count, int sampleRate,
            float speechChangeDb)
        {
            var faults = AuditionGateFault.None;
            if (speechChangeDb <= NeverOpensDb) faults |= AuditionGateFault.NeverOpens;

            int frame = Math.Max(1, sampleRate * FrameMs / 1000);
            int n = Math.Min(count, Math.Min(raw.Length, cleaned.Length)) / frame;
            if (n < 2 * MinOnsetRunFrames) return faults;

            var rawDb = new double[n];
            var rel = new double[n];
            for (int f = 0; f < n; f++)
            {
                rawDb[f] = FrameDb(raw, f * frame, frame);
                rel[f] = FrameDb(cleaned, f * frame, frame) - rawDb[f] - speechChangeDb;
            }

            var sorted = (double[])rawDb.Clone();
            Array.Sort(sorted);
            double floor = Percentile(sorted, 0.10);
            double loud = Percentile(sorted, 0.95);
            if (loud - floor < MinSpeechToNoiseLu) return faults;
            double mid = floor + (loud - floor) / 2;

            var runs = SpeechRuns(rawDb, mid);
            if (runs.Count == 0) return faults;

            // Never closes: the gaps well away from any word are hardly reduced.
            // Not asked when the speech itself was cut: a gate that never opened
            // reduces everything alike, which reads here as nothing reduced.
            if ((faults & AuditionGateFault.NeverOpens) == 0)
            {
                var near = new bool[n];
                foreach (var (start, end) in runs)
                    for (int f = Math.Max(0, start - DeepGapFrames); f < Math.Min(n, end + DeepGapFrames); f++) near[f] = true;
                var gaps = new List<double>();
                for (int f = 0; f < n; f++) if (!near[f]) gaps.Add(rel[f]);
                if (gaps.Count >= MinDeepGapFrames && Median(gaps) >= NeverClosesDb)
                    faults |= AuditionGateFault.NeverCloses;
            }

            int eligible = 0, clipped = 0, closures = 0, bodyFrames = 0;
            foreach (var (start, end) in runs)
            {
                if (end - start < MinOnsetRunFrames) continue;
                eligible++;
                double onset = 0;
                for (int f = start; f < start + OnsetFrames; f++) onset += rel[f];
                onset /= OnsetFrames;
                var body = new List<double>(end - start);
                for (int f = start + BodyFromFrame; f < end; f++) body.Add(rel[f]);
                if (onset <= Median(body) + ClippedOnsetDb) clipped++;

                bool open = true;
                foreach (double r in body)
                {
                    if (open && r <= ClosedDb) { closures++; open = false; }
                    else if (!open && r >= ReopenDb) open = true;
                }
                bodyFrames += body.Count;
            }
            if (eligible > 0 && clipped * 2 >= eligible) faults |= AuditionGateFault.ClipsFirstSyllable;
            double bodySeconds = bodyFrames * FrameMs / 1000.0;
            if (closures >= 3 && bodySeconds > 0 && closures / bodySeconds >= ChatterPerSecond)
                faults |= AuditionGateFault.Chatters;
            return faults;
        }

        /// <summary>
        /// The verdict, in the lexicon's words: what was heard, the change in
        /// speech-to-noise with the threshold that produced it, each gate failure
        /// found, and, while the chain is not connected, the sentence that says
        /// so. Never a figure when no speech was heard.
        /// </summary>
        // WORDING PLACEHOLDERS (#262): every key below is Codex's to write.
        public static string Verdict(ChainAuditionResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var parts = new List<string>();
            switch (result.Speech)
            {
                case AuditionSpeech.TooShort:
                    parts.Add(TooShortSentence(result.Seconds));
                    break;
                case AuditionSpeech.NoSpeech:
                    parts.Add(Lexicon.Get("audio.audition.verdict.no_speech"));
                    break;
                default:
                    parts.Add(Lexicon.Get("audio.audition.verdict.heard",
                        ("speech", Measured(result.RawSpeechLufs)), ("floor", Measured(result.RawFloorLufs)),
                        ("ratio", Measured(result.RawSpeechToNoiseLu))));
                    break;
            }

            if (!result.ChainAvailable)
            {
                if (result.ChainUnavailableKey.Length > 0) parts.Add(Lexicon.Get(result.ChainUnavailableKey));
                return string.Join(" ", parts);
            }
            if (result.Speech != AuditionSpeech.Heard) return string.Join(" ", parts);

            // The change only from a comparison that measured; otherwise why not.
            switch (result.Comparison)
            {
                case AuditionComparison.Valid:
                    parts.Add(Lexicon.Get("audio.audition.verdict.change",
                        ("change", Measured(result.SpeechToNoiseChangeLu)),
                        ("before", Measured(result.RawSpeechToNoiseLu)), ("after", Measured(result.CleanedSpeechToNoiseLu))));
                    break;
                case AuditionComparison.TooShortOnceAligned:
                    parts.Add(Lexicon.Get("audio.audition.verdict.no_comparison_too_short",
                        ("length", Number(result.ComparedSeconds ?? 0))));
                    break;
                case AuditionComparison.NoSpeechOnceAligned:
                    parts.Add(Lexicon.Get("audio.audition.verdict.no_comparison_no_speech"));
                    break;
                case AuditionComparison.NothingCameOut:
                    parts.Add(Lexicon.Get("audio.audition.verdict.no_comparison_nothing_came_out"));
                    break;
            }
            if (!result.GateEnabled) parts.Add(Lexicon.Get("audio.audition.verdict.gate_off"));
            else if (result.ThresholdDb is float threshold)
                parts.Add(Lexicon.Get("audio.audition.verdict.threshold", ("threshold", Number(threshold))));
            else parts.Add(Lexicon.Get("audio.audition.verdict.threshold_unknown"));

            foreach (var (fault, key) in FaultKeys)
                if ((result.Faults & fault) != 0) parts.Add(Lexicon.Get(key));
            if (!result.LevelMatched) parts.Add(Lexicon.Get("audio.audition.verdict.not_level_matched"));
            return string.Join(" ", parts);
        }

        /// <summary>Each gate failure's sentence, in the order they are said.</summary>
        public static readonly IReadOnlyList<(AuditionGateFault Fault, string Key)> FaultKeys = new[]
        {
            (AuditionGateFault.NeverOpens, "audio.audition.fault.never_opens"),
            (AuditionGateFault.NeverCloses, "audio.audition.fault.never_closes"),
            (AuditionGateFault.Chatters, "audio.audition.fault.chatters"),
            (AuditionGateFault.ClipsFirstSyllable, "audio.audition.fault.clips_first_syllable"),
        };

        /// <summary>
        /// One line for the trace: what was heard and the figures, never a path
        /// and never a sample (the take is the operator's voice).
        /// </summary>
        public static string TraceLine(ChainAuditionResult r) =>
            "Check your audio audition: " + Number(r.Seconds) + " s, speech " + r.Speech
            + ", raw " + Optional(r.RawSpeechLufs) + "/" + Optional(r.RawFloorLufs) + " LUFS"
            + (r.ChainAvailable
                ? ", comparison " + r.Comparison
                  + (r.ComparedSeconds is double c ? " over " + Number(c) + " s" : "")
                  + ", cleaned " + Optional(r.CleanedSpeechLufs) + "/" + Optional(r.CleanedFloorLufs) + " LUFS, change "
                  + Optional(r.SpeechToNoiseChangeLu) + " LU, match " + Number(r.MatchGainDb) + " dB"
                  + (r.LevelMatched ? "" : " (not matched)") + ", gate " + (r.GateEnabled ? "on" : "off")
                  + (r.ThresholdDb is float t ? " at " + Number(t) + " dB" : "") + ", faults " + r.Faults
                : ", chain not available");

        private static string Number(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        /// <summary>
        /// The audition's words for a take too short to measure, at
        /// <paramref name="seconds"/> long. Also what a take stopped before its
        /// file began, or with too little of it kept, says (Sprint 49 Track Q3
        /// repair 1): the microphone worked, and the operator stopped the take
        /// too soon, which is exactly what these words describe; before Track
        /// Q3 such a take reached this verdict, and "nothing was captured, check
        /// the microphone" blamed the wrong thing.
        /// </summary>
        public static string TooShortSentence(double seconds) =>
            Lexicon.Get("audio.audition.verdict.too_short", ("length", Number(Math.Max(0, seconds))));

        private static string Optional(float? v) => v is float x ? Number(x) : "none";

        /// <summary>A figure the verdict is about to say. Its absence is a defect in the caller's branch, never something to paper over with a sentinel.</summary>
        private static string Measured(float? v) =>
            v is float x ? Number(x) : throw new InvalidOperationException("the verdict was asked to say a figure that was not measured");

        private static List<(int Start, int End)> SpeechRuns(double[] rawDb, double mid)
        {
            var runs = new List<(int, int)>();
            int n = rawDb.Length;
            int f = 0;
            while (f < n)
            {
                if (rawDb[f] <= mid) { f++; continue; }
                int start = f, end = f + 1, quiet = 0;
                for (int g = f + 1; g < n; g++)
                {
                    if (rawDb[g] > mid) { end = g + 1; quiet = 0; }
                    else if (++quiet > BridgeFrames) break;
                }
                if (end - start >= MinRunFrames) runs.Add((start, end));
                f = end + 1;
            }
            return runs;
        }

        private static double FrameDb(float[] samples, int at, int length)
        {
            double sum = 0;
            for (int i = at; i < at + length; i++) sum += (double)samples[i] * samples[i];
            return 10 * Math.Log10(sum / length + 1e-12);
        }

        private static double Percentile(double[] sorted, double p) =>
            sorted[(int)Math.Round(p * (sorted.Length - 1))];

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            var copy = values.ToArray();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }

        private static float Peak(float[] samples, int count)
        {
            float peak = 0;
            for (int i = 0; i < count; i++)
            {
                float a = Math.Abs(samples[i]);
                if (a > peak) peak = a;
            }
            return peak;
        }

        private static float ScaleFor(float peak) => peak > PeakCeiling ? PeakCeiling / peak : 1f;

        private static float[] Scaled(float[] samples, int count, float gain, float scale)
        {
            var output = new float[count];
            float k = gain * scale;
            for (int i = 0; i < count; i++) output[i] = samples[i] * k;
            return output;
        }
    }
}
