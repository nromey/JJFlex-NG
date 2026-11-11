using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #989: jjprobe reads a named control's value, changing nothing, and a
    /// read that finds nothing says what it did not find instead of printing
    /// an empty line.
    /// </summary>
    /// <remarks>
    /// The tool has no test project and is not referenced here, so this
    /// compiles its REAL <c>tools/uia-probe/ValueRead.cs</c> with Roslyn, the
    /// way <c>JjprobeInputLayoutTests</c> does, and reads the wiring as source.
    /// The live read on the running app is a harness step: this cannot launch
    /// it.
    /// </remarks>
    public sealed class JjprobeValueReadTests
    {
        private static string Read(string name) =>
            File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "tools", "uia-probe", name));

        private static readonly Lazy<Type[]> Compiled = new(() =>
        {
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(x => MetadataReference.CreateFromFile(x)).ToList();
            var compilation = CSharpCompilation.Create("JjprobeValueRead_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(Read("ValueRead.cs")) }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var output = new MemoryStream();
            var emitted = compilation.Emit(output);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var asm = Assembly.Load(output.ToArray());
            return new[] { asm.GetType("JJFlex.UiaProbe.ValueReading")!, asm.GetType("JJFlex.UiaProbe.ValueRead")! };
        });

        private static object Reading(bool hasValue, string value, bool hasRange = false, double range = 0,
            double min = 0, double max = 0, string failure = null) =>
            Activator.CreateInstance(Compiled.Value[0], hasValue, value, hasRange, range, min, max, failure)!;

        private static string Describe(object reading) =>
            (string)Compiled.Value[1].GetMethod("Describe", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new[] { reading })!;

        private static string FocusText(object reading) =>
            (string)Compiled.Value[1].GetMethod("FocusText", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new[] { reading })!;

        [Fact]
        public void A_read_that_finds_nothing_says_what_it_did_not_find_and_is_never_empty()
        {
            Assert.Equal("NO SUCH CONTROL", Describe(null));
            string none = Describe(Reading(false, null));
            Assert.StartsWith("NO VALUE PATTERN", none, StringComparison.Ordinal);
            Assert.StartsWith("COULD NOT READ: gone", Describe(Reading(false, null, failure: "gone")), StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_value_is_told_apart_from_no_value_pattern()
        {
            string empty = Describe(Reading(true, ""));
            Assert.Contains("(empty", empty, StringComparison.Ordinal);
            Assert.DoesNotContain("NO VALUE PATTERN", empty, StringComparison.Ordinal);
            Assert.Equal("value: \"14.250000\"", Describe(Reading(true, "14.250000")));
        }

        [Fact]
        public void A_range_value_is_read_with_its_bounds()
        {
            Assert.Equal("range value: 35 (minimum 0, maximum 100)", Describe(Reading(false, null, true, 35, 0, 100)));
            string both = Describe(Reading(true, "35", true, 35, 0, 100));
            Assert.Contains("value: \"35\"", both, StringComparison.Ordinal);
            Assert.Contains("range value: 35", both, StringComparison.Ordinal);
        }

        [Fact]
        public void The_focus_line_shows_exactly_what_it_showed_before()
        {
            // Press compares the focus value before and after a key, so the
            // text must not change: the value, or "" when there is none.
            Assert.Equal("abc", FocusText(Reading(true, "abc")));
            Assert.Equal("", FocusText(Reading(true, "")));
            Assert.Equal("", FocusText(Reading(false, null, true, 5, 0, 10)));
            Assert.Equal("", FocusText(Reading(false, null, failure: "gone")));

            string observe = Read("Observe.cs");
            Assert.Contains("private static string TryValue(AutomationElement el) => ValueRead.FocusText(ReadValue(el));",
                observe, StringComparison.Ordinal);
        }

        [Fact]
        public void The_value_read_is_its_own_unarmed_verb_and_uses_acts_finder()
        {
            string program = Read("Program.cs");
            Assert.Contains("\"value\" => CmdValue(opt),", program, StringComparison.Ordinal);
            Assert.DoesNotContain("Armed(CmdValue", program, StringComparison.Ordinal);

            string tree = Read("Tree.cs");
            int read = tree.IndexOf("public static int ReadValue(", StringComparison.Ordinal);
            Assert.True(read > 0, "Act.ReadValue is gone; re-anchor this rule");
            string body = tree.Substring(read, tree.IndexOf("public static string Perform(", read, StringComparison.Ordinal) - read);
            Assert.Contains("Find(pid, automationId, name, className, index,", body, StringComparison.Ordinal);
            Assert.Contains("Observe.ReadValue(el)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SetValue", body, StringComparison.Ordinal);
            Assert.DoesNotContain(".Invoke(", body, StringComparison.Ordinal);
        }
    }
}
