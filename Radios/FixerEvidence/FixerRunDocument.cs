using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Radios.Fixer.Evidence
{
    /// <summary>
    /// The sections an EXPORTED run carries around the report it already holds
    /// — the half of the document that has to survive a reader who distrusts
    /// our software entirely (#217).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a wrapper and not a rewrite.</b> The report inside the
    /// record is the one <c>FixerReport</c> wrote at the moment of recording,
    /// and it is addressed to the operator: it leads with what was found and
    /// what to do, so somebody who stops after one screen still has the answer.
    /// That is right, and #217 explicitly does not want the app going coy with
    /// its own operator. But the same document's other reader is a support
    /// engineer at FlexRadio, and for them the ordering rule is the opposite:
    /// everything a reader could have taken for themselves comes first, and
    /// anything only JJ Flexible asserts comes last and says so.
    /// </para>
    /// <para>
    /// Both are satisfied by leaving the report exactly as written and putting
    /// the checkable material — the radio's own identity, and the conditions
    /// each measurement was taken under — in front of it, with the provenance
    /// behind it. The report body is then unmistakably the part this
    /// application wrote, which is what "labelled as ours" means in practice.
    /// </para>
    /// <para>
    /// <b>The conditions section is the payload, and it existed as data long
    /// before it existed as prose.</b> Every stage result already carries the
    /// settings fingerprint taken the moment it ran (#252 part 1), and until
    /// now that data was read only by the staleness check — so an exported
    /// report stated a power reading without ever stating the power, the
    /// antenna port, the frequency or the mode it was taken at. A meter reading
    /// with no recorded conditions is a number a support engineer cannot
    /// reproduce, which is #188's point promoted by #217 into a gap in the
    /// payload.
    /// </para>
    /// <para>
    /// <b>Absences are named, never omitted.</b> An unread setting and a
    /// setting with no value look identical in a report and need opposite
    /// responses from a reader, so an unreadable value says it could not be
    /// read and a missing identity says it was never recorded.
    /// </para>
    /// <para>
    /// Renders through <see cref="EvidenceReportDocument"/> like everything
    /// else here: one content model, two forms, no second renderer.
    /// </para>
    /// </remarks>
    public static class FixerRunDocument
    {
        /// <summary>
        /// The title of the exported document. The Test ID is always in it —
        /// it is what a support thread quotes — and the operator's own name for
        /// the run joins it when there is one, because a file called nothing
        /// but an id and a date is the problem the rename exists to solve.
        /// </summary>
        public static string Title(FixerRunRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string title = record.StageSetName + " test report — run " + record.RunId;
            return string.IsNullOrWhiteSpace(record.Label)
                ? title
                : title + " (" + record.Label.Trim() + ")";
        }

        /// <summary>Everything that goes in front of the recorded report.</summary>
        public static List<EvidenceSection> Before(FixerRunRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            var sections = new List<EvidenceSection>
            {
                HowToRead(record),
            };

            EvidenceSection declared = Declared(record);
            if (declared != null) sections.Add(declared);

            sections.Add(Radio(record));
            sections.Add(Conditions(record));

            EvidenceSection sittings = Sittings(record);
            if (sittings != null) sections.Add(sittings);

            return sections;
        }

        /// <summary>Everything that goes behind it.</summary>
        public static List<EvidenceSection> After(FixerRunRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return new List<EvidenceSection> { Provenance(record) };
        }

        /// <summary>The heading the recorded report sits under, so a reader can
        /// see where our voice starts.</summary>
        public const string ReportHeading = "The report, as JJ Flexible wrote it";

        // ---------------- the sections ----------------

        private static EvidenceSection HowToRead(FixerRunRecord record)
        {
            var s = new EvidenceSection { Title = "How to read this document" };

            s.Para("This is a saved record of one run of JJ Flexible's "
                 + record.StageSetName + " tests. Test ID: " + record.RunId + ".");

            if (!string.IsNullOrWhiteSpace(record.Label))
                s.Para("The operator named this run \"" + record.Label.Trim() + "\". The Test "
                     + "ID above is the part that never changes, and is what to quote.");

            s.Para("It is arranged so that everything a reader could have established for "
                 + "themselves comes first. The radio's own identity and the conditions each "
                 + "measurement was taken under are below. JJ Flexible's report follows them, "
                 + "under its own heading, and is the part this application wrote.");

            s.Para("Every reading was taken through FlexRadio's published API, and every "
                 + "measurement is stated with the settings it was taken at, so the same "
                 + "measurement can be attempted again on the same radio. Nothing here has "
                 + "to be taken on trust.");

            s.Para("Where something could not be read, this document says so rather than "
                 + "leaving the line out. A measurement that is absent and a measurement "
                 + "that came back empty are different facts.");

            return s;
        }

        private static EvidenceSection Declared(FixerRunRecord record)
        {
            if (record.Declarations.Count == 0) return null;

            var s = new EvidenceSection { Title = "What the operator stated" };
            s.Para("These are the operator's own statements about their station, given "
                 + "during the run. JJ Flexible recorded them and did not verify them.");

            foreach (RecordedDeclaration d in record.Declarations.OrderBy(d => d.AtUtc))
            {
                string what = d.AnswerLabel.Length > 0 ? d.AnswerLabel
                            : d.AnswerId.Length > 0 ? d.AnswerId
                            : "(the answer was not recorded)";
                s.Bullet(what + " — stated at " + Stamp(d.AtUtc) + ".");
            }
            return s;
        }

        private static EvidenceSection Radio(FixerRunRecord record)
        {
            var s = new EvidenceSection { Title = "Radio" };

            if (record.Station.Count == 0)
            {
                s.Para("The radio's identity was not recorded with this run. That is a gap in "
                     + "this document, not a statement about the radio.");
                return s;
            }

            s.Para("Read from the radio itself when the first measurement was recorded.");
            foreach (string line in record.Station) s.Bullet(line);
            return s;
        }

        private static EvidenceSection Conditions(FixerRunRecord record)
        {
            var s = new EvidenceSection
            {
                Title = "Conditions each measurement was taken under",
            };

            if (record.Results.Count == 0)
            {
                s.Para("Nothing was measured in this run, so there are no conditions to "
                     + "state.");
                return s;
            }

            s.Para("Each test records the settings it depends on, at the values they held "
                 + "the moment it ran. A test listing no settings either declared none or "
                 + "ran before they could be read; either way nothing was recorded, and the "
                 + "absence is stated rather than filled in.");

            var latest = record.LatestResultsPerStage();

            foreach (RecordedStage r in record.Results.OrderBy(r => r.Sequence))
            {
                RecordedStageInfo info = record.Stages.FirstOrDefault(
                    st => string.Equals(st.Id, r.StageId, StringComparison.OrdinalIgnoreCase));

                string where = info == null
                    ? r.StageId
                    : "Stage " + info.Number.ToString(CultureInfo.InvariantCulture)
                      + ", " + info.Title;

                string what = r.Status switch
                {
                    "Skipped" => " — not run at " + Stamp(r.AtUtc),
                    "CouldNotRun" => " — attempted at " + Stamp(r.AtUtc) + " and could not run",
                    _ => " — measured at " + Stamp(r.AtUtc),
                };

                // A superseded measurement is kept and marked. The record is
                // append-only precisely so a reading that cost a transmission
                // does not vanish because the operator ran the check again.
                bool superseded = latest.TryGetValue(r.StageId ?? "", out RecordedStage newest)
                                  && !ReferenceEquals(newest, r);
                string note = superseded
                    ? " This measurement was later replaced by a re-run of the same test; it "
                      + "is kept here because it happened."
                    : "";

                string settings = r.Settings.Count == 0
                    ? " No settings were recorded against this measurement."
                    : " " + string.Join(" ", r.Settings.Select(DescribeSetting));

                s.Bullet(where + what + "." + settings + note);
            }

            return s;
        }

        /// <summary>One recorded setting as a sentence. An empty value means it
        /// could not be read, which is said rather than shown as blank.</summary>
        private static string DescribeSetting(RecordedSetting setting)
        {
            string name = setting.Name.Length > 0 ? setting.Name : setting.Key;
            return setting.Value.Length == 0
                ? name + ": could not be read."
                : name + ": " + setting.Value + ".";
        }

        private static EvidenceSection Sittings(FixerRunRecord record)
        {
            // One sitting that closed properly is the ordinary case and needs
            // no paragraph. Anything else is a fact about when these
            // measurements happened, and the reader is entitled to it.
            bool oneCleanSitting = record.Sittings.Count == 1
                                   && record.Sittings[0].EndedUtc != null;
            if (record.Sittings.Count == 0 || oneCleanSitting) return null;

            var s = new EvidenceSection { Title = "When this run was worked on" };

            if (record.Sittings.Count > 1)
            {
                s.Para("This run was worked on in " + record.Sittings.Count
                     + " separate sittings. The tests in this document were NOT all "
                     + "measured in one continuous session: each carries its own timestamp "
                     + "and the conditions it ran under, and should be read that way.");
            }
            else
            {
                s.Para("This run was worked on in one sitting, which did not close normally "
                     + "— the application was closed or stopped while the run was live. What "
                     + "was recorded before that point is below and is unaffected.");
            }

            int n = 0;
            foreach (RecordedSitting sitting in record.Sittings)
            {
                n++;
                string line = "Sitting " + n.ToString(CultureInfo.InvariantCulture) + ": began "
                            + Stamp(sitting.StartedUtc);
                line += sitting.EndedUtc == null
                    ? ", and did not end normally — nothing recorded the close."
                    : ", ended " + Stamp(sitting.EndedUtc.Value)
                      + (sitting.EndReason.Length > 0 ? " (" + sitting.EndReason + ")." : ".");
                s.Bullet(line);
            }

            return s;
        }

        private static EvidenceSection Provenance(FixerRunRecord record)
        {
            var s = new EvidenceSection { Title = "How this document was produced" };

            s.Para("JJ Flexible is a third-party client for FlexRadio transceivers. It reads "
                 + "and writes the radio through FlexRadio's published FlexLib API and has no "
                 + "other access to it. The readings above are the radio's own values as that "
                 + "API reported them.");

            if (record.Software.Count == 0)
            {
                s.Para("The software versions were not recorded with this run. That is a gap "
                     + "in this document.");
            }
            else
            {
                foreach (string line in record.Software) s.Bullet(line);
            }

            if (record.CaptureNote.Length > 0) s.Para(record.CaptureNote);

            if (record.CaptureArchivePath.Length > 0)
            {
                // The one string that joins a run to its trace. Both carry the
                // Test ID, so the pair can be checked against each other by
                // somebody who trusts neither. Worded so it does not repeat the
                // capture note printed just above it.
                string lead = record.CaptureNote.Length > 0
                    ? "Its archive was saved to "
                    : "A diagnostic recording covering this run was saved to ";
                s.Para(lead + record.CaptureArchivePath
                     + ". It carries the same Test ID, " + record.RunId
                     + ", so the two can be read together.");
            }

            s.Para("Run started " + Stamp(record.StartedUtc) + ". Last recorded "
                 + Stamp(record.LastRecordedUtc) + "."
                 + (record.EndedUtc == null
                     ? " No end was recorded for this run."
                     : " Ended " + Stamp(record.EndedUtc.Value)
                       + (record.EndReason.Length > 0 ? " (" + record.EndReason + ")." : ".")));

            return s;
        }

        private static string Stamp(DateTime utc)
            => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }
}
