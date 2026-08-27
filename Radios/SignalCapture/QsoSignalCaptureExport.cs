#nullable enable
using System;
using System.Globalization;
using Radios.Fixer.Evidence;

namespace Radios.SignalCapture
{
    /// <summary>
    /// A saved capture as something an operator can hand to somebody else: a
    /// standalone HTML document, or plain text. A shell over the baked report,
    /// same as <see cref="FixerRunExport"/> and sharing its pieces.
    /// </summary>
    public static class QsoSignalCaptureExport
    {
        /// <summary>The record as a standalone HTML document.</summary>
        public static string StandaloneHtml(QsoSignalCaptureRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            string title = string.IsNullOrWhiteSpace(record.Label)
                ? "QSO signal capture — " + record.CaptureId
                : record.Label + " — QSO signal capture " + record.CaptureId;
            return EvidenceHtmlShell.Standalone(title, null, record.ReportHtml);
        }

        /// <summary>The plain-text form, exactly what Copy puts on the clipboard.</summary>
        public static string PlainText(QsoSignalCaptureRecord record)
            => Radios.Fixer.Evidence.EvidenceExportWriter.PlainText(record);

        /// <summary>A filename stem for exports, e.g.
        /// "signal-capture-A52-5T2-20260826-2114". The id is in the name
        /// because the id is what a conversation about the capture quotes.</summary>
        public static string FileBaseName(QsoSignalCaptureRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return "signal-capture-" + record.CaptureId + "-"
                 + record.StartedUtc.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        }

        /// <summary>Write the HTML form. False (traced) on failure — the
        /// caller owns telling the operator.</summary>
        public static bool WriteHtml(QsoSignalCaptureRecord record, string path)
            => EvidenceExportWriter.Write(path, () => StandaloneHtml(record),
                                          "QsoSignalCaptureExport");

        /// <summary>Write the plain-text form. False (traced) on failure.</summary>
        public static bool WriteText(QsoSignalCaptureRecord record, string path)
            => EvidenceExportWriter.Write(path, () => PlainText(record),
                                          "QsoSignalCaptureExport");
    }
}
