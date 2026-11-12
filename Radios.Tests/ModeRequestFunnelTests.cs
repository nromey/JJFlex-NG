using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1117, repair round 3: every mode request the app makes goes through ONE
    /// funnel, <c>MainWindow.AskRadioForMode</c>, which counts the choice before
    /// it asks. Round 2 counted at each road and a text search checked for a
    /// nearby count; Codex showed that search could be evaded (an alias, extra
    /// whitespace, <c>RequestMode</c>, another variable name) and satisfied by an
    /// unrelated count nearby. This reads the syntax instead: it finds every
    /// mode write, 60 m correction and memory recall in the WPF project and
    /// fails on any not made inside a call to the funnel (or inside the funnel
    /// itself). The app's VB files have one road (the memory scan) and are
    /// read line by line, outside <c>#If 0</c>.
    /// </summary>
    public class ModeRequestFunnelTests
    {
        private const string Funnel = "AskRadioForMode";

        /// <summary>
        /// Writes that are not a request to the radio, each with its reason. Keyed
        /// by file and the statement's text, so a new write never hides behind one.
        /// </summary>
        private static readonly (string File, string Statement, string Why)[] NotRequests =
        {
            ("MainWindow.xaml.cs", "mem.Mode = mode", "the Memories window edits a stored memory's mode; the radio is not asked"),
            ("FixerDialog.cs", "s.Mode = StationConditions.Mode(rig)", "a station snapshot's field, read from the radio"),
        };

        /// <summary>Every request the scanner finds in some C# source: its line and text, with whether it is inside the funnel.</summary>
        internal static List<(int Line, string Text, bool InFunnel)> Requests(string source)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            var found = new List<(int, string, bool)>();
            foreach (var node in root.DescendantNodes())
            {
                bool request = node switch
                {
                    // A write of the mode, through any receiver: rig.Mode, r.Mode, x?.Mode, this.RXMode.
                    // A bare "Mode = ..." is a type's own property or an object
                    // initializer (a log row, a filter preset), never the radio's.
                    AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        => a.Left is not IdentifierNameSyntax && Name(a.Left) is "Mode" or "RXMode",
                    InvocationExpressionSyntax i => Name(i.Expression) switch
                    {
                        "RequestMode" or "SelectMemory" or "SelectMemoryByName" => true,
                        // SixtyMeterModeDelivery.Deliver, however it is spaced or qualified.
                        "Deliver" => i.Expression.ToString().Replace(" ", "").Replace("\n", "").Replace("\r", "").Contains("SixtyMeterModeDelivery.Deliver"),
                        // A memory recalled: Memory.Select() takes no arguments (LINQ's Select always does).
                        "Select" => i.ArgumentList.Arguments.Count == 0,
                        _ => false,
                    },
                    _ => false,
                };
                if (!request) continue;
                bool inFunnel = node.Ancestors().Any(n =>
                    (n is ArgumentSyntax && n.Parent?.Parent is InvocationExpressionSyntax call && Name(call.Expression) == Funnel)
                    || (n is MethodDeclarationSyntax m && m.Identifier.ValueText == Funnel));
                int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                found.Add((line, node.ToString(), inFunnel));
            }
            return found;
        }

        private static string? Name(ExpressionSyntax e) => e switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
            ConditionalAccessExpressionSyntax ca => Name(ca.WhenNotNull),
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null,
        };

        [Fact]
        public void Every_mode_request_in_the_app_goes_through_the_funnel()
        {
            string repo = FieldKeyMapScan.RepoRoot();
            var outside = new List<string>();
            int requests = 0;
            foreach (string file in Directory.GetFiles(Path.Combine(repo, "JJFlexWpf"), "*.cs", SearchOption.AllDirectories)
                         .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                  && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            {
                string name = Path.GetFileName(file);
                foreach (var r in Requests(File.ReadAllText(file)))
                {
                    if (NotRequests.Any(x => x.File == name && r.Text.Contains(x.Statement))) continue;
                    requests++;
                    if (!r.InFunnel) outside.Add(name + ":" + r.Line + "  " + r.Text);
                }
            }

            // The VB side: the memory scan. Line by line, outside #If 0.
            var vbRoad = new Regex(@"(\.\s*(Mode|RXMode)\s*=)|RequestMode\s*\(|SelectMemoryByName\s*\(|SelectMemory\s*\(", RegexOptions.IgnoreCase);
            foreach (string file in Directory.GetFiles(repo, "*.vb", SearchOption.TopDirectoryOnly))
            {
                bool disabled = false;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("#If 0", StringComparison.OrdinalIgnoreCase)) { disabled = true; continue; }
                    if (disabled && t.StartsWith("#End If", StringComparison.OrdinalIgnoreCase)) { disabled = false; continue; }
                    if (disabled || t.StartsWith("'") || !vbRoad.IsMatch(t)) continue;
                    if (Regex.IsMatch(t, @"^(If|ElseIf|Case|While|Do|Loop|Return|Dim)\b", RegexOptions.IgnoreCase)
                        || t.Contains(" Function ") || t.Contains(" Sub ") && !t.Contains("Sub()")) continue;   // a comparison or a declaration
                    requests++;
                    if (!t.Contains(Funnel + "(")) outside.Add(Path.GetFileName(file) + ":" + (i + 1) + "  " + t);
                }
            }

            Assert.True(requests >= 10, "the scan found only " + requests + " mode requests, so it is not seeing them");
            Assert.True(outside.Count == 0, "mode requests made outside MainWindow.AskRadioForMode, so they never count a mode choice:\n" + string.Join("\n", outside));
        }

        /// <summary>
        /// The shapes Codex used to evade round 2's search, each caught; a request
        /// inside the funnel's call, and the funnel's own body, accepted; a count
        /// nearby does not stand in for the funnel.
        /// </summary>
        [Theory]
        [InlineData("var r = Rig; r.Mode = \"CW\";", true)]
        [InlineData("rig  .  Mode   =   m;", true)]
        [InlineData("Rig?.RXMode = m;", true)]
        [InlineData("rig.RequestMode(\"CW\");", true)]
        [InlineData("SixtyMeterModeDelivery .Deliver(rig, a, c, i);", true)]
        [InlineData("Radios.SixtyMeterModeDelivery.Deliver(rig, a, c, i);", true)]
        [InlineData("memory.Select();", true)]
        [InlineData("RigControl.SelectMemoryByName(n);", true)]
        [InlineData("_window.NoteModeChoice(); rig.Mode = m;", true)]
        [InlineData("_window.AskRadioForMode(() => rig.Mode = m);", false)]
        [InlineData("AskRadioForMode(() => Radios.SixtyMeterModeDelivery.Deliver(rig, a, c, i));", false)]
        [InlineData("AskRadioForMode(() => { var r = rig; r.Mode = m; });", false)]
        public void The_scan_catches_every_shape_and_accepts_only_the_funnel(string body, bool outsideTheFunnel)
        {
            string source = "class C { void M() { " + body + " } }";
            var found = Requests(source);
            Assert.NotEmpty(found);
            Assert.Equal(outsideTheFunnel, found.Any(r => !r.InFunnel));
        }

        [Fact]
        public void The_funnel_counts_the_choice_before_it_asks()
        {
            string main = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs")).Replace("\r\n", "\n");
            int at = main.IndexOf("public void AskRadioForMode(Action ask)", StringComparison.Ordinal);
            Assert.True(at >= 0, "MainWindow.AskRadioForMode was not found");
            string body = main.Substring(at, main.IndexOf("\n    }\n", at, StringComparison.Ordinal) - at);
            int count = body.IndexOf("NoteModeChoice();", StringComparison.Ordinal);
            int ask = body.IndexOf("ask();", StringComparison.Ordinal);
            Assert.True(count > 0 && ask > count, "the funnel must count the choice, then ask");
        }
    }
}
