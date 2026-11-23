using System;

namespace Radios.Tests
{
    /// <summary>
    /// One shared reader for source-shape tests: the WHOLE text of a member
    /// (or any block) named by its signature, never a fixed-length window of
    /// it (#604).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> A test that read "the next 1,500 characters after the
    /// signature" went vacuously green the day its method grew past 1,500
    /// characters: the line it asserted on moved out of the window, and an
    /// absence check then found nothing to object to. Methods only grow, so
    /// that drift runs in one direction and nothing reports it.
    /// </para>
    /// <para>
    /// <b>The built-in positive control.</b> Every reader here THROWS when the
    /// signature is not found or its block cannot be closed. A test whose
    /// subject was renamed, moved or deleted fails, rather than passing every
    /// absence check against an empty string.
    /// </para>
    /// <para>
    /// <b>C#</b> is read by brace depth from the first <c>{</c> after the
    /// signature, skipping braces inside comments, character literals and
    /// string literals (regular, verbatim, interpolated and raw). A signature
    /// whose member is expression-bodied (<c>=&gt;</c> before any <c>{</c>) is
    /// read to its terminating <c>;</c>, never into the next member's block;
    /// one that ends in <c>;</c> with no body at all is refused.
    /// <b>VB</b> is read to the first <c>End Sub</c>, <c>End Function</c>,
    /// <c>End Property</c> or <c>End Operator</c> line at the signature line's
    /// own indentation, so a multi-line lambda's <c>End Sub</c> inside the
    /// member does not end it.
    /// </para>
    /// </remarks>
    internal static class SourceMember
    {
        /// <summary>The C# member from its signature through its closing brace.</summary>
        public static string CSharp(string source, string signature, int startIndex = 0)
        {
            var (at, _, close) = Locate(source, signature, startIndex);
            return source.Substring(at, close - at + 1);
        }

        /// <summary>
        /// The C# member (or block) whose text starts at <paramref name="at"/>,
        /// an index the caller already found, through its closing brace. For
        /// converting a fixed window read from that same index. Throws when
        /// <paramref name="at"/> is not in the source (an IndexOf that found
        /// nothing), so a missing subject fails here too.
        /// </summary>
        public static string CSharpAt(string source, int at)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (at < 0 || at >= source.Length)
                throw new InvalidOperationException("Source member not found (index " + at
                    + "). A test that cannot see its subject must fail, not pass.");
            string head = source.Substring(at, Math.Min(80, source.Length - at)).Split('\n')[0].TrimEnd('\r');
            var (_, _, close) = Locate(source, string.Empty, at, head);
            return source.Substring(at, close - at + 1);
        }

        /// <summary>The C# member's block alone: from its opening brace through its closing one.</summary>
        public static string CSharpBlock(string source, string signature, int startIndex = 0)
        {
            var (_, open, close) = Locate(source, signature, startIndex);
            return source.Substring(open, close - open + 1);
        }

        /// <summary>
        /// The VB member from its signature through the end of its closing
        /// <c>End Sub</c> / <c>End Function</c> / <c>End Property</c> /
        /// <c>End Operator</c> line, found at the signature line's indentation.
        /// </summary>
        public static string VisualBasic(string source, string signature, int startIndex = 0)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            int at = source.IndexOf(signature, startIndex, StringComparison.Ordinal);
            if (at < 0)
                throw new InvalidOperationException("Source member not found: " + signature
                    + ". A test that cannot see its subject must fail, not pass.");

            int lineStart = source.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
            if (at == 0) lineStart = 0;
            int indentEnd = lineStart;
            while (indentEnd < source.Length && (source[indentEnd] == ' ' || source[indentEnd] == '\t')) indentEnd++;
            string indent = source.Substring(lineStart, indentEnd - lineStart);

