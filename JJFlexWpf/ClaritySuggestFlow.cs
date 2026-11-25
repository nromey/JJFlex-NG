using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using JJPortaudio;
using Radios;
using Radios.Listenback;

namespace JJFlexWpf
{
    /// <summary>
    /// JJ Clarity's "listen to my voice and suggest" pass as an operator flow,
    /// windowless (Sprint 49 Track J phase 2, J7, #736). It reads recordings
    /// the operator has ALREADY made, measures, proposes, renders before and
    /// after, and changes nothing: only the window's Keep, the operator's own
    /// choice, writes a setting (never auto-apply, #262).
    /// </summary>
    /// <remarks>
    /// <para><b>It never keys the radio</b> and never asks for a take to be
    /// made: nothing here reaches a radio, a microphone or the transmit
    /// stream. It reads a take's files and renders through the audition's
    /// offline twin, in instances of the twin's own. (Question 4's default;
    /// a source pin in the tests holds it.)</para>
    /// <para><b>Two recordings, two jobs.</b> The MEASUREMENT is of an off-air
    /// listen-back take: what another ham would hear (#456), the newest take
    /// under the takes folder. The GUARD and the before-and-after are run on
    /// the operator's own microphone audio, the retained Check your audio
    /// take: the audition's take while Check your audio holds one, else the
    /// newest take the operator kept or recorded in the recordings folder.
    /// A microphone recording never stands in for the off-air take (#456,
    /// question 3's standing ruling).</para>
    /// <para><b>The radio's own recorder is not wired.</b> Phase 1 built the
    /// step for it (<c>ClaritySuggest.FromAudio</c>, labelled as the stand-in),
    /// but the app has no road that brings the radio recorder's audio into
    /// memory: it records and plays on the radio. Reported for a ruling.</para>
    /// <para><b>The peak guard judges at the limiter's INPUT</b> (item 6),
    /// rendered through the twin's chain up to the limiter, the compressor
    /// included; with the limiter on, its ceiling is the ceiling the guard
    /// tests against. What is PLAYED is the whole chain, the limiter
    /// included: what would be sent.</para>
    /// </remarks>
    internal sealed class ClaritySuggestFlow
    {
        /// <summary>The folder the pass's two renders are written to for playing: private like the audition's, emptied when the window opens and closes, traced nowhere by path.</summary>
        internal const string FolderName = "ClaritySuggestTakes";

        internal static string DefaultFolder => Path.Combine(RadioConfig.AppDataRoot, FolderName);

        /// <summary>Which of the operator's own recordings the guard and the renders used.</summary>
        internal enum RetainedKind
        {
            /// <summary>None was found: the pass says one is needed.</summary>
            None,
            /// <summary>The audition's take, while Check your audio holds one.</summary>
            AuditionTake,
            /// <summary>The newest microphone take in the recordings folder (a kept audition take, or one recorded there).</summary>
            RecordingsTake,
        }

        /// <summary>The operator's own microphone recording, found on disk.</summary>
        internal sealed record RetainedTake(string Path, RetainedKind Kind);

        /// <summary>Everything the pass reads, as functions, so a test supplies its own and nothing here opens a window, a device or a radio.</summary>
        internal sealed class Sources
        {
            /// <summary>The newest off-air listen-back take's folder, or null.</summary>
            public Func<string?> NewestOffAirTake { get; init; } = Dialogs.ListenbackTakes.NewestDirectory;

            /// <summary>The operator's own retained microphone take, or null.</summary>
            public Func<RetainedTake?> Retained { get; init; } = () => FindRetained(ChainAuditionFolder.DefaultPath, RecordingStore.FolderPath);

            /// <summary>A take file as mono samples at the transmit rate, or null when it cannot be read. The app's is the audition's own loader.</summary>
            public Func<string, CancellationToken, (float[] Mono, int Rate)?> Load { get; init; } = Dialogs.CheckYourAudioDialog.LoadAuditionTake;

