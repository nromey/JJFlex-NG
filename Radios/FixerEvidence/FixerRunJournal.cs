using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JJTrace;

namespace Radios.Fixer.Evidence
{
    /// <summary>
    /// The radio's identity and the software's, as display lines, read by the
    /// host — because <c>Radios.Fixer</c> may never name the radio type in a
    /// signature (<c>FixerFrameworkTests</c> enforces it by reflection).
    /// </summary>
    /// <remarks>
    /// Both lists come from the readers the chain-check evidence block already
    /// uses, deliberately: two assemblers is how two documents about one radio
    /// end up disagreeing about its firmware.
    /// </remarks>
    public sealed class FixerRunIdentity
    {
        public IReadOnlyList<string> Station = Array.Empty<string>();
        public IReadOnlyList<string> Software = Array.Empty<string>();
    }

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
        private readonly Func<FixerRunIdentity> _identity;
        private bool _anythingRecorded;
        private bool _ended;

        /// <summary>The record being written. Exposed for the host surfaces
        /// that show it (the viewer's "current run" case) and for tests.</summary>
        public FixerRunRecord Record => _record;

        /// <param name="probes">May be null — runs still persist, with empty
        /// fingerprints, and the staleness check reports them honestly as
        /// unverifiable rather than fresh.</param>
        /// <param name="identity">May be null — the record then carries no
        /// radio or software lines, and the exported document names the
        /// absence rather than leaving a reader to assume they were read and
        /// came back empty.</param>
        public FixerRunJournal(FixerRun run, FixerRunStore store, FixerSettingProbeSet probes,
                               Func<FixerRunIdentity> identity = null)
        {
            _run = run ?? throw new ArgumentNullException(nameof(run));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _probes = probes;
            _identity = identity;
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
                                             FixerRunRecord record,
                                             Func<FixerRunIdentity> identity = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (!string.Equals(run.RunId, record.RunId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("run " + run.RunId + " cannot continue record "
                    + record.RunId + " — they are different runs", nameof(record));

            // A record whose checks no longer match the ones this build offers
            // cannot be continued honestly: new results would land beside old
            // ones under a stage list that describes neither sitting. Refused
            // rather than reconciled — the run stays readable and exportable,
            // which is what it is for.
            string was = StageShape(record.Stages.Select(s => s.Id));
            string now = StageShape(run.Set.Stages.Select(s => s.Id));
            if (!string.Equals(was, now, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("run " + record.RunId + " was recorded with a "
                    + "different set of checks (" + was + ") than this version offers ("
                    + now + "), so it cannot be continued", nameof(record));

            var journal = new FixerRunJournal(run, store, probes, record, identity);
            record.EndedUtc = null;
            record.EndReason = "";
            // A NEW sitting, never an extension of the old one. The stages
            // already recorded were measured in a window that closed; saying so
            // is the whole difference between resuming and pretending.
            record.Sittings.Add(new RecordedSitting { StartedUtc = run.NowUtc });
            return journal;
        }

        private static string StageShape(IEnumerable<string> ids)
            => string.Join(", ", ids.Select(id => (id ?? "").Trim()));

        private FixerRunJournal(FixerRun run, FixerRunStore store, FixerSettingProbeSet probes,
                                FixerRunRecord adopt, Func<FixerRunIdentity> identity)
        {
            _run = run;
            _store = store;
            _probes = probes;
            _record = adopt;
            _identity = identity;
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

                // Close the sitting this journal opened, and only that one. A
                // sitting left open belonged to a window that never closed
                // properly, which the document is entitled to say.
                RecordedSitting sitting = _record.Sittings.LastOrDefault();
                if (sitting != null && sitting.EndedUtc == null)
                {
                    sitting.EndedUtc = _record.EndedUtc;
                    sitting.EndReason = _record.EndReason;
                }

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
            CaptureIdentityOnce();

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

        /// <summary>
        /// Read the radio's identity and the software's, once, at the first
        /// recording.
        /// </summary>
        /// <remarks>
        /// <b>At the first recording, not when the window opened.</b> The Fixer
        /// is frequently opened before the operator connects, and identity read
        /// from nothing would put "not reported" against a radio that was
        /// perfectly readable by the time anything was measured. The first
        /// recording is the earliest moment a measurement exists to attach it
        /// to, which is the moment it describes.
        /// </remarks>
        private void CaptureIdentityOnce()
        {
            if (_identity == null) return;
            if (_record.Station.Count > 0 || _record.Software.Count > 0) return;
            try
            {
                FixerRunIdentity read = _identity();
                if (read == null) return;
                _record.Station.AddRange(read.Station ?? Array.Empty<string>());
                _record.Software.AddRange(read.Software ?? Array.Empty<string>());
            }
            catch (Exception ex)
            {
                // An unreadable identity costs the identity lines, never the
                // measurement being recorded.
                Trouble("reading the radio and software identity", ex);
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
