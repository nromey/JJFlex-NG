using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using JJTrace;

namespace Radios.Fixer.Evidence
{
    /// <summary>
    /// The one way an evidence export reaches a file: UTF-8 without BOM, false
    /// (traced) on failure — the caller owns telling the operator, because a
    /// silent failed export is an email that never gets its attachment.
    /// </summary>
    public static class EvidenceExportWriter
    {

        /// <summary>
        /// The plain-text form of any evidence record: exactly what Copy would
        /// have put on the clipboard when the record was written. One home, so
        /// a change to what "plain text" means reaches every family at once.
        /// </summary>
        public static string PlainText(IEvidenceRecord record)
        {
            if (record == null) throw new System.ArgumentNullException(nameof(record));
            return record.ReportText;
        }

        public static bool Write(string path, Func<string> content, string traceName)
        {
            try
            {
                File.WriteAllText(path, content(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(traceName + ": could not write " + path + " — "
                                  + ex.Message, TraceLevel.Warning);
                return false;
            }
        }
    }

    /// <summary>
    /// The one standalone-HTML wrapper every exported evidence document uses:
    /// a document head, a title, and enough CSS to read comfortably — nothing
    /// that touches the content.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="FixerRunExport"/> for the QSO signal capture
    /// export (#271). No colors, deliberately — the reader's own light/dark
    /// preference wins by default.
    /// </remarks>
    public static class EvidenceHtmlShell
    {
        /// <summary>Wrap a rendered report body in a standalone document.
        /// <paramref name="titleText"/> is plain text (escaped here);
        /// <paramref name="leadHtmlFragment"/> and <paramref name="bodyHtml"/>
        /// must already be HTML.</summary>
        public static string Standalone(string titleText, string leadHtmlFragment, string bodyHtml)
        {
            string title = EvidenceReportDocument.Esc(titleText ?? "");
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.Append("<title>").Append(title).AppendLine("</title>");
            // Readability only: measure, spacing, and pre blocks that wrap
            // instead of forcing a horizontal scroll.
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
            sb.AppendLine(bodyHtml);
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }
    }
}