            /// <summary>JJ Clarity as intended now.</summary>
            public Func<TxClaritySetup> Current { get; init; } = TxAudioConditioning.CaptureClarity;

            /// <summary>The offline twin of the configured chain; the app's is the audition's own.</summary>
            public Func<ChainAuditionTwin> Twin { get; init; } = () => ChainAuditionTwin.ForApp(() => null);
        }

        /// <summary>What one run of the pass found. Nothing in it has been applied anywhere.</summary>
        internal sealed class Outcome
        {
            /// <summary>The analysis step's result: the measurement, the proposal, the guard's work, or its refusal.</summary>
            public ClaritySuggestResult Result { get; init; } = null!;
            /// <summary>JJ Clarity as it was when the pass ran.</summary>
            public TxClaritySetup Current { get; init; } = new();
            /// <summary>The off-air take's mode, for the "cannot measure this mode" sentence; empty when unknown.</summary>
            public string TakeMode { get; init; } = "";
            public RetainedKind Retained { get; init; }
            /// <summary>The retained take's file name without its folder or extension, for the sentence that says which was used; empty when none.</summary>
            public string RetainedName { get; init; } = "";
            /// <summary>Why the chain could not be rendered (an engine the setup asks for cannot run, say); null when it was, or when there was no retained take to render.</summary>
            public ChainProcessing? RenderRefusal { get; init; }
            /// <summary>The offer Keep writes, from <c>TryGetGuardedOffer</c> only; null when there is nothing to keep.</summary>
            public ClarityProposal? Offer { get; init; }
            /// <summary>The before and after renders, level-matched and scaled for playing; null when there is no offer.</summary>
            public ChainAudition.MatchedPair? Pair { get; init; }
            public int Rate { get; init; }
            /// <summary>Whether Keep would also switch JJ Clarity on (it is off now).</summary>
            public bool KeepSwitchesOn => Offer != null && !Current.Enabled;
            public bool CanKeep => Offer != null;
        }

        private readonly Sources _sources;

        internal ClaritySuggestFlow(Sources? sources = null) => _sources = sources ?? new Sources();

        /// <summary>
        /// Find the operator's own retained microphone take: the audition's
        /// take in its working folder (never one of its three renders), else
        /// the newest "take-" file in the recordings folder. Reads names and
        /// dates only.
        /// </summary>
        internal static RetainedTake? FindRetained(string auditionFolder, string recordingsFolder)
        {
            static string? Newest(string folder, string pattern, Func<string, bool> keep)
            {
                try
                {
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
                    return Directory.EnumerateFiles(folder, pattern).Where(keep)
                        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                }
                catch (Exception) { return null; }
            }
            static bool IsRender(string path)
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                foreach (AuditionRender render in Enum.GetValues(typeof(AuditionRender)))
                    if (stem.EndsWith("-" + render.ToString().ToLowerInvariant(), StringComparison.Ordinal)) return true;
                return false;
            }
            if (Newest(auditionFolder, "audition-*.wav", p => !IsRender(p)) is { } audition) return new RetainedTake(audition, RetainedKind.AuditionTake);
            if (Newest(recordingsFolder, "take-*.wav", _ => true) is { } kept) return new RetainedTake(kept, RetainedKind.RecordingsTake);
            return null;
        }

