using System;
using System.Diagnostics;
using JJTrace;

namespace Radios.Fixer.Evidence
{
    /// <summary>
    /// Writes a run to disk as it happens, one recording at a time (#251).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>As it goes, never on close</b> — the close path is also the abandon
    /// path, and a crash loses everything either way. The host tells the
    /// journal about each recording the moment the engine returns it; the
    /// journal captures the stage's settings fingerprint at that same moment
    /// (the values the stage actually ran under), rebuilds both report forms
    /// through <c>FixerReport</c> — the page's own renderer, never a second
    /// one — and writes the whole record atomically.
    /// </para>
    /// <para>
    /// <b>Nothing touches disk until something is worth keeping.</b> A run
    /// that was opened and closed with nothing recorded leaves no file: there
    /// is no measurement to lose, and a saved-runs list padded with empty
    /// records would bury the ones that matter.
    /// </para>
    /// <para>
    /// <b>No public method here ever throws.</b> The evidence layer exists to
    /// make the diagnosis durable; it must never be the thing that takes the
    /// diagnosis down. Failures are traced and the run continues unrecorded —
    /// which the trace states plainly, because a recording that silently
    /// stopped is drift of exactly the kind this project hunts.
    /// </para>
    /// </remarks>
    public sealed class FixerRunJournal
    {
        private readonly FixerRun _run;
        private readonly FixerRunStore _store;
        private readonly FixerSettingProbeSet _probes;
        private readonly FixerRunRecord _record;
        private bool _anythingRecorded;
        private bool _ended;

        /// <summary>The record being written. Exposed for the host surfaces
        /// that show it (the viewer's "current run" case) and for tests.</summary>
        public FixerRunRecord Record => _record;

        /// <param name="probes">May be null — runs still persist, with empty
        /// fingerprints, and the staleness check reports them honestly as
        /// unverifiable rather than fresh.</param>
        public FixerRunJournal(FixerRun run, FixerRunStore store, FixerSettingProbeSet probes)
        {
            _run = run ?? throw new ArgumentNullException(nameof(run));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _probes = probes;
            _record = FixerRunRecord.NewFor(run);
        }

        /// <summary>
        /// Continue an existing record — the resume path. The saved history
        /// stays; new recordings append after it; the end stamp of the
        /// interrupted sitting is cleared, because the run is live again.
        /// The run must be the record's run: a journal quietly writing one
        /// run's results under another's ID would be the exact drift the
        /// Test ID exists to prevent, so that is refused loudly.
        /// </summary>
        public static FixerRunJournal Resume(FixerRun run, FixerRunStore store,
                                             FixerSettingProbeSet probes,
                                             FixerRunRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (!string.Equals(run.RunId, record.RunId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("run " + run.RunId + " cannot continue record "
                    + record.RunId + " — they are different runs", nameof(record));

            var journal = new FixerRunJournal(run, store, probes, record);
            record.EndedUtc = null;
            record.EndReason = "";
            return journal;
        }

        private FixerRunJournal(FixerRun run, FixerRunStore store, FixerSettingProbeSet probes,
                                FixerRunRecord adopt)
        {
            _run = run;
            _store = store;
            _probes = probes;
            _record = adopt;
            // The adopted record already holds evidence, so from the first
            // new recording onward every persist keeps the whole history.
            _anythingRecorded = adopt.Results.Count > 0 || adopt.Fixes.Count > 0
                             || adopt.Declarations.Count > 0;
        }

        /// <summary>A stage result was recorded. Fingerprints it and persists.</summary>
        public void StageRecorded(FixerStageResult result)
        {
            if (result == null) return;
            try
            {
                _record.Results.Add(RecordedStage.From(result,
                    _probes?.CaptureFor(result.StageId)));
                Persist();
            }
            catch (Exception ex)
            {
                Trouble("recording stage '" + result.StageId + "'", ex);
            }
        }

        /// <summary>A fix was applied or attempted. Persists.</summary>
        public void FixRecorded(FixerFixRecord fix)
        {
            if (fix == null) return;
            try
            {
                _record.Fixes.Add(RecordedFix.From(fix));
                Persist();
            }
            catch (Exception ex)
            {
                Trouble("recording a fix on stage '" + fix.StageId + "'", ex);
            }
        }

        /// <summary>The operator answered a per-run declaration. Persists.</summary>
        public void DeclarationRecorded(string declarationId, string answerId, string answerLabel)
        {
            if (string.IsNullOrWhiteSpace(declarationId)) return;
            try
            {
                // Re-declaring replaces: the gate holds one answer per run,
                // and the record must agree with the gate, not remember a
                // superseded answer beside the real one.
                _record.Declarations.RemoveAll(d =>
                    string.Equals(d.Id, declarationId, StringComparison.OrdinalIgnoreCase));
                _record.Declarations.Add(new RecordedDeclaration
                {
                    Id = declarationId,
                    AnswerId = answerId ?? "",
                    AnswerLabel = answerLabel ?? "",
                    AtUtc = _run.NowUtc,
                });
                Persist();
            }
            catch (Exception ex)
            {
                Trouble("recording the '" + declarationId + "' declaration", ex);
            }
        }

        /// <summary>What happened about the diagnostic capture, and where its
        /// archive landed. Persists only when the run has records — a capture
        /// note alone is not evidence.</summary>
        public void CaptureNoted(string note, string archivePath)
        {
            try
            {
                _record.CaptureNote = note ?? "";
                _record.CaptureArchivePath = archivePath ?? "";
                if (_anythingRecorded) Persist();
            }
            catch (Exception ex)
            {
                Trouble("recording the capture note", ex);
            }
        }

        /// <summary>
        /// The run ended in an orderly way. Stamps when and why, once — a
        /// second end (the close path after an abandon) changes nothing.
        /// A run with nothing recorded still leaves no file.
        /// </summary>
        public void RunEnded(string reason)
        {
            if (_ended) return;
            _ended = true;
            try
            {
                _record.EndedUtc = _run.NowUtc;
                _record.EndReason = string.IsNullOrWhiteSpace(reason) ? "ended" : reason.Trim();
                if (_anythingRecorded) Persist();
            }
            catch (Exception ex)
            {
                Trouble("recording the end of the run", ex);
            }
        }

        /// <summary>
        /// The live-run staleness picture: which already-recorded stages a
        /// setting change (typically a fix) has just invalidated, each change
        /// named. Null when no fingerprints are wired.
        /// </summary>
        public FixerStalenessReport StalenessNow()
        {
            if (_probes == null) return null;
            try
            {
                return FixerStalenessCheck.Check(_record, _probes);
            }
            catch (Exception ex)
            {
                Trouble("checking staleness", ex);
                return null;
            }
        }

        // -------- plumbing --------

        private void Persist()
        {
            _anythingRecorded = true;
            _record.LastRecordedUtc = _run.NowUtc;

            // The same renderer as the live page, at the moment of recording.
            // "This copy of the report was written ..." inside it is now a true
            // sentence about the file on disk.
            _record.ReportText = FixerReport.PlainText(_run);
            _record.ReportHtml = FixerReport.HtmlFragment(_run, headingLevel: 2);

            if (!_store.Save(_record))
            {
                Tracing.TraceLine("FixerRunJournal " + _record.RunId + ": run is continuing "
                    + "UNRECORDED — the store could not save it", TraceLevel.Warning);
            }
        }

        private void Trouble(string doing, Exception ex)
        {
            Tracing.TraceLine("FixerRunJournal " + _record.RunId + ": " + doing + " failed — "
                + ex.Message + ". The run continues; this recording was lost.",
                TraceLevel.Warning);
        }
    }
}
