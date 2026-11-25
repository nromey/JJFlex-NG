using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using JJPortaudio;

namespace Radios.Listenback;

/// <summary>Which kind of recording the suggest pass measured. Said to the operator in phase 2 (DRAFT PROSE).</summary>
public enum ClaritySuggestSource
{
    /// <summary>An off-air listen-back take: what another ham would hear (#456).</summary>
    OffAirTake = 0,
    /// <summary>The radio's own slice recorder, the labelled stand-in for a radio that cannot hear itself off the air (#456, 2026-10-21).</summary>
    RadioRecorderStandIn = 1,
}

/// <summary>Why the suggest pass could not measure. No words here: phase 2 says each with its reason.</summary>
public enum ClaritySuggestRefusal
{
    None = 0,
    /// <summary>No take: the operator has to make one first.</summary>
    NoTake,
    NotPlayable,
    /// <summary>The take's mode has no speech render (anything but USB and LSB), so it cannot be measured.</summary>
    NoSpeechRender,
    TooLittleSpeech,
    PassbandHoldsNoBand,
    NoTransmitFilter,
}

/// <summary>
/// What the radio was doing to the audio when the take was made, read from
/// the take's header (null where a take did not record a fact). Clarity acts
/// BEFORE the radio's processing, so a measurement of the take characterises
/// the whole chain, not the voice alone (#262's honesty constraint); phase 2
/// says so in one plain sentence with these facts.
/// </summary>
public sealed record RadioProcessingFacts(string Processor, string ProcessorLevel, string Compander,
    string CompanderLevel, string MicGain)
{
    public static RadioProcessingFacts Unknown { get; } = new(null, null, null, null, null);

    public static RadioProcessingFacts FromTake(TakeInspection take) => take == null ? Unknown :
        new(take.Get("processor"), take.Get("processor-level"), take.Get("compander"), take.Get("compander-level"), take.Get("mic-gain"));
}

/// <summary>
/// The suggest step's inputs besides the recording: the Clarity settings in
/// force now, and the operator's own audio to guard the proposal against (the
/// retained Check your audio microphone take, in phase 2), at its rate, with
/// the limiter's ceiling where one is on and, optionally, the renderer the
/// guard should use (phase 2: the audition twin's chain up to the limiter's
/// input).
/// </summary>
/// <param name="StageEnabled">
/// Whether JJ Clarity as a whole is on now (phase 2, item 6). The whole
/// stage's switch is separate from <see cref="ClaritySettings"/>, which holds
/// only the bands. With the stage off nothing is being added whatever the
/// bands say, so the rule and the guard judge against
/// <see cref="EffectiveCurrent"/>.
/// </param>
public sealed record ClaritySuggestInput(
    ClaritySettings Current,
    float[] RetainedAudio = null,
    int RetainedRate = 0,
    double? LimiterCeilingDbfs = null,
    Func<ClaritySettings, float[]> Render = null,
    bool StageEnabled = true)
{
    /// <summary>
    /// The settings as they act now: <see cref="Current"/> while the stage is
    /// on; with the stage off, the same bands each read as off. An off band's
    /// mix counts as zero, and its stored tune, drive, mix and harmonics are
    /// kept exactly (phase 1's repair 2, the orchestrator's ruling, applied to
    /// the whole switch as it is to a band's own): so a proposal made with
    /// the stage off proposes each band on from zero, never below a stored
    /// drive, and a band it does not propose stays off in what is offered,
    /// which is what the operator then hears as "after".
    /// </summary>
    public ClaritySettings EffectiveCurrent
    {
        get
        {
            var current = Current ?? ClaritySettings.Default;
            return StageEnabled ? current
                : new ClaritySettings(current.High.WithEnabled(false), current.Low.WithEnabled(false));
        }
    }
}

/// <summary>
/// The suggest step's result: the measurement, the proposal, the peak
/// guard's work and the facts it rests on.
/// </summary>
/// <remarks>
/// <para><b>Nothing is keepable until the guard has run</b> (repair 1, B2).
/// <see cref="Proposal"/> is the rule's raw proposal, for analysis and for
/// saying what was found; it is never the thing an operator keeps. The only
/// road to a keepable offer is <see cref="TryGetGuardedOffer"/>, which
/// answers false when no retained audio was given (<see cref="Guard"/> is
/// null: phase 2 says a recording of the operator's voice is needed) and when
/// the guarded proposal changes nothing (withdrawn, or nothing was
/// proposed).</para>
/// </remarks>
public sealed record ClaritySuggestResult(
    bool Available,
    ClaritySuggestRefusal Refusal,
    ClaritySuggestSource Source,
    RadioProcessingFacts Radio,
    LongTermSpectrum Spectrum,
    ClarityProposal Proposal,
    ClarityGuardResult Guard)
{
    public bool GuardRan => Guard != null;

    /// <summary>True only when the guard has run and its proposal changes something: the one state in which Keep may be offered.</summary>
    public bool CanKeep => Guard != null && Guard.Proposal != null && !Guard.Proposal.IsNothing;

    /// <summary>
    /// The guarded proposal, when there is one to keep. False, with
    /// <paramref name="offer"/> null, when the guard has not run or its
    /// proposal changes nothing. Never returns the raw <see cref="Proposal"/>.
    /// </summary>
    public bool TryGetGuardedOffer(out ClarityProposal offer)
    {
        offer = CanKeep ? Guard.Proposal : null;
        return offer != null;
    }
}

