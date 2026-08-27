using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Radios.Fixer.Evidence
{
    public enum EvidenceItemKind { Paragraph, Bullet, Preformatted }

    public readonly struct EvidenceItem
    {
        public readonly EvidenceItemKind Kind;
        public readonly string Text;
        public EvidenceItem(EvidenceItemKind kind, string text) { Kind = kind; Text = text ?? ""; }
    }

    public sealed class EvidenceSection
    {
        public string Title = "";
        public readonly List<EvidenceItem> Items = new List<EvidenceItem>();
        public void Para(string t) => Items.Add(new EvidenceItem(EvidenceItemKind.Paragraph, t));
        public void Bullet(string t) => Items.Add(new EvidenceItem(EvidenceItemKind.Bullet, t));
        public void Pre(string t) => Items.Add(new EvidenceItem(EvidenceItemKind.Preformatted, t));
    }

    /// <summary>
    /// One content model, two renderings — the rule every evidence report
    /// follows, in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted from <see cref="Radios.Fixer.FixerReport"/> when the QSO
    /// signal analyzer (#271) needed the identical guarantee: if the HTML form
    /// and the plain-text form could drift apart, an operator and a support
    /// engineer would each be reading a different report with the same ID on
    /// it. Each report family builds its own list of sections; the two
    /// renderings here are the only way either form is ever produced.
    /// </para>
    /// <para>
    /// The HTML form is prose only — no controls, no tabindex — so however
    /// long it grows it costs zero tab stops. Headings start at the caller's
    /// level so a hosting page can slot the fragment under its own hierarchy
    /// without a skip.
    /// </para>
    /// </remarks>
    public static class EvidenceReportDocument
    {
        /// <summary>The plain-text form — what Copy puts on the clipboard.</summary>
        public static string PlainText(IEnumerable<EvidenceSection> sections)
        {
            if (sections == null) throw new ArgumentNullException(nameof(sections));

            var sb = new StringBuilder();
            foreach (EvidenceSection s in sections)
            {
                if (s.Title.Length > 0)
                {
                    sb.AppendLine(s.Title);
                    sb.AppendLine(new string('-', s.Title.Length));
                }
                foreach (EvidenceItem item in s.Items)
                {
                    switch (item.Kind)
                    {
                        case EvidenceItemKind.Paragraph: sb.AppendLine(item.Text); break;
                        case EvidenceItemKind.Bullet: sb.AppendLine("- " + item.Text); break;
                        case EvidenceItemKind.Preformatted: sb.AppendLine(item.Text.TrimEnd()); break;
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        /// <summary>The HTML form, as a fragment.</summary>
        public static string HtmlFragment(IEnumerable<EvidenceSection> sections, int headingLevel)
        {
            if (sections == null) throw new ArgumentNullException(nameof(sections));
            if (headingLevel < 2 || headingLevel > 6)
                throw new ArgumentOutOfRangeException(nameof(headingLevel));

            string h = "h" + headingLevel.ToString(CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            foreach (EvidenceSection s in sections)
            {
                if (s.Title.Length > 0)
                    sb.Append('<').Append(h).Append('>').Append(Esc(s.Title))
                      .Append("</").Append(h).AppendLine(">");

                bool inList = false;
                foreach (EvidenceItem item in s.Items)
                {
                    if (inList && item.Kind != EvidenceItemKind.Bullet)
                    { sb.AppendLine("</ul>"); inList = false; }

                    switch (item.Kind)
                    {
                        case EvidenceItemKind.Paragraph:
                            sb.Append("<p>").Append(Esc(item.Text)).AppendLine("</p>");
                            break;
                        case EvidenceItemKind.Bullet:
                            if (!inList) { sb.AppendLine("<ul>"); inList = true; }
                            sb.Append("<li>").Append(Esc(item.Text)).AppendLine("</li>");
                            break;
                        case EvidenceItemKind.Preformatted:
                            sb.Append("<pre>").Append(Esc(item.Text.TrimEnd())).AppendLine("</pre>");
                            break;
                    }
                }
                if (inList) sb.AppendLine("</ul>");
            }
            return sb.ToString();
        }

        internal static string Esc(string s)
            => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
