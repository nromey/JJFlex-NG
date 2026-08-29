using System;
using System.Diagnostics;
using JJTrace;
using Radios;
using Radios.Fixer;
using Radios.Fixer.Evidence;

namespace JJFlexWpf;

/// <summary>
/// Everything the Fixer dialog needs from the evidence layer, behind one
/// object: the journal that persists the run as it goes (#251), the settings
/// fingerprints (#252), and the diagnostic capture around the run (#194,
/// left-as-found per #173).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the seam, kept deliberately narrow.</b> The dialog is being
/// restructured by another track this sprint, so its side of the wiring is
/// five one-line calls: <see cref="Begin"/> at construction,
/// <see cref="StageRecorded"/> / <see cref="FixRecorded"/> /
/// <see cref="DeclarationRecorded"/> after the matching engine calls, and
/// <see cref="End"/> on every close path — abandon, orderly close, and the
/// failed-init close alike (End is idempotent precisely so no path needs to
/// know about the others).
/// </para>
/// <para>
/// Nothing in here throws to its caller. The evidence layer must never take
/// the diagnosis down; anything that fails is traced and the run continues,
/// unrecorded but intact.
/// </para>
/// </remarks>
public sealed class FixerEvidenceKit
{
    private readonly FixerRunJournal? _journal;
    private readonly FixerCaptureScope? _capture;
    private bool _ended;

    /// <summary>The record being written, or null when persistence could not
    /// be set up. The viewer's "current run" case and tests read it.</summary>
    public FixerRunRecord? Record => _journal?.Record;

    private FixerEvidenceKit(FixerRunJournal? journal, FixerCaptureScope? capture)
    {
        _journal = journal;
        _capture = capture;
    }

    /// <summary>
    /// Open the evidence layer around a run: journal, fingerprints, capture.
    /// Never throws — a kit that could not set up still answers every call.
    /// </summary>
    public static FixerEvidenceKit Begin(FixerRun run, Func<FlexBase?> radio)
        => Open(run, radio, resumeInto: null);

    /// <summary>
    /// Open the evidence layer around a RESUMED run: the same journal, writing
    /// on into the record the run came from, with a new sitting opened on it.
    /// Never throws.
    /// </summary>
    /// <remarks>
    /// The record must be the one <paramref name="run"/> was rehydrated from;
    /// the journal refuses any other, and refuses a record whose checks differ
    /// from the ones this build offers. A refusal leaves the run live and
    /// unrecorded rather than taking the diagnosis down — traced, so a
    /// recording that silently is not happening cannot pass for one that is.
    /// </remarks>
    public static FixerEvidenceKit Resume(FixerRun run, Func<FlexBase?> radio,
                                          FixerRunRecord resumeInto)
        => Open(run, radio, resumeInto);

