using System;

namespace Radios
{
    /// <summary>
    /// Every help page opens with a short YAML block carrying its index keywords
    /// (#826), between two lines of three hyphens. The help build reads it; a
    /// reader that shows a page itself must not, or an operator hears
    /// "keywords: connectors, front panel" before the page's first heading.
    /// </summary>
    /// <remarks>
    /// Lives here rather than beside <c>JJFlexWpf.HelpMarkdown</c>, its only
    /// caller today, so <c>Radios.Tests</c> can test it without constructing
    /// anything from the WPF project. Deliberately small: it removes one block at
    /// the very top and nothing else. A line of three hyphens anywhere later in a
    /// page is a horizontal rule and stays.
    /// </remarks>
    public static class HelpFrontMatter
    {
        /// <summary>
        /// The page without its leading YAML block, if it has one; otherwise the
        /// page unchanged. A byte-order mark before the block is dropped with it.
        /// An opening line with no closing one is not a block, and the page is
        /// returned unchanged rather than half removed.
        /// </summary>
        public static string Strip(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return markdown ?? string.Empty;

            string text = markdown[0] == '﻿' ? markdown.Substring(1) : markdown;
            int firstEnd = LineEnd(text, 0);
            if (Line(text, 0, firstEnd).TrimEnd() != "---") return markdown;

            int pos = Next(text, firstEnd);
            while (pos < text.Length)
            {
                int end = LineEnd(text, pos);
                string line = Line(text, pos, end).TrimEnd();
                if (line == "---" || line == "...")
                {
                    int rest = Next(text, end);
                    // The blank lines between the block and the page are the
                    // block's, not the page's.
                    while (rest < text.Length)
                    {
                        int e = LineEnd(text, rest);
                        if (Line(text, rest, e).Trim().Length != 0) break;
                        rest = Next(text, e);
                    }
                    return text.Substring(rest);
                }
                pos = Next(text, end);
            }
            return markdown;
        }

        private static int LineEnd(string text, int start)
        {
            int i = text.IndexOf('\n', start);
            return i < 0 ? text.Length : i;
        }

        private static string Line(string text, int start, int end) =>
            text.Substring(start, end - start).TrimEnd('\r');

        private static int Next(string text, int lineEnd) =>
            lineEnd >= text.Length ? text.Length : lineEnd + 1;
    }
}
