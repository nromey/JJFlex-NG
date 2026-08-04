using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JJFlexWpf
{
    /// <summary>
    /// Renders the help markdown subset to HTML (for <see cref="Dialogs.HtmlInfoDialog"/>)
    /// and to plain text (for the fallback path when WebView2 is unavailable).
    ///
    /// The subset deliberately matches <c>docs/help/convert-md.ps1</c>, which builds
    /// the CHM from the same .md files. One document authored once therefore reaches
    /// the user two ways — F1 help and the in-app dialog — without a second copy to
    /// keep in sync. Anything this renderer cannot express is something the CHM
    /// build would render differently, which is exactly the divergence to avoid.
    ///
    /// Tables are the one deliberate omission: they are banned in screen-reader
    /// facing material, so a table here means the source document needs fixing.
    /// A stray table row renders as an ordinary paragraph rather than vanishing —
    /// losing content silently is worse than rendering it plainly.
    /// </summary>
    public static class HelpMarkdown
    {
        private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.ExplicitCapture;

        private static readonly Regex Bold = new(@"\*\*(?<text>.+?)\*\*", Opts);
        private static readonly Regex Italic = new(@"(?<!\*)\*(?!\*)(?<text>.+?)(?<!\*)\*(?!\*)", Opts);
        private static readonly Regex Code = new(@"`(?<text>.+?)`", Opts);
        private static readonly Regex Link = new(@"\[(?<text>.+?)\]\((?<url>.+?)\)", Opts);
        private static readonly Regex Heading = new(@"^(?<hashes>#{1,6})\s+(?<text>.+)$", Opts);
        private static readonly Regex Bullet = new(@"^\s*[-*]\s+(?<text>.+)$", Opts);
        private static readonly Regex Numbered = new(@"^\s*(?<number>\d+)\.\s+(?<text>.+)$", Opts);
        private static readonly Regex TableSeparator = new(@"^\|[\s\-:]+\|", Opts);
        private static readonly Regex TableRow = new(@"^\|(?<cells>.+)\|$", Opts);
        private static readonly Regex TipLine = new(@"^\*\*Tip:\*\*\s*(?<text>.+)$", Opts);
        private static readonly Regex WarningLine = new(@"^\*\*Warning:\*\*\s*(?<text>.+)$", Opts);

        /// <summary>
        /// Wrap rendered markdown in a complete, self-contained document.
        ///
        /// Styling is inline because the dialog navigates to a string with no
        /// origin — there is nowhere to link a stylesheet from. Colours are stated
        /// for the ordinary case and overridden for dark mode; Windows High
        /// Contrast needs no special handling here, since forced-colors mode
        /// replaces them wholesale.
        /// </summary>
        public static string ToHtml(string markdown, string title)
        {
            return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8""/>
<title>{Escape(title)}</title>
<style>
body {{ font-family: Segoe UI, sans-serif; font-size: 14px; color: #222; background: #f9f9f9; margin: 16px; line-height: 1.5; }}
h1 {{ font-size: 20px; margin-bottom: 4px; }}
h2 {{ font-size: 16px; margin-top: 18px; margin-bottom: 4px; }}
h3 {{ font-size: 14px; margin-top: 14px; margin-bottom: 4px; }}
p {{ margin: 6px 0; }}
ul, ol {{ margin: 6px 0 6px 22px; padding: 0; }}
li {{ margin: 3px 0; }}
code {{ font-family: Consolas, monospace; }}
.tip, .warning {{ margin: 10px 0; padding: 8px; border-left: 4px solid #888; }}
h1:focus, h2:focus, body:focus {{ outline: none; }}
@media (prefers-color-scheme: dark) {{
  body {{ color: #eee; background: #1e1e1e; }}
}}
</style>
</head>
<body>
{RenderBody(markdown)}
</body>
</html>";
        }

        /// <summary>
        /// Flatten markdown to plain prose for a read-only text box: markers
        /// removed, headings and list structure kept as blank lines and bullets
        /// so the shape survives when the markup cannot.
        /// </summary>
        public static string ToPlainText(string markdown)
        {
            var sb = new StringBuilder();
            bool lastWasBlank = true;

            foreach (var rawLine in SplitLines(markdown))
            {
                var line = rawLine.TrimEnd();

                if (line.Length == 0)
                {
                    if (!lastWasBlank) sb.AppendLine();
                    lastWasBlank = true;
                    continue;
                }

                if (TableSeparator.IsMatch(line)) continue;

                var heading = Heading.Match(line);
                if (heading.Success)
                {
                    if (!lastWasBlank) sb.AppendLine();
                    sb.AppendLine(StripInline(heading.Groups["text"].Value));
                    lastWasBlank = false;
                    continue;
                }

                var bullet = Bullet.Match(line);
                if (bullet.Success)
                {
                    sb.AppendLine("• " + StripInline(bullet.Groups["text"].Value));
                    lastWasBlank = false;
                    continue;
                }

                var numbered = Numbered.Match(line);
                if (numbered.Success)
                {
                    sb.AppendLine($"{numbered.Groups["number"].Value}. {StripInline(numbered.Groups["text"].Value)}");
                    lastWasBlank = false;
                    continue;
                }

                sb.AppendLine(StripInline(line));
                lastWasBlank = false;
            }

            return sb.ToString().TrimEnd();
        }

        private static string RenderBody(string markdown)
        {
            var html = new List<string>();
            string? openList = null;

            void CloseList()
            {
                if (openList != null)
                {
                    html.Add($"</{openList}>");
                    openList = null;
                }
            }

            foreach (var rawLine in SplitLines(markdown))
            {
                var line = rawLine.TrimEnd();

                if (line.Length == 0) { CloseList(); continue; }

                // Tables are banned in screen-reader material; drop the pipes and
                // render the cells as prose rather than losing the text.
                if (TableSeparator.IsMatch(line)) continue;
                var tableRow = TableRow.Match(line);
                if (tableRow.Success)
                {
                    CloseList();
                    var cells = tableRow.Groups["cells"].Value.Split('|');
                    html.Add($"<p>{Inline(string.Join(" — ", TrimAll(cells)))}</p>");
                    continue;
                }

                var heading = Heading.Match(line);
                if (heading.Success)
                {
                    CloseList();
                    int level = heading.Groups["hashes"].Value.Length;
                    html.Add($"<h{level}>{Inline(heading.Groups["text"].Value)}</h{level}>");
                    continue;
                }

                var bullet = Bullet.Match(line);
                if (bullet.Success)
                {
                    if (openList != "ul") { CloseList(); html.Add("<ul>"); openList = "ul"; }
                    html.Add($"<li>{Inline(bullet.Groups["text"].Value)}</li>");
                    continue;
                }

                var numbered = Numbered.Match(line);
                if (numbered.Success)
                {
                    if (openList != "ol") { CloseList(); html.Add("<ol>"); openList = "ol"; }
                    html.Add($"<li>{Inline(numbered.Groups["text"].Value)}</li>");
                    continue;
                }

                CloseList();

                var tip = TipLine.Match(line);
                if (tip.Success)
                {
                    html.Add($"<div class=\"tip\"><strong>Tip:</strong> {Inline(tip.Groups["text"].Value)}</div>");
                    continue;
                }

                var warning = WarningLine.Match(line);
                if (warning.Success)
                {
                    html.Add($"<div class=\"warning\"><strong>Warning:</strong> {Inline(warning.Groups["text"].Value)}</div>");
                    continue;
                }

                html.Add($"<p>{Inline(line)}</p>");
            }

            CloseList();
            return string.Join("\n", html);
        }

        private static IEnumerable<string> TrimAll(string[] parts)
        {
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) yield return trimmed;
            }
        }

        /// <summary>
        /// HTML-escape first, then apply inline markup — so text can never inject
        /// tags, and the tags this method adds survive.
        /// </summary>
        private static string Inline(string text)
        {
            var escaped = Escape(text);
            escaped = Code.Replace(escaped, "<code>${text}</code>");
            escaped = Link.Replace(escaped, "<a href=\"${url}\">${text}</a>");
            escaped = Bold.Replace(escaped, "<strong>${text}</strong>");
            escaped = Italic.Replace(escaped, "<em>${text}</em>");
            return escaped;
        }

        private static string StripInline(string text)
        {
            text = Link.Replace(text, "${text}");
            text = Bold.Replace(text, "${text}");
            text = Italic.Replace(text, "${text}");
            text = Code.Replace(text, "${text}");
            return text;
        }

        private static string Escape(string text) => WebUtility.HtmlEncode(text);

        private static string[] SplitLines(string text) =>
            (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
}