        /// <summary>
        /// Run the pass: measure the newest off-air take, propose, guard the
        /// proposal against the operator's own audio at the limiter's input,
        /// and render before and after. On a worker: it may take a while, and
        /// gives up with an <see cref="OperationCanceledException"/> once
        /// <paramref name="cancel"/> is set. Changes nothing.
        /// </summary>
        internal Outcome Run(CancellationToken cancel, Action<double>? progress)
        {
            var current = (_sources.Current() ?? new TxClaritySetup()).Clone();
            string? directory = _sources.NewestOffAirTake();
            string mode = "";
            if (directory != null)
            {
                try { mode = TakeReader.Inspect(directory).Get("mode") ?? ""; }
                catch (Exception) { mode = ""; }
            }

            // The operator's own audio, taken as far as the in-process stages once.
            var retained = _sources.Retained();
            ChainAuditionTwin.ClarityRenders? renders = null;
            ChainProcessing? refusal = null;
            float[]? raw = null;
            int rate = 0;
            if (retained != null)
            {
                var loaded = _sources.Load(retained.Path, cancel);
                if (loaded is { } take && take.Mono.Length > 0)
                {
                    raw = take.Mono;
                    rate = take.Rate;
                    renders = _sources.Twin().PrepareClarity(raw, rate, cancel, progress, out refusal);
                }
                else retained = null; // found, and could not be read: the same as none
            }
            cancel.ThrowIfCancellationRequested();

            // The guard renders at the LIMITER'S INPUT, with JJ Clarity on and
            // the settings it is given: settings that add nothing are the
            // stage off, so the baseline is right with the whole stage off too.
            var input = new ClaritySuggestInput(current.ToSettings(),
                RetainedAudio: renders != null ? raw : null,
                RetainedRate: renders != null ? rate : 0,
                LimiterCeilingDbfs: renders?.LimiterCeilingDbfs,
                Render: renders == null ? null : settings => renders.AtLimiterInput(true, settings),
                StageEnabled: current.Enabled);
            var result = ClaritySuggest.FromTake(directory ?? "", input, reuseRender: true);
            cancel.ThrowIfCancellationRequested();

            ClarityProposal? offer = null;
            ChainAudition.MatchedPair? pair = null;
            if (renders != null && result.TryGetGuardedOffer(out var guarded))
            {
                offer = guarded;
                // What would be SENT, before and after, the limiter included,
                // level-matched on speech loudness (#262).
                var before = renders.Sent(current.Enabled, current.ToSettings());
                cancel.ThrowIfCancellationRequested();
                var after = renders.Sent(true, guarded.Proposed);
                pair = ChainAudition.MatchPair(before, after, renders.SentLatencySamples, rate);
            }
            System.Diagnostics.Trace.WriteLine(TraceLine(result, retained?.Kind ?? RetainedKind.None, refusal, offer));
            return new Outcome
            {
                Result = result, Current = current, TakeMode = mode,
                Retained = retained?.Kind ?? RetainedKind.None,
                RetainedName = retained == null ? "" : Path.GetFileNameWithoutExtension(retained.Path),
                RenderRefusal = refusal, Offer = offer, Pair = pair, Rate = rate,
            };
        }

        /// <summary>One trace line for a run: facts only, no path and no words of the operator's.</summary>
        internal static string TraceLine(ClaritySuggestResult r, RetainedKind retained, ChainProcessing? refusal, ClarityProposal? offer) =>
            FormattableString.Invariant($"JJ Clarity suggest pass: {(r.Available ? "measured" : "refused (" + r.Refusal + ")")}, source {r.Source}, retained take {retained}")
            + (refusal != null ? ", chain not rendered (" + refusal.UnavailableKey + ")" : "")
            + (r.Proposal == null ? "" : FormattableString.Invariant(
                $", presence shortfall {r.Proposal.PresenceShortfallDb:F1} dB ({r.Proposal.Brightness}), fullness shortfall {r.Proposal.FullnessShortfallDb:F1} dB ({r.Proposal.Fullness})"))
            + (r.Guard == null ? ", guard not run" : FormattableString.Invariant(
                $", guard at the limiter's input: {r.Guard.Steps} steps, {(r.Guard.Withdrawn ? "withdrawn" : r.Guard.Reduced ? "reduced" : "as proposed")}, over full scale {r.Guard.OverFullScaleBefore} before and {r.Guard.OverFullScaleAfter} after, peak {r.Guard.PeakBeforeDbfs:F1} to {r.Guard.PeakAfterDbfs:F1} dBFS, ceiling {(r.Guard.CeilingDbfs is double c ? c.ToString("F1", CultureInfo.InvariantCulture) : "none")}"))
            + (offer == null ? ", nothing to keep" : ", offer " + offer.Proposed);

