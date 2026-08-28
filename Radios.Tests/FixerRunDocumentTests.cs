using System;
using Radios.Fixer.Evidence;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The exported run: the document an operator hands to FlexRadio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Most of these are tests about SEQUENCE and about ABSENCE</b>, which
    /// is unusual and is the point — the same reasoning as
    /// <see cref="EvidenceOrderTests"/>. Both orders produce a document that
    /// reads perfectly well, and a missing line looks exactly like a line that
    /// was never worth printing, so neither property can be caught by reading.
    /// </para>
    /// <para>
    /// The standard is #217: would this line still be useful to a reader who
    /// distrusts our software completely? The radio's own identity passes. A
    /// meter reading with the frequency, mode, antenna and power it was taken
    /// at passes. Our report does not — not because it is wrong, but because it
    /// is ours — so it goes behind everything checkable, under a heading that
    /// says whose it is.
    /// </para>
    /// </remarks>
    public class FixerRunDocumentTests
    {
        private static FixerRunRecord Measured()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.ReportText = "BODY-OF-THE-REPORT";
            record.ReportHtml = "<p>BODY-OF-THE-REPORT</p>";
            record.Station.Add("Model: FLEX-6300");
            record.Station.Add("Serial number: 1234-5678-6300-0001");
            record.Station.Add("Firmware version: 3.8.24");
            record.Software.Add("JJ Flexible version: 4.1.16.1403");
            record.Results.Add(EvidenceRecords.Ran("boil", 1,
                EvidenceRecords.Setting("tune-power", "Tune power", "10 watts"),
                EvidenceRecords.Setting("tx-antenna", "Transmit antenna", "ANT1")));
            return record;
        }

        private static int At(string text, string fragment)
        {
            int i = text.IndexOf(fragment, StringComparison.Ordinal);
            Assert.True(i >= 0, "the exported document is missing: " + fragment);
            return i;
        }

        /// <summary>A station line, used as the anchor for "where the radio
        /// section is". Deliberately not the word "Radio", which also occurs
        /// inside "FlexRadio" in the opening paragraph — an anchor that matches
        /// something earlier than the thing it names turns an order test into a
        /// test that cannot fail.</summary>
        private const string RadioAnchor = "Model: FLEX-6300";

        // -------- the order that carries the meaning --------

        [Fact]
        public void Everything_checkable_comes_before_anything_only_we_assert()
        {
            string t = FixerRunExport.PlainText(Measured());

            Assert.True(At(t, RadioAnchor) < At(t, FixerRunDocument.ReportHeading),
                "the radio's own identity must precede our report");
            Assert.True(At(t, "Conditions each measurement was taken under")
                      < At(t, FixerRunDocument.ReportHeading),
                "the conditions must precede our report");
            Assert.True(At(t, FixerRunDocument.ReportHeading)
                      < At(t, "How this document was produced"),
                "the provenance footer comes last");
        }

        [Fact]
        public void The_report_is_labelled_as_ours_rather_than_left_to_look_like_a_measurement()
        {
            string t = FixerRunExport.PlainText(Measured());
            Assert.Contains("as JJ Flexible wrote it", t);
        }

        [Fact]
        public void The_same_order_holds_in_the_html_form()
        {
            // One content model, two renderings. If the forms could drift, an
            // operator and a support engineer would read different documents
            // with the same Test ID on them.
            string html = FixerRunExport.StandaloneHtml(Measured());

            Assert.True(At(html, RadioAnchor) < At(html, FixerRunDocument.ReportHeading));
            Assert.True(At(html, FixerRunDocument.ReportHeading)
                      < At(html, "How this document was produced"));
        }

        [Fact]
        public void The_recorded_report_travels_verbatim_and_is_never_rerendered()
        {
            FixerRunRecord record = Measured();

            Assert.Contains(record.ReportHtml, FixerRunExport.StandaloneHtml(record));
            Assert.Contains(record.ReportText, FixerRunExport.PlainText(record));
        }

        // -------- the payload: conditions per measurement --------

        [Fact]
        public void Every_measurement_states_the_settings_it_was_taken_at()
        {
            // #217's third section, and #188's point: a meter reading with no
            // recorded conditions is a number a support engineer cannot
            // reproduce. This data was stored from the day fingerprints landed
            // and appeared in no document until now.
            string t = FixerRunExport.PlainText(Measured());

            Assert.Contains("Stage 1, Boil", t);
            Assert.Contains("Tune power: 10 watts.", t);
            Assert.Contains("Transmit antenna: ANT1.", t);
        }

        [Fact]
        public void A_setting_that_could_not_be_read_says_so_rather_than_reading_as_blank()
        {
            // An absent measurement and a null one look identical in a report
            // and need opposite responses from a reader.
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("boil", 1,
                EvidenceRecords.Setting("mode", "Mode", "")));

            Assert.Contains("Mode: could not be read.", FixerRunExport.PlainText(record));
        }

        [Fact]
        public void A_measurement_with_no_settings_names_the_absence()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1));

            Assert.Contains("No settings were recorded against this measurement.",
                            FixerRunExport.PlainText(record));
        }

        [Fact]
        public void A_measurement_replaced_by_a_rerun_is_kept_and_marked()
        {
            // The record is append-only precisely so a reading that cost a
            // transmission does not vanish because the check was run again.
            // The document has to say which one is current, or the reader sees
            // two contradictory measurements and no way to order them.
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("boil", 1,
                EvidenceRecords.Setting("tune-power", "Tune power", "10 watts")));
            var again = EvidenceRecords.Ran("boil", 2,
                EvidenceRecords.Setting("tune-power", "Tune power", "100 watts"));
            again.WasReRun = true;
            record.Results.Add(again);

            string t = FixerRunExport.PlainText(record);

            // The marker sits on the superseded measurement, not the current
            // one: between the two, in the order they were recorded.
            int first = At(t, "10 watts");
            int marker = At(t, "later replaced by a re-run");
            int second = At(t, "100 watts");
            Assert.True(first < marker && marker < second,
                "the re-run marker must mark the earlier measurement, not the later one");
        }

        [Fact]
        public void A_skipped_check_reads_as_not_run_never_as_a_measurement()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            var skipped = EvidenceRecords.Ran("boil", 1);
            skipped.Status = "Skipped";
            record.Results.Add(skipped);

            string t = FixerRunExport.PlainText(record);
            Assert.Contains("not run at", t);
            Assert.DoesNotContain("measured at", t);
        }

        // -------- named absences --------

        [Fact]
        public void A_run_with_no_recorded_radio_calls_that_a_gap_in_the_document()
        {
            // Not silence. A support desk reading no identity must know whether
            // the radio would not say or nobody asked it.
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1));

            string t = FixerRunExport.PlainText(record);

            Assert.Contains("The radio's identity was not recorded with this run", t);
            Assert.Contains("a gap in this document, not a statement about the radio", t);
            Assert.Contains("The software versions were not recorded", t);
        }

        [Fact]
        public void A_run_with_nothing_measured_says_so_rather_than_showing_an_empty_section()
        {
            string t = FixerRunExport.PlainText(EvidenceRecords.TwoStages());
            Assert.Contains("Nothing was measured in this run", t);
        }

        // -------- provenance and the trace join --------

        [Fact]
        public void The_provenance_says_we_read_the_radio_through_the_published_api()
        {
            string t = FixerRunExport.PlainText(Measured());

            Assert.Contains("published FlexLib API", t);
            Assert.Contains("JJ Flexible version: 4.1.16.1403", t);
        }

        [Fact]
        public void A_saved_trace_is_named_with_the_id_that_joins_it_to_this_run()
        {
            // The correlation #252 calls half-built: an archived run plus its
            // trace is a complete package joined by one string, and either half
            // can be checked against the other by somebody who trusts neither.
            FixerRunRecord record = Measured();
            record.CaptureArchivePath = @"C:\traces\jjflex-20260825.zip";

            string t = FixerRunExport.PlainText(record);

            Assert.Contains(@"C:\traces\jjflex-20260825.zip", t);
            Assert.Contains("carries the same Test ID, " + record.RunId, t);
        }

        // -------- the operator's own statements --------

        [Fact]
        public void Operator_declarations_are_marked_as_unverified_and_lead_the_document()
        {
            FixerRunRecord record = Measured();
            record.Declarations.Add(new RecordedDeclaration
            {
                Id = "antenna-load",
                AnswerId = "dummy-load",
                AnswerLabel = "A dummy load",
                AtUtc = EvidenceRecords.T0,
            });

            string t = FixerRunExport.PlainText(record);

            Assert.Contains("A dummy load", t);
            Assert.Contains("recorded them and did not verify them", t);
            Assert.True(At(t, "What the operator stated") < At(t, RadioAnchor),
                "the operator's own statements come before anything we read");
        }

        [Fact]
        public void With_no_declarations_the_section_is_omitted_rather_than_left_empty()
        {
            Assert.DoesNotContain("What the operator stated",
                                  FixerRunExport.PlainText(Measured()));
        }

        // -------- sittings: what a continued run does not claim --------

        [Fact]
        public void One_sitting_that_closed_properly_needs_no_paragraph()
        {
            FixerRunRecord record = Measured();
            record.Sittings.Add(new RecordedSitting
            {
                StartedUtc = EvidenceRecords.T0,
                EndedUtc = EvidenceRecords.T0.AddMinutes(20),
                EndReason = "closed",
            });

            Assert.DoesNotContain("When this run was worked on",
                                  FixerRunExport.PlainText(record));
        }

        [Fact]
        public void A_continued_run_says_outright_that_it_was_not_one_continuous_session()
        {
            // THE test for resume. A resumed run keeps one Test ID because the
            // measurements belong to one investigation — but a document that
            // let a reader assume they were taken at one sitting would be
            // exactly the lie the QSO analyzer refuses resume outright to
            // avoid. Ours can resume honestly only because it says this.
            FixerRunRecord record = Measured();
            record.Sittings.Add(new RecordedSitting
            {
                StartedUtc = EvidenceRecords.T0,
                EndedUtc = EvidenceRecords.T0.AddMinutes(20),
                EndReason = "abandoned",
            });
            record.Sittings.Add(new RecordedSitting { StartedUtc = EvidenceRecords.T0.AddDays(1) });

            string t = FixerRunExport.PlainText(record);

            Assert.Contains("worked on in 2 separate sittings", t);
            Assert.Contains("NOT all measured in one continuous session", t);
            Assert.Contains("Sitting 1", t);
            Assert.Contains("Sitting 2", t);
            Assert.Contains("(abandoned)", t);
        }

        [Fact]
        public void A_sitting_that_never_closed_is_reported_as_such()
        {
            FixerRunRecord record = Measured();
            record.Sittings.Add(new RecordedSitting { StartedUtc = EvidenceRecords.T0 });

            string t = FixerRunExport.PlainText(record);
            Assert.Contains("did not close normally", t);
            Assert.Contains("did not end normally", t);
        }

        // -------- the name --------

        [Fact]
        public void The_title_always_carries_the_id_and_carries_the_name_when_there_is_one()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            Assert.Equal("Kettle check report — run AAA-222", FixerRunDocument.Title(record));

            record.Label = "Don's radio, before the fix";
            Assert.Equal("Kettle check report — run AAA-222 (Don's radio, before the fix)",
                         FixerRunDocument.Title(record));
            Assert.Contains("AAA-222", FixerRunDocument.Title(record));
        }

        [Fact]
        public void A_named_run_says_the_id_is_still_the_thing_to_quote()
        {
            FixerRunRecord record = Measured();
            record.Label = "Tuesday, when it worked";

            string t = FixerRunExport.PlainText(record);

            Assert.Contains("The operator named this run \"Tuesday, when it worked\"", t);
            Assert.Contains("is what to quote", t);
        }
    }
}
