using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// No string the application ships carries a task number: not anything an
    /// operator hears or sees, and not a line written to the diagnostic log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rulings (#468 (b), and #826 on 2026-10-27).</b> Task numbers stay
    /// in code comments and tests, where they link a change to the decision
    /// behind it, and are banned from anything an operator hears or sees. Trace
    /// strings were ruled the same way a day later: the diagnostic log travels
    /// in the support bundle a user sends, so a number written there is a number
    /// published.
    /// </para>
    /// <para>
    /// <b>Why trace strings are covered too, and not only operator text.</b>
    /// About a hundred trace lines carried numbers when the cleanup ran, and a
    /// branch not yet merged was adding more. A guard over operator text alone
    /// would let the cleanup come undone at the next merge.
    /// </para>
    /// <para>
    /// <b>It reads source text, not types.</b> <c>Radios.Tests</c> does not
    /// reference the WPF project, and most of these strings are built by
    /// concatenation at the call site, which no reflection can see. So it lexes
    /// every authored C# and VB file git knows about (tracked, plus untracked
    /// files not ignored, so a brand-new file counts), reads every string
    /// literal, and skips comments. A literal split across lines with <c>+</c>
    /// or <c>&amp;</c> is read piece by piece, so a number on any line of it is
    /// seen.
    /// </para>
    /// <para>
    /// <b>What it reads:</b> string literals in authored C# and VB (which holds
    /// every trace line, every <c>[Earcon]</c> description, every unbound-note
    /// detail in <c>KeyCommands</c>, every message box and every literal
    /// accessible name or help text set in code); every value in
    /// <c>Radios/Lexicon/*.json</c> (keys beginning with an underscore are notes
    /// and are skipped); and the text and attribute values of every XAML and
    /// resx file, comments excluded.
    /// </para>
    /// <para>
    /// <b>What it cannot see:</b> a number assembled at run time from separate
    /// pieces (a <c>"#"</c> literal joined to an integer), text that arrives from
    /// outside the tree (a radio's own replies, FlexLib's strings, the help
    /// pages, which are built from <c>docs/help/</c> and checked by the help
    /// build rather than here), and anything in <c>FlexLib_API/</c> or
    /// <c>tools/</c>, which are vendor code and developer utilities.
    /// </para>
    /// </remarks>
    public class TaskNumbersStayOutOfShippedTextTests
    {
        /// <summary>
        /// A task citation: <c>#</c> and one to four digits standing alone, or
        /// "task 245" with or without the hash.
        /// </summary>
        /// <remarks>
        /// Three things that look like a citation and are not, each excluded by
        /// the shape rather than by a list: a Windows class atom such as
        /// <c>#32770</c> (five digits), an HTML character reference such as
        /// <c>&amp;#160;</c> (the ampersand), and a CSS colour such as
        /// <c>color: #222</c> or <c>border-left: 4px solid #888</c> (the CSS
        /// property the value belongs to). A list of exempt
        /// strings would rot as the strings were edited.
        /// </remarks>
        internal static readonly Regex Citation = new(
            @"(?<![&\w])(?<!(?:color|background(?:-color)?|border(?:-[a-z]+)?|outline|fill|stroke)\s*:[^;{}""\n]*)#\d{1,4}(?!\w)"
            + @"|\btasks?\s+#?\d{1,4}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ------------------------------------------------------------------
        // The tree
        // ------------------------------------------------------------------

        [Fact]
        public void No_string_literal_in_shipped_code_carries_a_task_number()
        {
            var findings = new List<string>();
            int files = 0, literals = 0;

            foreach (string file in ShippedSource())
            {
                files++;
                string text = File.ReadAllText(file);
                foreach (var (line, literal) in Literals(file, text))
                {
                    literals++;
                    if (Citation.IsMatch(literal))
                        findings.Add(IntegrationPassTree.Relative(file) + ":" + line + "  " + Shorten(literal));
                }
            }

            // Measured 2026-10-27 with the same exclusions: 866 files and about
            // 36,000 literals. The
            // floors are far below that and far above zero, so a scan that
            // found nothing to read fails instead of passing.
            Assert.True(files > 300, "Only " + files + " shipped source files were read; the scan did not find the tree.");
            Assert.True(literals > 10000, "Only " + literals + " string literals were read; the lexer is not seeing them.");

            Assert.True(findings.Count == 0,
                findings.Count + " string literal(s) in shipped code carry a task number. Task numbers belong in "
                + "comments and tests; a string is spoken, shown, or written to the diagnostic log that travels "
                + "in a user's support bundle. Drop the citation and keep the sentence:\n"
                + string.Join("\n", findings));
        }

        [Fact]
        public void No_lexicon_value_carries_a_task_number()
        {
            var findings = new List<string>();
            int files = 0, values = 0;

            foreach (string file in LexiconFiles())
            {
                files++;
                using var doc = JsonDocument.Parse(File.ReadAllText(file),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var (key, value) in LexiconValues(doc.RootElement, ""))
                {
                    values++;
                    if (Citation.IsMatch(value))
                        findings.Add(IntegrationPassTree.Relative(file) + "  " + key + " = " + Shorten(value));
                }
            }

            Assert.True(files >= 5, "Only " + files + " Lexicon files were read.");
            Assert.True(values > 1000, "Only " + values + " Lexicon values were read.");
            Assert.True(findings.Count == 0,
                findings.Count + " Lexicon value(s) carry a task number, and every Lexicon value is "
                + "something an operator hears or sees:\n" + string.Join("\n", findings));
        }

        [Fact]
        public void No_xaml_or_resx_text_carries_a_task_number()
        {
            var findings = new List<string>();
            int files = 0;

            foreach (string file in IntegrationPassTree.AllFiles
                .Where(f => !IntegrationPassTree.IsVendor(f) && !IntegrationPassTree.IsTest(f))
                .Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)))
            {
                files++;
                string[] lines = WithoutMarkupComments(File.ReadAllText(file)).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    if (Citation.IsMatch(lines[i]))
                        findings.Add(IntegrationPassTree.Relative(file) + ":" + (i + 1) + "  " + Shorten(lines[i].Trim()));
            }

            Assert.True(files > 50, "Only " + files + " XAML and resx files were read.");
            Assert.True(findings.Count == 0,
                findings.Count + " XAML or resx line(s) outside a comment carry a task number:\n"
                + string.Join("\n", findings));
        }

        /// <summary>
        /// The three operator-visible sinks the ruling names are inside what the
        /// scan reads. Without this, the scan could stop reading one of them (a
        /// file moved, a lexer change) and still pass, having looked at less.
        /// </summary>
        [Fact]
        public void The_named_operator_visible_sinks_are_inside_what_the_scan_reads()
        {
            var shipped = ShippedSource().ToList();

            // An unbound-note detail, which the Command Finder shows and the
            // exported key list writes verbatim.
            string keyCommands = shipped.Single(f => f.EndsWith(Path.Combine("JJFlexWpf", "KeyCommands.cs"), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(Literals(keyCommands, File.ReadAllText(keyCommands)),
                l => l.Text.Contains("Tools menu, Operator alarms", StringComparison.Ordinal));

            // An [Earcon] description, which becomes the HelpText on its
            // audition button and is announced on focus.
            string earcons = shipped.Single(f => f.EndsWith(Path.Combine("JJFlexWpf", "EarconPlayer.cs"), StringComparison.OrdinalIgnoreCase));
            string earconText = File.ReadAllText(earcons);
            int attr = earconText.IndexOf("[Earcon(\"PTT warning 1", StringComparison.Ordinal);
            Assert.True(attr >= 0, "The PTT warning earcon's attribute has moved; point this control at another earcon.");
            int attrLine = earconText.Take(attr).Count(c => c == '\n') + 1;
            Assert.Contains(Literals(earcons, earconText), l => l.Line > attrLine && l.Line <= attrLine + 6);

            // A Lexicon value.
            Assert.Contains(LexiconFiles(), f => Path.GetFileName(f) == "connect.json");
        }

        // ------------------------------------------------------------------
        // The lexer's positive controls
        // ------------------------------------------------------------------

        /// <summary>
        /// Every shape of literal the scan claims to see, each carrying a planted
        /// citation, and every shape it claims to skip, each carrying one too.
        /// A lexer that silently stopped seeing a shape would let a citation
        /// through and stay green; this fails first.
        /// </summary>
        [Fact]
        public void The_csharp_lexer_sees_each_shape_and_skips_comments()
        {
            const string src = "#region Planted #9001\n"
                + "// a line comment #9002\n"
                + "/* a block comment #9003 */\n"
                + "class X { void M() {\n"
                + "    Trace(\"first piece \" +\n"
                + "          \"second piece (#9101) \" +\n"
                + "          $\"hole {(x ? \"inner #9102\" : \"b\")} done\");\n"
                + "    var v = @\"verbatim \"\"quoted\"\" #9103\";\n"
                + "    var r = \"\"\"raw #9104\"\"\";\n"
                + "    char c = '\"'; var after = \"after a quote char (task 9105)\";\n"
                + "    var vi = $@\"verbatim hole {y} #9106\";\n"
                + "    var colon = \"StationConnect: refused - #9107\";\n"
                + "    var css = \"body { color: #222; background: #f9f9f9; border-left: 4px solid #888 }\";\n"
                + "    var atom = \"#32770\"; var entity = \"&#160;\";\n"
                + "}}\n";

            var found = Literals("x.cs", src).Where(l => Citation.IsMatch(l.Text)).Select(l => l.Text).ToList();

            // #9107 sits after a colon, and must not be mistaken for a CSS value.
            foreach (string planted in new[] { "#9101", "#9102", "#9103", "#9104", "task 9105", "#9106", "#9107" })
                Assert.Contains(found, t => t.Contains(planted, StringComparison.Ordinal));
            Assert.Equal(7, found.Count);   // and nothing from the comments, the region, the CSS, the atom or the entity
        }

        [Fact]
        public void The_vb_lexer_sees_each_shape_and_skips_comments()
        {
            const string src = "#Region \"R #9001\"\n"
                + "' a comment #9002\n"
                + "Sub M()\n"
                + "    Trace(\"vb piece one \" &\n"
                + "          \"vb piece two (#9201) \" & _\n"
                + "          $\"hole {If(x, \"inner #9202\", \"b\")}\")\n"
                + "    Dim d = #1/2/2003#\n"
                + "    Dim s = \"after a date \"\"quoted\"\" #9203\" REM a rem #9004\n"
                + "End Sub\n";

            var found = Literals("x.vb", src).Where(l => Citation.IsMatch(l.Text)).Select(l => l.Text).ToList();

            foreach (string planted in new[] { "#9201", "#9202", "#9203" })
                Assert.Contains(found, t => t.Contains(planted, StringComparison.Ordinal));
            Assert.Equal(3, found.Count);
        }

        [Fact]
        public void The_lexicon_walk_reads_values_and_skips_notes()
        {
            using var doc = JsonDocument.Parse(
                "{\"_note\":\"#9001\",\"a.b\":\"value #9301\",\"a.c\":{\"text\":\"nested #9302\",\"_why\":\"#9002\"},\"a.d\":[\"listed #9303\"]}");
            var hits = LexiconValues(doc.RootElement, "").Where(v => Citation.IsMatch(v.Value)).Select(v => v.Value).ToList();
            Assert.Equal(new[] { "value #9301", "nested #9302", "listed #9303" }, hits);
        }

        // ------------------------------------------------------------------
        // Corpus
        // ------------------------------------------------------------------

        /// <summary>
        /// Authored C# and VB that ships: tracked, plus untracked files not
        /// ignored, without tests, vendor trees or developer tools.
        /// </summary>
        internal static IEnumerable<string> ShippedSource()
            => IntegrationPassTree.AuthoredSource
                .Concat(IntegrationPassTree.UntrackedSource())
                .Where(f => !IntegrationPassTree.IsTest(f))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<string> LexiconFiles()
            => IntegrationPassTree.AllFiles.Where(f =>
                f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "Lexicon", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), "Radios", StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<(string Key, string Value)> LexiconValues(JsonElement e, string key)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                        foreach (var v in LexiconValues(p.Value, key.Length == 0 ? p.Name : key + "." + p.Name))
                            yield return v;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                        foreach (var v in LexiconValues(item, key))
                            yield return v;
                    break;
                case JsonValueKind.String:
                    yield return (key, e.GetString() ?? "");
                    break;
            }
        }

        private static string WithoutMarkupComments(string text)
            => Regex.Replace(text, "<!--.*?-->", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);

        private static string Shorten(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 200 ? s : s.Substring(0, 200) + "...";
        }

        internal static IEnumerable<(int Line, string Text)> Literals(string path, string src)
            => path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
                ? SourceLiterals.VisualBasic(src)
                : SourceLiterals.CSharp(src);
    }

    /// <summary>
    /// Reads the string literals out of C# and VB source, skipping comments and
    /// preprocessor lines. A lexer, not a parser: it knows where strings and
    /// comments begin and end, which is all a citation check needs.
    /// </summary>
    internal static class SourceLiterals
    {
        public static List<(int Line, string Text)> CSharp(string src)
        {
            var output = new List<(int, string)>();
            int i = 0, line = 1;
            bool lineStart = true;
            int n = src.Length;

            while (i < n)
            {
                char c = src[i];
                if (c == '\n') { line++; i++; lineStart = true; continue; }
                if (c == ' ' || c == '\t' || c == '\r') { i++; continue; }
                if (lineStart && c == '#') { i = EndOfLine(src, i); continue; }
                lineStart = false;

                if (At(src, i, "//")) { i = EndOfLine(src, i); continue; }
                if (At(src, i, "/*"))
                {
                    int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 2;
                    line += Count(src, i, end, '\n');
                    i = end;
                    continue;
                }
                if (c == '\'') { i = SkipCharLiteral(src, i); continue; }
                if (IsCSharpStringStart(src, i))
                {
                    int startLine = line;
                    i = ReadCSharpString(src, i, ref line, output, out string text);
                    output.Add((startLine, text));
                    continue;
                }
                i++;
            }
            return output;
        }

        private static bool IsCSharpStringStart(string s, int i)
        {
            int j = i;
            while (j < s.Length && (s[j] == '@' || s[j] == '$') && j - i < 4) j++;
            return j < s.Length && s[j] == '"' && (j == i || IsPrefixStart(s, i));
        }

        // A '$' or '@' only starts a string when it is not the tail of an identifier.
        private static bool IsPrefixStart(string s, int i) => i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_');

        /// <summary>Reads one C# string starting at <paramref name="i"/> (its
        /// prefix included). Strings nested inside interpolation holes are added
        /// to <paramref name="output"/> as literals of their own.</summary>
        private static int ReadCSharpString(string s, int i, ref int line, List<(int, string)> output, out string text)
        {
            bool verbatim = false, interpolated = false;
            while (s[i] == '@' || s[i] == '$')
            {
                if (s[i] == '@') verbatim = true; else interpolated = true;
                i++;
            }

            int quotes = 0;
            while (i + quotes < s.Length && s[i + quotes] == '"') quotes++;
            if (quotes >= 3)
            {
                string close = new string('"', quotes);
                int start = i + quotes;
                int end = s.IndexOf(close, start, StringComparison.Ordinal);
                if (end < 0) end = s.Length;
                text = s.Substring(start, end - start);
                line += Count(s, start, end, '\n');
                return Math.Min(s.Length, end + quotes);
            }

            i++;   // the opening quote
            var buf = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\n') line++;
                if (verbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { buf.Append('"'); i += 2; continue; }
                        text = buf.ToString();
                        return i + 1;
                    }
                }
                else
                {
                    if (c == '\\' && i + 1 < s.Length) { buf.Append(c).Append(s[i + 1]); i += 2; continue; }
                    if (c == '"') { text = buf.ToString(); return i + 1; }
                    if (c == '\n') { text = buf.ToString(); return i; }   // unterminated; do not run away
                }

                if (interpolated && c == '{')
                {
                    if (i + 1 < s.Length && s[i + 1] == '{') { buf.Append('{'); i += 2; continue; }
                    i = SkipCSharpHole(s, i + 1, ref line, output);
                    buf.Append("{…}");
                    continue;
                }
                buf.Append(c);
                i++;
            }
            text = buf.ToString();
            return i;
        }

        private static int SkipCSharpHole(string s, int i, ref int line, List<(int, string)> output)
        {
            int depth = 1;
            while (i < s.Length && depth > 0)
            {
                char c = s[i];
                if (c == '\n') line++;
                if (c == '{') { depth++; i++; continue; }
                if (c == '}') { depth--; i++; continue; }
                if (c == '\'') { i = SkipCharLiteral(s, i); continue; }
                if (IsCSharpStringStart(s, i))
                {
                    int startLine = line;
                    i = ReadCSharpString(s, i, ref line, output, out string inner);
                    output.Add((startLine, inner));
                    continue;
                }
                i++;
            }
            return i;
        }

        private static int SkipCharLiteral(string s, int i)
        {
            int k = i + 1;
            if (k < s.Length && s[k] == '\\') k += 2; else k += 1;
            while (k < s.Length && s[k] != '\'' && s[k] != '\n') k++;
            return Math.Min(s.Length, k + 1);
        }

        public static List<(int Line, string Text)> VisualBasic(string src)
        {
            var output = new List<(int, string)>();
            int i = 0, line = 1;
            bool lineStart = true;
            int n = src.Length;
            var dateLiteral = new Regex(@"\G#[^#\n]{1,40}#", RegexOptions.CultureInvariant);

            while (i < n)
            {
                char c = src[i];
                if (c == '\n') { line++; i++; lineStart = true; continue; }
                if (c == ' ' || c == '\t' || c == '\r') { i++; continue; }
                if (lineStart && c == '#') { i = EndOfLine(src, i); continue; }
                lineStart = false;

                if (c == '\'' || c == '‘' || c == '’') { i = EndOfLine(src, i); continue; }
                if ((c == 'R' || c == 'r') && i + 3 <= n
                    && string.Compare(src, i, "REM", 0, 3, StringComparison.OrdinalIgnoreCase) == 0
                    && (i + 3 == n || !char.IsLetterOrDigit(src[i + 3]))
                    && IsPrefixStart(src, i))
                {
                    i = EndOfLine(src, i);
                    continue;
                }
                if (c == '#')
                {
                    var m = dateLiteral.Match(src, i);
                    i = m.Success ? i + m.Length : i + 1;
                    continue;
                }

                bool interpolated = false;
                if (c == '$' && i + 1 < n && src[i + 1] == '"') { interpolated = true; i++; c = '"'; }
                if (c == '"')
                {
                    int startLine = line;
                    i = ReadVbString(src, i + 1, interpolated, ref line, output, out string text);
                    output.Add((startLine, text));
                    continue;
                }
                i++;
            }
            return output;
        }

        private static int ReadVbString(string s, int i, bool interpolated, ref int line, List<(int, string)> output, out string text)
        {
            var buf = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\n') line++;
                if (c == '"')
                {
                    if (i + 1 < s.Length && s[i + 1] == '"') { buf.Append('"'); i += 2; continue; }
                    text = buf.ToString();
                    return i + 1;
                }
                if (interpolated && c == '{')
                {
                    if (i + 1 < s.Length && s[i + 1] == '{') { buf.Append('{'); i += 2; continue; }
                    int depth = 1;
                    i++;
                    while (i < s.Length && depth > 0)
                    {
                        char h = s[i];
                        if (h == '\n') line++;
                        if (h == '{') { depth++; i++; continue; }
                        if (h == '}') { depth--; i++; continue; }
                        if (h == '"')
                        {
                            int startLine = line;
                            i = ReadVbString(s, i + 1, false, ref line, output, out string inner);
                            output.Add((startLine, inner));
                            continue;
                        }
                        i++;
                    }
                    buf.Append("{…}");
                    continue;
                }
                buf.Append(c);
                i++;
            }
            text = buf.ToString();
            return i;
        }

        private static bool At(string s, int i, string what) => string.CompareOrdinal(s, i, what, 0, what.Length) == 0;

        private static int EndOfLine(string s, int i)
        {
            int j = s.IndexOf('\n', i);
            return j < 0 ? s.Length : j;
        }

        private static int Count(string s, int from, int to, char c)
        {
            int k = 0;
            for (int x = from; x < to && x < s.Length; x++) if (s[x] == c) k++;
            return k;
        }
    }
}