        // ── The report: assembled sentences, every one a DRAFT PROSE key ──

        private static string N(double value, string format = "0.#") => value.ToString(format, CultureInfo.InvariantCulture);

        private static string Fact(string? value) => string.IsNullOrWhiteSpace(value) ? Lexicon.Get("audio.clarity_suggest.fact_unknown") : value!;

        /// <summary>
        /// What the pass found, as the sentences an operator hears, in order:
        /// which recording it measured, what the radio was doing to it, what
        /// it found and proposes for each band, what the peak guard did, which
        /// of the operator's own recordings it used, what Keep would do, and
        /// that nothing has been changed. A refusal says why and what is
        /// needed. It never says the chain is free of clipping.
        /// </summary>
        internal static IReadOnlyList<string> Report(Outcome o)
        {
            var parts = new List<string>();
            var r = o.Result;
            if (!r.Available)
            {
                parts.Add(r.Refusal switch
                {
                    ClaritySuggestRefusal.NoTake => Lexicon.Get("audio.clarity_suggest.refused.no_take"),
                    ClaritySuggestRefusal.NotPlayable => Lexicon.Get("audio.clarity_suggest.refused.not_playable"),
                    ClaritySuggestRefusal.NoSpeechRender => Lexicon.Get("audio.clarity_suggest.refused.no_speech_render", ("mode", Fact(o.TakeMode))),
                    ClaritySuggestRefusal.PassbandHoldsNoBand => Lexicon.Get("audio.clarity_suggest.refused.passband_holds_no_band"),
                    ClaritySuggestRefusal.NoTransmitFilter => Lexicon.Get("audio.clarity_suggest.refused.no_transmit_filter"),
                    _ => Lexicon.Get("audio.clarity_suggest.refused.too_little_speech"),
                });
                parts.Add(Lexicon.Get("audio.clarity_suggest.changes_nothing"));
                return parts;
            }

            parts.Add(Lexicon.Get(r.Source == ClaritySuggestSource.RadioRecorderStandIn
                ? "audio.clarity_suggest.source_radio_recorder" : "audio.clarity_suggest.source_off_air"));
            // What the radio was doing: the measurement is of the whole chain,
            // not of the voice alone (#262's honesty constraint).
            var radio = r.Radio ?? RadioProcessingFacts.Unknown;
            parts.Add(Lexicon.Get("audio.clarity_suggest.radio_was",
                ("processor", Fact(radio.Processor)), ("processor_level", Fact(radio.ProcessorLevel)),
                ("compander", Fact(radio.Compander)), ("compander_level", Fact(radio.CompanderLevel)), ("mic_gain", Fact(radio.MicGain))));

            var p = r.Proposal;
            var shown = o.Offer ?? r.Guard?.Proposal ?? p;
            if (p != null && shown != null)
            {
                parts.Add(BandSentence(high: true, shown.Brightness, p.PresenceShortfallDb, shown.Proposed.High));
                parts.Add(BandSentence(high: false, shown.Fullness, p.FullnessShortfallDb, shown.Proposed.Low));
            }

            if (r.Guard is { } guard)
            {
                if (guard.Withdrawn) parts.Add(Lexicon.Get("audio.clarity_suggest.guard_withdrawn"));
                else if (guard.Reduced) parts.Add(Lexicon.Get("audio.clarity_suggest.guard_reduced", ("steps", guard.Steps.ToString(CultureInfo.InvariantCulture))));
                // Overs that were there before stay there: said, never called fixed.
                if (guard.OverFullScaleBefore > 0)
                    parts.Add(Lexicon.Get("audio.clarity_suggest.already_past_full_scale",
                        ("samples", guard.OverFullScaleBefore.ToString(CultureInfo.InvariantCulture))));
            }
            else if (p is { IsNothing: false })
            {
                // Something is proposed and the guard could not run: nothing can be heard or kept.
                parts.Add(o.RenderRefusal is { } refused && refused.UnavailableKey.Length > 0
                    ? Lexicon.Get("audio.clarity_suggest.cannot_render", ("reason", Lexicon.Get(refused.UnavailableKey, refused.UnavailableArgs.ToArray())))
                    : Lexicon.Get("audio.clarity_suggest.recording_needed"));
            }

            if (o.CanKeep)
            {
                // Which of the operator's own recordings before and after are made from: said only when there is something to hear.
                if (o.Retained != RetainedKind.None)
                    parts.Add(Lexicon.Get(o.Retained == RetainedKind.AuditionTake
                        ? "audio.clarity_suggest.retained_audition" : "audio.clarity_suggest.retained_recording", ("file", o.RetainedName)));
                if (o.Pair is { LevelMatched: false }) parts.Add(Lexicon.Get("audio.clarity_suggest.not_level_matched"));
                parts.Add(Lexicon.Get(o.KeepSwitchesOn ? "audio.clarity_suggest.can_keep_switches_on" : "audio.clarity_suggest.can_keep"));
            }
            else if (p is { IsNothing: true }) parts.Add(Lexicon.Get("audio.clarity_suggest.nothing_needed"));
            parts.Add(Lexicon.Get("audio.clarity_suggest.changes_nothing"));
            return parts;
        }

