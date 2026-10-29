using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Radios.Tests
{
    /// <summary>
    /// Reads XAML and code-behind from source to find spoken names that do not
    /// contain the label a control shows (#840). Read from source because the
    /// controls are windows, and a window is not built on a live desk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule is containment, not equality.</b> "No, cancel" on a plain
    /// "_No" is right: the screen reader says what the screen shows and adds
    /// to it. What is wrong is a name the operator hears that does not carry
    /// the words on the screen, because every sentence, help page and sighted
    /// helper names the control by what it shows.
    /// </para>
    /// <para>
    /// <b>Compared as words.</b> Access-key underscores are removed, case is
    /// ignored, and so is punctuation: "Keep a diagnostic log (recommended)"
    /// and "Keep a diagnostic log, recommended" say the same words, and a
    /// trailing "..." is a visual convention the names here drop on purpose.
    /// A different WORD is a mismatch, including an abbreviation spelled out.
    /// </para>
    /// </remarks>
    internal static class SpokenNameScan
    {
        internal sealed record XamlControl(string File, int Line, string Tag, string? XName,
            string Shown, string SpokenName);

        internal sealed record CodeAssignment(string File, int Line, string Control,
            string Shown, string? Rename);

        private static readonly Regex Tag = new(@"<([\w:.]+)\b([^<>]*?)/?>", RegexOptions.Singleline);

        /// <summary>The words of a label or name, lower case, punctuation dropped.</summary>
        internal static string Words(string text) =>
            string.Join(" ", Regex.Matches(text.Replace("_", "").ToLowerInvariant(), "[a-z0-9]+")
                .Select(m => m.Value));

        /// <summary>True when the spoken name carries every word shown, in order.</summary>
        internal static bool NameContainsShown(string spokenName, string shown) =>
            (" " + Words(spokenName) + " ").Contains(" " + Words(shown) + " ", StringComparison.Ordinal);

        internal static string? Attribute(string attributes, string name)
        {
            var m = Regex.Match(attributes, @"(?<![\w.:])" + Regex.Escape(name) + @"\s*=\s*""([^""]*)""");
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
        }

        /// <summary>
        /// Every element in one XAML text whose spoken name and shown label are
        /// both literal. Bindings and resources are skipped: they are not text.
        /// </summary>
        internal static IEnumerable<XamlControl> XamlControls(string file, string xaml)
        {
            foreach (Match m in Tag.Matches(xaml))
            {
                string attributes = m.Groups[2].Value;
                string? name = Attribute(attributes, "AutomationProperties.Name");
                if (name == null || name.StartsWith("{", StringComparison.Ordinal)) continue;
                string? shown = Attribute(attributes, "Content") ?? Attribute(attributes, "Header");
                if (shown == null || shown.StartsWith("{", StringComparison.Ordinal)) continue;
                int line = xaml.Take(m.Index).Count(c => c == '\n') + 1;
                yield return new XamlControl(file, line, m.Groups[1].Value,
                    Attribute(attributes, "x:Name"), shown, name);
            }
        }

        /// <summary>x:Name to literal spoken name, for one XAML text.</summary>
        internal static Dictionary<string, string> NamedControls(string xaml)
        {
            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Tag.Matches(xaml))
            {
                string attributes = m.Groups[2].Value;
                string? xName = Attribute(attributes, "x:Name");
                string? name = Attribute(attributes, "AutomationProperties.Name");
                if (xName != null && name != null && !name.StartsWith("{", StringComparison.Ordinal))
                    named[xName] = name;
            }
            return named;
        }

        /// <summary>
        /// Every <c>X.Content =</c> or <c>X.Header =</c> in a code file, for a
        /// control X the XAML gives a spoken name, paired with the rename that
        /// follows it in the same block (before the block closes and before the
        /// next relabel of X), or with null when there is none. Each assignment
        /// is judged on its own: one SetName elsewhere in the file does not
        /// cover a second relabel.
        /// </summary>
        internal static IEnumerable<CodeAssignment> Assignments(string file, string code,
            IReadOnlyDictionary<string, string> named)
        {
            var assign = new Regex(@"(?<![\w.])(\w+)\.(?:Content|Header)\s*=(?!=)");
            foreach (Match m in assign.Matches(code))
            {
                string control = m.Groups[1].Value;
                if (!named.ContainsKey(control)) continue;

                int statementEnd = code.IndexOf(';', m.Index);
                if (statementEnd < 0) continue;
                string shown = code.Substring(m.Index + m.Length, statementEnd - m.Index - m.Length).Trim();

                int limit = BlockEnd(code, statementEnd);
                var next = assign.Matches(code, statementEnd)
                    .FirstOrDefault(n => n.Groups[1].Value == control);
                if (next != null && next.Index < limit) limit = next.Index;

                string? rename = null;
                var setName = new Regex(@"AutomationProperties\.SetName\(\s*" + Regex.Escape(control) + @"\s*,");
                var found = setName.Match(code, statementEnd, limit - statementEnd);
                if (found.Success) rename = Argument(code, found.Index + found.Length);

                int line = code.Take(m.Index).Count(c => c == '\n') + 1;
                yield return new CodeAssignment(file, line, control, shown, rename);
            }
        }

        /// <summary>
        /// Whether a rename sets the same text the assignment shows: the same
        /// expression, allowing for access-key underscores being stripped
        /// (".Replace("_", "")", or an underscore missing from a literal).
        /// </summary>
        internal static bool SameText(string shown, string rename) =>
            Expression(shown) == Expression(rename);

        private static string Expression(string text)
        {
            string s = Regex.Replace(text, @"\s+", "");
            s = s.Replace(".Replace(\"_\",\"\")", "");
            s = Regex.Replace(s, "\"[^\"]*\"", lit => lit.Value.Replace("_", ""));
            // "_" + label, once its underscore is gone, shows exactly label.
            s = s.Replace("\"\"+", "");
            return s;
        }

        private static int BlockEnd(string code, int from)
        {
            int depth = 0;
            for (int i = from; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth < 0) return i;
            }
            return code.Length;
        }

        private static string Argument(string code, int from)
        {
            int depth = 0;
            for (int i = from; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '(') depth++;
                else if (c == ')' && depth-- == 0) return code.Substring(from, i - from).Trim();
            }
            return code.Substring(from).Trim();
        }

        internal sealed record CodeBuiltControl(string File, int Line, string Variable,
            string Shown, string SpokenName);

        private static readonly Regex Literal = new(@"@?""(?:[^""\\]|\\.)*""");

        private static string? LiteralText(string expression)
        {
            string e = expression.Trim();
            var m = Literal.Match(e);
            if (!m.Success || m.Index != 0 || m.Length != e.Length) return null;
            string body = e.StartsWith("@", StringComparison.Ordinal) ? e.Substring(2, e.Length - 3) : e.Substring(1, e.Length - 2);
            return body.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        /// <summary>
        /// Controls built in code rather than XAML, where both the shown label
        /// and the spoken name are literals: an object initializer's
        /// <c>Content = "..."</c> followed by <c>SetName(variable, "...")</c>
        /// in the same block, and a helper method that takes the label and
        /// the name as two parameters, judged at each call site that passes
        /// two literals (RadioOwnershipDialog.MakeAnswer is the shape).
        /// </summary>
        internal static IEnumerable<CodeBuiltControl> CodeBuiltControls(string file, string code)
        {
            var init = new Regex(@"\b(\w+)\s*=\s*new\s+[\w.]+\s*(?:\(\s*\))?\s*\{");
            foreach (Match m in init.Matches(code))
            {
                string variable = m.Groups[1].Value;
                int open = m.Index + m.Length - 1;
                int close = Matching(code, open);
                if (close < 0) continue;
                string body = code.Substring(open + 1, close - open - 1);
                var content = Regex.Match(body, @"(?<![\w.])(?:Content|Header)\s*=\s*([^,\r\n]+)");
                if (!content.Success) continue;
                string? shown = LiteralText(content.Groups[1].Value.TrimEnd(',', ' '));
                if (shown == null) continue;
                int limit = BlockEnd(code, close + 1);
                var setName = new Regex(@"AutomationProperties\.SetName\(\s*" + Regex.Escape(variable) + @"\s*,");
                var found = setName.Match(code, close, limit - close);
                if (!found.Success) continue;
                string? name = LiteralText(Argument(code, found.Index + found.Length));
                if (name == null) continue;
                yield return new CodeBuiltControl(file, LineOf(code, m.Index), variable, shown, name);
            }

            var method = new Regex(@"(?:private|internal|public|protected)(?:\s+static)?\s+[\w.<>?]+\s+(\w+)\s*\(([^)]*)\)\s*\{");
            foreach (Match m in method.Matches(code))
            {
                string methodName = m.Groups[1].Value;
                var parameters = m.Groups[2].Value.Split(',')
                    .Select(p => p.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Select(p => p.Length >= 2 ? p[^1] : "")
                    .ToList();
                int open = m.Index + m.Length - 1;
                int close = Matching(code, open);
                if (close < 0) continue;
                string body = code.Substring(open + 1, close - open - 1);
                var content = Regex.Match(body, @"(?<![\w.])(?:Content|Header)\s*=\s*(\w+)\s*[,;}]");
                var named = Regex.Match(body, @"AutomationProperties\.SetName\(\s*\w+\s*,\s*(\w+)\s*\)");
                if (!content.Success || !named.Success) continue;
                int shownAt = parameters.IndexOf(content.Groups[1].Value);
                int nameAt = parameters.IndexOf(named.Groups[1].Value);
                if (shownAt < 0 || nameAt < 0) continue;

                var call = new Regex(@"(?<![\w.])" + Regex.Escape(methodName) + @"\s*\(");
                foreach (Match c in call.Matches(code))
                {
                    if (c.Index == m.Groups[1].Index) continue;
                    var args = SplitArguments(Argument(code, c.Index + c.Length));
                    if (args.Count <= Math.Max(shownAt, nameAt)) continue;
                    string? shown = LiteralText(args[shownAt]);
                    string? name = LiteralText(args[nameAt]);
                    if (shown == null || name == null) continue;
                    yield return new CodeBuiltControl(file, LineOf(code, c.Index), methodName, shown, name);
                }
            }
        }

        private static int LineOf(string code, int index) => code.Take(index).Count(ch => ch == '\n') + 1;

        private static int Matching(string code, int open)
        {
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        private static List<string> SplitArguments(string arguments)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            bool inString = false;
            for (int i = 0; i < arguments.Length; i++)
            {
                char ch = arguments[i];
                if (inString)
                {
                    if (ch == '\\') i++;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') inString = true;
                else if (ch == '(' || ch == '[' || ch == '{') depth++;
                else if (ch == ')' || ch == ']' || ch == '}') depth--;
                else if (ch == ',' && depth == 0)
                {
                    parts.Add(arguments.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            parts.Add(arguments.Substring(start).Trim());
            return parts;
        }

        /// <summary>The WPF project's C# files, as repository-relative paths.</summary>
        internal static IEnumerable<string> CodeFiles(string root)
        {
            string wpf = Path.Combine(root, "JJFlexWpf");
            return Directory.EnumerateFiles(wpf, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal);
        }

        /// <summary>The WPF project's XAML files, as repository-relative paths.</summary>
        internal static IEnumerable<string> XamlFiles(string root)
        {
            string wpf = Path.Combine(root, "JJFlexWpf");
            return Directory.EnumerateFiles(wpf, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal);
        }

        /// <summary>
        /// The code that drives one XAML file: its code-behind and the partial
        /// files beside it (SettingsDialog.Diagnostics.cs and the like).
        /// </summary>
        internal static IEnumerable<string> CodeFilesFor(string root, string xamlFile)
        {
            string full = Path.Combine(root, xamlFile);
            string dir = Path.GetDirectoryName(full)!;
            string stem = Path.GetFileNameWithoutExtension(full);
            if (File.Exists(full + ".cs")) yield return xamlFile + ".cs";
            foreach (string partial in Directory.EnumerateFiles(dir, stem + ".*.cs")
                         .Where(p => !p.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(p => p, StringComparer.Ordinal))
                yield return Path.GetRelativePath(root, partial).Replace('\\', '/');
        }
    }
}
