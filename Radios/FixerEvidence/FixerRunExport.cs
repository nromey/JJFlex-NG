using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using JJTrace;

namespace Radios.Fixer.Evidence
{
    /// <summary>
    /// A saved run as something an operator can hand to somebody else: a
    /// standalone HTML document, or plain text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Still not a renderer.</b> The report body is the one
    /// <c>FixerReport</c> produced and the record carries, and it travels
    /// through here verbatim. What this class assembles around it is
    /// <see cref="FixerRunDocument"/>'s sections — the radio's identity, the
    /// conditions each measurement was taken under, the provenance — rendered
    /// through the same <see cref="EvidenceReportDocument"/> as everything
    /// else. Two forms, one content model, no second renderer.
    /// </para>
    /// <para>
    /// <b>Why the exported document is bigger than the report.</b> #217: it has
    /// to hold up for a reader who distrusts our software entirely, and the
    /// report alone does not — it is written for the operator and leads with
    /// our conclusions. See <see cref="FixerRunDocument"/> for the whole
    /// argument.
    /// </para>
    /// <para>
    /// HTML and plain text are the two formats our own code emits (per #252's
    /// ruling): HTML is the universal pandoc input, so an operator who wants
    /// PDF or DOCX converts freely, and we do not ship an external binary for
    /// a runtime export.
    /// </para>
    /// </remarks>
    public static class FixerRunExport
    {
        /// <summary>Heading level for the wrapper sections: h2, the same level
        /// the recorded report fragment was baked at, so the exported document
        /// has one flat, arrowable hierarchy under its h1.</summary>
        private const int WrapperHeadingLevel = 2;

        /// <summary>The record as a standalone HTML document.
        /// <paramref name="leadHtmlFragment"/>, when given, is inserted after
        /// the h1 — the viewer uses it for the what-has-changed section on a
        /// stopped run. It must already be HTML.</summary>
        public static string StandaloneHtml(FixerRunRecord record, string leadHtmlFragment = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            var body = new StringBuilder();
            body.Append(EvidenceReportDocument.HtmlFragment(
                FixerRunDocument.Before(record), WrapperHeadingLevel));
            body.Append("<h").Append(WrapperHeadingLevel).Append('>')
                .Append(EvidenceReportDocument.Esc(FixerRunDocument.ReportHeading))
                .Append("</h").Append(WrapperHeadingLevel).AppendLine(">");
            body.AppendLine(record.ReportHtml);
            body.Append(EvidenceReportDocument.HtmlFragment(
                FixerRunDocument.After(record), WrapperHeadingLevel));

            // The shell — head, title, readability CSS, no colors — is shared
            // with the QSO signal capture export via EvidenceHtmlShell.
            return EvidenceHtmlShell.Standalone(
                FixerRunDocument.Title(record),
                leadHtmlFragment,
                body.ToString());
        }

        /// <summary>
        /// The plain-text form — what an operator pastes into an email.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT <see cref="EvidenceExportWriter.PlainText"/>, which
        /// returns the bare recorded report and is what the QSO signal capture
        /// export wants. A Fixer run's export is the vendor-facing document, so
        /// it carries the identity and conditions around that report. Do not
        /// "simplify" this back to the shared helper; the two are different
        /// documents on purpose.
        /// </remarks>
        public static string PlainText(FixerRunRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            var sb = new StringBuilder();
            sb.Append(EvidenceReportDocument.PlainText(FixerRunDocument.Before(record)));
            sb.AppendLine();
            sb.AppendLine(FixerRunDocument.ReportHeading);
            sb.AppendLine(new string('-', FixerRunDocument.ReportHeading.Length));
            sb.AppendLine(record.ReportText.TrimEnd());
            sb.AppendLine();
            sb.Append(EvidenceReportDocument.PlainText(FixerRunDocument.After(record)));
            return sb.ToString();
        }

        /// <summary>A filename stem for exports: the check, the run id and the
        /// start stamp, e.g. "transmit-check-A52-5T2-20260825-2114". The id is
        /// in the name because the id is what a support thread quotes.</summary>
        public static string FileBaseName(FixerRunRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string set = (record.StageSetId ?? "").Trim();
            if (set.Length == 0) set = "fixer";
            return set + "-check-" + record.RunId + "-"
                 + record.StartedUtc.ToString("yyyyMMdd-HHmm",
                       System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Write the HTML form. False (traced) on failure — the
        /// caller owns telling the operator, because a silent failed export is
        /// an email that never gets its attachment.</summary>
        public static bool WriteHtml(FixerRunRecord record, string path,
                                     string leadHtmlFragment = null)
            => EvidenceExportWriter.Write(path, () => StandaloneHtml(record, leadHtmlFragment),
                                          "FixerRunExport");

        /// <summary>Write the plain-text form. False (traced) on failure.</summary>
        public static bool WriteText(FixerRunRecord record, string path)
            => EvidenceExportWriter.Write(path, () => PlainText(record), "FixerRunExport");
    }
}