        /// <summary>One band's sentence: what was found against the reference, and what is proposed for it. Each a whole key.</summary>
        private static string BandSentence(bool high, ClarityJudgement judgement, double shortfallDb, ClarityBand proposed)
        {
            (string, object?) shortfall = ("shortfall", N(shortfallDb));
            switch (judgement)
            {
                case ClarityJudgement.Proposed:
                    return Lexicon.Get(high ? "audio.clarity_suggest.brightness.proposed" : "audio.clarity_suggest.fullness.proposed", shortfall,
                        ("mix", N(proposed.MixPercent)), ("tune", N(proposed.TuneHz, "0")), ("drive", N(proposed.DriveDb)),
                        ("harmonics", NoiseController.ClarityHarmonicsName(TxClarityHarmonics.ToWord(proposed.Harmonics))));
                case ClarityJudgement.AlreadyEnough:
                    return Lexicon.Get(high ? "audio.clarity_suggest.brightness.already_enough" : "audio.clarity_suggest.fullness.already_enough", shortfall);
                case ClarityJudgement.RegionOutsidePassband:
                    return Lexicon.Get(high ? "audio.clarity_suggest.brightness.outside_passband" : "audio.clarity_suggest.fullness.outside_passband");
                case ClarityJudgement.AtTheRulesMost:
                    return Lexicon.Get(high ? "audio.clarity_suggest.brightness.at_most" : "audio.clarity_suggest.fullness.at_most", shortfall);
                default:
                    return Lexicon.Get(high ? "audio.clarity_suggest.brightness.withdrawn" : "audio.clarity_suggest.fullness.withdrawn", shortfall);
            }
        }

        // ── The two renders, written for playing ──

        /// <summary>
        /// Write the before and after renders into <paramref name="folder"/>
        /// (made if need be) and return their paths. Mono WAV at the take's
        /// rate, the audition's own writer.
        /// </summary>
        internal static (string Before, string After) WriteRenders(Outcome o, string folder)
        {
            if (o.Pair == null) throw new InvalidOperationException("there is no offer to render");
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss-fff", CultureInfo.InvariantCulture);
            string before = Path.Combine(folder, "clarity-" + stamp + "-before.wav");
            string after = Path.Combine(folder, "clarity-" + stamp + "-after.wav");
            Write(before, o.Pair.Before, o.Rate);
            Write(after, o.Pair.After, o.Rate);
            return (before, after);
        }

        private static void Write(string path, float[] samples, int rate)
        {
            using var writer = new WavWriter(path, rate, 1);
            writer.Write(samples, samples.Length);
            writer.Finish();
        }
    }
}
