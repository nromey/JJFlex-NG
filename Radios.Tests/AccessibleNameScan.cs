using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Radios.Tests
{
    /// <summary>
    /// Reads every operator-facing accessible name out of SOURCE and reports the
    /// ones that are EXPLANATIONS rather than names (#363).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE RULE: a name is an ACTION or a THING. An explanation is help
    /// text.</b> The Release All Extra Slices button shows three words and
    /// announced "Release every slice except the one you are on, back to one
    /// slice" — plus "button" from the reader — on every single landing. The
    /// Settings signal-strength combo showed "Read the S-meter in:" and
    /// announced "Unit the S-meter is read in for this radio": one control, two
    /// different sentences, and only the second is ever heard.
    /// </para>
    /// <para>
    /// <b>WHY NOTHING CAUGHT EITHER.</b> The invariant checks in
    /// <c>JJFlexWpf.Tests</c> walk every focusable control and assert a name
    /// EXISTS. Existence is the wrong question here: both names existed, and
    /// both were carefully written. No sighted review catches it either,
    /// because only one of the two strings is on screen. The single detector
    /// was a screen-reader user landing on the control, which is why the
    /// operator found both of them inside one hour.
    /// </para>
    /// <para>
    /// <b>WHAT THIS PROVES, AND WHAT IT CANNOT.</b> It proves a name is
    /// SHAPED like an explanation — sentence structure, or far more words than
    /// the label beside it. It cannot prove a name is WRONG: a long name can be
    /// the right call, and a short one can be useless. Length is a crude proxy
    /// for "this is an explanation", chosen because it is cheap and it fires in
    /// the right direction. Every finding is a question for a person, and the
    /// baseline in <see cref="AccessibleNameTests"/> is where the answered ones
    /// live.
    /// </para>
    /// <para>
    /// <b>The destination is <c>JJFlexHelp.Text</c>, never
    /// <c>AutomationProperties.HelpText</c>.</b> That distinction is the whole
    /// point and it has been got wrong before: <c>JJFlexHelp</c>'s own header
    /// records that the 2026-08-18 sweep moved long explanations out of names
    /// and into HelpText, which NVDA reads aloud as the control's description
    /// on every focus. Same words, same moment, same cost, different UIA slot.
    /// Only <c>JJFlexHelp.Text</c> is genuinely on-demand.
    /// </para>
    /// <para>
    /// <b>XAML is read as XML, not as text.</b> Every authored file parses
    /// cleanly, and XLinq distinguishes <c>Text</c> from
    /// <c>local:JJFlexHelp.Text</c> for free — a distinction a regex scan gets
    /// wrong, and gets wrong in the direction that reports a control's own help
    /// text as its visible label. Code-behind is read as syntax for the same
    /// reason the key-map scan is.
    /// </para>
    /// </remarks>
    // LEXICON_SCANNER_EXEMPT — this file is the thing that READS Lexicon.Get
    // call sites, so its own doc comments and matcher describe the shape of a
    // call rather than making one. The key-coverage sweep skips any file
    // carrying this token.
    internal static class AccessibleNameScan
    {
        // ────────────────────────────────────────────────────────────────
        //  Thresholds — named, so a report can state the rule it applied
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Words a name must carry BEFORE a sentence break for the break to
        /// count. "OK. Applies the auto-connect settings and closes." is the
        /// shape; a stray full stop after one word is not.
        /// </summary>
        internal const int ProseMinWordsBeforeBreak = 1;

        /// <summary>
        /// How many words a name may add to the control's OWN visible content
        /// before it stops being a name. A button's content IS its name, so the
        /// gap should be nothing; the threshold sits well clear of the one
        /// legitimate reason for a gap, which is expanding an abbreviation —
        /// TNF to Tracking Notch Filter, ESC to Emergency Signal Cancel.
        /// </summary>
        internal const int ExceedsLabelByWords = 6;

        /// <summary>
        /// The same question for a control labelled by the caption BESIDE it,
        /// where the threshold has to be tighter and the reason is different.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Measured against the operator's own two findings, and the task's
        /// prediction did not hold.</b> #363 says a length check "would have
        /// caught both of today's instances". It catches the release-all button
        /// easily — four words on screen, thirteen in the ear. It does NOT
        /// catch the signal-strength combo at any threshold that survives
        /// contact with the codebase: "Read the S-meter in:" against "Unit the
        /// S-meter is read in for this radio" is a gap of four words, and
        /// setting the bar there adds thirty-two findings that are almost all
        /// correct — "Low" naming a box called "Filter low edge in hertz" is
        /// exactly what a terse on-screen caption is supposed to become in
        /// speech.
        /// </para>
        /// <para>
        /// <b>So the caption's own length is the discriminator.</b> One or two
        /// words is an abbreviation fitted to a column, and expanding it is
        /// right. Three or more is already a phrase making a full statement,
        /// and a name that restates it in DIFFERENT words is two vocabularies
        /// for one control — which is the defect the S-meter fix actually
        /// closed. Its commit says so outright: two names for one thing.
        /// </para>
        /// </remarks>
        internal const int ExceedsCaptionByWords = 4;

        /// <summary>
        /// Words that make a caption a phrase rather than an abbreviation.
        /// Below this, a longer name is the caption being spoken properly.
        /// </summary>
        internal const int CaptionIsAPhrase = 3;

        /// <summary>
        /// Words that make a name a sentence when there is no visible label to
        /// compare it against. Deliberately generous: a control labelled only
        /// by a separate TextBlock has nowhere else to put a range or a unit,
        /// so this is the crudest of the three rules and the one whose findings
        /// most often turn out to be someone's deliberate choice.
        /// </summary>
        internal const int LongNameWords = 10;

        // ────────────────────────────────────────────────────────────────
        //  Where the names live
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Authored UI. FlexLib_API is vendor code and is not ours to rename.
        /// </summary>
        internal static readonly string[] UiRoots = { "JJFlexWpf", "Radios" };

        internal const string LexiconDirectory = "Radios/Lexicon";

        /// <summary>
        /// Layout that an operator never lands on. A Grid with a name is not a
        /// control with a sentence for a name, and holding containers to this
        /// rule produces noise instead of defects.
        /// </summary>
        private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
        {
            "Grid", "StackPanel", "DockPanel", "WrapPanel", "Canvas", "UniformGrid",
            "Border", "ScrollViewer", "Viewbox", "Window", "UserControl", "Page",
            "ContentPresenter", "ItemsPresenter", "AdornerDecorator", "Popup",
        };

        /// <summary>
        /// Controls whose <c>Text</c> is a VALUE, not a label. A TextBox
        /// carrying Text="4992" is showing a port number, and reading that as
        /// the control's visible label invents a finding out of nothing.
        /// </summary>
        private static readonly HashSet<string> TextIsAValue = new(StringComparer.Ordinal)
        {
            "TextBox", "PasswordBox", "RichTextBox", "NumericUpDown",
        };

        /// <summary>Attributes that carry an element's own visible label.</summary>
        private static readonly string[] LabelAttributes = { "Content", "Header", "Text" };

        // ────────────────────────────────────────────────────────────────
        //  Model
        // ────────────────────────────────────────────────────────────────

        /// <summary>One control that carries an accessible name.</summary>
        internal sealed class Named
        {
            /// <summary>Repo-relative, forward-slashed.</summary>
            internal string File { get; init; } = "";

            internal int Line { get; init; }

            /// <summary>The XAML element name, or the code-behind variable.</summary>
            internal string Element { get; init; } = "";

            /// <summary>The accessible name, resolved through the lexicon.</summary>
            internal string Name { get; init; } = "";

            /// <summary>
            /// The element's OWN visible label, when it has one. Null when the
            /// control is labelled by something else, or by nothing.
            /// </summary>
            internal string? Label { get; init; }

            /// <summary>Where the label came from, for the report.</summary>
            internal string LabelSource { get; init; } = "";

            /// <summary>
            /// The lexicon keys this control's name and label were read
            /// through, so a report can say where to make the change.
            /// </summary>
            internal string NameKey { get; init; } = "";
            internal string LabelKey { get; init; } = "";

            internal string Where => Path.GetFileName(File) + " " + Element;
        }

        /// <summary>A name that is shaped like an explanation.</summary>
        internal sealed record Finding(string Direction, Named Control, string Detail)
        {
            /// <summary>
            /// Stable identity for the baseline. The offending SENTENCE is part
            /// of it on purpose: rewording the name is exactly what closes the
            /// finding, so the entry must stop matching when it happens.
            /// </summary>
            internal string Id =>
                Direction + " " + Path.GetFileName(Control.File) + " · " + Truncate(Control.Name, 60);

            internal string Line =>
                Direction + " · " + Control.Where + " · name=\"" + Control.Name + "\""
                + (Control.Label == null ? "" : " · label=\"" + Control.Label + "\"")
                + " — " + Detail;
        }

        internal sealed class Result
        {
            internal List<Named> Controls { get; } = new();

            /// <summary>Anything the scan met and could not read. Never silent.</summary>
            internal List<string> Notes { get; } = new();

            internal int XamlFilesRead { get; set; }
            internal int CodeFilesRead { get; set; }
            internal int LexiconEntries { get; set; }

            /// <summary>
            /// <c>SetName</c> calls whose name argument is a variable, a
            /// concatenation or an interpolation. Not defects and not readable
            /// here — but the count is reported, because a scan that silently
            /// stopped resolving them would look exactly like a clean tree.
            /// </summary>
            internal int UnresolvedCodeNames { get; set; }
        }

        // ────────────────────────────────────────────────────────────────
        //  Entry points
        // ────────────────────────────────────────────────────────────────

        internal static Result ScanRepository()
        {
            string root = FieldKeyMapScan.RepoRoot();

            var xaml = new List<(string File, string Text)>();
            var code = new List<(string File, string Text)>();

            foreach (string uiRoot in UiRoots)
            {
                string dir = Path.Combine(root, uiRoot);
                if (!Directory.Exists(dir)) continue;

                foreach (string path in Directory.EnumerateFiles(dir, "*.xaml", SearchOption.AllDirectories))
                {
                    if (IsBuildOutput(path)) continue;
                    xaml.Add((Relative(root, path), File.ReadAllText(path)));
                }
                foreach (string path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    if (IsBuildOutput(path)) continue;
                    string text = File.ReadAllText(path);
                    if (!text.Contains("AutomationProperties.SetName", StringComparison.Ordinal)) continue;
                    code.Add((Relative(root, path), text));
                }
            }

            return Scan(xaml, code, ReadLexicon(root));
        }

        internal static Result Scan(
            IEnumerable<(string File, string Text)> xamlFiles,
            IEnumerable<(string File, string Text)> codeFiles,
            IReadOnlyDictionary<string, string> lexicon)
        {
            var result = new Result { LexiconEntries = lexicon.Count };

            foreach (var (file, text) in xamlFiles)
            {
                result.XamlFilesRead++;
                ReadXaml(file, text, result);
            }
            foreach (var (file, text) in codeFiles)
            {
                result.CodeFilesRead++;
                ReadCodeBehind(file, text, lexicon, result);
            }
            return result;
        }

        private static bool IsBuildOutput(string path)
            => path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static string Relative(string root, string path)
            => Path.GetRelativePath(root, path).Replace('\\', '/');

        internal static Dictionary<string, string> ReadLexicon(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string dir = Path.Combine(root, LexiconDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) return map;

            foreach (string path in Directory.EnumerateFiles(dir, "*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    map[property.Name] = property.Value.GetString() ?? "";
                }
            }
            return map;
        }

        // ────────────────────────────────────────────────────────────────
        //  XAML
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>AutomationProperties.Name</c> written without a prefix. XLinq
        /// gives an unprefixed attribute no namespace at all, so the attached
        /// property arrives as a local name with a dot in it — which is exactly
        /// what distinguishes it from <c>local:JJFlexHelp.Text</c>, whose
        /// namespace is the app's own.
        /// </summary>
        private static readonly XName AutomationName = "AutomationProperties.Name";

        private static void ReadXaml(string file, string text, Result result)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
            }
            catch (Exception ex)
            {
                result.Notes.Add(file + " did not parse as XML, so its names were not read: " + ex.Message);
                return;
            }

            foreach (var element in doc.Descendants())
            {
                var attribute = element.Attribute(AutomationName);
                if (attribute == null) continue;

                string name = attribute.Value;
                if (IsMarkupExtension(name)) continue;      // resolved at runtime; nothing to read
                if (Normalise(name).Length == 0) continue;  // an empty name is invariant 1's problem

                string tag = element.Name.LocalName;
                if (Containers.Contains(tag)) continue;

                var (label, source) = VisibleLabel(element, tag);
                if (label == null) (label, source) = LabelBeside(element);

                result.Controls.Add(new Named
                {
                    File = file,
                    Line = (element as System.Xml.IXmlLineInfo).LineNumber,
                    Element = "<" + tag + ">",
                    Name = Collapse(name),
                    Label = label,
                    LabelSource = source,
                });
            }
        }

        /// <summary>
        /// The element's OWN visible text. Attribute first, then direct text
        /// content — <c>&lt;Button&gt;Save&lt;/Button&gt;</c> is the same
        /// statement as Content="Save".
        /// </summary>
        private static (string? Label, string Source) VisibleLabel(XElement element, string tag)
        {
            foreach (string candidate in LabelAttributes)
            {
                if (candidate == "Text" && TextIsAValue.Contains(tag)) continue;

                // Unprefixed only. local:JJFlexHelp.Text carries a namespace
                // and is the ON-DEMAND explanation, not a visible label; a
                // scan that confuses the two reports the fix as the defect.
                var attribute = element.Attribute(candidate);
                if (attribute == null) continue;
                if (IsMarkupExtension(attribute.Value)) continue;

                string value = Normalise(attribute.Value);
                if (value.Length == 0) continue;
                return (value, candidate);
            }

            string direct = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
            string inline = Normalise(direct);
            return inline.Length == 0 ? (null, "") : (inline, "content");
        }

        /// <summary>
        /// The label written BESIDE the control rather than on it. This is the
        /// shape of the Settings signal-strength combo, which is half of what
        /// made #363 a task rather than a tweak: the visible label said "Read
        /// the S-meter in:" and the name said "Unit the S-meter is read in for
        /// this radio". One control, two sentences, and only the second is ever
        /// heard. A rule that only reads a control's own Content cannot see
        /// that at all, and it is the more common layout of the two.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately narrow. A <c>Label</c> with a <c>Target</c> is WPF
        /// saying outright which control it labels, so that one is exact. The
        /// other is a convention rather than a declaration — a TextBlock
        /// immediately before the control, ending in a colon — so it is taken
        /// ONLY with the colon, which is the whole of the signal that the text
        /// introduces the next thing rather than heading a group. Widening this
        /// to any preceding TextBlock pairs controls with headings and turns a
        /// checker into a nuisance.
        /// </para>
        /// </remarks>
        private static (string? Label, string Source) LabelBeside(XElement element)
        {
            string? id = element.Attribute(XName.Get("Name", XamlNamespace))?.Value
                      ?? element.Attribute("Name")?.Value;

            if (id != null && element.Document != null)
            {
                foreach (var label in element.Document.Descendants()
                             .Where(e => e.Name.LocalName is "Label" or "AccessText"))
                {
                    string target = label.Attribute("Target")?.Value ?? "";
                    if (!target.Contains("ElementName=" + id, StringComparison.Ordinal)
                        && target != "{Binding ElementName=" + id + "}") continue;

                    var (text, _) = VisibleLabel(label, label.Name.LocalName);
                    if (text != null) return (text, "Label Target");
                }
            }

            var previous = element.ElementsBeforeSelf().LastOrDefault();
            if (previous == null) return (null, "");
            if (previous.Name.LocalName is not ("TextBlock" or "Label" or "AccessText")) return (null, "");

            var attribute = previous.Attribute("Content") ?? previous.Attribute("Text");
            string raw = attribute != null && !IsMarkupExtension(attribute.Value)
                ? attribute.Value
                : string.Concat(previous.Nodes().OfType<XText>().Select(t => t.Value));

            string collapsed = Collapse(raw);
            if (!collapsed.TrimEnd().EndsWith(":", StringComparison.Ordinal)) return (null, "");

            string normalised = Normalise(collapsed);
            return normalised.Length == 0 ? (null, "") : (normalised, "the label before it");
        }

        private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static bool IsMarkupExtension(string value)
            => value.StartsWith("{", StringComparison.Ordinal)
            && !value.StartsWith("{}", StringComparison.Ordinal);

        // ────────────────────────────────────────────────────────────────
        //  Code-behind
        // ────────────────────────────────────────────────────────────────

        private static void ReadCodeBehind(string file, string text,
            IReadOnlyDictionary<string, string> lexicon, Result result)
        {
            var tree = CSharpSyntaxTree.ParseText(text);
            var root = tree.GetRoot();

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member) continue;
                if (member.Name.Identifier.ValueText != "SetName") continue;
                if (!member.Expression.ToString().EndsWith("AutomationProperties", StringComparison.Ordinal)) continue;

                var args = invocation.ArgumentList.Arguments;
                if (args.Count < 2) continue;

                string target = args[0].Expression.ToString();
                if (target == "this") continue;   // a window's name is its title

                var (name, nameKey) = ResolveString(args[1].Expression, lexicon);
                if (name == null) { result.UnresolvedCodeNames++; continue; }
                if (Normalise(name).Length == 0) continue;

                var (label, labelKey) = VisibleLabelInCode(root, target, lexicon);

                result.Controls.Add(new Named
                {
                    File = file,
                    Line = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1,
                    Element = target,
                    Name = Collapse(name),
                    Label = label,
                    LabelSource = label == null ? "" : "Content",
                    NameKey = nameKey,
                    LabelKey = labelKey,
                });
            }
        }

        /// <summary>
        /// A string literal, or <c>Lexicon.Get("key")</c> resolved against the
        /// shipped lexicon. Anything else is a variable whose value this scan
        /// cannot know, and is counted rather than guessed at.
        /// </summary>
        private static (string? Value, string Key) ResolveString(
            ExpressionSyntax expression, IReadOnlyDictionary<string, string> lexicon)
        {
            if (expression is LiteralExpressionSyntax literal && literal.Token.Value is string s)
                return (s, "");

            if (expression is InvocationExpressionSyntax call
                && call.Expression is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText == "Get"
                && member.Expression.ToString().EndsWith("Lexicon", StringComparison.Ordinal)
                && call.ArgumentList.Arguments.Count >= 1
                && call.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax keyLiteral
                && keyLiteral.Token.Value is string key)
            {
                return lexicon.TryGetValue(key, out var value) ? (value, key) : (null, key);
            }

            return (null, "");
        }

        /// <summary>
        /// The visible label of a control built in code: the <c>Content</c> of
        /// the object initializer that created it, or a later assignment to its
        /// Content. This is the shape the Release All Extra Slices button uses,
        /// and the reason the two strings never met a reader who could compare
        /// them.
        /// </summary>
        private static (string? Label, string Key) VisibleLabelInCode(
            SyntaxNode root, string target, IReadOnlyDictionary<string, string> lexicon)
        {
            foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Identifier.ValueText != target) continue;
                if (declarator.Initializer?.Value is not ObjectCreationExpressionSyntax creation) continue;
                var found = ContentOf(creation.Initializer, lexicon);
                if (found.Label != null) return found;
            }

            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is not MemberAccessExpressionSyntax member) continue;
                if (member.Name.Identifier.ValueText != "Content" && member.Name.Identifier.ValueText != "Header") continue;
                if (member.Expression.ToString() != target) continue;

                if (assignment.Right is ObjectCreationExpressionSyntax) continue;   // a control, not a label
                var (value, key) = ResolveString(assignment.Right, lexicon);
                if (value != null) return (Normalise(value), key);
            }

            // Assigned to a field in its own initializer: _button = new Button { Content = ... }
            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left.ToString() != target) continue;
                if (assignment.Right is not ObjectCreationExpressionSyntax creation) continue;
                var found = ContentOf(creation.Initializer, lexicon);
                if (found.Label != null) return found;
            }

            return (null, "");
        }

        private static (string? Label, string Key) ContentOf(
            InitializerExpressionSyntax? initializer, IReadOnlyDictionary<string, string> lexicon)
        {
            if (initializer == null) return (null, "");

            foreach (var expression in initializer.Expressions.OfType<AssignmentExpressionSyntax>())
            {
                string property = expression.Left.ToString();
                if (property != "Content" && property != "Header") continue;

                var (value, key) = ResolveString(expression.Right, lexicon);
                if (value == null) return (null, key);

                string label = Normalise(value);
                return label.Length == 0 ? (null, key) : (label, key);
            }
            return (null, "");
        }

        // ────────────────────────────────────────────────────────────────
        //  The rules
        // ────────────────────────────────────────────────────────────────

        internal const string Prose = "NAME-IS-PROSE";
        internal const string ExceedsLabel = "NAME-EXCEEDS-LABEL";
        internal const string TooLong = "NAME-IS-LONG";

        /// <summary>
        /// One finding per control, at the most specific rule that fires. A
        /// control reported three times reads as three defects and is one.
        /// </summary>
        internal static List<Finding> Findings(Result result)
        {
            var findings = new List<Finding>();

            foreach (var control in result.Controls)
            {
                var finding = Judge(control);
                if (finding != null) findings.Add(finding);
            }

            return findings
                .GroupBy(f => f.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(f => f.Id, StringComparer.Ordinal)
                .ToList();
        }

        internal static Finding? Judge(Named control)
        {
            string name = control.Name;
            string? label = control.Label;

            // A block of prose that names itself. Its visible text IS the
            // sentence, the reader reads the sentence either way, and nothing
            // is being explained twice.
            //
            // Compared with the punctuation flattened, because the two copies
            // are typed by hand and drift: the silenced-messages note carries
            // curly quotes and an em dash on screen and straight quotes and a
            // comma in its name. Same sentence, and reporting it would send
            // somebody to rewrite prose that is already right.
            if (label != null && SameWords(label, name)) return null;

            if (SentenceBreak(name) is string tail)
            {
                return new Finding(Prose, control,
                    "the name carries a second sentence (\"" + Truncate(tail, 60)
                    + "\"), which a reader speaks in full on every landing. The first sentence "
                    + "is the name; the rest is JJFlexHelp.Text.");
            }

            if (label != null)
            {
                bool ownContent = control.LabelSource is "Content" or "Header" or "Text" or "content";
                int allowed = ownContent
                    ? ExceedsLabelByWords
                    : Words(label) >= CaptionIsAPhrase ? ExceedsCaptionByWords : int.MaxValue;

                if (Words(name) - Words(label) >= allowed)
                {
                    return new Finding(ExceedsLabel, control,
                        "the visible label is " + Words(label) + " word(s) and the spoken name is "
                        + Words(name) + ". The control looks short and sounds long, and only the "
                        + "long one is ever heard.");
                }
                return null;
            }

            if (Words(name) >= LongNameWords)
            {
                return new Finding(TooLong, control,
                    Words(name) + " words, with no visible label of its own to compare against. "
                    + "Length is a crude proxy for an explanation — judge it, then fix it or "
                    + "baseline it with the reason.");
            }

            return null;
        }

        /// <summary>
        /// The text after a sentence break, or null. A break is a terminator
        /// that follows a word and precedes a letter, which rules out an
        /// ellipsis and a decimal without needing to know about either.
        /// </summary>
        internal static string? SentenceBreak(string name)
        {
            foreach (Match match in Regex.Matches(name, @"(?<=[\p{L}\p{N})\]""'])[.!?]\s+(?=[\p{L}])"))
            {
                string head = name[..match.Index];
                if (Words(head) < ProseMinWordsBeforeBreak) continue;
                if (EndsWithAbbreviation(head)) continue;
                string tail = name[(match.Index + match.Length)..].Trim();
                if (tail.Length == 0) continue;
                return tail;
            }
            return null;
        }

        private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        {
            "e.g", "i.e", "vs", "etc", "approx", "no", "dr", "mr", "mrs", "ms", "st", "fig",
        };

        private static bool EndsWithAbbreviation(string head)
        {
            string last = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            return Abbreviations.Contains(last.Trim());
        }

        // ────────────────────────────────────────────────────────────────
        //  Text
        // ────────────────────────────────────────────────────────────────

        internal static int Words(string s)
            => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        /// <summary>
        /// Compare-ready: the access-key underscore removed, whitespace
        /// collapsed, and a trailing colon or ellipsis dropped. "App_ly to
        /// radio" and "Apply to radio" are one label written twice.
        /// </summary>
        internal static string Normalise(string s)
        {
            string t = Collapse(s.Replace("_", "", StringComparison.Ordinal));
            t = Regex.Replace(t, @"(\.\.\.|…|[:：])\s*$", "");
            return t.Trim();
        }

        internal static string Collapse(string s)
            => Regex.Replace(s.Replace("&#10;", " ", StringComparison.Ordinal), @"\s+", " ").Trim();

        /// <summary>
        /// Two strings that say the same words, whatever punctuation they were
        /// typed with. Curly and straight quotes, an em dash and a comma, a
        /// trailing full stop: all invisible in speech, and all differences a
        /// literal comparison would report as a defect.
        /// </summary>
        internal static bool SameWords(string a, string b)
            => string.Equals(WordsOnly(a), WordsOnly(b), StringComparison.OrdinalIgnoreCase);

        private static string WordsOnly(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in Normalise(s))
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        internal static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..max].TrimEnd() + "…";

        // ────────────────────────────────────────────────────────────────
        //  The report — prose and bullets, never a table (screen readers)
        // ────────────────────────────────────────────────────────────────

        internal static string Report(Result result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Accessible names read from source, and the ones shaped like explanations (#363).");
            sb.AppendLine();
            sb.AppendLine("WHAT THIS PROVES: that a name is SHAPED like an explanation — a second");
            sb.AppendLine("sentence, or far more words than the label beside it.");
            sb.AppendLine("WHAT IT CANNOT PROVE: that a name is wrong. A long name is sometimes the");
            sb.AppendLine("right call. Every finding is a question for a person, and the answer to a");
            sb.AppendLine("finding that is fine is a baseline entry saying why.");
            sb.AppendLine();
            sb.AppendLine("Read " + result.XamlFilesRead + " XAML file(s), " + result.CodeFilesRead
                + " code-behind file(s) and " + result.LexiconEntries + " lexicon entries; found "
                + result.Controls.Count + " accessible name(s), "
                + result.Controls.Count(c => c.Label != null) + " of them on a control that also "
                + "carries its own visible label.");
            if (result.UnresolvedCodeNames > 0)
            {
                sb.AppendLine(result.UnresolvedCodeNames + " SetName call(s) name a variable this scan "
                    + "cannot resolve. Not defects — but not checked either, so the number is here "
                    + "rather than nowhere.");
            }
            sb.AppendLine();

            var findings = Findings(result);
            foreach (string direction in new[] { Prose, ExceedsLabel, TooLong })
            {
                var mine = findings.Where(f => f.Direction == direction).ToList();
                sb.AppendLine(Headline(direction) + " (" + mine.Count + "):");
                if (mine.Count == 0) sb.AppendLine("  none");
                foreach (var f in mine)
                {
                    sb.AppendLine("  " + f.Control.File + ":" + f.Control.Line + " " + f.Control.Element);
                    sb.AppendLine("    name:  \"" + f.Control.Name + "\"");
                    if (f.Control.Label != null)
                        sb.AppendLine("    label: \"" + f.Control.Label + "\" (" + f.Control.LabelSource + ")");
                    if (f.Control.NameKey.Length > 0)
                        sb.AppendLine("    name comes from lexicon key " + f.Control.NameKey);
                    sb.AppendLine("    baseline id: " + f.Id);
                }
                sb.AppendLine();
            }

            if (result.Notes.Count > 0)
            {
                sb.AppendLine("Scanner notes (" + result.Notes.Count + "):");
                foreach (string note in result.Notes.Distinct(StringComparer.Ordinal)
                             .OrderBy(s => s, StringComparer.Ordinal))
                {
                    sb.AppendLine("  " + note);
                }
            }
            return sb.ToString();
        }

        private static string Headline(string direction) => direction switch
        {
            Prose => "A second sentence inside the name — spoken in full on every landing",
            ExceedsLabel => "The name says far more than the visible label beside it",
            TooLong => "A long name with no visible label to compare against",
            _ => direction,
        };
    }
}
