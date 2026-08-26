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
    /// <b>This is a shell, not a renderer.</b> The report body is the one
    /// <c>FixerReport</c> produced and the record carries; this class wraps it
    /// in a document head, a title and enough CSS to read comfortably —
    /// nothing that touches the content. HTML and plain text are the two
    /// formats our own code emits (per #252's ruling): HTML is the universal
    /// pandoc input, so an operator who wants PDF or DOCX converts freely,
    /// and we do not ship an external binary for a runtime export.
    /// </para>
    /// </remarks>
    public static class FixerRunExport
    {
        /// <summary>The record as a standalone HTML document.
        /// <paramref name="leadHtmlFragment"/>, when given, is inserted after
        /// the h1 — the viewer uses it for the what-has-changed section on a
        /// stopped run. It must already be HTML.</summary>
        public static string StandaloneHtml(FixerRunRecord record, string leadHtmlFragment = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            string title = Esc(record.StageSetName + " check report — run " + record.RunId);
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.Append("<title>").Append(title).AppendLine("</title>");
            // Readability only: measure, spacing, and pre blocks that wrap
            // instead of forcing a horizontal scroll. No colors — the reader's
            // own light/dark preference wins by default.
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: Segoe UI, sans-serif; max-width: 70ch; "
                        + "margin: 1em auto; padding: 0 1em; line-height: 1.5; }");
            sb.AppendLine("pre { white-space: pre-wrap; overflow-wrap: break-word; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.Append("<h1>").Append(title).AppendLine("</h1>");
            if (!string.IsNullOrEmpty(leadHtmlFragment))
                sb.AppendLine(leadHtmlFragment);
            sb.AppendLine(record.ReportHtml);
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        /// <summary>The plain-text form, exactly what Copy would have put on
        /// the clipboard when the run was last recorded.</summary>
        public static string PlainText(FixerRunRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return record.ReportText;
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
            => Write(path, () => StandaloneHtml(record, leadHtmlFragment));

        /// <summary>Write the plain-text form. False (traced) on failure.</summary>
        public static bool WriteText(FixerRunRecord record, string path)
            => Write(path, () => PlainText(record));

        private static bool Write(string path, Func<string> content)
        {
            try
            {
                File.WriteAllText(path, content(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerRunExport: could not write " + path + " — "
                                  + ex.Message, TraceLevel.Warning);
                return false;
            }
        }

        private static string Esc(string s)
            => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