            int pos = source.IndexOf('\n', at);
            while (pos >= 0 && pos < source.Length)
            {
                int next = pos + 1;
                int eol = source.IndexOf('\n', next);
                if (eol < 0) eol = source.Length;
                string line = source.Substring(next, eol - next).TrimEnd('\r');
                if (line.StartsWith(indent, StringComparison.Ordinal))
                {
                    string rest = line.Substring(indent.Length);
                    if (rest.Length > 0 && rest[0] != ' ' && rest[0] != '\t' && IsVbMemberEnd(rest))
                        return source.Substring(at, eol - at).TrimEnd('\r');
                }
                if (eol >= source.Length) break;
                pos = eol;
            }
            throw new InvalidOperationException("No closing End line for VB member: " + signature);
        }

        private static bool IsVbMemberEnd(string rest)
        {
            foreach (string end in new[] { "End Sub", "End Function", "End Property", "End Operator" })
            {
                if (rest.StartsWith(end, StringComparison.Ordinal)
                    && (rest.Length == end.Length || !char.IsLetterOrDigit(rest[end.Length])))
                    return true;
            }
            return false;
        }

        private static (int At, int Open, int Close) Locate(string source, string signature, int startIndex, string? describe = null)
        {
            describe ??= signature;
            if (source == null) throw new ArgumentNullException(nameof(source));
            int at = source.IndexOf(signature, startIndex, StringComparison.Ordinal);
            if (at < 0)
                throw new InvalidOperationException("Source member not found: " + signature
                    + ". A test that cannot see its subject must fail, not pass.");

            int i = at + signature.Length;
            // The opening brace, outside comments and literals. An "=>" first
            // means an expression-bodied member (or a lambda statement): it is
            // read to its terminating ';' at its own nesting level. A ';' before
            // either means the member has no body here at all.
            int open = -1;
            while (i < source.Length)
            {
                int skipped = Skip(source, i);
                if (skipped != i) { i = skipped; continue; }
                char c = source[i];
                if (c == '{') { open = i; break; }
                if (c == '=' && i + 1 < source.Length && source[i + 1] == '>')
                    return (at, i, ExpressionEnd(source, i + 2, describe));
                if (c == ';')
                    throw new InvalidOperationException("Member has no body (it ends in ';' first): " + describe);
                i++;
            }
            if (open < 0) throw new InvalidOperationException("No opening brace after: " + describe);

            int depth = 0;
            i = open;
            while (i < source.Length)
            {
                int skipped = Skip(source, i);
                if (skipped != i) { i = skipped; continue; }
                char c = source[i];
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return (at, open, i);
                i++;
            }
            throw new InvalidOperationException("Unbalanced braces after: " + describe);
        }

        /// <summary>The ';' that ends an expression body begun at <paramref name="from"/>,
        /// at the expression's own nesting level (braces, parentheses and
        /// brackets inside it are stepped over).</summary>
        private static int ExpressionEnd(string source, int from, string describe)
        {
            int depth = 0;
            int i = from;
            while (i < source.Length)
            {
                int skipped = Skip(source, i);
                if (skipped != i) { i = skipped; continue; }
                char c = source[i];
                if (c == '{' || c == '(' || c == '[') depth++;
                else if (c == '}' || c == ')' || c == ']') depth--;
                else if (c == ';' && depth <= 0) return i;
                i++;
            }
            throw new InvalidOperationException("Unterminated expression body after: " + describe);
        }

        /// <summary>
        /// If a comment, character literal or string literal starts at
        /// <paramref name="i"/>, the index just past it; otherwise
        /// <paramref name="i"/> itself.
        /// </summary>
        private static int Skip(string s, int i)
        {
            char c = s[i];
            char next = i + 1 < s.Length ? s[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                int eol = s.IndexOf('\n', i);
                return eol < 0 ? s.Length : eol;
            }
            if (c == '/' && next == '*')
            {
                int close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                return close < 0 ? s.Length : close + 2;
            }
            if (c == '\'')
            {
                int j = i + 1;
                if (j < s.Length && s[j] == '\\') j += 2; else j += 1;
                while (j < s.Length && s[j] != '\'' && s[j] != '\n') j++;
                return Math.Min(s.Length, j + 1);
            }
            if (c != '"') return i;

            // How many quotes in a row: three or more opens a raw string.
            int run = 0;
            while (i + run < s.Length && s[i + run] == '"') run++;
            if (run >= 3)
            {
                string fence = new string('"', run);
                int close = s.IndexOf(fence, i + run, StringComparison.Ordinal);
                return close < 0 ? s.Length : close + run;
            }

            bool verbatim = (i >= 1 && s[i - 1] == '@') || (i >= 2 && s[i - 1] == '$' && s[i - 2] == '@');
            int k = i + 1;
            while (k < s.Length)
            {
                char d = s[k];
                if (verbatim)
                {
                    if (d == '"')
                    {
                        if (k + 1 < s.Length && s[k + 1] == '"') { k += 2; continue; }
                        return k + 1;
                    }
                }
                else
                {
                    if (d == '\\') { k += 2; continue; }
                    if (d == '"' || d == '\n') return k + 1;
                }
                k++;
            }
            return s.Length;
        }
    }
}