/// <summary>
/// The "listen to my voice and suggest" pass's analysis as ONE STEP (Sprint
/// 49 Track J, J3, #736): an input (a recording and the current settings), an
/// output (the measurement, the proposal, the facts it rests on). No UI, no
/// radio, no audio device; it never keys anything and changes nothing. Built
/// so #152's convergence loop can call it later; that loop is not built here.
/// </summary>
public static class ClaritySuggest
{
    /// <summary>Measure an off-air listen-back take and propose.</summary>
    public static ClaritySuggestResult FromTake(string directory, ClaritySuggestInput input, bool reuseRender = false)
    {
        input ??= new ClaritySuggestInput(ClaritySettings.Default);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return Refused(ClaritySuggestRefusal.NoTake, ClaritySuggestSource.OffAirTake, RadioProcessingFacts.Unknown, null);
        var take = TakeReader.Inspect(directory);
        var radio = RadioProcessingFacts.FromTake(take);
        var spectrum = LongTermSpectrumMeter.MeasureTake(directory, reuseRender);
        return Finish(spectrum, ClaritySuggestSource.OffAirTake, radio, input);
    }

    /// <summary>
    /// Measure any recording already in memory (the radio's own recorder, in
    /// phase 2): its keyed spans, at its rate, inside the transmit filter it was
    /// made through.
    /// </summary>
    public static ClaritySuggestResult FromAudio(float[] samples, IReadOnlyList<TakeSpan> spans, int rate,
        double filterLow, double filterHigh, ClaritySuggestSource source, RadioProcessingFacts radio, ClaritySuggestInput input)
    {
        input ??= new ClaritySuggestInput(ClaritySettings.Default);
        radio ??= RadioProcessingFacts.Unknown;
        if (samples == null || samples.Length == 0) return Refused(ClaritySuggestRefusal.NoTake, source, radio, null);
        var spectrum = spans == null
            ? LongTermSpectrumMeter.Measure(samples, rate, filterLow, filterHigh)
            : LongTermSpectrumMeter.Measure(samples, spans, rate, filterLow, filterHigh);
        return Finish(spectrum, source, radio, input);
    }

    private static ClaritySuggestResult Finish(LongTermSpectrum spectrum, ClaritySuggestSource source,
        RadioProcessingFacts radio, ClaritySuggestInput input)
    {
        if (!spectrum.Available) return Refused(Map(spectrum.Refusal), source, radio, spectrum);
        var proposal = ClarityProposalRule.Propose(spectrum, input.EffectiveCurrent);
        ClarityGuardResult guard = null;
        if (input.RetainedAudio != null && input.RetainedAudio.Length > 0 && (input.RetainedRate > 0 || input.Render != null))
            guard = ClarityPeakGuard.Apply(proposal, input.RetainedAudio, input.RetainedRate > 0 ? input.RetainedRate : (int)ClarityDesign.DesignRate,
                (float)spectrum.PassbandLowHz, (float)spectrum.PassbandHighHz, input.LimiterCeilingDbfs, input.Render);
        return new ClaritySuggestResult(true, ClaritySuggestRefusal.None, source, radio, spectrum, proposal, guard);
    }

    private static ClaritySuggestResult Refused(ClaritySuggestRefusal why, ClaritySuggestSource source,
        RadioProcessingFacts radio, LongTermSpectrum spectrum) =>
        new(false, why, source, radio, spectrum, null, null);

    private static ClaritySuggestRefusal Map(LongTermSpectrumRefusal r) => r switch
    {
        LongTermSpectrumRefusal.NotPlayable => ClaritySuggestRefusal.NotPlayable,
        LongTermSpectrumRefusal.NoSpeechRender => ClaritySuggestRefusal.NoSpeechRender,
        LongTermSpectrumRefusal.PassbandHoldsNoBand => ClaritySuggestRefusal.PassbandHoldsNoBand,
        LongTermSpectrumRefusal.NoTransmitFilter => ClaritySuggestRefusal.NoTransmitFilter,
        _ => ClaritySuggestRefusal.TooLittleSpeech,
    };
}