    private static FixerEvidenceKit Open(FixerRun run, Func<FlexBase?> radio,
                                         FixerRunRecord? resumeInto)
    {
        FixerRunJournal? journal = null;
        FixerCaptureScope? capture = null;
        try
        {
            string root = RadioConfig.AppDataRoot;
            if (string.IsNullOrEmpty(root))
            {
                // No settings root means nowhere honest to write. Trace it —
                // a recording that silently is not happening is drift.
                Tracing.TraceLine("FixerEvidenceKit: no settings root — run "
                    + run.RunId + " will NOT be persisted", TraceLevel.Warning);
            }
            else
            {
                FixerSettingProbeSet probes = TransmitSettingProbes.Build(Readers(radio));
                journal = resumeInto == null
                    ? new FixerRunJournal(run, FixerRunStore.Default(), probes,
                                          () => Identity(radio))
                    : FixerRunJournal.Resume(run, FixerRunStore.Default(), probes,
                                             resumeInto, () => Identity(radio));
            }

            capture = FixerCaptureScope.Begin(run.RunId, run.Set.Name, new FixerCaptureScope.Plumbing
            {
                IsAvailable = () => DiagnosticsBridge.IsAvailable,
                IsCapturing = DiagnosticsBridge.Capturing,
                Start = reason => DiagnosticsBridge.StartCapture?.Invoke(reason),
                Stop = () => DiagnosticsBridge.StopCapture?.Invoke(),
                LastArchivePath = () => DiagnosticsBridge.LastCaptureArchivePath?.Invoke() ?? "",
                Announce = text => ScreenReaderOutput.Speak(text, VerbosityLevel.Critical,
                                                            interrupt: false),
            });

            // The note goes in NOW, not at the end: a run that crashes mid-way
            // must still say on disk whether a capture was running beside it.
            // The archive path is filled in by End, once one exists.
            journal?.CaptureNoted(capture.Note, "");
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerEvidenceKit: begin failed — " + ex.Message
                + ". The run continues without evidence-keeping.", TraceLevel.Warning);
        }
        return new FixerEvidenceKit(journal, capture);
    }

    /// <summary>Call right after the engine records a stage (run or skip).</summary>
    public void StageRecorded(FixerStageResult? result) => _journal?.StageRecorded(result!);

    /// <summary>Call right after the engine records a fix.</summary>
    public void FixRecorded(FixerFixRecord? fix) => _journal?.FixRecorded(fix!);

    /// <summary>Call when the operator answers a run declaration.</summary>
    public void DeclarationRecorded(string declarationId, string answerId, string answerLabel)
        => _journal?.DeclarationRecorded(declarationId, answerId, answerLabel);

    /// <summary>Which recorded stages the current settings have invalidated,
    /// each change named — for the page to show after a fix. Null when
    /// fingerprints are not running.</summary>
    public FixerStalenessReport? StalenessNow() => _journal?.StalenessNow();

    /// <summary>
    /// Close the evidence layer: stop the capture if this run started it
    /// (announced; an inherited capture is left running), stamp the archive
    /// path and the end reason into the record. Idempotent — call it from
    /// every close path without coordination.
    /// </summary>
    /// <param name="reason">"closed" or "abandoned" — the first caller's word
    /// wins.</param>
    public void End(string reason)
    {
        if (_ended) return;
        _ended = true;
        try
        {
            _capture?.End();
            if (_capture != null)
                _journal?.CaptureNoted(_capture.Note, _capture.ArchivePath);
            _journal?.RunEnded(reason);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerEvidenceKit: end failed — " + ex.Message,
                              TraceLevel.Warning);
        }
    }

    /// <summary>
    /// Close the evidence layer AND delete the run's file — the operator's
    /// "exit without saving" (#376). Runs persist as they go, so abandoning
    /// deliberately must remove what was written; otherwise the option is a
    /// lie in the other direction. Ends first (the capture must still stop
    /// and be archived), then deletes. True when nothing remains on disk —
    /// including the case where nothing was ever persisted.
    /// </summary>
    public bool Discard()
    {
        End("discarded");
        try { return _journal == null || _journal.DeleteRecord(); }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerEvidenceKit: discard failed — " + ex.Message,
                              TraceLevel.Warning);
            return false;
        }
    }

    /// <summary>
    /// The radio's identity and the software's, for the exported document's
    /// #217 sections — read through the SAME assemblers the chain-check
    /// evidence block uses.
    /// </summary>
    /// <remarks>
    /// Reusing them is the point, not a convenience. Two assemblers is how one
    /// radio ends up with two documents disagreeing about its firmware, and
    /// this document's second reader is a support desk that would be entitled
    /// to notice. Each half is guarded on its own: a radio that cannot be read
    /// must not cost the software lines, which need no radio at all.
    /// </remarks>
    private static FixerRunIdentity Identity(Func<FlexBase?> radio)
    {
        var identity = new FixerRunIdentity();
        try
        {
            FlexBase? rig = null;
            try { rig = radio?.Invoke(); } catch { rig = null; }
            identity.Station = Radios.ChainChecks.TxChainFacts.StationLines(rig!);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerEvidenceKit: the radio's identity could not be read — "
                              + ex.Message, TraceLevel.Warning);
        }

        try { identity.Software = TxChainPcFacts.BuildLines(); }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerEvidenceKit: the software versions could not be read — "
                              + ex.Message, TraceLevel.Warning);
        }

        return identity;
    }

    /// <summary>
    /// The radio-side fingerprint reads, each guarded on its own so one
    /// unreadable value never costs the others. The radio type stays on this
    /// side of the delegate bag — Radios.Fixer must never see it. Internal
    /// because the saved-runs viewer builds the same probes for its staleness
    /// lead: one wiring, so the live run and the viewer can never read a
    /// setting two different ways.
    /// </summary>
    internal static TransmitSettingReaders Readers(Func<FlexBase?> radio)
    {
        FlexBase? Rig()
        {
            try { return radio?.Invoke(); } catch { return null; }
        }

        return new TransmitSettingReaders
        {
            // The same reader stage 0 uses, so the fingerprint and the stage
            // cannot disagree about what the configuration says.
            AudioSetup = Dialogs.FixerHostWiring.AudioSetup(),
            PcAudioOn = () => Rig() is { } r1 ? r1.PCAudio : null,
            MicProfileEmpty = () => Rig() is { } r2 ? r2.MicProfileSelectionEmpty : null,
            TunePowerWatts = () => Rig() is { } r3 ? r3.TunePower : null,
            RfPowerWatts = () => Rig() is { } r4 ? r4.XmitPower : null,
            TxAntennaName = () => Rig()?.TXAntennaName ?? "",
            TxFrequencyHz = () => Rig() is { } r5 ? r5.TXFrequency : null,
            ModeName = () => Rig()?.Mode ?? "",
            MicGain = () => Rig() is { } r6 ? r6.MicGain : null,
        };
    }
}
