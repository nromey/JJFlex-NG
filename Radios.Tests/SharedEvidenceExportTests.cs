using System;
using Radios.Fixer.Evidence;
using Radios.SignalCapture;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The export surface the two evidence families SHARE, and the one place
    /// they deliberately diverge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written because none of this was guarded.</b> Sprint 37 Track P
    /// checked, by positive control, whether the QSO analyzer's export was
    /// covered: gutting <see cref="EvidenceExportWriter.PlainText"/> — the
    /// shared helper the entire QSO plain-text export path runs through — left
    /// all 1487 tests green. No test source file mentioned
    /// <c>QsoSignalCaptureExport</c> or <c>QsoSignalCaptureReport</c> at all.
    /// </para>
    /// <para>
    /// That is the dangerous shape: the store had six tests, so a green suite
    /// felt like evidence the extraction was undamaged, while the report and
    /// export halves of it were entirely unwatched. A shared helper with two
    /// callers and no tests is one edit away from silently breaking the caller
    /// nobody is looking at.
    /// </para>
    /// </remarks>
    public class SharedEvidenceExportTests
    {
        private static QsoSignalCaptureRecord Capture() => new QsoSignalCaptureRecord
        {
            CaptureId = "B31-9K4",
            StartedUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            EndedUtc = new DateTime(2026, 1, 1, 12, 4, 0, DateTimeKind.Utc),
            ReportText = "CAPTURE-REPORT-TEXT",
            ReportHtml = "<p>CAPTURE-REPORT-HTML</p>",
        };

        // -------- the shared helper, over both families --------

        [Fact]
        public void The_shared_plain_text_helper_returns_the_baked_report_for_either_family()
        {
            // The report is BAKED, never re-rendered: what comes back out is
            // what a reader would have been told at the time, which is the
            // property that makes it evidence rather than a recalculation.
            Assert.Equal("CAPTURE-REPORT-TEXT", EvidenceExportWriter.PlainText(Capture()));

            FixerRunRecord run = EvidenceRecords.TwoStages();
            run.ReportText = "RUN-REPORT-TEXT";
            Assert.Equal("RUN-REPORT-TEXT", EvidenceExportWriter.PlainText(run));
        }

        [Fact]
        public void The_shared_helper_refuses_a_null_record_rather_than_returning_empty()
        {
            // Empty and absent are different facts everywhere else in this
            // layer; they must be here too, or a null slips out as a blank
            // export nobody notices until it is in an email.
            Assert.Throws<ArgumentNullException>(
                () => EvidenceExportWriter.PlainText(null!));
        }

        // -------- the QSO export, which had no tests at all --------

        [Fact]
        public void The_capture_export_carries_its_baked_report_in_both_forms()
        {
            QsoSignalCaptureRecord capture = Capture();

            Assert.Equal("CAPTURE-REPORT-TEXT", QsoSignalCaptureExport.PlainText(capture));
            Assert.Contains("<p>CAPTURE-REPORT-HTML</p>",
                            QsoSignalCaptureExport.StandaloneHtml(capture));
        }

        [Fact]
        public void The_capture_export_titles_by_the_id_and_by_the_name_when_there_is_one()
        {
            QsoSignalCaptureRecord capture = Capture();
            Assert.Contains("QSO signal capture — B31-9K4",
                            QsoSignalCaptureExport.StandaloneHtml(capture));

            capture.Label = "Tony, 20 metres";
            string titled = QsoSignalCaptureExport.StandaloneHtml(capture);
            Assert.Contains("Tony, 20 metres", titled);
            Assert.Contains("B31-9K4", titled);   // the id never leaves
        }

        [Fact]
        public void Capture_exports_reach_disk_and_report_a_failure_rather_than_throwing()
        {
            using var dir = new TempFolder();
            QsoSignalCaptureRecord capture = Capture();

            string htmlPath = System.IO.Path.Combine(dir.Path, "c.html");
            string textPath = System.IO.Path.Combine(dir.Path, "c.txt");
            Assert.True(QsoSignalCaptureExport.WriteHtml(capture, htmlPath));
            Assert.True(QsoSignalCaptureExport.WriteText(capture, textPath));
            Assert.Contains("CAPTURE-REPORT-HTML", System.IO.File.ReadAllText(htmlPath));
            Assert.Equal("CAPTURE-REPORT-TEXT", System.IO.File.ReadAllText(textPath));

            // The destination is a directory, so the write must fail — and say
            // so, because a silent failed export is an email that never gets
            // its attachment.
            Assert.False(QsoSignalCaptureExport.WriteText(capture, dir.Path));
        }

        // -------- where the two families deliberately part company --------

        [Fact]
        public void A_capture_exports_its_report_alone_and_a_run_exports_the_vendor_document()
        {
            // NOT an accident, and not a thing to tidy up. A Fixer run's export
            // is the document handed to FlexRadio, so it wraps the report in
            // the radio's identity, the conditions each measurement was taken
            // under, and the provenance (#217). A signal capture is a
            // measurement of a contact and its export is the report itself.
            //
            // Pinned because the obvious "simplification" — routing the run
            // through the shared helper like the capture does — would quietly
            // strip a support document back to a bare report, conflict with
            // nothing, and build cleanly.
            FixerRunRecord run = EvidenceRecords.TwoStages();
            run.ReportText = "RUN-REPORT-TEXT";
            run.Results.Add(EvidenceRecords.Ran("fill", 1));

            string runExport = FixerRunExport.PlainText(run);
            Assert.Contains("RUN-REPORT-TEXT", runExport);
            Assert.Contains("published FlexLib API", runExport);
            Assert.NotEqual(EvidenceExportWriter.PlainText(run), runExport);

            Assert.Equal(EvidenceExportWriter.PlainText(Capture()),
                         QsoSignalCaptureExport.PlainText(Capture()));
        }
    }
}
